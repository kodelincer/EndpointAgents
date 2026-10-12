using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Platform.Windows.Windows;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.EndpointInfo;

[SupportedOSPlatform("windows")]
public sealed class EndpointInfoCommandHandler : IEndpointCommandHandler
{
    private readonly ILogger<EndpointInfoCommandHandler> _logger;
    private readonly InteractiveUserSessionLocator _sessionLocator;
    private readonly EndpointInfoOptions _options;

    public EndpointInfoCommandHandler(
        ILogger<EndpointInfoCommandHandler> logger,
        InteractiveUserSessionLocator sessionLocator,
        IOptions<EndpointInfoOptions> options)
    {
        _logger = logger;
        _sessionLocator = sessionLocator;
        _options = options.Value;
    }

    public string Method => "endpoint.get_info";

    public async Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var warnings = new List<DiagnosticWarning>();
        var requestedUserSid = ParseUserSid(request.Params);
        var activeSession = Safe("user.active_session",
            () => string.IsNullOrWhiteSpace(requestedUserSid)
                ? _sessionLocator.GetActiveSessions().FirstOrDefault()
                : _sessionLocator.GetActiveSessions().FirstOrDefault(session =>
                    session.UserSid.Equals(requestedUserSid,
                        StringComparison.OrdinalIgnoreCase)), warnings);
        var result = new
        {
            User = CollectUserInfo(activeSession),
            Machine = CollectMachineInfo(warnings),
            Adapters = CollectAdapters(warnings),
            Proxy = CollectProxySettings(warnings),
            Internet = await CollectInternetStatusAsync(warnings, cancellationToken),
            Time = new
            {
                LocalNow = DateTimeOffset.Now,
                UtcNow = DateTimeOffset.UtcNow,
                TimeZone = TimeZoneInfo.Local.DisplayName
            }
        };

        return RpcResponse.Success(
            request.Id,
            DiagnosticResponse.Create(context, result, warnings));
    }

    private static object CollectUserInfo(InteractiveUserSession? activeSession)
    {
        return new
        {
            ActiveInteractiveUser = activeSession is null ? null : new
            {
                activeSession.SessionId,
                activeSession.UserName,
                activeSession.DomainName,
                activeSession.UserSid,
                activeSession.DesktopPath
            },
            AgentExecutionIdentity = new
            {
                UserName = Environment.UserName,
                DomainName = Environment.UserDomainName,
                Environment.UserInteractive
            }
        };
    }

    private static string? ParseUserSid(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("userSid", out var sid)
        && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()
            : null;

    private object CollectMachineInfo(List<DiagnosticWarning> warnings)
    {
        return new
        {
            Hostname = SafeValue(() => Dns.GetHostName(), Environment.MachineName),
            MachineName = Environment.MachineName,
            OSDescription = RuntimeInformation.OSDescription,
            OSVersion = Environment.OSVersion.VersionString,
            Windows = Safe("machine.windows_version", CollectWindowsVersion, warnings),
            OSArchitecture = RuntimeInformation.OSArchitecture.ToString(),
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            Framework = RuntimeInformation.FrameworkDescription,
            Environment.Is64BitOperatingSystem,
            Environment.Is64BitProcess,
            ProcessorCount = Environment.ProcessorCount,
            SystemDirectory = Environment.SystemDirectory,
            Uptime = TimeSpan.FromMilliseconds(Environment.TickCount64).ToString()
        };
    }

    private static object CollectWindowsVersion()
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        var build = key?.GetValue("CurrentBuildNumber")?.ToString();

        return new
        {
            ProductName = WindowsVersionInfo.NormalizeProductName(
                key?.GetValue("ProductName")?.ToString(), build),
            DisplayVersion = key?.GetValue("DisplayVersion")?.ToString(),
            CurrentBuild = build,
            UpdateBuildRevision = key?.GetValue("UBR"),
            InstallationType = key?.GetValue("InstallationType")?.ToString()
        };
    }

    private async Task<InternetStatus> CollectInternetStatusAsync(
        List<DiagnosticWarning> warnings,
        CancellationToken cancellationToken)
    {
        var networkAvailable = NetworkInterface.GetIsNetworkAvailable();
        var checkUri = new Uri(_options.InternetCheckUrl);
        var addresses = Array.Empty<string>();
        string? dnsError = null;
        string? httpError = null;

        try
        {
            addresses = (await Dns.GetHostAddressesAsync(checkUri.Host, cancellationToken))
                .Select(address => address.ToString())
                .ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            dnsError = ex.GetType().Name;
            AddWarning(warnings, "internet.dns",
                $"Failed to resolve the internet-check host '{checkUri.Host}'.", ex);
        }

        int? statusCode = null;
        string? redirectLocation = null;
        var expectedContentMatched = false;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(_options.InternetCheckTimeoutSeconds));
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            using var response = await client.GetAsync(checkUri,
                HttpCompletionOption.ResponseHeadersRead, timeout.Token);

            statusCode = (int)response.StatusCode;
            redirectLocation = response.Headers.Location?.ToString();

            await using var content = await response.Content.ReadAsStreamAsync(timeout.Token);
            var buffer = new byte[512];
            var read = await content.ReadAsync(buffer, timeout.Token);
            var text = Encoding.UTF8.GetString(buffer, 0, read);
            expectedContentMatched = string.IsNullOrWhiteSpace(_options.ExpectedInternetContent)
                || text.Contains(_options.ExpectedInternetContent, StringComparison.Ordinal);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            httpError = "Timeout";
            AddWarning(warnings, "internet.http",
                $"Internet check exceeded {_options.InternetCheckTimeoutSeconds} seconds.", ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            httpError = ex.GetType().Name;
            AddWarning(warnings, "internet.http",
                $"Failed to request the internet-check URL '{checkUri}'.", ex);
        }

        var dnsResolved = addresses.Length > 0;
        var httpReachable = statusCode is >= 200 and < 300;
        var captivePortalLikely = statusCode is >= 300 and < 400
            || httpReachable && !expectedContentMatched;
        var online = networkAvailable && dnsResolved && httpReachable
            && expectedContentMatched && !captivePortalLikely;

        return new InternetStatus(
            online,
            networkAvailable,
            checkUri.ToString(),
            dnsResolved,
            addresses,
            statusCode,
            httpReachable,
            expectedContentMatched,
            redirectLocation,
            captivePortalLikely,
            dnsError,
            httpError);
    }

    private object[] CollectAdapters(List<DiagnosticWarning> warnings)
    {
        try
        {
            return NetworkInterface
                .GetAllNetworkInterfaces()
                .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
                .Select(adapter => CollectAdapter(adapter, warnings))
                .ToArray();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "network.adapters", "Failed to collect network adapters.", ex);
            return Array.Empty<object>();
        }
    }

    private object CollectAdapter(NetworkInterface adapter, List<DiagnosticWarning> warnings)
    {
        try
        {
            var ipProperties = adapter.GetIPProperties();
            var ipv4Properties = Safe("adapter.ipv4_properties", () => ipProperties.GetIPv4Properties(), warnings);

            return new
            {
                adapter.Name,
                adapter.Description,
                Id = adapter.Id,
                Type = adapter.NetworkInterfaceType.ToString(),
                OperationalStatus = adapter.OperationalStatus.ToString(),
                adapter.Speed,
                MacAddress = FormatMacAddress(adapter.GetPhysicalAddress()),
                IsLikelyVirtual = IsLikelyVirtual(adapter),
                IsLikelyVpn = IsLikelyVpn(adapter),
                DnsSuffix = ipProperties.DnsSuffix,
                DhcpEnabled = ipv4Properties?.IsDhcpEnabled,
                IpAddresses = ipProperties.UnicastAddresses
                    .Select(address => new
                    {
                        Address = address.Address.ToString(),
                        Family = address.Address.AddressFamily.ToString(),
                        address.PrefixLength,
                        SuffixOrigin = address.SuffixOrigin.ToString(),
                        PrefixOrigin = address.PrefixOrigin.ToString()
                    })
                    .ToArray(),
                IPv4Addresses = ipProperties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                    .Select(address => address.Address.ToString())
                    .ToArray(),
                IPv6Addresses = ipProperties.UnicastAddresses
                    .Where(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                    .Select(address => address.Address.ToString())
                    .ToArray(),
                Gateways = ipProperties.GatewayAddresses
                    .Select(gateway => gateway.Address.ToString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToArray(),
                DnsServers = ipProperties.DnsAddresses
                    .Select(dns => dns.ToString())
                    .ToArray(),
                WinsServers = ipProperties.WinsServersAddresses
                    .Select(wins => wins.ToString())
                    .ToArray()
            };
        }
        catch (Exception ex)
        {
            AddWarning(warnings, $"network.adapter.{adapter.Name}", "Failed to collect one network adapter.", ex);

            return new
            {
                adapter.Name,
                adapter.Description,
                Type = adapter.NetworkInterfaceType.ToString(),
                Error = DiagnosticResponse.Sanitize(ex)
            };
        }
    }

    private object CollectProxySettings(List<DiagnosticWarning> warnings)
    {
        try
        {
            var proxy = WebRequest.DefaultWebProxy;
            var testUri = new Uri("http://example.com/");
            var proxyUri = proxy?.GetProxy(testUri);

            return new
            {
                ProxyConfigured = proxyUri is not null && proxyUri != testUri,
                ProxyUri = proxyUri is null || proxyUri == testUri ? null : proxyUri.ToString()
            };
        }
        catch (Exception ex)
        {
            AddWarning(warnings, "network.proxy", "Failed to collect proxy settings.", ex);
            return new
            {
                ProxyConfigured = (bool?)null
            };
        }
    }

    private T? Safe<T>(string source, Func<T> action, List<DiagnosticWarning> warnings)
    {
        try
        {
            return action();
        }
        catch (Exception ex)
        {
            AddWarning(warnings, source, "A non-critical collector failed.", ex);
            return default;
        }
    }

    private void AddWarning(
        List<DiagnosticWarning> warnings,
        string source,
        string message,
        Exception ex)
    {
        _logger.LogWarning(ex, "{Source}: {Message}", source, message);
        warnings.Add(new DiagnosticWarning(source, message, ex.GetType().Name));
    }

    private static string? FormatMacAddress(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 0 ? null : string.Join("-", bytes.Select(x => x.ToString("X2")));
    }

    internal static bool IsLikelyVirtual(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return text.Contains("virtual", StringComparison.Ordinal)
            || text.Contains("hyper-v", StringComparison.Ordinal)
            || text.Contains("vmware", StringComparison.Ordinal)
            || text.Contains("virtualbox", StringComparison.Ordinal)
            || text.Contains("loopback", StringComparison.Ordinal);
    }

    internal static bool IsLikelyVpn(NetworkInterface adapter)
    {
        var text = $"{adapter.Name} {adapter.Description}".ToLowerInvariant();
        return adapter.NetworkInterfaceType == NetworkInterfaceType.Ppp
            || text.Contains("vpn", StringComparison.Ordinal)
            || text.Contains("tap", StringComparison.Ordinal)
            || text.Contains("tun", StringComparison.Ordinal)
            || text.Contains("pppoe", StringComparison.Ordinal)
            || text.Contains("wan miniport", StringComparison.Ordinal);
    }

    private static T SafeValue<T>(Func<T> getValue, T fallback)
    {
        try
        {
            return getValue();
        }
        catch
        {
            return fallback;
        }
    }

    private sealed record InternetStatus(
        bool Online,
        bool NetworkAvailable,
        string CheckUrl,
        bool DnsResolved,
        string[] ResolvedAddresses,
        int? HttpStatusCode,
        bool HttpReachable,
        bool ExpectedContentMatched,
        string? RedirectLocation,
        bool CaptivePortalLikely,
        string? DnsError,
        string? HttpError);
}
