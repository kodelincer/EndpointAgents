namespace EndpointAgents.Shared.Rpc;

public sealed record RpcError(
    int Code,
    string Message,
    object? Data = null);