using EndpointAgents.Shared.LocalIpc;

namespace EndpointAgents.Platform.Windows.Ibsng;

public interface ITrayCommandClient
{
    Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
        CancellationToken cancellationToken);

    /// <summary>Same as <see cref="SendAsync(int, TrayIpcRequest, CancellationToken)"/> with a
    /// caller-chosen reply timeout, for user-session actions that take longer than the default.</summary>
    Task<TrayIpcResponse> SendAsync(int sessionId, TrayIpcRequest request,
        TimeSpan responseTimeout, CancellationToken cancellationToken) =>
        SendAsync(sessionId, request, cancellationToken);
}
