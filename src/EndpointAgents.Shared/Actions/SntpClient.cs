using System.Buffers.Binary;
using System.Net.Sockets;

namespace EndpointAgents.Shared.Actions;

/// <summary>One SNTP request (UDP 123): how far the local clock is from a time server.</summary>
public static class SntpClient
{
    /// <summary>
    /// Server time minus local time, in seconds (positive = this clock is behind), or null when the
    /// server does not answer. Reads only; never changes the clock.
    /// </summary>
    public static async Task<double?> ClockOffsetSecondsAsync(string server, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var request = new byte[48];
        request[0] = 0x1B; // client, version 3
        using var udp = new UdpClient();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        limit.CancelAfter(timeout);
        try
        {
            var sent = DateTimeOffset.UtcNow;
            await udp.SendAsync(request, server, 123, limit.Token);
            var reply = await udp.ReceiveAsync(limit.Token);
            var received = DateTimeOffset.UtcNow;
            if (reply.Buffer.Length < 48) return null;
            var seconds = BinaryPrimitives.ReadUInt32BigEndian(reply.Buffer.AsSpan(40));
            var fraction = BinaryPrimitives.ReadUInt32BigEndian(reply.Buffer.AsSpan(44));
            if (seconds == 0) return null; // "kiss of death" / unsynchronised server
            var serverTime = new DateTimeOffset(1900, 1, 1, 0, 0, 0, TimeSpan.Zero)
                .AddSeconds(seconds + fraction / (double)uint.MaxValue);
            var localMidpoint = sent + (received - sent) / 2;
            return Math.Round((serverTime - localMidpoint).TotalSeconds, 1);
        }
        catch (Exception ex) when (ex is SocketException
            || ex is OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
