using System.Net;
using System.Net.Sockets;

namespace EndpointAgents.Platform.Windows.SystemInfo;

/// <summary>Pure IPv4 helpers so the agent gets ready-made facts instead of doing subnet math.</summary>
internal static class NetworkAddressFacts
{
    public static string SubnetMask(int prefixLength) => FromUInt32(Mask(prefixLength)).ToString();

    public static string NetworkAddress(IPAddress address, int prefixLength) =>
        FromUInt32(ToUInt32(address) & Mask(prefixLength)).ToString();

    private static uint Mask(int prefixLength) =>
        prefixLength <= 0 ? 0u : uint.MaxValue << (32 - Math.Min(prefixLength, 32));

    public static bool InSameSubnet(IPAddress address, IPAddress other, int prefixLength) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && other.AddressFamily == AddressFamily.InterNetwork
        && NetworkAddress(address, prefixLength) == NetworkAddress(other, prefixLength);

    /// <summary>169.254.0.0/16: Windows picked its own address because DHCP did not answer.</summary>
    public static bool IsApipa(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && address.GetAddressBytes() is [169, 254, ..];

    /// <summary>Private, CGNAT, link-local or loopback IPv4 (not routable on the internet).</summary>
    public static bool IsPrivate(IPAddress address)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork) return false;
        var b = address.GetAddressBytes();
        return b[0] == 10 || b[0] == 127
            || b[0] == 172 && b[1] is >= 16 and <= 31
            || b[0] == 192 && b[1] == 168
            || b[0] == 169 && b[1] == 254
            || b[0] == 100 && b[1] is >= 64 and <= 127;
    }

    private static uint ToUInt32(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    private static IPAddress FromUInt32(uint value) =>
        new([(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value]);
}
