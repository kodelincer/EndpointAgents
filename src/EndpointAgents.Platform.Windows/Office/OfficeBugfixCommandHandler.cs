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
    IOfficeVolumeActivationProbe volumeProbe,
    OfficeArtifactDownloader downloader,
    OfficeBugfixRunner runner,
    ILogger<OfficeBugfixCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "software.office_bugfix_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!HasOnlyApprovedParameters(request.Params))
            return RpcResponse.Failure(request.Id, 400,
                "Only params.artifactId is accepted from the caller.");
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
        if (!Version.TryParse(snapshot.Inventory.Version, out _))
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
        if (!IsCompatible(descriptor, snapshot.Inventory,
                Environment.OSVersion.Version))
            return RpcResponse.Failure(request.Id, 409,
                "The requested Office bugfix is not compatible with this Office or Windows installation.",
                new
                {
                    OfficeVersion = snapshot.Inventory.Version,
                    descriptor.MinOfficeVersion,
                    descriptor.MaxOfficeVersionExclusive,
                    descriptor.OfficeProductReleaseIds,
                    descriptor.MinWindowsVersion,
                    descriptor.MaxWindowsVersionExclusive,
                    descriptor.AdditionalCompatibility
                });
        if (!descriptor.FileName.Equals($"{descriptor.ArtifactId}.bat",
                StringComparison.OrdinalIgnoreCase))
            return RpcResponse.Failure(request.Id, 412,
                "The Office repair catalog returned an invalid batch filename.");
        if (!BatchPlaybookPolicy.TryNormalize(descriptor.Playbook,
                out var normalizedPlaybook, out _)
            || descriptor.PlaybookSha256 is not { Length: 64 }
            || !descriptor.PlaybookSha256.All(Uri.IsHexDigit)
            || !BatchPlaybookPolicy.ComputeSha256(normalizedPlaybook!).Equals(
                descriptor.PlaybookSha256, StringComparison.OrdinalIgnoreCase))
            return RpcResponse.Failure(request.Id, 412,
                "The Office repair catalog returned an invalid playbook revision.");
        if (!BatchSupportFilePolicy.IsValidBundle(descriptor.Sha256,
                descriptor.SupportFiles, descriptor.BundleSha256,
                $"/api/files/office/bugfix/{descriptor.ArtifactId}/file"))
            return RpcResponse.Failure(request.Id, 412,
                "The Office repair catalog returned an invalid supporting-file revision.");
        descriptor = descriptor with { Playbook = normalizedPlaybook! };
        if (authorized && (!ChatActionApproval.MatchesArtifactAndPlaybook(
                request.Params, descriptor.FileName, descriptor.Sha256,
                descriptor.PlaybookSha256)
            || !ChatActionApproval.MatchesBundle(request.Params,
                descriptor.BundleSha256)))
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

        var activationBefore = await volumeProbe.ProbeAsync(snapshot.Inventory,
            cancellationToken);
        if (OfficeActivationRecommendation.IsHealthyKms(activationBefore))
            return RpcResponse.Failure(request.Id, 409,
                "Office already has healthy KMS activation; the repair was not run.");

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
                cancellationToken, requirePortableExecutable: false,
                revisionSha256: descriptor.BundleSha256);
            await downloader.EnsureSupportFilesAsync(download.FilePath,
                descriptor.SupportFiles, cancellationToken);
            var desktopPath = descriptor.SupportFiles is { Count: > 0 }
                ? null
                : await DesktopArtifactFile.CopyForSessionAsync(
                    download.FilePath, session, descriptor.FileName,
                    descriptor.Sha256, cancellationToken);
            var execution = await runner.RunAsync(download.FilePath, descriptor,
                cancellationToken);
            if (TryGetExecutionFailure(execution, out var executionCode,
                    out var executionMessage))
                return RpcResponse.Failure(request.Id, executionCode,
                    executionMessage, new
                {
                    execution.State,
                    execution.ProcessId,
                    execution.ExitCode,
                    execution.CompletedStepCount,
                    execution.FailedStepIndex,
                    execution.OutputTail
                });

            var activationAfter = await volumeProbe.ProbeAsync(snapshot.Inventory,
                cancellationToken);
            if (!IsVerifiedSuccess(execution, activationAfter))
                return RpcResponse.Failure(request.Id, 409,
                    "The approved Office repair completed, but Office did not report healthy KMS activation.",
                    new
                    {
                        execution.ProcessId,
                        execution.ExitCode,
                        activationAfter.ToolAvailable,
                        activationAfter.Succeeded,
                        activationAfter.Products,
                        activationAfter.FailureCode
                    });

            var warnings = snapshot.Issues.Select(issue => new DiagnosticWarning(
                issue.Source, issue.Message, issue.Exception)).ToArray();
            return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
            {
                execution.State,
                descriptor.ArtifactId,
                descriptor.DisplayName,
                OfficeVersion = snapshot.Inventory.Version,
                DesktopPath = desktopPath,
                download.Downloaded,
                execution.ProcessId,
                execution.ExitCode,
                KmsVerified = true,
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
        OfficeInventory inventory, Version windowsVersion)
    {
        if (!Version.TryParse(inventory.Version, out var officeVersion))
            return false;
        var primary = new OfficeBugfixCompatibility(artifact.MinOfficeVersion,
            artifact.MaxOfficeVersionExclusive, artifact.OfficeProductReleaseIds ?? [],
            artifact.MinWindowsVersion, artifact.MaxWindowsVersionExclusive);
        return IsCompatible(primary, inventory, officeVersion, windowsVersion)
            || artifact.AdditionalCompatibility?.Any(rule =>
                rule is not null && IsCompatible(rule, inventory, officeVersion,
                    windowsVersion)) == true;
    }

    private static bool IsCompatible(OfficeBugfixCompatibility rule,
        OfficeInventory inventory, Version officeVersion, Version windowsVersion)
    {
        if (!Version.TryParse(rule.MinOfficeVersion, out var minimum)
            || !Version.TryParse(rule.MaxOfficeVersionExclusive, out var maximum)
            || officeVersion < minimum || officeVersion >= maximum)
            return false;

        if (rule.MinWindowsVersion is not null
            && (!Version.TryParse(rule.MinWindowsVersion, out var minWindows)
                || windowsVersion < minWindows)
            || rule.MaxWindowsVersionExclusive is not null
            && (!Version.TryParse(rule.MaxWindowsVersionExclusive, out var maxWindows)
                || windowsVersion >= maxWindows))
            return false;

        if (rule.OfficeProductReleaseIds is not { Count: > 0 } allowedIds)
            return true;
        var installedIds = inventory.ClickToRun
            .SelectMany(item => (item.ProductReleaseIds ?? string.Empty).Split(
                [',', ';'], StringSplitOptions.TrimEntries
                    | StringSplitOptions.RemoveEmptyEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return installedIds.Length > 0 && installedIds.All(id =>
            allowedIds.Contains(id, StringComparer.OrdinalIgnoreCase));
    }

    internal static bool HasOnlyApprovedParameters(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
            return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("artifactId")
                || property.NameEquals("userSid")
                || property.NameEquals(ChatActionApproval.ParameterName)
                || property.NameEquals(ControlPlaneActionAuthorization.ParameterName)
                || ChatActionApproval.IsArtifactPin(property.Name))
                continue;
            return false;
        }
        return true;
    }

    internal static bool TryGetExecutionFailure(OfficeBugfixRunResult execution,
        out int code, out string message)
    {
        (code, message) = execution.State switch
        {
            OfficeBugfixRunStates.Completed => (0, string.Empty),
            OfficeBugfixRunStates.PromptTimedOut => (408,
                "The approved Office repair did not show the expected prompt before its timeout."),
            OfficeBugfixRunStates.TimedOut => (408,
                "The approved Office repair did not finish before its time limit."),
            OfficeBugfixRunStates.ExitedBeforePlaybookCompleted => (409,
                "The approved Office repair exited before all approved playbook steps completed."),
            OfficeBugfixRunStates.OutputLimitExceeded => (413,
                "The approved Office repair exceeded the terminal output limit."),
            OfficeBugfixRunStates.Cancelled => (499,
                "The approved Office repair was cancelled."),
            OfficeBugfixRunStates.FailureMarkerDetected => (422,
                "The approved Office repair printed an approved failure message."),
            OfficeBugfixRunStates.SuccessMarkerMissing => (422,
                "The approved Office repair finished without its approved success message."),
            OfficeBugfixRunStates.RepeatLimitExceeded => (508,
                "The approved Office repair repeated a prompt more often than approved."),
            OfficeBugfixRunStates.GuiDialogBlocked => (409,
                "The approved Office repair opened a window that needs a mouse click, which unattended runs cannot answer."),
            _ => (500, "The approved Office repair process failed.")
        };
        return execution.State != OfficeBugfixRunStates.Completed;
    }

    internal static bool IsVerifiedSuccess(OfficeBugfixRunResult execution,
        OfficeActivationProbeResult activationAfter) =>
        execution.State == OfficeBugfixRunStates.Completed
        && OfficeActivationRecommendation.IsHealthyKms(activationAfter);
}
