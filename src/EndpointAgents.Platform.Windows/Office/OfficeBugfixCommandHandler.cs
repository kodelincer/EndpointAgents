using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Office;

/// <summary>Downloads and launches one version-compatible retailer Office bugfix.</summary>
[SupportedOSPlatform("windows")]
public sealed class OfficeBugfixCommandHandler(
    OfficeInventoryCollector inventoryCollector,
    IOfficeBugfixCatalogClient catalogClient,
    IOfficeUserSessionSource sessionSource,
    OfficeRuntimeInspector runtimeInspector,
    ITrayCommandClient trayClient,
    OfficeArtifactDownloader downloader,
    OfficeBugfixRunner runner,
    ILogger<OfficeBugfixCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "software.office_bugfix_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!OfficeRemediationCommandHandler.TryParseArtifactId(
                request.Params, out var artifactId))
            return RpcResponse.Failure(request.Id, 400,
                "params.artifactId must be an approved artifact ID.");
        if (!OfficeRemediationCommandHandler.TryParseUserSid(
                request.Params, out var userSid))
            return RpcResponse.Failure(request.Id, 400,
                "params.userSid is invalid.");
        var hasChatApproval = ChatActionApproval.HasMarker(request.Params);
        var chatApproved = ChatActionApproval.MatchesRequest(
            request.Params, request.Id);
        if (hasChatApproval && !chatApproved)
            return RpcResponse.Failure(request.Id, 400,
                "The chat approval does not match this action.");
        var hasControlPlaneAuthorization =
            ControlPlaneActionAuthorization.HasMarker(request.Params);
        var controlPlaneAuthorized =
            ControlPlaneActionAuthorization.MatchesRequest(request.Params, request.Id);
        if (hasControlPlaneAuthorization && !controlPlaneAuthorized)
            return RpcResponse.Failure(request.Id, 400,
                "The ControlPlane authorization does not match this action.");
        var authorized = chatApproved || controlPlaneAuthorized;

        var snapshot = inventoryCollector.Collect(cancellationToken);
        if (!snapshot.Inventory.Installed)
            return RpcResponse.Failure(request.Id, 409,
                "No Microsoft Office installation was found on this endpoint.");
        if (!Version.TryParse(snapshot.Inventory.Version, out var officeVersion))
            return RpcResponse.Failure(request.Id, 409,
                "The installed Office version could not be determined.");

        IReadOnlyList<OfficeBugfixArtifactDescriptor> artifacts;
        try
        {
            artifacts = await catalogClient.GetArtifactsAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The Office bugfix catalog could not be read.");
            return RpcResponse.Failure(request.Id, 502,
                "The Office bugfix catalog could not be read from the control plane.",
                DiagnosticResponse.Sanitize(ex));
        }

        var descriptor = artifacts.FirstOrDefault(item => item.ArtifactId.Equals(
            artifactId, StringComparison.OrdinalIgnoreCase));
        if (descriptor is null)
            return RpcResponse.Failure(request.Id, 404,
                "The requested Office bugfix artifact is not available.");
        if (!IsCompatible(descriptor, officeVersion))
            return RpcResponse.Failure(request.Id, 409,
                "The requested Office bugfix is not compatible with the installed Office version.",
                new
                {
                    OfficeVersion = snapshot.Inventory.Version,
                    descriptor.MinOfficeVersion,
                    descriptor.MaxOfficeVersionExclusive
                });
        if (!descriptor.FileName.Equals($"{descriptor.ArtifactId}.bat",
                StringComparison.OrdinalIgnoreCase))
            return RpcResponse.Failure(request.Id, 412,
                "The Office repair catalog returned an invalid batch filename.");
        if (authorized && !ChatActionApproval.MatchesArtifact(request.Params,
                descriptor.FileName, descriptor.Sha256))
            return RpcResponse.Failure(request.Id, 412,
                "The authorized Office repair revision has changed; request the current catalog revision before running it.");

        var session = sessionSource.GetActiveSession(userSid);
        if (session is null)
            return RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user is available to approve and run the Office bugfix.");
        var runtime = runtimeInspector.Inspect(session.SessionId);
        if (!runtime.InspectionSucceeded)
            return RpcResponse.Failure(request.Id, 500,
                "Office application state could not be inspected.");
        if (runtime.OfficeApplicationsOpen)
            return RpcResponse.Failure(request.Id, 409,
                "Close the listed Office applications before running this repair.",
                new { runtime.RunningApplications });

        try
        {
            if (!authorized)
            {
                var consent = new OfficeBugfixConsentRequest(descriptor.ArtifactId,
                    descriptor.DisplayName, descriptor.Description);
                var trayRequest = new TrayIpcRequest(request.Id,
                    TrayIpcProtocol.OfficeBugfixConsentMethod, string.Empty,
                    string.Empty, null, OfficeBugfixConsent: consent);
                var trayResponse = await trayClient.SendAsync(session.SessionId,
                    trayRequest, cancellationToken);
                if (!trayResponse.Success)
                {
                    var code = trayResponse.State == "TrayUnavailable" ? 503 : 409;
                    return RpcResponse.Failure(request.Id, code, trayResponse.State,
                        new { trayResponse.Message, descriptor.ArtifactId,
                            session.UserName });
                }
            }

            var download = await downloader.EnsureAsync(descriptor.ArtifactId,
                descriptor.FileName, descriptor.Sha256, descriptor.DownloadPath,
                cancellationToken, requirePortableExecutable: false);
            var desktopPath = await DesktopArtifactFile.CopyForSessionAsync(
                download.FilePath, session, descriptor.FileName, descriptor.Sha256,
                cancellationToken);
            var execution = await runner.RunAsync(download.FilePath,
                descriptor.ArtifactId, descriptor.FileName, descriptor.Sha256,
                cancellationToken);
            if (execution.TimedOut)
                return RpcResponse.Failure(request.Id, 408,
                    "The approved Office repair timed out.");
            if (execution.ExitCode != 0)
                return RpcResponse.Failure(request.Id, 500,
                    "The approved Office repair returned a failure exit code.",
                    new { execution.ExitCode });

            var warnings = snapshot.Issues.Select(issue => new DiagnosticWarning(
                issue.Source, issue.Message, issue.Exception)).ToArray();
            return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
            {
                State = "Completed",
                descriptor.ArtifactId,
                descriptor.DisplayName,
                OfficeVersion = snapshot.Inventory.Version,
                DesktopPath = desktopPath,
                download.Downloaded,
                execution.ProcessId,
                execution.ExitCode,
                session.SessionId,
                session.UserName
            }, warnings));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Office bugfix {ArtifactId} failed before launch.",
                artifactId);
            return RpcResponse.Failure(request.Id, 500,
                "The approved Office bugfix could not be prepared.",
                DiagnosticResponse.Sanitize(ex));
        }
    }

    internal static bool IsCompatible(OfficeBugfixArtifactDescriptor artifact,
        Version officeVersion) =>
        Version.TryParse(artifact.MinOfficeVersion, out var minimum)
        && Version.TryParse(artifact.MaxOfficeVersionExclusive, out var maximum)
        && officeVersion >= minimum
        && officeVersion < maximum;
}
