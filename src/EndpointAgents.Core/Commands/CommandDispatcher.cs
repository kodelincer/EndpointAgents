using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Diagnostics;

namespace EndpointAgents.Core.Commands;

public sealed class CommandDispatcher
{
    private readonly IReadOnlyDictionary<string, IEndpointCommandHandler> _handlers;
    private readonly ILogger<CommandDispatcher> _logger;

    public CommandDispatcher(
        IEnumerable<IEndpointCommandHandler> handlers,
        ILogger<CommandDispatcher> logger)
    {
        _logger = logger;

        _handlers = handlers.ToDictionary(
            handler => handler.Method,
            StringComparer.OrdinalIgnoreCase);
    }

    public async Task<RpcResponse> DispatchAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            _logger.LogWarning("Rejected command {Method}: request id is missing", request.Method);
            return RpcResponse.Failure(
                id: string.Empty,
                code: 400,
                message: "RPC request id is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Method))
        {
            _logger.LogWarning("Rejected request {RequestId}: command method is missing", request.Id);
            return RpcResponse.Failure(
                request.Id,
                400,
                "RPC method is required.");
        }

        if (!_handlers.TryGetValue(request.Method, out var handler))
        {
            _logger.LogWarning(
                "Rejected unknown command {Method} with request id {RequestId}",
                request.Method, request.Id);
            return RpcResponse.Failure(
                request.Id,
                404,
                $"Unknown endpoint command method: {request.Method}");
        }

        try
        {
            var started = Stopwatch.GetTimestamp();
            _logger.LogInformation(
                "Executing command {Method} with request id {RequestId}",
                request.Method,
                request.Id);

            var response = await handler.HandleAsync(request, context, cancellationToken);
            var elapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            if (response.Error is null)
            {
                _logger.LogInformation(
                    "Completed command {Method} with request id {RequestId} successfully in {ElapsedMilliseconds:F0} ms",
                    request.Method, request.Id, elapsedMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Completed command {Method} with request id {RequestId} with error code {ErrorCode} in {ElapsedMilliseconds:F0} ms",
                    request.Method, request.Id, response.Error.Code, elapsedMilliseconds);
            }
            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Command {Method} with request id {RequestId} was cancelled",
                request.Method, request.Id);
            return RpcResponse.Failure(
                request.Id,
                499,
                "Command was cancelled.");
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex,
                "Command {Method} failed with request id {RequestId}",
                request.Method,
                request.Id);

            return RpcResponse.Failure(
                request.Id,
                500,
                "Command execution failed.",
                new
                {
                    exception = ex.GetType().Name,
                    message = ex.Message
                });
        }
    }

    public IReadOnlyCollection<string> GetAvailableMethods()
    {
        return _handlers.Keys.OrderBy(x => x).ToArray();
    }
}
