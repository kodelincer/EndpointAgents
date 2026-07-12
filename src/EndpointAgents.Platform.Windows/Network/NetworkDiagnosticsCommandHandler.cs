using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Network;

[SupportedOSPlatform("windows")]
public sealed class NetworkDiagnosticsCommandHandler : IEndpointCommandHandler
{
    private static readonly string[] DefaultTargets =
    [
        "https://www.msftconnecttest.com/connecttest.txt",
        "https://www.microsoft.com"
    ];

    private readonly ILogger<NetworkDiagnosticsCommandHandler> _logger;

    public NetworkDiagnosticsCommandHandler(ILogger<NetworkDiagnosticsCommandHandler> logger)
    {
        _logger = logger;
    }

    public string Method => "network.diagnostics";

    public async Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var options = ParseOptions(request.Params);
        var warnings = new List<DiagnosticWarning>();
        var checks = new List<DiagnosticCheckResult>();

        checks.Add(await RunCheckAsync("ping.gateway", () => PingGatewaysAsync(cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("ping.dns_servers", () => PingDnsServersAsync(cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("ping.public_ip", () => PingHostAsync("1.1.1.1", cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("dns.resolve", () => ResolveHostAsync("www.microsoft.com", cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("http.targets", () => ProbeHttpTargetsAsync(options.Targets, cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("firewall.profiles", () => RunProcessAsync("netsh", "advfirewall show allprofiles", cancellationToken), cancellationToken));
        checks.Add(await RunCheckAsync("ras.connections", () => RunProcessAsync("rasdial", string.Empty, cancellationToken), cancellationToken));

        if (options.IncludeWifi)
        {
            checks.Add(await RunCheckAsync("wifi.interfaces", () => RunProcessAsync("netsh", "wlan show interfaces", cancellationToken), cancellationToken));
        }

        if (options.IncludeEventLogs)
        {
            checks.Add(RunEventLogCheck());
        }

        var result = new
        {
            Options = new
            {
                options.Targets,
                options.IncludeWifi,
                options.IncludeEventLogs
            },
            Summary = new
            {
                Passed = checks.Count(x => x.Status == "Pass"),
                Failed = checks.Count(x => x.Status == "Fail"),
                Warnings = checks.Count(x => x.Status == "Warning")
            }
        };

        return RpcResponse.Success(
            request.Id,
            DiagnosticResponse.Create(context, result, warnings, checks));
    }

    private static NetworkDiagnosticsOptions ParseOptions(JsonElement? parameters)
    {
        var targets = DefaultTargets;
        var includeWifi = true;
        var includeEventLogs = false;

        if (parameters is { ValueKind: JsonValueKind.Object } element)
        {
            if (element.TryGetProperty("targets", out var targetsElement)
                && targetsElement.ValueKind == JsonValueKind.Array)
            {
                targets = targetsElement
                    .EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Select(x => x!)
                    .Take(10)
                    .ToArray();
            }

            if (element.TryGetProperty("includeWifi", out var wifiElement)
                && wifiElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                includeWifi = wifiElement.GetBoolean();
            }

            if (element.TryGetProperty("includeEventLogs", out var eventsElement)
                && eventsElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                includeEventLogs = eventsElement.GetBoolean();
            }
        }

        return new NetworkDiagnosticsOptions(
            targets.Length == 0 ? DefaultTargets : targets,
            includeWifi,
            includeEventLogs);
    }

    private async Task<DiagnosticCheckResult> RunCheckAsync(
        string name,
        Func<Task<object>> check,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var details = await check();
            stopwatch.Stop();

            return new DiagnosticCheckResult(name, "Pass", details, stopwatch.ElapsedMilliseconds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            _logger.LogWarning(ex, "Network diagnostic check {CheckName} failed.", name);

            return new DiagnosticCheckResult(
                name,
                "Fail",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static async Task<object> PingGatewaysAsync(CancellationToken cancellationToken)
    {
        var gateways = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().GatewayAddresses)
            .Select(gateway => gateway.Address.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return new
        {
            Gateways = await PingManyAsync(gateways, cancellationToken)
        };
    }

    private static async Task<object> PingDnsServersAsync(CancellationToken cancellationToken)
    {
        var dnsServers = NetworkInterface
            .GetAllNetworkInterfaces()
            .Where(adapter => adapter.OperationalStatus == OperationalStatus.Up)
            .SelectMany(adapter => adapter.GetIPProperties().DnsAddresses)
            .Select(dns => dns.ToString())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToArray();

        return new
        {
            DnsServers = await PingManyAsync(dnsServers, cancellationToken)
        };
    }

    private static async Task<object> PingHostAsync(string host, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var reply = await ping.SendPingAsync(host, 3000).WaitAsync(cancellationToken);

        return new
        {
            Host = host,
            Status = reply.Status.ToString(),
            Address = reply.Address?.ToString(),
            reply.RoundtripTime
        };
    }

    private static async Task<object[]> PingManyAsync(string[] hosts, CancellationToken cancellationToken)
    {
        var results = new List<object>();

        foreach (var host in hosts)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                results.Add(await PingHostAsync(host, cancellationToken));
            }
            catch (Exception ex)
            {
                results.Add(new
                {
                    Host = host,
                    Error = DiagnosticResponse.Sanitize(ex)
                });
            }
        }

        return results.ToArray();
    }

    private static async Task<object> ResolveHostAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);

        return new
        {
            Host = host,
            Addresses = addresses.Select(address => address.ToString()).ToArray()
        };
    }

    private static async Task<object> ProbeHttpTargetsAsync(string[] targets, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false
        };

        using var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(8)
        };

        var results = new List<object>();

        foreach (var target in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                var uri = BuildUri(target);
                using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                stopwatch.Stop();

                results.Add(new
                {
                    Target = uri.ToString(),
                    StatusCode = (int)response.StatusCode,
                    response.ReasonPhrase,
                    RedirectLocation = response.Headers.Location?.ToString(),
                    DurationMs = stopwatch.ElapsedMilliseconds
                });
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                results.Add(new
                {
                    Target = target,
                    DurationMs = stopwatch.ElapsedMilliseconds,
                    Error = DiagnosticResponse.Sanitize(ex)
                });
            }
        }

        return new
        {
            Targets = results.ToArray()
        };
    }

    private static Uri BuildUri(string target)
    {
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            return uri;
        }

        return new Uri($"https://{target}");
    }

    private static async Task<object> RunProcessAsync(
        string fileName,
        string arguments,
        CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        process.Start();
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);

        var exited = await WaitForExitAsync(process, TimeSpan.FromSeconds(5), cancellationToken);
        if (!exited)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // Best effort cleanup after a timed out diagnostic command.
            }
        }

        var output = await outputTask;
        var error = await errorTask;

        return new
        {
            fileName,
            ExitCode = exited ? process.ExitCode : (int?)null,
            TimedOut = !exited,
            Output = Truncate(output),
            Error = Truncate(error)
        };
    }

    private static async Task<bool> WaitForExitAsync(
        Process process,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var exitTask = process.WaitForExitAsync(cancellationToken);
        var timeoutTask = Task.Delay(timeout, cancellationToken);
        return await Task.WhenAny(exitTask, timeoutTask) == exitTask;
    }

    private static DiagnosticCheckResult RunEventLogCheck()
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var output = RunProcessAsync(
                "wevtutil.exe",
                "qe System /c:20 /rd:true /f:text /q:\"*[System[(Level=2 or Level=3)]]\"",
                CancellationToken.None).GetAwaiter().GetResult();
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "eventlog.network",
                "Pass",
                output,
                stopwatch.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            return new DiagnosticCheckResult(
                "eventlog.network",
                "Warning",
                null,
                stopwatch.ElapsedMilliseconds,
                DiagnosticResponse.Sanitize(ex));
        }
    }

    private static string Truncate(string value, int maxLength = 4000)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= maxLength)
        {
            return value;
        }

        return value[..maxLength] + "...";
    }

    private sealed record NetworkDiagnosticsOptions(
        string[] Targets,
        bool IncludeWifi,
        bool IncludeEventLogs);
}
