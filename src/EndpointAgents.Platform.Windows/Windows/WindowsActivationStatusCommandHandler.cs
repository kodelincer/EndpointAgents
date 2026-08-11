using EndpointAgents.Core.Commands;
using EndpointAgents.Shared.Rpc;

namespace EndpointAgents.Platform.Windows.Windows;

public sealed class WindowsActivationStatusCommandHandler(
    IWindowsActivationProbe probe) : IEndpointCommandHandler
{
    public string Method => "system.windows_activation_status";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var report = await probe.ProbeAsync(cancellationToken);
        return RpcResponse.Success(request.Id, report);
    }
}
