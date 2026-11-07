using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace EndpointAgents.Platform.Windows.Actions.Infrastructure;

public sealed record PortProbe(bool Reachable, long ElapsedMs, string? Error);

public sealed record DnsAnswer(string Server, bool Answered, IReadOnlyList<string> Addresses,
    string? Error);

/// <summary>Plain network probes used by the network actions. All targets come from the
/// control plane's environment map or the PC's own configuration.</summary>
public static class NetworkProbes
{
    public static async Task<PortProbe> TestTcpAsync(string host, int port, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var watch = Stopwatch.StartNew();
        using var client = new TcpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, limit.Token);
            return new PortProbe(true, watch.ElapsedMilliseconds, null);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new PortProbe(false, watch.ElapsedMilliseconds, "Timeout");
        }
        catch (SocketException ex)
        {
            return new PortProbe(false, watch.ElapsedMilliseconds, ex.SocketErrorCode.ToString());
        }
    }

    public static async Task<(int Sent, int Received, long? AverageMs)> PingAsync(string host,
        int count, CancellationToken cancellationToken)
    {
        using var ping = new Ping();
        var times = new List<long>();
        for (var index = 0; index < count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var reply = await ping.SendPingAsync(host, TimeSpan.FromSeconds(2), null, null,
                    cancellationToken);
                if (reply.Status == IPStatus.Success) times.Add(reply.RoundtripTime);
            }
            catch (PingException) { }
        }
        return (count, times.Count, times.Count == 0 ? null : (long)times.Average());
    }

    /// <summary>Resolves with the Windows resolver (cache, hosts file, suffix search).</summary>
    public static async Task<(IReadOnlyList<string> Addresses, string? Error)> ResolveAsync(
        string host, CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(host, out var literal)) return ([literal.ToString()], null);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(host, AddressFamily.InterNetwork,
                cancellationToken);
            return (addresses.Select(item => item.ToString()).Distinct().ToArray(), null);
        }
        catch (SocketException ex)
        {
            return ([], ex.SocketErrorCode == SocketError.HostNotFound ? "NameNotFound"
                : ex.SocketErrorCode.ToString());
        }
    }

    /// <summary>The IPv4 DNS servers configured on connected adapters.</summary>
    public static IReadOnlyList<IPAddress> ConfiguredDnsServers() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(item => item.OperationalStatus == OperationalStatus.Up
                && item.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(item => item.GetIPProperties().DnsAddresses)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork)
            .Distinct()
            .ToArray();

    /// <summary>Asks one DNS server directly for the A records of a name (no cache, no hosts file).</summary>
    public static async Task<DnsAnswer> QueryServerAsync(IPAddress server, string name,
        TimeSpan timeout, CancellationToken cancellationToken)
    {
        var id = (ushort)Random.Shared.Next(1, ushort.MaxValue);
        var query = BuildQuery(id, name);
        using var udp = new UdpClient(server.AddressFamily);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            await udp.SendAsync(query, new IPEndPoint(server, 53), limit.Token);
            var reply = await udp.ReceiveAsync(limit.Token);
            return ParseAnswer(server.ToString(), id, reply.Buffer);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new DnsAnswer(server.ToString(), false, [], "Timeout");
        }
        catch (SocketException ex)
        {
            return new DnsAnswer(server.ToString(), false, [], ex.SocketErrorCode.ToString());
        }
    }

    internal static byte[] BuildQuery(ushort id, string name)
    {
        var bytes = new List<byte>(64);
        Span<byte> header = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header[2..], 0x0100); // recursion desired
        BinaryPrimitives.WriteUInt16BigEndian(header[4..], 1);      // one question
        bytes.AddRange(header.ToArray());
        foreach (var label in name.TrimEnd('.').Split('.'))
        {
            var encoded = Encoding.ASCII.GetBytes(label);
            if (encoded.Length is 0 or > 63) throw new ArgumentException("Invalid DNS name.");
            bytes.Add((byte)encoded.Length);
            bytes.AddRange(encoded);
        }
        bytes.Add(0);
        bytes.AddRange([0, 1, 0, 1]); // type A, class IN
        return bytes.ToArray();
    }

    internal static DnsAnswer ParseAnswer(string server, ushort id, byte[] buffer)
    {
        if (buffer.Length < 12 || BinaryPrimitives.ReadUInt16BigEndian(buffer) != id)
            return new DnsAnswer(server, false, [], "BadReply");
        var flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2));
        var rcode = flags & 0x000F;
        if (rcode == 3) return new DnsAnswer(server, true, [], "NameNotFound");
        if (rcode != 0) return new DnsAnswer(server, true, [], $"ServerError{rcode}");
        var questions = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4));
        var answers = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6));
        var offset = 12;
        for (var index = 0; index < questions; index++)
            offset = SkipName(buffer, offset) + 4;
        var addresses = new List<string>();
        for (var index = 0; index < answers && offset < buffer.Length; index++)
        {
            offset = SkipName(buffer, offset);
            if (offset + 10 > buffer.Length) break;
            var type = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset));
            var length = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(offset + 8));
            offset += 10;
            if (type == 1 && length == 4 && offset + 4 <= buffer.Length)
                addresses.Add(new IPAddress(buffer.AsSpan(offset, 4)).ToString());
            offset += length;
        }
        return new DnsAnswer(server, true, addresses, addresses.Count == 0 ? "NoAddress" : null);
    }

    private static int SkipName(byte[] buffer, int offset)
    {
        while (offset < buffer.Length)
        {
            var length = buffer[offset];
            if (length == 0) return offset + 1;
            if ((length & 0xC0) == 0xC0) return offset + 2;
            offset += length + 1;
        }
        return offset;
    }

    /// <summary>Clock difference to an NTP server (a domain controller), via one SNTP request.</summary>
    public static Task<double?> ClockOffsetSecondsAsync(string server, TimeSpan timeout,
        CancellationToken cancellationToken) =>
        EndpointAgents.Shared.Actions.SntpClient.ClockOffsetSecondsAsync(server, timeout, cancellationToken);
}
