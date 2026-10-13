using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Packages;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Packages;

/// <summary>
/// Downloads, verifies and runs one general catalog package. Every package is
/// administrator-reviewed and hash-pinned, so it runs unattended without asking the
/// signed-in user; the caller supplies only the package id.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class PackageRunCommandHandler(
    PackageArtifactClient artifacts,
    PackageRunner runner,
    IOptions<PackageSigningOptions> signing,
    ILogger<PackageRunCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => PackageCommands.Run;

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!HasOnlyApprovedParameters(request.Params))
            return RpcResponse.Failure(request.Id, 400,
                "Only params.artifactId is accepted from the caller.");
        if (!OfficeRemediationCommandHandler.TryParseArtifactId(request.Params,
                out var packageId)
            || !PackageDescriptorPolicy.IsValidId(packageId))
            return RpcResponse.Failure(request.Id, 400,
                "params.artifactId must be an approved package ID.");
        if (ControlPlaneActionAuthorization.HasMarker(request.Params)
            && !ControlPlaneActionAuthorization.MatchesRequest(request.Params, request.Id))
            return RpcResponse.Failure(request.Id, 400,
                "The ControlPlane authorization does not match this action.");
        if (ChatActionApproval.HasMarker(request.Params)
            && !ChatActionApproval.MatchesRequest(request.Params, request.Id))
            return RpcResponse.Failure(request.Id, 400,
                "The chat approval does not match this action.");

        IReadOnlyList<PackageDescriptor> catalog;
        try
        {
            catalog = await artifacts.GetCatalogAsync(cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The package catalog could not be read.");
            return RpcResponse.Failure(request.Id, 502,
                "The package catalog could not be read from the control plane.",
                DiagnosticResponse.Sanitize(ex));
        }

        var descriptor = catalog.FirstOrDefault(item => item.PackageId == packageId);
        if (descriptor is null)
            return RpcResponse.Failure(request.Id, 404,
                "The requested package is not available.");
        if (!PackageDescriptorPolicy.TryValidate(descriptor, out _))
            return RpcResponse.Failure(request.Id, 412,
                "The control plane returned an invalid package revision.");
        if (!PackageSignaturePolicy.Verify(descriptor, signing.Value.TrustedPublicKeys))
            return RpcResponse.Failure(request.Id, 412,
                "The package is not signed by a trusted administrator key.");
        // The control plane pins the revision it chose; a newer catalog revision
        // must be requested again rather than run in its place.
        var pinned = HasPins(request.Params)
            || ControlPlaneActionAuthorization.HasMarker(request.Params);
        if (pinned && (!ChatActionApproval.MatchesArtifactAndPlaybook(request.Params,
                    descriptor.FileName, descriptor.Sha256, descriptor.PlaybookSha256)
                || !ChatActionApproval.MatchesBundle(request.Params,
                    descriptor.BundleSha256)))
            return RpcResponse.Failure(request.Id, 412,
                "The package revision has changed; request the current catalog revision before running it.");
        var windowsVersion = Environment.OSVersion.Version;
        if (!PackageDescriptorPolicy.IsCompatible(descriptor, windowsVersion))
            return RpcResponse.Failure(request.Id, 409,
                "The requested package is not compatible with this Windows version.",
                new
                {
                    WindowsVersion = windowsVersion.ToString(),
                    descriptor.MinWindowsVersion,
                    descriptor.MaxWindowsVersionExclusive
                });

        try
        {
            var staged = await artifacts.EnsureAsync(descriptor, cancellationToken);
            // The control plane adds the chat user's SID; packages that change the
            // user's own profile read it from their environment.
            OfficeRemediationCommandHandler.TryParseUserSid(request.Params, out var userSid);
            var execution = await runner.RunAsync(staged.Path, descriptor,
                cancellationToken, PackageUserContext.Resolve(userSid));
            if (TryGetExecutionFailure(execution, out var code, out var message))
                return RpcResponse.Failure(request.Id, code, message, new
                {
                    execution.State,
                    descriptor.PackageId,
                    execution.ProcessId,
                    execution.ExitCode,
                    execution.CompletedStepCount,
                    execution.FailedStepIndex,
                    execution.OutputTail
                });
            return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
            {
                execution.State,
                descriptor.PackageId,
                descriptor.DisplayName,
                staged.Downloaded,
                execution.ProcessId,
                execution.ExitCode,
                execution.RestartRequired
            }, Array.Empty<DiagnosticWarning>()));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Package {PackageId} failed before launch.", packageId);
            return RpcResponse.Failure(request.Id, 500,
                "The approved package could not be prepared.",
                DiagnosticResponse.Sanitize(ex));
        }
    }

    internal static bool HasOnlyApprovedParameters(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value) return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("artifactId") || property.NameEquals("userSid")
                || property.NameEquals(ChatActionApproval.ParameterName)
                || property.NameEquals(ControlPlaneActionAuthorization.ParameterName)
                || ChatActionApproval.IsArtifactPin(property.Name))
                continue;
            return false;
        }
        return true;
    }

    private static bool HasPins(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } value
        && value.EnumerateObject().Any(property =>
            ChatActionApproval.IsArtifactPin(property.Name));

    internal static bool TryGetExecutionFailure(PackageRunResult execution,
        out int code, out string message)
    {
        (code, message) = execution.State switch
        {
            OfficeBugfixRunStates.Completed => (0, string.Empty),
            OfficeBugfixRunStates.PromptTimedOut => (408,
                "The package did not show the expected prompt before its timeout."),
            OfficeBugfixRunStates.TimedOut => (408,
                "The package did not finish before its time limit."),
            OfficeBugfixRunStates.ExitedBeforePlaybookCompleted => (409,
                "The package exited before all playbook steps completed."),
            OfficeBugfixRunStates.OutputLimitExceeded => (413,
                "The package exceeded the terminal output limit."),
            OfficeBugfixRunStates.Cancelled => (499, "The package run was cancelled."),
            OfficeBugfixRunStates.FailureMarkerDetected => (422,
                "The package printed an approved failure message."),
            OfficeBugfixRunStates.SuccessMarkerMissing => (422,
                "The package finished without its approved success message."),
            OfficeBugfixRunStates.RepeatLimitExceeded => (508,
                "The package repeated a prompt more often than approved."),
            OfficeBugfixRunStates.GuiDialogBlocked => (409,
                "The package opened a window that needs a mouse click, which unattended runs cannot answer."),
            PackageRunStates.SignatureRejected => (412,
                "The package is not signed by its approved publisher."),
            _ => (500, "The package process failed.")
        };
        return execution.State != OfficeBugfixRunStates.Completed;
    }
}
