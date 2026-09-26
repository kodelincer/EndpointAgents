using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Windows;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsActivationBugfixCommandHandler(
    IWindowsActivationProbe probe,
    WindowsArtifactClient artifacts,
    IOfficeUserSessionSource sessions,
    ITrayCommandClient tray,
    WindowsBugfixRunner runner,
    ILogger<WindowsActivationBugfixCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "system.windows_activation_bugfix_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParse(request.Params, out var artifactId, out var userSid))
            return RpcResponse.Failure(request.Id, 400,
                "An approved Windows repair artifactId is required.");
        var hasChatApproval = ChatActionApproval.HasMarker(request.Params);
        var chatApproved = ChatActionApproval.MatchesRequest(request.Params, request.Id);
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

        var before = await probe.ProbeAsync(cancellationToken);
        if (!before.ProbeSucceeded)
            return RpcResponse.Failure(request.Id, 409,
                "Windows activation status could not be verified before remediation.");
        if (before.State == WindowsActivationStates.Licensed)
            return RpcResponse.Failure(request.Id, 409,
                "Windows is already activated; remediation was not run.");

        var session = sessions.GetActiveSession(userSid);
        if (session is null)
            return RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user is available for the remediation.");

        try
        {
            var descriptor = await artifacts.GetArtifactAsync(artifactId!,
                cancellationToken);
            if (descriptor is null)
                return RpcResponse.Failure(request.Id, 404,
                    "The approved Windows activation remediation is not available.");
            if (artifactId == WindowsRemediationPaths.ArtifactId
                && (await artifacts.GetArtifactsAsync(cancellationToken))
                    .Any(item => item.MinWindowsVersion is not null))
                return RpcResponse.Failure(request.Id, 409,
                    "The legacy Windows repair is unavailable while versioned repairs are configured.");
            if (!WindowsBugfixCompatibility.IsCompatible(descriptor, before,
                    Environment.OSVersion.Version))
                return RpcResponse.Failure(request.Id, 409,
                    "The requested Windows repair is not compatible with this Windows installation.");
            if (authorized && !(descriptor.Playbook is null
                    ? ChatActionApproval.MatchesArtifact(request.Params,
                        descriptor.FileName, descriptor.Sha256)
                    : ChatActionApproval.MatchesArtifactAndPlaybook(
                        request.Params, descriptor.FileName, descriptor.Sha256,
                        descriptor.PlaybookSha256!))
                || authorized && !ChatActionApproval.MatchesBundle(request.Params,
                    descriptor.BundleSha256))
                return RpcResponse.Failure(request.Id, 412,
                    "The approved Windows remediation revision has changed; request the current repair again.");

            if (!authorized)
            {
                var consent = new WindowsBugfixConsentRequest(descriptor.ArtifactId,
                    descriptor.DisplayName, descriptor.Description);
                var trayRequest = new TrayIpcRequest(request.Id,
                    TrayIpcProtocol.WindowsBugfixConsentMethod, string.Empty,
                    string.Empty, null, WindowsBugfixConsent: consent);
                var trayResponse = await tray.SendAsync(session.SessionId,
                    trayRequest, cancellationToken);
                if (!trayResponse.Success)
                    return RpcResponse.Failure(request.Id,
                        trayResponse.State == "TrayUnavailable" ? 503 : 409,
                        trayResponse.State);
            }

            var download = await artifacts.EnsureAsync(descriptor, cancellationToken);
            var execution = await runner.RunAsync(download.Path, descriptor,
                cancellationToken);
            if (execution.TimedOut)
                return RpcResponse.Failure(request.Id, 408,
                    "The Windows activation remediation timed out.");
            if (execution.ExitCode != 0)
                return RpcResponse.Failure(request.Id, 500,
                    "The Windows activation remediation returned a failure exit code.",
                    new { execution.ExitCode, execution.State,
                        execution.CompletedStepCount, execution.FailedStepIndex });

            var after = await probe.ProbeAsync(cancellationToken);
            var state = after.ProbeSucceeded
                && after.State == WindowsActivationStates.Licensed
                    ? "Resolved" : "CompletedUnresolved";
            return RpcResponse.Success(request.Id, new
            {
                State = state,
                Before = before,
                After = after,
                download.Downloaded,
                execution.ExitCode
            });
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Windows activation remediation failed.");
            return RpcResponse.Failure(request.Id, 500,
                "The approved Windows activation remediation could not be prepared.");
        }
    }

    internal static bool TryParse(JsonElement? parameters, out string? userSid)
        => TryParse(parameters, out _, out userSid);

    internal static bool TryParse(JsonElement? parameters,
        out string? artifactId, out string? userSid)
    {
        artifactId = null;
        userSid = null;
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
            return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("artifactId"))
            {
                if (property.Value.ValueKind != JsonValueKind.String)
                    return false;
                artifactId = property.Value.GetString();
                if (artifactId is null || artifactId.Length is < 1 or > 64
                    || artifactId == WindowsHelloSamplePaths.ArtifactId
                    || !char.IsAsciiLetterOrDigit(artifactId[0])
                    || !char.IsAsciiLetterOrDigit(artifactId[^1])
                    || artifactId.Any(character =>
                        !(character is >= 'a' and <= 'z' or >= '0' and <= '9'
                            or '-' or '_' or '.')))
                    return false;
            }
            else if (property.NameEquals("userSid"))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                userSid = property.Value.GetString();
            }
            else if (property.NameEquals(ChatActionApproval.ParameterName))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
            }
            else if (property.NameEquals(ControlPlaneActionAuthorization.ParameterName))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
            }
            else if (ChatActionApproval.IsArtifactPin(property.Name))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
            }
            else return false;
        }
        return artifactId is not null
            && OfficeRemediationCommandHandler.TryParseUserSid(parameters,
                out userSid);
    }
}
