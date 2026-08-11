using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Reports Office applications running in the bound user's session.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeRuntimeStatusCommandHandler(
    IOfficeUserSessionSource sessions,
    OfficeRuntimeInspector inspector) : IEndpointCommandHandler
{
    public string Method => "software.office_runtime_status";

    public Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!OfficeRemediationCommandHandler.TryParseUserSid(
                request.Params, out var userSid))
            return Task.FromResult(RpcResponse.Failure(request.Id, 400,
                "params.userSid is invalid."));

        var session = sessions.GetActiveSession(userSid);
        if (session is null)
            return Task.FromResult(RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user session is available."));

        var status = inspector.Inspect(session.SessionId);
        return Task.FromResult(status.InspectionSucceeded
            ? RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, status,
                Array.Empty<DiagnosticWarning>()))
            : RpcResponse.Failure(request.Id, 500,
                "Office application state could not be inspected."));
    }
}
