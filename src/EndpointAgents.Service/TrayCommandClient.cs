using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using Microsoft.Extensions.Options;

namespace EndpointAgents.Service;

/// <summary>Sends a catalogued task to the Tray attached for one Windows session.</summary>
public sealed class TrayCommandClient(IOptions<IbsngOptions> options, TrayChannelRegistry trays)
    : ITrayCommandClient
{
    private readonly IbsngOptions _options = options.Value;

    public async Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
        CancellationToken cancellationToken)
    {
        var channel = await trays.WaitAsync(sessionId,
            TimeSpan.FromSeconds(_options.TrayConnectTimeoutSeconds), cancellationToken);
        if (channel is null)
            return new TrayIpcResponse(request.Id, false, "TrayUnavailable",
                Message: "The active user's Tray is not running.");
        return await channel.SendAsync(request,
            TimeSpan.FromSeconds(_options.TrayResponseTimeoutSeconds), cancellationToken);
    }
}
