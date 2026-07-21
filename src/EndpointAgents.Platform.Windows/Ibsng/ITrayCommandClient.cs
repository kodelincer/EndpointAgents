using EndpointAgents.Shared.LocalIpc;

namespace EndpointAgents.Platform.Windows.Ibsng;

public interface ITrayCommandClient
{
    Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
        CancellationToken cancellationToken);
}
