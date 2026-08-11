using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>
/// Downloads and launches one compatible administrator-approved Office remediation.
/// The caller supplies only an artifact ID; filenames, hashes, paths, and arguments
/// are never accepted from the command channel.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class OfficeRemediationCommandHandler(
    OfficeActivationService activation,
    IOfficeUserSessionSource sessionSource,
    OfficeRuntimeInspector runtimeInspector,
    ITrayCommandClient trayClient,
    OfficeArtifactDownloader downloader,
    ILogger<OfficeRemediationCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "software.office_remediation_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParseArtifactId(request.Params, out var artifactId))
            return RpcResponse.Failure(request.Id, 400,
                "params.artifactId must be an approved artifact ID.");

        if (!TryParseUserSid(request.Params, out var userSid))
            return RpcResponse.Failure(request.Id, 400,
                "params.userSid is invalid.");
        var hasChatApproval = ChatActionApproval.HasMarker(request.Params);
        var chatApproved = ChatActionApproval.MatchesRequest(
            request.Params, request.Id);
        if (hasChatApproval && !chatApproved)
            return RpcResponse.Failure(request.Id, 400,
                "The chat approval does not match this action.");
        var status = await activation.GetStatusAsync(
            request.Id, cancellationToken, userSid);
        var descriptor = status.ApprovedArtifacts.FirstOrDefault(item =>
            item.ArtifactId.Equals(artifactId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null
            || !status.Report.CompatibleArtifactIds.Contains(artifactId,
                StringComparer.OrdinalIgnoreCase))
        {
            return RpcResponse.Failure(request.Id, 409,
                "The requested artifact is not compatible with the endpoint's current Office product, version, and activation state.",
                new
                {
                    status.Report.OfficeVersion,
                    status.Report.ProductFamilies,
                    status.Report.OverallState,
                    status.Report.CompatibleArtifactIds
                });
        }

        if (!descriptor.Kind.Equals(OfficeRemediationArtifactKinds.MicrosoftSignedExecutable,
                StringComparison.OrdinalIgnoreCase)
            || !Path.GetExtension(descriptor.FileName).Equals(".exe",
                StringComparison.OrdinalIgnoreCase))
        {
            return RpcResponse.Failure(request.Id, 415,
                "Only Microsoft-signed executable remediation artifacts can be run by the endpoint agent.");
        }

        if (Path.GetFileName(descriptor.FileName) != descriptor.FileName)
            return RpcResponse.Failure(request.Id, 412, "The catalog artifact filename is invalid.");
        if (chatApproved && !ChatActionApproval.MatchesArtifact(request.Params,
                descriptor.FileName, descriptor.Sha256))
            return RpcResponse.Failure(request.Id, 412,
                "The approved Office remediation revision has changed; approve the new proposal before running it.");

        if (descriptor.RequiresOfficeAppsClosed)
        {
            var runtimeSession = sessionSource.GetActiveSession(userSid);
            if (runtimeSession is null)
                return RpcResponse.Failure(request.Id, 404,
                    "No interactive Windows user is available to run the remediation.");
            var runtime = runtimeInspector.Inspect(runtimeSession.SessionId);
            if (!runtime.InspectionSucceeded)
                return RpcResponse.Failure(request.Id, 500,
                    "Office application state could not be inspected.");
            if (runtime.OfficeApplicationsOpen)
                return RpcResponse.Failure(request.Id, 409,
                    "Close the listed Office applications before running this remediation.",
                    new { runtime.RunningApplications });
        }

        var session = sessionSource.GetActiveSession(userSid);
        if (session is null)
            return RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user is available to approve and run the remediation.");

        try
        {
            var download = await downloader.EnsureAsync(descriptor.ArtifactId,
                descriptor.FileName, descriptor.Sha256, descriptor.DownloadPath,
                cancellationToken);

            var launch = new OfficeRemediationLaunchRequest(descriptor.ArtifactId,
                descriptor.DisplayName, descriptor.Description, download.FilePath,
                descriptor.Sha256, OfficeArtifactTrustPolicies.MicrosoftSigned,
                chatApproved);
            var trayRequest = new TrayIpcRequest(request.Id,
                TrayIpcProtocol.OfficeRemediationLaunchMethod, string.Empty, string.Empty, null,
                OfficeRemediation: launch);
            var trayResponse = await trayClient.SendAsync(session.SessionId, trayRequest,
                cancellationToken);
            if (!trayResponse.Success)
            {
                var code = trayResponse.State == "TrayUnavailable" ? 503
                    : trayResponse.State == "UserDeclined" ? 409 : 412;
                return RpcResponse.Failure(request.Id, code, trayResponse.State,
                    new { trayResponse.Message, descriptor.ArtifactId, session.UserName });
            }

            return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
            {
                State = trayResponse.State,
                descriptor.ArtifactId,
                descriptor.DisplayName,
                descriptor.RequiresRestart,
                download.Downloaded,
                trayResponse.ProcessId,
                trayResponse.SignerSubject,
                session.SessionId,
                session.UserName
            }, status.Issues.Select(issue => new DiagnosticWarning(issue.Source,
                issue.Message, issue.Exception)).ToArray()));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Office remediation {ArtifactId} failed before launch.",
                artifactId);
            return RpcResponse.Failure(request.Id, 500,
                "The approved Office remediation could not be prepared.",
                DiagnosticResponse.Sanitize(ex));
        }
    }

    internal static bool TryParseArtifactId(JsonElement? parameters, out string artifactId)
    {
        artifactId = string.Empty;
        if (parameters is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("artifactId", out var value)
            || value.ValueKind != JsonValueKind.String)
            return false;
        artifactId = value.GetString()?.Trim() ?? string.Empty;
        return ArtifactIdPattern().IsMatch(artifactId);
    }

    internal static bool TryParseUserSid(JsonElement? parameters,
        out string? userSid)
    {
        userSid = null;
        if (parameters is not { ValueKind: JsonValueKind.Object } element
            || !element.TryGetProperty("userSid", out var value))
            return true;
        if (value.ValueKind != JsonValueKind.String)
            return false;
        userSid = value.GetString()?.Trim();
        if (userSid is not { Length: <= 184 }
            || !userSid.StartsWith("S-1-", StringComparison.Ordinal))
        {
            userSid = null;
            return false;
        }
        try
        {
            _ = new SecurityIdentifier(userSid);
            return true;
        }
        catch (ArgumentException)
        {
            userSid = null;
            return false;
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$")]
    private static partial Regex ArtifactIdPattern();
}
