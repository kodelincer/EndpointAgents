using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Options;
using System.Runtime.Versioning;
using System.Text.Json;

namespace EndpointAgents.Platform.Windows.Ibsng;

[SupportedOSPlatform("windows")]
public sealed class IbsngLaunchCommandHandler : IEndpointCommandHandler
{
    private readonly InteractiveUserSessionLocator _sessionLocator;
    private readonly IbsngDesktopReconciler _desktopReconciler;
    private readonly IbsngCommandCoordinator _coordinator;
    private readonly ITrayCommandClient _trayClient;
    private readonly IbsngOptions _options;

    public IbsngLaunchCommandHandler(
        InteractiveUserSessionLocator sessionLocator,
        IbsngDesktopReconciler desktopReconciler,
        IbsngCommandCoordinator coordinator,
        ITrayCommandClient trayClient,
        IOptions<IbsngOptions> options)
    {
        _sessionLocator = sessionLocator;
        _desktopReconciler = desktopReconciler;
        _coordinator = coordinator;
        _trayClient = trayClient;
        _options = options.Value;
    }

    public string Method => "network.ibsng_launch";

    public async Task<RpcResponse> HandleAsync(RpcRequest request,
        CommandExecutionContext context, CancellationToken cancellationToken)
    {
        var session = SelectSession(
            ParseUserSid(request.Params), ParseUserName(request.Params));
        if (session is null)
            return RpcResponse.Failure(request.Id, 404, "NoInteractiveUser");

        var trustedHash = IbsngArtifactVerifier.NormalizeSha256(_options.TrustedSha256);
        if (trustedHash is null)
            return RpcResponse.Failure(request.Id, 412,
                "IBSng launch is disabled until Diagnostics:Ibsng:TrustedSha256 is configured.");

        using var commandLease = await _coordinator.AcquireAsync(session.DesktopPath,
            cancellationToken);
        var path = Path.Combine(session.DesktopPath, _options.DialerFileName);
        var processResponse = await ReconcileProcessesAsync(request.Id, session.SessionId,
            path, trustedHash, cancellationToken);
        if (!processResponse.Success)
            return ProcessFailure(request.Id, processResponse, session);

        var reconciliation = await _desktopReconciler.ReconcileAsync(session.DesktopPath,
            _options, cancellationToken);
        var inspection = reconciliation.CanonicalArtifact;
        if (!inspection.Exists)
            return RpcResponse.Failure(request.Id, 412, "DialerMissing", new { path });
        if (!inspection.IsTrusted)
            return RpcResponse.Failure(request.Id, 412, "DialerUntrusted", inspection);

        var trayRequest = new TrayIpcRequest(request.Id, TrayIpcProtocol.LaunchIbsngMethod,
            path, trustedHash, _options.ServerHost, _options.DialerSearchPattern);
        var trayResponse = await _trayClient.SendAsync(session.SessionId, trayRequest,
            cancellationToken);

        if (!trayResponse.Success)
        {
            var code = trayResponse.State == "TrayUnavailable" ? 503 : 500;
            return RpcResponse.Failure(request.Id, code, trayResponse.State,
                new { trayResponse.Message, session.SessionId, session.UserName });
        }

        return RpcResponse.Success(request.Id, DiagnosticResponse.Create(context, new
        {
            trayResponse.State,
            trayResponse.ProcessId,
            QuarantinedPaths = reconciliation.QuarantinedPaths,
            StoppedProcessIds = (processResponse.StoppedProcessIds ?? Array.Empty<int>())
                .Concat(trayResponse.StoppedProcessIds ?? Array.Empty<int>())
                .Distinct()
                .ToArray(),
            session.SessionId,
            session.UserName,
            session.DesktopPath,
            DialerPath = path,
            ServerHost = _options.ServerHost,
            CredentialsCollected = false,
            Instruction = "Enter your username and password directly in the IBSng dialer, then click Connect."
        }, Array.Empty<DiagnosticWarning>()));
    }

    private async Task<TrayIpcResponse> ReconcileProcessesAsync(string requestId, int sessionId,
        string dialerPath, string trustedHash, CancellationToken cancellationToken)
    {
        var trayRequest = new TrayIpcRequest(requestId, TrayIpcProtocol.ReconcileIbsngMethod,
            dialerPath, trustedHash, _options.ServerHost, _options.DialerSearchPattern);
        return await _trayClient.SendAsync(sessionId, trayRequest, cancellationToken);
    }

    private static RpcResponse ProcessFailure(string requestId, TrayIpcResponse response,
        InteractiveUserSession session)
    {
        var code = response.State == "TrayUnavailable" ? 503 : 409;
        return RpcResponse.Failure(requestId, code, response.State, new
        {
            response.Message,
            response.StoppedProcessIds,
            session.SessionId,
            session.UserName
        });
    }

    private InteractiveUserSession? SelectSession(string? requestedSid, string? requestedUser)
    {
        var sessions = _sessionLocator.GetActiveSessions();
        if (!string.IsNullOrWhiteSpace(requestedSid))
            return sessions.FirstOrDefault(session => session.UserSid.Equals(
                requestedSid, StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(requestedUser))
            return sessions.FirstOrDefault();
        return sessions.FirstOrDefault(session =>
            session.UserName.Equals(requestedUser, StringComparison.OrdinalIgnoreCase)
            || $"{session.DomainName}\\{session.UserName}".Equals(requestedUser,
                StringComparison.OrdinalIgnoreCase));
    }

    private static string? ParseUserName(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("userName", out var user)
        && user.ValueKind == JsonValueKind.String
            ? user.GetString()
            : null;

    private static string? ParseUserSid(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("userSid", out var sid)
        && sid.ValueKind == JsonValueKind.String
            ? sid.GetString()
            : null;
}
