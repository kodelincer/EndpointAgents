using EndpointAgents.Shared.Actions;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace ControlPlane.Api.Actions;

public sealed record ServerProbeResult(string TargetId, string Label, bool NameResolves,
    bool ExpectedAddressMatches, IReadOnlyList<ServerPortResult> Ports, int? HttpStatus,
    string Verdict);

public sealed record ServerPortResult(int Port, bool Open, long? Milliseconds);

public sealed record ServerHostPortResult(string Host, int Port, bool NameResolves, bool Open,
    long? Milliseconds);

/// <summary>
/// Checks an environment-map target from the control-plane server itself, so the agent can
/// tell "the service is down for everyone" from "only this PC cannot reach it". Only
/// administrator-listed targets are probed; the agent cannot pass a host name.
/// </summary>
public sealed class ServerProbe(IHttpClientFactory httpClients)
{
    public const string HttpClientName = "server-probe";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(4);

    public async Task<ServerProbeResult> ProbeAsync(EnvironmentTarget target,
        CancellationToken cancellationToken)
    {
        var host = EndpointActionValidator.HostOf(target.ToResolved()) ?? "";
        IPAddress[] addresses = [];
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException
            or ArgumentException)
        {
        }
        var expectedMatches = target.ExpectedAddresses.Count == 0
            || addresses.Any(address => target.ExpectedAddresses.Contains(address.ToString(),
                StringComparer.OrdinalIgnoreCase));

        var ports = target.Ports.ToList();
        if (target.Kind == EnvironmentTargetKinds.WebApp
            && Uri.TryCreate(target.Address, UriKind.Absolute, out var web) && !ports.Contains(web.Port))
            ports.Add(web.Port);
        if (target.Kind == EnvironmentTargetKinds.Share && ports.Count == 0) ports.Add(445);
        if (target.Kind == EnvironmentTargetKinds.Printer && ports.Count == 0) ports.Add(445);

        var portResults = new List<ServerPortResult>();
        if (addresses.Length > 0)
            foreach (var port in ports.Distinct().Take(6))
                portResults.Add(await ConnectAsync(addresses[0], port, cancellationToken));

        int? httpStatus = null;
        if (target.Kind == EnvironmentTargetKinds.WebApp && addresses.Length > 0)
            httpStatus = await HttpStatusAsync(target.Address, cancellationToken);

        var verdict = addresses.Length == 0 ? "NameDoesNotResolveFromServer"
            : !expectedMatches ? "UnexpectedAddressFromServer"
            : portResults.Count > 0 && portResults.All(port => !port.Open) ? "DownFromServer"
            : httpStatus is >= 500 ? "WebAppErrorFromServer"
            : "ReachableFromServer";
        return new ServerProbeResult(target.Id, target.Label, addresses.Length > 0,
            expectedMatches, portResults, httpStatus, verdict);
    }

    /// <summary>Resolves an administrator-set host and opens one TCP port (the KMS test).</summary>
    public static async Task<ServerHostPortResult> TestTcpAsync(string host, int port,
        CancellationToken cancellationToken)
    {
        IPAddress[] addresses = [];
        if (IPAddress.TryParse(host, out var literal)) addresses = [literal];
        else
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(ConnectTimeout);
                addresses = await Dns.GetHostAddressesAsync(host, timeout.Token);
            }
            catch (Exception exception) when (exception is SocketException
                or OperationCanceledException or ArgumentException)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        if (addresses.Length == 0)
            return new ServerHostPortResult(host, port, false, false, null);
        var result = await ConnectAsync(addresses[0], port, cancellationToken);
        return new ServerHostPortResult(host, port, true, result.Open, result.Milliseconds);
    }

    private static async Task<ServerPortResult> ConnectAsync(IPAddress address, int port,
        CancellationToken cancellationToken)
    {
        using var client = new TcpClient(address.AddressFamily);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);
        var watch = Stopwatch.StartNew();
        try
        {
            await client.ConnectAsync(address, port, timeout.Token);
            return new ServerPortResult(port, true, watch.ElapsedMilliseconds);
        }
        catch (Exception exception) when (exception is SocketException or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new ServerPortResult(port, false, null);
        }
    }

    private async Task<int?> HttpStatusAsync(string address, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            using var request = new HttpRequestMessage(HttpMethod.Get, address);
            using var response = await httpClients.CreateClient(HttpClientName)
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            return (int)response.StatusCode;
        }
        catch (Exception exception) when (exception is HttpRequestException
            or OperationCanceledException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }
}
