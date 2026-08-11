using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Platform.Windows.Office;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Windows;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Windows;

[SupportedOSPlatform("windows")]
public sealed class WindowsHelloSampleCommandHandler(
    WindowsArtifactClient artifacts,
    IOfficeUserSessionSource sessions,
    ITrayCommandClient tray,
    ILogger<WindowsHelloSampleCommandHandler> logger) : IEndpointCommandHandler
{
    public string Method => "system.windows_hello_sample_run";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        if (!TryParse(request.Params, out var userSid, out var chatApproved))
            return RpcResponse.Failure(request.Id, 400,
                "Only the approved windows-hello-sample artifactId is accepted.");
        if (chatApproved && !ChatActionApproval.MatchesRequest(
                request.Params, request.Id))
            return RpcResponse.Failure(request.Id, 400,
                "The chat approval does not match this action.");
        var session = sessions.GetActiveSession(userSid);
        if (session is null)
            return RpcResponse.Failure(request.Id, 404,
                "No interactive Windows user is available for the Hello demo.");

        try
        {
            var descriptor = await artifacts.GetArtifactAsync(
                WindowsHelloSamplePaths.ArtifactId, cancellationToken);
            if (descriptor is null)
                return RpcResponse.Failure(request.Id, 404,
                    "The approved Windows Hello demo is not available.");
            if (chatApproved && !ChatActionApproval.MatchesArtifact(request.Params,
                    descriptor.FileName, descriptor.Sha256))
                return RpcResponse.Failure(request.Id, 412,
                    "The approved Windows demo revision has changed; approve the new proposal before running it.");
            var staged = await artifacts.EnsureAsync(descriptor, cancellationToken);
            var desktopPath = await DesktopArtifactFile.CopyForSessionAsync(
                staged.Path, session, WindowsHelloSamplePaths.FileName,
                descriptor.Sha256, cancellationToken);
            var launch = new WindowsHelloLaunchRequest(descriptor.ArtifactId,
                desktopPath, descriptor.Sha256, chatApproved);
            var trayRequest = new TrayIpcRequest(request.Id,
                TrayIpcProtocol.WindowsHelloLaunchMethod, string.Empty, string.Empty,
                null, WindowsHello: launch);
            var trayResponse = await tray.SendAsync(session.SessionId, trayRequest,
                cancellationToken);
            if (!trayResponse.Success)
                return RpcResponse.Failure(request.Id,
                    trayResponse.State == "TrayUnavailable" ? 503 : 409,
                    trayResponse.State, new { trayResponse.Message });
            return RpcResponse.Success(request.Id, new
            {
                State = trayResponse.State,
                descriptor.ArtifactId,
                DesktopPath = desktopPath,
                staged.Downloaded,
                trayResponse.ProcessId
            });
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "The Windows Hello demo could not be prepared.");
            return RpcResponse.Failure(request.Id, 500,
                "The approved Windows Hello demo could not be prepared.");
        }
    }

    internal static bool TryParse(JsonElement? parameters, out string? userSid,
        out bool chatApproved)
    {
        userSid = null;
        chatApproved = ChatActionApproval.TryRead(parameters, out _);
        if (parameters is not { ValueKind: JsonValueKind.Object } value)
            return false;
        foreach (var property in value.EnumerateObject())
        {
            if (property.NameEquals("artifactId"))
            {
                if (property.Value.ValueKind != JsonValueKind.String
                    || property.Value.GetString() != WindowsHelloSamplePaths.ArtifactId)
                    return false;
            }
            else if (property.NameEquals("userSid"))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
                userSid = property.Value.GetString();
            }
            else if (property.NameEquals(ChatActionApproval.ParameterName))
            {
                if (!chatApproved) return false;
            }
            else if (ChatActionApproval.IsArtifactPin(property.Name))
            {
                if (property.Value.ValueKind != JsonValueKind.String) return false;
            }
            else return false;
        }
        return value.TryGetProperty("artifactId", out _)
            && OfficeRemediationCommandHandler.TryParseUserSid(parameters,
                out userSid);
    }

}
