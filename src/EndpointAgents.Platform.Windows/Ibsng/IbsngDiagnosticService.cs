using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Ibsng;

/// <summary>Runs the complete read-only IBSng, network, and VPN diagnostic pipeline.</summary>
[SupportedOSPlatform("windows")]
public sealed class IbsngDiagnosticService
{
    private readonly ILogger<IbsngDiagnosticService> _logger;
    private readonly IbsngOptions _options;
    private readonly InteractiveUserSessionLocator _sessionLocator;
    private readonly IbsngDesktopReconciler _desktopReconciler;
    private readonly ITrayCommandClient _trayClient;
    private readonly IbsngConnectionInspector _connectionInspector;

    public IbsngDiagnosticService(ILogger<IbsngDiagnosticService> logger,
        IOptions<IbsngOptions> options,
        InteractiveUserSessionLocator sessionLocator,
        IbsngDesktopReconciler desktopReconciler,
        ITrayCommandClient trayClient,
        IbsngConnectionInspector connectionInspector)
    {
        _logger = logger;
        _options = options.Value;
        _sessionLocator = sessionLocator;
        _desktopReconciler = desktopReconciler;
        _trayClient = trayClient;
        _connectionInspector = connectionInspector;
    }

    /// <summary>Collects a typed report without reading or transporting credentials.</summary>
    public async Task<IbsngDiagnosticReport> DiagnoseAsync(string requestId,
        IbsngDiagnosticRequest request, CancellationToken cancellationToken)
    {
        var checks = new List<IbsngDiagnosticCheck>();
        var session = _sessionLocator.GetActiveSessions().FirstOrDefault();
        var dialerPath = session is null
            ? null
            : Path.Combine(session.DesktopPath, _options.DialerFileName);
        var dialerTrusted = false;

        if (session is null)
        {
            checks.Add(Fail("ibsng.session", "No interactive Windows user session is available."));
        }
        else
        {
            try
            {
                var inventory = await _desktopReconciler.InspectAsync(session.DesktopPath,
                    _options, cancellationToken);
                dialerTrusted = inventory.CanonicalArtifact.IsTrusted;
                checks.Add(new IbsngDiagnosticCheck("ibsng.dialer.file",
                    dialerTrusted ? "Pass" : "Fail",
                    dialerTrusted ? "The approved IBSng dialer is present."
                        : $"The IBSng dialer is {inventory.CanonicalArtifact.State}.",
                    inventory.CanonicalArtifact.Path));
                if (inventory.Duplicates.Count > 0)
                    checks.Add(Warn("ibsng.dialer.duplicates",
                        $"{inventory.Duplicates.Count} additional IBSng executable(s) were found."));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                checks.Add(Fail("ibsng.dialer.file", "The dialer file could not be inspected.",
                    ex.GetType().Name));
            }
        }

        var uiSnapshot = await ReadDialerUiAsync(requestId, request, session, dialerPath,
            cancellationToken);
        checks.Add(new IbsngDiagnosticCheck("ibsng.dialer.ui",
            uiSnapshot.ReadState == IbsngUiReadState.Success ? "Pass" : "Warning",
            UiSummary(uiSnapshot), uiSnapshot.Message));

        var adapters = GetNetworkAdapters();
        AddAdapterChecks(checks, adapters);
        var addresses = await ResolveServerAsync(request.ServerHost, checks, cancellationToken);
        await AddGatewayCheckAsync(checks, adapters, cancellationToken);
        AddRouteCheck(checks, adapters, addresses, request.Protocol);
        await AddProtocolCheckAsync(checks, addresses, request.Protocol, cancellationToken);
        await AddPortalCheckAsync(checks, cancellationToken);

        IbsngConnectionStateResult connection;
        try
        {
            connection = await _connectionInspector.GetConnectionStateAsync(request.Protocol,
                uiSnapshot, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Windows RAS inspection failed.");
            connection = new IbsngConnectionStateResult(uiSnapshot.ParsedState,
                Array.Empty<IbsngRasConnectionSnapshot>(),
                "Windows RAS information could not be read.");
            checks.Add(Warn("ibsng.ras", "Windows RAS information could not be read.",
                ex.GetType().Name));
        }

        checks.Add(new IbsngDiagnosticCheck("ibsng.connection",
            connection.State == IbsngConnectionState.Connected ? "Pass"
                : connection.State is IbsngConnectionState.Failed
                    or IbsngConnectionState.Inconsistent ? "Fail" : "Warning",
            connection.Summary));
        var summary = BuildSummary(connection.State, checks);
        return new IbsngDiagnosticReport(DateTimeOffset.UtcNow, request.ServerHost,
            request.Protocol, connection.State, summary, false, dialerPath, dialerTrusted,
            uiSnapshot.ProcessId is not null, uiSnapshot, connection.RasConnections,
            adapters, checks);
    }

    private async Task<IbsngDialerUiSnapshot> ReadDialerUiAsync(string requestId,
        IbsngDiagnosticRequest request, InteractiveUserSession? session, string? dialerPath,
        CancellationToken cancellationToken)
    {
        if (!request.IncludeDialerLog)
            return new IbsngDialerUiSnapshot(IbsngUiReadState.LogControlNotAccessible,
                null, false, IbsngConnectionState.Unknown, Array.Empty<string>(),
                "Dialer log reading was not requested.");
        if (session is null || dialerPath is null)
            return new IbsngDialerUiSnapshot(IbsngUiReadState.WindowNotFound, null, false,
                IbsngConnectionState.Unknown, Array.Empty<string>(),
                "No interactive user session is available.");

        var trustedHash = IbsngArtifactVerifier.NormalizeSha256(_options.TrustedSha256);
        if (trustedHash is null)
            return new IbsngDialerUiSnapshot(IbsngUiReadState.Error, null, false,
                IbsngConnectionState.Unknown, Array.Empty<string>(),
                "The approved dialer SHA-256 is not configured.");
        var trayRequest = new TrayIpcRequest(requestId, TrayIpcProtocol.ReadIbsngUiMethod,
            dialerPath, trustedHash, request.ServerHost, _options.DialerSearchPattern);
        var response = await _trayClient.SendAsync(session.SessionId, trayRequest,
            cancellationToken);
        return response.DialerUi ?? new IbsngDialerUiSnapshot(IbsngUiReadState.Error,
            response.ProcessId, false, IbsngConnectionState.Unknown, Array.Empty<string>(),
            response.Message ?? response.State);
    }

    private static IReadOnlyList<IbsngNetworkAdapterSnapshot> GetNetworkAdapters()
    {
        var results = new List<IbsngNetworkAdapterSnapshot>();
        foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up
                && item.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            try
            {
                var properties = adapter.GetIPProperties();
                var addresses = properties.UnicastAddresses.Select(item => item.Address.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var gateways = properties.GatewayAddresses.Select(item => item.Address.ToString())
                    .Where(value => value is not "0.0.0.0" and not "::")
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                var dns = properties.DnsAddresses.Select(item => item.ToString())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                bool? dhcpEnabled = null;
                try { dhcpEnabled = properties.GetIPv4Properties()?.IsDhcpEnabled; }
                catch (NetworkInformationException) { }

                results.Add(new IbsngNetworkAdapterSnapshot(adapter.Name, adapter.Description,
                    adapter.NetworkInterfaceType.ToString(), dhcpEnabled, GetDhcpServer(adapter.Id),
                    addresses.Any(IsApipa), addresses, gateways, dns));
            }
            catch (NetworkInformationException)
            {
                // An adapter may disappear while Windows is returning its properties.
            }
        }
        return results;
    }

    private static void AddAdapterChecks(List<IbsngDiagnosticCheck> checks,
        IReadOnlyList<IbsngNetworkAdapterSnapshot> adapters)
    {
        if (adapters.Count == 0)
        {
            checks.Add(Fail("network.adapters", "No operational network adapter was found."));
            return;
        }
        checks.Add(new IbsngDiagnosticCheck("network.adapters",
            adapters.Any(adapter => adapter.HasApipaAddress) ? "Warning" : "Pass",
            $"{adapters.Count} operational network adapter(s) were found."));
        var dhcpAdapters = adapters.Where(adapter => adapter.DhcpEnabled == true).ToArray();
        checks.Add(new IbsngDiagnosticCheck("network.dhcp",
            dhcpAdapters.Length == 0 ? "NotApplicable"
                : dhcpAdapters.Any(adapter => !string.IsNullOrWhiteSpace(adapter.DhcpServer))
                    ? "Pass" : "Warning",
            dhcpAdapters.Length == 0
                ? "Active adapters use static addressing or do not expose DHCP state."
                : $"DHCP is enabled on {dhcpAdapters.Length} active adapter(s)."));
        checks.Add(new IbsngDiagnosticCheck("network.dns.servers",
            adapters.Any(adapter => adapter.DnsServers.Count > 0) ? "Pass" : "Fail",
            adapters.Any(adapter => adapter.DnsServers.Count > 0)
                ? "At least one DNS server is configured." : "No DNS server is configured."));
    }

    private static async Task<IPAddress[]> ResolveServerAsync(string host,
        List<IbsngDiagnosticCheck> checks, CancellationToken cancellationToken)
    {
        try
        {
            var addresses = IPAddress.TryParse(host, out var address)
                ? [address]
                : await Dns.GetHostAddressesAsync(host, cancellationToken);
            checks.Add(new IbsngDiagnosticCheck("ibsng.server.resolve", "Pass",
                $"Resolved {host} to {addresses.Length} address(es).",
                string.Join(", ", addresses.Select(item => item.ToString()))));
            return addresses;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            checks.Add(Fail("ibsng.server.resolve", $"The IBSng server {host} could not be resolved.",
                ex.GetType().Name));
            return Array.Empty<IPAddress>();
        }
    }

    private async Task AddGatewayCheckAsync(List<IbsngDiagnosticCheck> checks,
        IReadOnlyList<IbsngNetworkAdapterSnapshot> adapters, CancellationToken cancellationToken)
    {
        var gateways = adapters.SelectMany(adapter => adapter.Gateways)
            .Distinct(StringComparer.OrdinalIgnoreCase).Take(_options.MaxProbeTargets).ToArray();
        if (gateways.Length == 0)
        {
            checks.Add(Fail("network.default_gateway", "No default gateway was found."));
            return;
        }
        var reachable = 0;
        foreach (var gateway in gateways)
        {
            try
            {
                using var ping = new Ping();
                var reply = await ping.SendPingAsync(gateway,
                    _options.ProbeTimeoutSeconds * 1000).WaitAsync(cancellationToken);
                if (reply.Status == IPStatus.Success) reachable++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "Default gateway {Gateway} did not answer ICMP.", gateway);
            }
        }
        checks.Add(new IbsngDiagnosticCheck("network.default_gateway",
            reachable > 0 ? "Pass" : "Warning",
            reachable > 0 ? $"{reachable} default gateway(s) answered."
                : "A default gateway exists but did not answer ICMP; ICMP may be blocked.",
            string.Join(", ", gateways)));
    }

    private static void AddRouteCheck(List<IbsngDiagnosticCheck> checks,
        IReadOnlyList<IbsngNetworkAdapterSnapshot> adapters, IReadOnlyList<IPAddress> addresses,
        IbsngVpnProtocol protocol)
    {
        if (addresses.Count == 0)
        {
            checks.Add(Fail("ibsng.server.route", "No route can be selected until the server resolves."));
            return;
        }
        try
        {
            using var socket = new Socket(addresses[0].AddressFamily, SocketType.Dgram,
                ProtocolType.Udp);
            socket.Connect(new IPEndPoint(addresses[0], ProtocolPort(protocol) ?? 443));
            var source = (socket.LocalEndPoint as IPEndPoint)?.Address.ToString();
            var adapter = adapters.FirstOrDefault(item => source is not null
                && item.Addresses.Contains(source, StringComparer.OrdinalIgnoreCase));
            checks.Add(new IbsngDiagnosticCheck("ibsng.server.route", "Pass",
                adapter is null ? "Windows selected a source address for the IBSng route."
                    : $"Windows routes IBSng traffic through {adapter.Name}.", source));
        }
        catch (SocketException ex)
        {
            checks.Add(Fail("ibsng.server.route", "Windows could not select a route to the IBSng server.",
                ex.SocketErrorCode.ToString()));
        }
    }

    private async Task AddProtocolCheckAsync(List<IbsngDiagnosticCheck> checks,
        IReadOnlyList<IPAddress> addresses, IbsngVpnProtocol protocol,
        CancellationToken cancellationToken)
    {
        var port = ProtocolPort(protocol);
        if (port is null)
        {
            checks.Add(new IbsngDiagnosticCheck("ibsng.server.protocol", "NotApplicable",
                protocol is IbsngVpnProtocol.L2tp or IbsngVpnProtocol.Ikev2
                    ? "L2TP/IKEv2 UDP reachability is determined from Windows RAS status; a UDP connect does not prove that a server port is open."
                    : "Select PPTP or SSTP for a reliable TCP endpoint probe."));
            return;
        }
        if (addresses.Count == 0)
        {
            checks.Add(Fail("ibsng.server.protocol", "The protocol endpoint could not be tested because server resolution failed."));
            return;
        }
        foreach (var address in addresses.Take(_options.MaxProbeTargets))
        {
            try
            {
                using var client = new TcpClient(address.AddressFamily);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds));
                await client.ConnectAsync(address, port.Value, timeout.Token);
                checks.Add(new IbsngDiagnosticCheck("ibsng.server.protocol", "Pass",
                    $"The {protocol.ToString().ToUpperInvariant()} TCP endpoint answered on port {port}.",
                    address.ToString()));
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException
                || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogDebug(ex, "IBSng protocol probe failed for {Address}:{Port}.", address, port);
            }
        }
        checks.Add(Warn("ibsng.server.protocol",
            $"The {protocol.ToString().ToUpperInvariant()} TCP endpoint did not answer on port {port}."));
    }

    private async Task AddPortalCheckAsync(List<IbsngDiagnosticCheck> checks,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.PortalUrl))
        {
            checks.Add(new IbsngDiagnosticCheck("ibsng.portal", "NotApplicable",
                "No IBSng portal URL is configured."));
            return;
        }
        try
        {
            var uri = Uri.TryCreate(_options.PortalUrl, UriKind.Absolute, out var absolute)
                ? absolute : new Uri($"http://{_options.PortalUrl}");
            using var handler = new HttpClientHandler { AllowAutoRedirect = false };
            using var client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(_options.ProbeTimeoutSeconds)
            };
            using var response = await client.GetAsync(uri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            checks.Add(new IbsngDiagnosticCheck("ibsng.portal",
                (int)response.StatusCode is >= 200 and < 400 ? "Pass" : "Warning",
                $"The configured portal returned HTTP {(int)response.StatusCode}.", uri.ToString()));
        }
        catch (Exception ex) when (ex is not OperationCanceledException
            || !cancellationToken.IsCancellationRequested)
        {
            checks.Add(Warn("ibsng.portal", "The configured IBSng portal did not answer.",
                ex.GetType().Name));
        }
    }

    private static string? GetDhcpServer(string adapterId)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(
                $@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{adapterId}");
            return key?.GetValue("DhcpServer") as string is { Length: > 0 } value
                && value != "255.255.255.255" ? value : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsApipa(string value) =>
        IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, _, _];

    private static int? ProtocolPort(IbsngVpnProtocol protocol) => protocol switch
    {
        IbsngVpnProtocol.Pptp => 1723,
        IbsngVpnProtocol.Sstp => 443,
        _ => null
    };

    private static string UiSummary(IbsngDialerUiSnapshot snapshot) => snapshot.ReadState switch
    {
        IbsngUiReadState.Success => snapshot.LogLines.Count == 0
            ? "The Connection Log is accessible and currently empty."
            : $"Read {snapshot.LogLines.Count} sanitized line(s) from the Connection Log.",
        IbsngUiReadState.ProcessNotRunning => "The approved IBSng dialer is not running.",
        IbsngUiReadState.WindowNotFound => "The IBSng window is not currently visible.",
        IbsngUiReadState.LogControlNotAccessible => "The dialer log is not exposed through Windows accessibility APIs.",
        _ => "The dialer UI could not be inspected."
    };

    private static string BuildSummary(IbsngConnectionState state,
        IReadOnlyCollection<IbsngDiagnosticCheck> checks)
    {
        var failures = checks.Count(check => check.Status == "Fail");
        var warnings = checks.Count(check => check.Status == "Warning");
        return $"IBSng is {state}. {failures} failed check(s), {warnings} warning(s).";
    }

    private static IbsngDiagnosticCheck Fail(string name, string summary, string? detail = null) =>
        new(name, "Fail", summary, detail);

    private static IbsngDiagnosticCheck Warn(string name, string summary, string? detail = null) =>
        new(name, "Warning", summary, detail);
}
