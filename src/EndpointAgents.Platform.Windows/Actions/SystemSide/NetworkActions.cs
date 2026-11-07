using EndpointAgents.Platform.Windows.Actions.Infrastructure;
using EndpointAgents.Shared.Actions;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions.SystemSide;

/// <summary>Facts about the connected adapters shared by the network actions.</summary>
[SupportedOSPlatform("windows")]
internal static class AdapterFacts
{
    public sealed record Adapter(string Name, string Type, bool Connected, string AddressKind,
        string? Gateway, int DnsServerCount, bool IsVirtual);

    public static IReadOnlyList<Adapter> Read() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(item => item.NetworkInterfaceType is not (NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel))
        .Select(item =>
        {
            var properties = item.GetIPProperties();
            var ipv4 = properties.UnicastAddresses
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(address => address.Address)
                .ToArray();
            var dhcp = SafeDhcp(item);
            var kind = ipv4.Length == 0 ? "None"
                : ipv4.Any(IsApipa) ? "Apipa"
                : dhcp ? "Dhcp" : "Static";
            var gateway = properties.GatewayAddresses
                .Select(address => address.Address)
                .FirstOrDefault(address => address.AddressFamily == AddressFamily.InterNetwork
                    && !address.Equals(IPAddress.Any));
            var description = item.Description ?? string.Empty;
            var isVirtual = description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                || description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase)
                || description.Contains("VPN", StringComparison.OrdinalIgnoreCase)
                || description.Contains("TAP", StringComparison.OrdinalIgnoreCase);
            return new Adapter(item.Name, item.NetworkInterfaceType.ToString(),
                item.OperationalStatus == OperationalStatus.Up, kind, gateway?.ToString(),
                properties.DnsAddresses.Count(address =>
                    address.AddressFamily == AddressFamily.InterNetwork), isVirtual);
        })
        .ToArray();

    private static bool SafeDhcp(NetworkInterface item)
    {
        try { return item.GetIPProperties().GetIPv4Properties()?.IsDhcpEnabled == true; }
        catch (NetworkInformationException) { return false; }
    }

    public static bool IsApipa(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes.Length == 4 && bytes[0] == 169 && bytes[1] == 254;
    }
}

[SupportedOSPlatform("windows")]
public sealed class NetworkStatusAction : IEndpointAction
{
    public string Id => "network.status";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var adapters = AdapterFacts.Read();
        var connected = adapters.Where(item => item.Connected).ToArray();
        var gateways = new List<object>();
        var gatewayReachable = false;
        foreach (var adapter in connected.Where(item => item.Gateway is not null))
        {
            var ping = await NetworkProbes.PingAsync(adapter.Gateway!, 2, cancellationToken);
            gatewayReachable |= ping.Received > 0;
            gateways.Add(new { Adapter = adapter.Name, Reachable = ping.Received > 0 });
        }
        var working = connected.Any(item => item.AddressKind is "Dhcp" or "Static"
            && item.Gateway is not null);
        return EndpointActionResult.Ok(
            !working ? "No adapter has a usable address and gateway."
            : gatewayReachable ? "The network connection works up to the gateway."
            : "The gateway does not answer.",
            new
            {
                Summary = new
                {
                    HasWorkingAdapter = working,
                    HasApipaAddress = connected.Any(item => item.AddressKind == "Apipa"),
                    GatewayReachable = gatewayReachable,
                    EthernetConnected = connected.Any(item => item.Type.StartsWith("Ethernet",
                        StringComparison.Ordinal) && !item.IsVirtual),
                    WifiConnected = connected.Any(item => item.Type == "Wireless80211")
                },
                Adapters = adapters.Select(item => new
                {
                    item.Name, item.Type, item.Connected, item.AddressKind,
                    HasGateway = item.Gateway is not null, item.DnsServerCount, item.IsVirtual
                }),
                Gateways = gateways
            });
    }
}

/// <summary>Walks adapter, address, gateway, DNS, port and HTTP to one target.</summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkTriageAction : IEndpointAction
{
    public string Id => "network.triage";

    private sealed record Layer(string Name, bool Passed, string Detail);

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var host = EndpointActionValidator.HostOf(target)!;
        var layers = new List<Layer>();

        var connected = AdapterFacts.Read().Where(item => item.Connected).ToArray();
        if (!Add(layers, "Adapter", connected.Length > 0,
                connected.Length > 0 ? $"{connected.Length} connected" : "No connected adapter"))
            return Finish(target, layers);

        var usable = connected.Where(item => item.AddressKind is "Dhcp" or "Static").ToArray();
        if (!Add(layers, "Address", usable.Length > 0, usable.Length > 0 ? "Valid address"
                : connected.Any(item => item.AddressKind == "Apipa")
                    ? "Automatic private address (DHCP did not answer)" : "No IPv4 address"))
            return Finish(target, layers);

        var gateway = usable.FirstOrDefault(item => item.Gateway is not null)?.Gateway;
        var gatewayPing = gateway is null ? default
            : await NetworkProbes.PingAsync(gateway, 2, cancellationToken);
        // Some gateways drop ping; a failed ping is reported but does not stop the walk.
        layers.Add(new Layer("Gateway", gateway is not null && gatewayPing.Received > 0,
            gateway is null ? "No default gateway" : gatewayPing.Received > 0
                ? "Gateway answers" : "Gateway does not answer ping"));
        if (gateway is null) return Finish(target, layers);

        var resolved = await NetworkProbes.ResolveAsync(host, cancellationToken);
        var expected = target.ExpectedAddresses ?? [];
        var matches = expected.Count == 0 || resolved.Addresses.Any(expected.Contains);
        if (!Add(layers, "Dns", resolved.Addresses.Count > 0 && matches,
                resolved.Addresses.Count == 0 ? $"Name not resolved ({resolved.Error})"
                : matches ? "Name resolves" : "Name resolves to an unexpected address"))
            return Finish(target, layers);

        var address = resolved.Addresses[0];
        var ports = PortsFor(target);
        if (ports.Count > 0)
        {
            var anyOpen = false;
            var details = new List<string>();
            foreach (var port in ports.Take(4))
            {
                var probe = await NetworkProbes.TestTcpAsync(address, port,
                    TimeSpan.FromSeconds(4), cancellationToken);
                anyOpen |= probe.Reachable;
                details.Add($"{port}:{(probe.Reachable ? "open" : probe.Error)}");
            }
            if (!Add(layers, "Port", anyOpen, string.Join(", ", details)))
                return Finish(target, layers);
        }

        if (target.Kind == EnvironmentTargetKinds.WebApp)
        {
            var http = await WebProbe.ProbeAsync(target.Address, useDefaultCredentials: false,
                cancellationToken);
            Add(layers, "Http", http.Reachable,
                http.Reachable ? $"HTTP {http.StatusCode}{(http.CertificateValid == false ? ", certificate problem" : "")}"
                    : http.Error ?? "No answer");
        }
        return Finish(target, layers);
    }

    private static IReadOnlyList<int> PortsFor(ResolvedTarget target)
    {
        if (target.Ports is { Count: > 0 } ports) return ports;
        return target.Kind switch
        {
            EnvironmentTargetKinds.WebApp => [Uri.TryCreate(target.Address, UriKind.Absolute,
                out var uri) ? uri.Port : 443],
            EnvironmentTargetKinds.Share or EnvironmentTargetKinds.Printer => [445],
            _ => []
        };
    }

    private static bool Add(List<Layer> layers, string name, bool passed, string detail)
    {
        layers.Add(new Layer(name, passed, detail));
        return passed;
    }

    private static EndpointActionResult Finish(ResolvedTarget target, List<Layer> layers)
    {
        var failed = layers.FirstOrDefault(layer => !layer.Passed && layer.Name != "Gateway")
            ?? layers.FirstOrDefault(layer => !layer.Passed);
        return EndpointActionResult.Ok(failed is null
                ? $"Everything works up to {target.Label}."
                : $"The first broken layer to {target.Label} is {failed.Name}: {failed.Detail}.",
            new { Target = target.Id, FirstFailedLayer = failed?.Name, Layers = layers });
    }
}

[SupportedOSPlatform("windows")]
public sealed class DnsResolveAction : IEndpointAction
{
    public string Id => "dns.resolve";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var host = EndpointActionValidator.HostOf(target)!;
        var windows = await NetworkProbes.ResolveAsync(host, cancellationToken);
        var perServer = new List<DnsAnswer>();
        if (!IPAddress.TryParse(host, out _))
            foreach (var server in NetworkProbes.ConfiguredDnsServers().Take(4))
                perServer.Add(await NetworkProbes.QueryServerAsync(server, host,
                    TimeSpan.FromSeconds(3), cancellationToken));
        var expected = target.ExpectedAddresses ?? [];
        bool? matches = expected.Count == 0 ? null
            : windows.Addresses.Any(expected.Contains);
        var answered = perServer.Where(item => item.Answered && item.Addresses.Count > 0)
            .Select(item => string.Join(",", item.Addresses.Order()))
            .Distinct().Count();
        return EndpointActionResult.Ok(
            windows.Addresses.Count == 0 ? $"{target.Label} does not resolve ({windows.Error})."
            : matches == false ? $"{target.Label} resolves to an unexpected address."
            : $"{target.Label} resolves.",
            new
            {
                Target = target.Id,
                WindowsAnswer = new { windows.Addresses, windows.Error },
                PerServer = perServer,
                MatchesExpected = matches,
                Disagreement = answered > 1
                    || perServer.Any(item => item.Error == "NameNotFound")
                        && perServer.Any(item => item.Addresses.Count > 0)
            });
    }
}

[SupportedOSPlatform("windows")]
public sealed class TestPortAction : IEndpointAction
{
    public string Id => "net.test_port";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var port = invocation.IntArgument("port", 0);
        var allowed = target.Ports ?? [];
        var webPort = target.Kind == EnvironmentTargetKinds.WebApp
            && Uri.TryCreate(target.Address, UriKind.Absolute, out var uri) ? uri.Port : -1;
        if (!allowed.Contains(port) && port != webPort)
            return EndpointActionResult.Blocked("That port is not listed for this target.");
        var probe = await NetworkProbes.TestTcpAsync(EndpointActionValidator.HostOf(target)!,
            port, TimeSpan.FromSeconds(5), cancellationToken);
        return EndpointActionResult.Ok(probe.Reachable
            ? $"Port {port} on {target.Label} is reachable."
            : $"Port {port} on {target.Label} is not reachable ({probe.Error}).", probe);
    }
}

[SupportedOSPlatform("windows")]
public sealed class PingAction : IEndpointAction
{
    public string Id => "net.ping";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var target = invocation.Target();
        var result = await NetworkProbes.PingAsync(EndpointActionValidator.HostOf(target)!, 4,
            cancellationToken);
        return EndpointActionResult.Ok($"{result.Received} of {result.Sent} pings to {target.Label} answered.",
            new { result.Sent, result.Received, result.AverageMs });
    }
}

[SupportedOSPlatform("windows")]
public sealed class HostsCheckAction : IEndpointAction
{
    public string Id => "hosts.check";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var path = Path.Combine(Environment.SystemDirectory, "drivers", "etc", "hosts");
        if (!File.Exists(path))
            return EndpointActionResult.Ok("There is no hosts file.", new { EntryCount = 0 });
        var names = (await File.ReadAllLinesAsync(path, cancellationToken))
            .Select(line => line.Split('#', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .Select(line => line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length >= 2)
            .SelectMany(parts => parts.Skip(1))
            .Where(name => !name.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        return EndpointActionResult.Ok(names.Length == 0
                ? "The hosts file has only the Windows defaults."
                : $"The hosts file has {names.Length} custom entries.",
            new { EntryCount = names.Length, Names = names.Take(20) });
    }
}

[SupportedOSPlatform("windows")]
public sealed class DnsFlushAction : IEndpointAction
{
    public string Id => "dns.flush";

    public Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken) =>
        Task.FromResult(ActionNative.DnsFlushResolverCache()
            ? EndpointActionResult.Done("The DNS cache was cleared.")
            : EndpointActionResult.Failed("Windows did not clear the DNS cache."));
}

[SupportedOSPlatform("windows")]
public sealed class DhcpRenewAction : IEndpointAction
{
    public string Id => "dhcp.renew";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        if (!AdapterFacts.Read().Any(item => item.Connected && item.AddressKind is "Dhcp" or "Apipa"))
            return EndpointActionResult.NotNeeded("No connected adapter uses DHCP.");
        var result = await FixedTool.RunAsync("ipconfig.exe", ["/renew"],
            TimeSpan.FromSeconds(50), cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("The IP address was renewed.")
            : EndpointActionResult.Failed(result.TimedOut
                ? "The DHCP server did not answer in time." : "Renewing the address failed.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class RegisterDnsAction : IEndpointAction
{
    public string Id => "net.register_dns";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("ipconfig.exe", ["/registerdns"],
            TimeSpan.FromSeconds(25), cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("DNS registration was started.")
            : EndpointActionResult.Failed("DNS registration could not be started.");
    }
}

[SupportedOSPlatform("windows")]
public sealed class ArpClearAction : IEndpointAction
{
    public string Id => "arp.clear";

    public async Task<EndpointActionResult> RunAsync(ActionInvocation invocation,
        CancellationToken cancellationToken)
    {
        var result = await FixedTool.RunAsync("netsh.exe",
            ["interface", "ip", "delete", "arpcache"], TimeSpan.FromSeconds(20),
            cancellationToken);
        return result.Succeeded
            ? EndpointActionResult.Done("The ARP cache was cleared.")
            : EndpointActionResult.Failed("Clearing the ARP cache failed.");
    }
}
