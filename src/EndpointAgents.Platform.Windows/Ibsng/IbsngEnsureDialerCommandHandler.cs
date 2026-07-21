using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Diagnostics;
using EndpointAgents.Platform.Windows.UserSessions;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;
using System.Runtime.Versioning;

namespace EndpointAgents.Platform.Windows.Ibsng;

[SupportedOSPlatform("windows")]
public sealed class IbsngEnsureDialerCommandHandler : IEndpointCommandHandler
{
    private readonly ILogger<IbsngEnsureDialerCommandHandler> _logger;
    private readonly InteractiveUserSessionLocator _sessionLocator;
    private readonly IbsngArtifactVerifier _verifier;
    private readonly IbsngDesktopReconciler _desktopReconciler;
    private readonly IbsngCommandCoordinator _coordinator;
    private readonly ITrayCommandClient _trayClient;
    private readonly IbsngAtomicInstaller _installer;
    private readonly IbsngOptions _options;
    private readonly Uri _hubUri;

    public IbsngEnsureDialerCommandHandler(
        ILogger<IbsngEnsureDialerCommandHandler> logger,
        InteractiveUserSessionLocator sessionLocator,
        IbsngArtifactVerifier verifier,
        IbsngDesktopReconciler desktopReconciler,
        IbsngCommandCoordinator coordinator,
        ITrayCommandClient trayClient,
        IbsngAtomicInstaller installer,
        IOptions<IbsngOptions> options,
        Uri hubUri)
    {
        _logger = logger;
        _sessionLocator = sessionLocator;
        _verifier = verifier;
        _desktopReconciler = desktopReconciler;
        _coordinator = coordinator;
        _trayClient = trayClient;
        _installer = installer;
        _options = options.Value;
        _hubUri = hubUri;
    }

    public string Method => "network.ibsng_ensure_dialer";

    public async Task<RpcResponse> HandleAsync(
        RpcRequest request,
        CommandExecutionContext context,
        CancellationToken cancellationToken)
    {
        var session = SelectSession(ParseUserName(request.Params));
        if (session is null)
            return RpcResponse.Failure(request.Id, 404, "NoInteractiveUser");

        if (!Directory.Exists(session.DesktopPath))
            return RpcResponse.Failure(request.Id, 404, "The active user's desktop folder does not exist.");

        var trustedHash = IbsngArtifactVerifier.NormalizeSha256(_options.TrustedSha256);
        if (trustedHash is null)
            return RpcResponse.Failure(request.Id, 412,
                "IBSng delivery is disabled until Diagnostics:Ibsng:TrustedSha256 is configured.");

        using var commandLease = await _coordinator.AcquireAsync(session.DesktopPath,
            cancellationToken);
        var destination = Path.Combine(session.DesktopPath, _options.DialerFileName);
        var processResponse = await ReconcileProcessesAsync(request.Id, session.SessionId,
            destination, trustedHash, cancellationToken);
        if (!processResponse.Success)
            return ProcessFailure(request.Id, processResponse, session);

        var partialPath = Path.Combine(session.DesktopPath,
            $".{_options.DialerFileName}.{Guid.NewGuid():N}.download");
        try
        {
            var reconciliation = await _desktopReconciler.ReconcileAsync(session.DesktopPath,
                _options, cancellationToken);
            foreach (var quarantinedPath in reconciliation.QuarantinedPaths)
                _logger.LogWarning("Quarantined duplicate IBSng dialer at {Destination}.",
                    quarantinedPath);

            if (reconciliation.CanonicalArtifact.IsTrusted)
                return Success(request.Id, context, session, "AlreadyPresentApproved",
                    reconciliation.CanonicalArtifact, reconciliation.QuarantinedPaths,
                    processResponse.StoppedProcessIds);

            var artifactUri = new Uri(_hubUri, _options.ArtifactApiPath);
            using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
            using var response = await client.GetAsync(artifactUri,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
                return RpcResponse.Failure(request.Id, 502,
                    "The control-plane IBSng artifact could not be downloaded.",
                    new { ArtifactUrl = artifactUri.ToString(), StatusCode = (int)response.StatusCode });

            var advertisedHash = response.Headers.TryGetValues("X-Artifact-Sha256", out var values)
                ? IbsngArtifactVerifier.NormalizeSha256(values.FirstOrDefault())
                : null;
            if (advertisedHash is not null && advertisedHash != trustedHash)
                return RpcResponse.Failure(request.Id, 412,
                    "The control plane advertised an unapproved IBSng artifact hash.");

            await DownloadAsync(response, partialPath, cancellationToken);
            var downloaded = await _verifier.InspectAsync(partialPath, trustedHash, cancellationToken);
            if (!downloaded.IsTrusted)
                return RpcResponse.Failure(request.Id, 412,
                    "The downloaded IBSng dialer is not the approved executable.", downloaded);

            _installer.Install(partialPath, destination);
            var installed = await _verifier.InspectAsync(destination, trustedHash, cancellationToken);
            var state = reconciliation.QuarantinedPaths.Count == 0 ? "Installed" : "Repaired";
            return Success(request.Id, context, session, state, installed,
                reconciliation.QuarantinedPaths, processResponse.StoppedProcessIds);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to ensure the IBSng dialer for {UserName}.", session.UserName);
            return RpcResponse.Failure(request.Id, 500,
                "The IBSng dialer could not be delivered.", DiagnosticResponse.Sanitize(ex));
        }
        finally
        {
            try
            {
                File.Delete(partialPath);
            }
            catch
            {
                // Best-effort cleanup of a partial download.
            }
        }
    }

    private async Task DownloadAsync(HttpResponseMessage response, string destination,
        CancellationToken cancellationToken)
    {
        if (response.Content.Headers.ContentLength > _options.MaxDownloadBytes)
            throw new InvalidDataException("The IBSng dialer exceeds the configured download limit.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[64 * 1024];
        long totalBytes = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;

            totalBytes += read;
            if (totalBytes > _options.MaxDownloadBytes)
                throw new InvalidDataException("The IBSng dialer exceeds the configured download limit.");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
    }

    private InteractiveUserSession? SelectSession(string? requestedUser)
    {
        var sessions = _sessionLocator.GetActiveSessions();
        if (string.IsNullOrWhiteSpace(requestedUser))
            return sessions.FirstOrDefault();
        return sessions.FirstOrDefault(session =>
            session.UserName.Equals(requestedUser, StringComparison.OrdinalIgnoreCase)
            || $"{session.DomainName}\\{session.UserName}".Equals(requestedUser,
                StringComparison.OrdinalIgnoreCase));
    }

    private static RpcResponse Success(string requestId, CommandExecutionContext context,
        InteractiveUserSession session, string state, IbsngArtifactInspection inspection,
        IReadOnlyList<string> quarantinedPaths, IReadOnlyList<int>? stoppedProcessIds) =>
        RpcResponse.Success(requestId, DiagnosticResponse.Create(context, new
        {
            State = state,
            session.SessionId,
            session.UserName,
            session.DesktopPath,
            DialerPath = inspection.Path,
            inspection.Sha256,
            QuarantinedPaths = quarantinedPaths,
            StoppedProcessIds = stoppedProcessIds ?? Array.Empty<int>(),
            Downloaded = state is "Installed" or "Repaired",
            Executed = false
        }, Array.Empty<DiagnosticWarning>()));

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

    private static string? ParseUserName(JsonElement? parameters) =>
        parameters is { ValueKind: JsonValueKind.Object } element
        && element.TryGetProperty("userName", out var user)
        && user.ValueKind == JsonValueKind.String
            ? user.GetString()
            : null;
}
