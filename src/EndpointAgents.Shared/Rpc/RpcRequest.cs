using System.Text.Json;

namespace EndpointAgents.Shared.Rpc;

public sealed record RpcRequest(
    string JsonRpc,
    string Id,
    string Method,
    JsonElement? Params)
{
    public static RpcRequest Create(string id, string method, JsonElement? parameters = null)
    {
        return new RpcRequest("2.0", id, method, parameters);
    }
}