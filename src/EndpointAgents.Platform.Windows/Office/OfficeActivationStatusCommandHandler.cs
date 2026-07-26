using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Reports sanitized Office activation state without changing licensing.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeActivationStatusCommandHandler(
    OfficeActivationService activation,
    ILogger<OfficeActivationStatusCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "software.office_activation_status";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var userSid = OfficeRemediationCommandHandler.ParseUserSid(request.Params);
        var snapshot = await activation.GetStatusAsync(
            request.Id, cancellationToken, userSid);
        foreach (var issue in snapshot.Issues)
            logger.LogWarning("{Source}: {Message} ({Exception})",
                issue.Source, issue.Message, issue.Exception);

        var warnings = snapshot.Issues.Select(issue =>
            new DiagnosticWarning(issue.Source, issue.Message, issue.Exception)).ToArray();
        return RpcResponse.Success(request.Id,
            DiagnosticResponse.Create(context, snapshot.Report, warnings));
    }
}
