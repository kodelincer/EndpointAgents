using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.Actions;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Actions;

/// <summary>
/// The single RPC entry point for catalogued general actions. System actions run here in the
/// service; user actions are handed to the signed-in user's Tray, which validates and runs
/// them with the user's own token. Anything not in <see cref="EndpointActionCatalog"/>, or
/// with arguments outside the catalogue, is refused before any code runs.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class EndpointActionCommandHandler(
    IEnumerable<IEndpointAction> actions,
    InteractiveUserSessionLocator sessions,
    ITrayCommandClient tray,
    ILogger<EndpointActionCommandHandler> logger) : IEndpointCommandHandler
{
    private readonly IReadOnlyDictionary<string, IEndpointAction> _actions =
        actions.ToDictionary(action => action.Id, StringComparer.Ordinal);

    public string Method => EndpointActionProtocol.Method;

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!EndpointActionRequestParser.TryParse(request.Params, out var actionRequest,
                out var parseError))
            return RpcResponse.Failure(request.Id, 400, parseError);
        var definition = EndpointActionCatalog.Find(actionRequest!.ActionId);
        if (definition is null)
            return RpcResponse.Failure(request.Id, 404, "Unknown action.");

        if (definition.RunAs == ActionRunAs.User)
            return await RunInUserSessionAsync(request.Id, actionRequest, cancellationToken);

        if (!EndpointActionRequestParser.TryCreateInvocation(actionRequest, ActionRunAs.System,
                out var invocation, out var error))
            return RpcResponse.Failure(request.Id, 400, error);
        if (!_actions.TryGetValue(definition.Id, out var action))
            return RpcResponse.Failure(request.Id, 501, "The action is not available on this endpoint version.");

        logger.LogInformation("Running endpoint action {ActionId}", definition.Id);
        var result = await EndpointActionRequestParser.RunAsync(action, invocation!,
            cancellationToken);
        return RpcResponse.Success(request.Id, result);
    }

    private async Task<RpcResponse> RunInUserSessionAsync(string requestId,
        EndpointActionRequest actionRequest, CancellationToken cancellationToken)
    {
        // Validate here too, so a malformed request never reaches the Tray.
        if (!EndpointActionRequestParser.TryCreateInvocation(actionRequest, ActionRunAs.User,
                out _, out var error))
            return RpcResponse.Failure(requestId, 400, error);

        var active = sessions.GetActiveSessions();
        var session = string.IsNullOrWhiteSpace(actionRequest.UserSid)
            ? active.FirstOrDefault()
            : active.FirstOrDefault(item => item.UserSid.Equals(actionRequest.UserSid,
                StringComparison.OrdinalIgnoreCase));
        if (session is null)
            return RpcResponse.Success(requestId, EndpointActionResult.Blocked(
                "The user is not signed in on this PC right now."));

        var trayRequest = new TrayIpcRequest(requestId,
            TrayIpcProtocol.EndpointUserActionMethod, string.Empty, string.Empty, null,
            Action: actionRequest with { UserSid = session.UserSid });
        var definition = EndpointActionCatalog.Find(actionRequest.ActionId)!;
        var response = await tray.SendAsync(session.SessionId, trayRequest,
            TimeSpan.FromSeconds(definition.TimeoutSeconds + 10), cancellationToken);
        if (response.ActionResult is not null)
            return RpcResponse.Success(requestId, response.ActionResult);
        return RpcResponse.Success(requestId, response.State == "TrayUnavailable"
            ? EndpointActionResult.Blocked("The support app is not running for the user.")
            : EndpointActionResult.Failed($"The user-side action did not complete ({response.State})."));
    }
}
