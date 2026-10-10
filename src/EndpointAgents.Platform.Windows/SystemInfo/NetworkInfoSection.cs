using EndpointAgents.Platform.Windows.EndpointInfo;
using EndpointAgents.Shared.Support;
using Microsoft.Win32;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>
/// Adapter-level IP configuration (address, subnet mask, gateway, DNS, DHCP server and
/// lease, WINS, MTU), every non-loopback adapter including unplugged ones, the DNS
/// suffix search list and Windows Firewall profile state.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class NetworkInfoSection : ISystemInfoSection
{
    private const string TcpipParameters =
        @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters";

    public string Name => SystemInfoContracts.Network;

    public Task<object?> CollectAsync(SystemInfoWarnings warnings,
        CancellationToken cancellationToken)
    {
        var adapters = NetworkInterface.GetAllNetworkInterfaces()
            .Where(IsReportable)
            .Select(adapter => CollectAdapter(adapter, warnings))
            .OrderByDescending(adapter => adapter.Connected)
            .ThenByDescending(adapter => adapter.DefaultGateways.Length > 0)
            .ToArray();
        var connected = adapters.Where(adapter => adapter.Connected).ToArray();
        var primary = connected.FirstOrDefault(adapter =>
            adapter.DefaultGateways.Length > 0 && !adapter.IsLikelyVirtual);

        object? result = new
        {
            Summary = new
            {
                NetworkAvailable = NetworkInterface.GetIsNetworkAvailable(),
                PrimaryAdapter = primary?.Name,
                ConnectedAdapterCount = connected.Length,
                EthernetConnected = connected.Any(adapter => adapter.Type.StartsWith(
                    "Ethernet", StringComparison.Ordinal) && !adapter.IsLikelyVirtual),
                WifiConnected = connected.Any(adapter =>
                    adapter.Type == nameof(NetworkInterfaceType.Wireless80211)),
                VpnConnected = connected.Any(adapter => adapter.IsLikelyVpn),
                HasDefaultGateway = connected.Any(adapter => adapter.DefaultGateways.Length > 0),
                HasDnsServers = connected.Any(adapter => adapter.DnsServers.Length > 0),
                HasApipaAddress = connected.Any(adapter =>
                    adapter.IPv4.Any(address => address.IsApipa))
            },
            PrimaryDnsSuffix = warnings.Try("network.dns_suffix",
                () => IPGlobalProperties.GetIPGlobalProperties().DomainName),
            DnsSuffixSearchList = warnings.Try("network.dns_search_list", ReadSearchList),
            Firewall = warnings.Try("network.firewall", ReadFirewallProfiles),
            Adapters = adapters
        };
        return Task.FromResult(result);
    }

    /// <summary>
    /// Connected adapters that have an IP address, plus physical Ethernet/Wi-Fi adapters
    /// that are down (an unplugged cable or switched-off Wi-Fi is often the problem).
    /// Filter-driver bindings, WAN miniports and absent adapters carry no IP configuration
    /// and are noise.
    /// </summary>
    private static bool IsReportable(NetworkInterface adapter)
    {
        if (adapter.NetworkInterfaceType is NetworkInterfaceType.Loopback
            or NetworkInterfaceType.Tunnel)
            return false;
        try
        {
            var properties = adapter.GetIPProperties();
            if (adapter.OperationalStatus == OperationalStatus.Up)
                return properties.UnicastAddresses.Count > 0;
            return adapter.OperationalStatus == OperationalStatus.Down
                && adapter.NetworkInterfaceType is NetworkInterfaceType.Ethernet
                    or NetworkInterfaceType.GigabitEthernet
                    or NetworkInterfaceType.FastEthernetT
                    or NetworkInterfaceType.Wireless80211
                && !EndpointInfoCommandHandler.IsLikelyVirtual(adapter)
                && !EndpointInfoCommandHandler.IsLikelyVpn(adapter)
                && properties.GetIPv4Properties() is not null;
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static AdapterInfo CollectAdapter(NetworkInterface adapter,
        SystemInfoWarnings warnings)
    {
        var source = $"network.adapter.{adapter.Name}";
        var properties = warnings.Try(source, adapter.GetIPProperties);
        var ipv4 = warnings.Try(source + ".ipv4", () => properties?.GetIPv4Properties());
        var unicast = properties?.UnicastAddresses.ToArray() ?? [];
        var gateways = properties?.GatewayAddresses
            .Select(gateway => gateway.Address)
            .Where(address => !address.Equals(IPAddress.Any)
                && !address.Equals(IPAddress.IPv6Any))
            .ToArray() ?? [];
        var v4 = unicast
            .Where(address => address.Address.AddressFamily == AddressFamily.InterNetwork)
            .Select(address => new IPv4Info(
                address.Address.ToString(),
                NetworkAddressFacts.SubnetMask(address.PrefixLength),
                address.PrefixLength,
                NetworkAddressFacts.NetworkAddress(address.Address, address.PrefixLength),
                NetworkAddressFacts.IsApipa(address.Address),
                address.PrefixOrigin.ToString()))
            .ToArray();
        var lease = ipv4?.IsDhcpEnabled == true
            ? warnings.Try(source + ".lease", () => ReadDhcpLease(adapter.Id))
            : default;
        var connected = adapter.OperationalStatus == OperationalStatus.Up;

        return new AdapterInfo(
            adapter.Name,
            adapter.Description,
            adapter.NetworkInterfaceType.ToString(),
            adapter.OperationalStatus.ToString(),
            connected,
            connected && adapter.Speed > 0 ? adapter.Speed / 1_000_000 : null,
            EndpointInfoCommandHandler.IsLikelyVirtual(adapter),
            EndpointInfoCommandHandler.IsLikelyVpn(adapter),
            ipv4?.Mtu,
            ipv4?.IsDhcpEnabled,
            properties?.DhcpServerAddresses.Select(address => address.ToString()).ToArray()
                ?? [],
            lease.Obtained,
            lease.Expires,
            v4,
            unicast
                .Where(address => address.Address.AddressFamily == AddressFamily.InterNetworkV6)
                .Select(address => address.Address.ToString())
                .ToArray(),
            gateways.Select(gateway => gateway.ToString()).ToArray(),
            gateways.Any(gateway => gateway.AddressFamily == AddressFamily.InterNetwork)
                ? gateways
                    .Where(gateway => gateway.AddressFamily == AddressFamily.InterNetwork)
                    .All(gateway => unicast.Any(address =>
                        NetworkAddressFacts.InSameSubnet(address.Address, gateway,
                            address.PrefixLength)))
                : null,
            properties?.DnsAddresses.Select(address => address.ToString()).ToArray() ?? [],
            properties?.DnsSuffix,
            properties?.IsDynamicDnsEnabled,
            properties?.WinsServersAddresses.Select(address => address.ToString()).ToArray()
                ?? []);
    }

    private static string[] ReadSearchList()
    {
        using var key = Registry.LocalMachine.OpenSubKey(TcpipParameters);
        return (key?.GetValue("SearchList") as string ?? string.Empty)
            .Split([',', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static (DateTimeOffset? Obtained, DateTimeOffset? Expires) ReadDhcpLease(
        string adapterId)
    {
        using var key = Registry.LocalMachine.OpenSubKey(
            $@"{TcpipParameters}\Interfaces\{adapterId}");
        return (FromUnixSeconds(key?.GetValue("LeaseObtainedTime")),
            FromUnixSeconds(key?.GetValue("LeaseTerminatesTime")));
    }

    internal static DateTimeOffset? FromUnixSeconds(object? value) =>
        value is int seconds && seconds > 0
            ? DateTimeOffset.FromUnixTimeSeconds((uint)seconds)
            : value is long longSeconds && longSeconds > 0
                ? DateTimeOffset.FromUnixTimeSeconds(longSeconds)
                : null;

    private static object ReadFirewallProfiles()
    {
        const string policy = @"SOFTWARE\Policies\Microsoft\WindowsFirewall";
        const string local =
            @"SYSTEM\CurrentControlSet\Services\SharedAccess\Parameters\FirewallPolicy";
        bool? Enabled(string policyName, string localName)
        {
            using var policyKey = Registry.LocalMachine.OpenSubKey($@"{policy}\{policyName}");
            if (policyKey?.GetValue("EnableFirewall") is int enforced) return enforced != 0;
            using var localKey = Registry.LocalMachine.OpenSubKey($@"{local}\{localName}");
            return localKey?.GetValue("EnableFirewall") is int value ? value != 0 : null;
        }
        return new
        {
            DomainEnabled = Enabled("DomainProfile", "DomainProfile"),
            PrivateEnabled = Enabled("PrivateProfile", "StandardProfile"),
            PublicEnabled = Enabled("PublicProfile", "PublicProfile")
        };
    }

    private sealed record IPv4Info(string Address, string SubnetMask, int PrefixLength,
        string Network, bool IsApipa, string Origin);

    private sealed record AdapterInfo(
        string Name,
        string Description,
        string Type,
        string Status,
        bool Connected,
        long? SpeedMbps,
        bool IsLikelyVirtual,
        bool IsLikelyVpn,
        int? Mtu,
        bool? DhcpEnabled,
        string[] DhcpServers,
        DateTimeOffset? DhcpLeaseObtained,
        DateTimeOffset? DhcpLeaseExpires,
        IPv4Info[] IPv4,
        string[] IPv6,
        string[] DefaultGateways,
        bool? GatewayInSubnet,
        string[] DnsServers,
        string? DnsSuffix,
        bool? DynamicDnsRegistration,
        string[] WinsServers);
}
