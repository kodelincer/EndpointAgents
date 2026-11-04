using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Channels;

namespace ControlPlane.Api.Problems.Forwarding;

public sealed record LogForwarderStatus(
    bool Enabled, bool Down, DateTimeOffset? LastSuccessUtc, DateTimeOffset? LastErrorUtc, string? LastError,
    int Waiting, long Dropped, long Sent);

/// <summary>Sends events to one log server. The token never appears in a message or an exception.</summary>
public static class LogServerClient
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public static async Task SendAsync(LogServerSettings settings, string? token,
        IReadOnlyList<LogForwardEvent> events, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        switch (settings.Type)
        {
            case LogServerTypes.Splunk:
                using (var client = CreateHttpClient(settings))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, Url(settings, SplunkFormat.Path))
                    {
                        Content = new StringContent(SplunkFormat.Body(events, settings.Index), Encoding.UTF8, "application/json")
                    };
                    request.Headers.TryAddWithoutValidation("Authorization", SplunkFormat.AuthorizationValue(token ?? ""));
                    await EnsureOkAsync(client, request, timeout.Token);
                }
                break;
            case LogServerTypes.Graylog:
                using (var client = CreateHttpClient(settings))
                    foreach (var item in events)
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Post, Url(settings, GelfFormat.Path))
                        {
                            Content = new StringContent(GelfFormat.Body(item), Encoding.UTF8, "application/json")
                        };
                        if (!string.IsNullOrEmpty(token))
                            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                        await EnsureOkAsync(client, request, timeout.Token);
                    }
                break;
            case LogServerTypes.Syslog when settings.Protocol == LogServerProtocols.Udp:
                using (var udp = new UdpClient())
                {
                    udp.Connect(settings.Host, settings.Port);
                    foreach (var item in events)
                        await udp.SendAsync(SyslogFormat.UdpPacket(item), timeout.Token);
                }
                break;
            case LogServerTypes.Syslog:
                await SendSyslogStreamAsync(settings, events, timeout.Token);
                break;
            default:
                throw new InvalidOperationException("The log server type is not known.");
        }
    }

    private static async Task SendSyslogStreamAsync(LogServerSettings settings,
        IReadOnlyList<LogForwardEvent> events, CancellationToken cancellationToken)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(settings.Host, settings.Port, cancellationToken);
        Stream stream = tcp.GetStream();
        if (settings.Protocol == LogServerProtocols.Tls)
        {
            var ssl = new SslStream(stream, false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = settings.Host,
                RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
                    CertificateAccepted(settings, certificate, errors)
            }, cancellationToken);
            stream = ssl;
        }
        await using (stream)
        {
            foreach (var item in events)
                await stream.WriteAsync(SyslogFormat.TcpFrame(item), cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }
    }

    private static HttpClient CreateHttpClient(LogServerSettings settings)
    {
        var handler = new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.SslOptions.RemoteCertificateValidationCallback = (_, certificate, _, errors) =>
            CertificateAccepted(settings, certificate, errors);
        return new HttpClient(handler) { Timeout = Timeout };
    }

    /// <summary>
    /// A normal trusted certificate passes. Internal servers often use their own certificate, so an
    /// admin can pin exactly that one by its SHA-256 fingerprint instead of turning the check off.
    /// </summary>
    internal static bool CertificateAccepted(LogServerSettings settings, X509Certificate? certificate,
        SslPolicyErrors errors)
    {
        if (errors == SslPolicyErrors.None) return true;
        var pinned = LogServerValidation.NormalizeThumbprint(settings.CertificateThumbprint);
        return pinned is not null && certificate is not null
            && Convert.ToHexString(SHA256.HashData(certificate.GetRawCertData())) == pinned;
    }

    private static Uri Url(LogServerSettings settings, string path) =>
        new UriBuilder(settings.Protocol, settings.Host, settings.Port, path).Uri;

    private static async Task EnsureOkAsync(HttpClient client, HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        using var response = await client.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"The log server answered HTTP {(int)response.StatusCode}.");
    }
}

/// <summary>
/// Sends a copy of every report to the outside log server, from this control plane only. The queue
/// has a fixed size and drops the oldest copies, so a server that is down never slows down the PCs
/// or the API; it only shows a warning on the dashboard.
/// </summary>
public sealed class LogForwarder(LogServerSettingsStore settingsStore, TimeProvider clock,
    ILogger<LogForwarder> logger) : BackgroundService
{
    public const int QueueCapacity = 2000;
    private const int BatchSize = 25;
    private static readonly TimeSpan FirstRetry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LastRetry = TimeSpan.FromMinutes(5);

    private readonly Channel<LogForwardEvent> _queue = Channel.CreateBounded<LogForwardEvent>(
        new BoundedChannelOptions(QueueCapacity) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true },
        _ => { });
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly object _statusGate = new();
    private volatile bool _enabled;
    private long _dropped;
    private long _sent;
    private DateTimeOffset? _lastSuccessUtc;
    private DateTimeOffset? _lastErrorUtc;
    private string? _lastError;

    /// <summary>Replaced in tests.</summary>
    internal Func<LogServerSettings, string?, IReadOnlyList<LogForwardEvent>, CancellationToken, Task> Send { get; set; }
        = LogServerClient.SendAsync;

    public void Enqueue(LogForwardEvent item)
    {
        if (!_enabled) return;
        // A full queue drops its oldest copy to make room for this one.
        if (_queue.Reader.Count >= QueueCapacity) Interlocked.Increment(ref _dropped);
        _queue.Writer.TryWrite(item);
    }

    public LogForwarderStatus GetStatus()
    {
        lock (_statusGate)
            return new LogForwarderStatus(_enabled, _enabled && IsDown(), _lastSuccessUtc, _lastErrorUtc, _lastError,
                _queue.Reader.Count, Interlocked.Read(ref _dropped), Interlocked.Read(ref _sent));
    }

    private bool IsDown() => _lastErrorUtc is { } error && (_lastSuccessUtc is not { } success || error > success);

    /// <summary>The settings changed: use them from now on, and forget the old errors.</summary>
    public async Task ReloadAsync(CancellationToken cancellationToken)
    {
        _enabled = (await settingsStore.GetAsync(cancellationToken)).Enabled;
        lock (_statusGate) { _lastError = null; _lastErrorUtc = null; }
        try { _changed.Release(); }
        catch (SemaphoreFullException) { }
    }

    /// <summary>Sends one test event now, with what the admin typed. Returns what went wrong, or null.</summary>
    public async Task<string?> TestAsync(LogServerSettings settings, string? token, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var test = new LogForwardEvent("test", "Test", "ControlPlane",
            "Test message from the EndpointAgents control plane.", null, null, "control-plane",
            Environment.MachineName, null, null, null, null, null, null, null, null, 1, now, now);
        try
        {
            await Send(settings, token, [test], cancellationToken);
            return null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return "The log server did not answer in time.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Describe(ex);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await ReloadAsync(stoppingToken); }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        catch (Exception ex) { logger.LogError(ex, "The log server settings could not be read."); }

        var delay = FirstRetry;
        var batch = new List<LogForwardEvent>(BatchSize);
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (batch.Count == 0)
                {
                    await _queue.Reader.WaitToReadAsync(stoppingToken);
                    while (batch.Count < BatchSize && _queue.Reader.TryRead(out var item)) batch.Add(item);
                }
                var settings = await settingsStore.GetAsync(stoppingToken);
                if (!settings.Enabled) { batch.Clear(); continue; }
                await Send(settings, await settingsStore.GetTokenAsync(stoppingToken), batch, stoppingToken);
                Interlocked.Add(ref _sent, batch.Count);
                batch.Clear();
                delay = FirstRetry;
                lock (_statusGate) _lastSuccessUtc = clock.GetUtcNow();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                var message = Describe(ex);
                lock (_statusGate) { _lastError = message; _lastErrorUtc = clock.GetUtcNow(); }
                logger.LogWarning("Forwarding to the log server failed: {Problem}", message);
                await WaitAsync(delay, stoppingToken);
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, LastRetry.Ticks));
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken stoppingToken)
    {
        try { await _changed.WaitAsync(delay, stoppingToken); }
        catch (OperationCanceledException) { }
    }

    /// <summary>Short and without the token: only the exception's own text, which never holds it.</summary>
    private static string Describe(Exception ex) => ex switch
    {
        OperationCanceledException => "The log server did not answer in time.",
        global::System.Security.Authentication.AuthenticationException =>
            "The log server's certificate is not trusted. Trust it on this server or enter its fingerprint.",
        HttpRequestException { InnerException: global::System.Security.Authentication.AuthenticationException } =>
            "The log server's certificate is not trusted. Trust it on this server or enter its fingerprint.",
        HttpRequestException or SocketException or IOException => ex.Message.Length <= 200 ? ex.Message : ex.Message[..200],
        _ => "Sending failed: " + ex.GetType().Name
    };
}
