namespace EndpointAgents.Shared.Rpc;

public sealed record RpcResponse(
    string JsonRpc,
    string Id,
    object? Result,
    RpcError? Error = null)
{
    public static RpcResponse Success(string id, object? result)
    {
        return new RpcResponse("2.0", id, result);
    }

    public static RpcResponse Failure(string id, int code, string message, object? data = null)
    {
        return new RpcResponse("2.0", id, null, new RpcError(code, message, data));
    }
}