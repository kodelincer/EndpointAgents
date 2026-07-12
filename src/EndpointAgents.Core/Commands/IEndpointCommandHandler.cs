using EndpointAgents.Shared.Rpc;

namespace EndpointAgents.Core.Commands;

public interface IEndpointCommandHandler
{
    string Method { get; }

    Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken);
}

// Note: This is the main extension point for your teammates.