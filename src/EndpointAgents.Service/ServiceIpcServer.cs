using EndpointAgents.Core.Commands;
using EndpointAgents.Platform.Windows.Ibsng;
using EndpointAgents.Shared.Ibsng;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.LocalIpc;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using Microsoft.Extensions.Options;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace EndpointAgents.Service;

public sealed class ServiceIpcServer(
    AgentRuntimeState runtime,
    AgentWorker worker,
    CommandDispatcher dispatcher,
    IbsngDiagnosticService ibsngDiagnostics,
    IOptions<IbsngOptions> ibsngOptions,
    TrayChannelRegistry trays,
    EndpointAgents.Shared.Problems.ProblemOutbox problems,
    ILogger<ServiceIpcServer> logger) : BackgroundService
{
    // Each signed-in user keeps one Tray connection open, so leave room for several sessions
    // plus the short request/response calls.
    private const int MaxConnections = 32;

    private readonly object _submissionGate = new();
    private DateTimeOffset _lastSubmissionUtc = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var clients = new HashSet<Task>();
        while (!stoppingToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                clients.RemoveWhere(task => task.IsCompleted);
                if (clients.Count >= MaxConnections)
                {
                    var completed = await Task.WhenAny(clients);
                    clients.Remove(completed);
                    await completed;
                }

                pipe = CreatePipe();
                await pipe.WaitForConnectionAsync(stoppingToken);
                clients.Add(HandleClientAsync(pipe, stoppingToken));
                pipe = null;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "The local Tray status pipe failed. Retrying.");
                await Task.Delay(500, stoppingToken);
            }
            finally
            {
                if (pipe is not null) await pipe.DisposeAsync();
            }
        }
        await Task.WhenAll(clients);
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        await using (pipe)
        {
            try
            {
                await HandleConnectionAsync(pipe, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "A local Tray operation ended unexpectedly.");
            }
        }
    }

    private static NamedPipeServerStream CreatePipe()
    {
        var security = new PipeSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.FullControl, AccessControlType.Allow));

        return NamedPipeServerStreamAcl.Create(ServiceIpcProtocol.PipeName,
            PipeDirection.InOut, MaxConnections, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
            16 * 1024, 16 * 1024, security);
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        using var writer = new StreamWriter(pipe, Encoding.UTF8, leaveOpen: true) { AutoFlush = true };
        ServiceIpcResponse response;
        ServiceIpcRequest? request = null;
        try
        {
            var line = await ReadBoundedLineAsync(reader, cancellationToken);
            request = JsonSerializer.Deserialize<ServiceIpcRequest>(line)
                ?? throw new InvalidDataException("The local IPC request was empty.");
            if (request.Method == ServiceIpcProtocol.AttachTrayMethod)
            {
                await AttachTrayAsync(pipe, request, writer, cancellationToken);
                return;
            }
            if (ShouldReport(request.Method))
                logger.LogInformation(
                    "Received local Tray operation {Method} with request id {RequestId}.",
                    request.Method, request.Id);
            response = await DispatchAsync(pipe, request, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Rejected an invalid local Tray request.");
            response = new ServiceIpcResponse(string.Empty, false, "InvalidRequest",
                Message: "The local request was invalid.");
        }

        if (request is not null && ShouldReport(request.Method))
        {
            if (response.Success)
                logger.LogInformation(
                    "Completed local Tray operation {Method} with request id {RequestId}: {State}.",
                    request.Method, request.Id, response.State);
            else
                logger.LogWarning(
                    "Local Tray operation {Method} with request id {RequestId} failed: {State}.",
                    request.Method, request.Id, response.State);
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(response).AsMemory(), cancellationToken);
    }

    /// <summary>
    /// Keeps this connection as the Tray channel of the caller's session until it disconnects.
    /// </summary>
    private async Task AttachTrayAsync(NamedPipeServerStream pipe, ServiceIpcRequest request,
        StreamWriter writer, CancellationToken cancellationToken)
    {
        TrayAttachPolicy.Caller caller;
        string? refusal;
        try
        {
            caller = TrayAttachPolicy.Identify(pipe);
            refusal = TrayAttachPolicy.Check(caller);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not identify a Tray that tried to attach: {Detail}", ex.ToString());
            caller = new TrayAttachPolicy.Caller(-1, string.Empty, string.Empty);
            refusal = "The Tray could not be identified.";
        }

        if (refusal is not null)
        {
            logger.LogWarning("Refused a Tray attach from session {SessionId} ({UserName}): {Reason}",
                caller.SessionId, caller.UserName, refusal);
            await writer.WriteLineAsync(JsonSerializer.Serialize(new ServiceIpcResponse(
                request.Id, false, "TrayRefused", Message: refusal)).AsMemory(), cancellationToken);
            return;
        }

        await writer.WriteLineAsync(JsonSerializer.Serialize(new ServiceIpcResponse(
            request.Id, true, "Attached")).AsMemory(), cancellationToken);
        // Nothing else is in flight until the service sends a task, so a fresh reader on the same
        // stream cannot miss buffered input.
        await using var channel = new TrayChannel(pipe, caller.SessionId, caller.UserSid,
            caller.UserName);
        trays.Register(channel);
        logger.LogInformation("Tray attached for session {SessionId} ({UserName}).",
            caller.SessionId, caller.UserName);
        try
        {
            await channel.RunAsync(cancellationToken);
        }
        finally
        {
            trays.Unregister(channel);
            logger.LogInformation("Tray detached from session {SessionId}.", caller.SessionId);
        }
    }

    private async Task<ServiceIpcResponse> DispatchAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        switch (request.Method)
        {
            case ServiceIpcProtocol.StatusMethod:
                return Success(request.Id, "Status", runtime.GetSnapshot());

            case ServiceIpcProtocol.PauseMethod:
                if (!IsCallerAdministrator(pipe)) return AccessDenied(request.Id);
                runtime.SetPaused(true);
                logger.LogWarning("Endpoint functions were paused by local administrator {UserName}.",
                    GetCallerName(pipe));
                return Success(request.Id, "Paused", runtime.GetSnapshot());

            case ServiceIpcProtocol.ResumeMethod:
                if (!IsCallerAdministrator(pipe)) return AccessDenied(request.Id);
                runtime.SetPaused(false);
                logger.LogInformation("Endpoint functions were resumed by local administrator {UserName}.",
                    GetCallerName(pipe));
                return Success(request.Id, "Active", runtime.GetSnapshot());

            case ServiceIpcProtocol.SubmitSupportMethod:
                return await SubmitSupportAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.OpenChatMethod:
                return await OpenChatAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.ListChatsMethod:
                return await ListChatsAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.SendChatMessageMethod:
                return await SendChatMessageAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.GetChatDeltaMethod:
                return await GetChatDeltaAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.CloseChatMethod:
                return await CloseChatAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.ChatNotFixedMethod:
                return await ChatNotFixedAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.ReportProblemsMethod:
                return ReportTrayProblems(request);

            case ServiceIpcProtocol.StopChatRunMethod:
                return await StopChatRunAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.RespondToChatActionMethod:
                return await RespondToChatActionAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.DiagnoseIbsngMethod:
                return await DiagnoseIbsngAsync(pipe, request, cancellationToken);

            case ServiceIpcProtocol.OpenIbsngMethod:
                return await OpenIbsngAsync(request, cancellationToken);

            default:
                return new ServiceIpcResponse(request.Id, false, "UnknownMethod",
                    Message: "The requested local operation is not supported.");
        }
    }

    private async Task<ServiceIpcResponse> OpenChatAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var supplied = request.Payload?.Deserialize<ChatIdentity>();
        var identity = GetCallerIdentity(pipe, supplied?.SessionId ?? 0);
        var conversation = await worker.OpenChatAsync(identity, cancellationToken);
        return Success(request.Id, "Opened", conversation);
    }

    private async Task<ServiceIpcResponse> ListChatsAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var supplied = request.Payload?.Deserialize<ChatConversationListRequest>()
            ?? throw new InvalidDataException("The chat list request is missing.");
        var normalized = supplied with
        {
            Identity = GetCallerIdentity(pipe, supplied.Identity.SessionId),
            Limit = Math.Clamp(supplied.Limit, 1, 50)
        };
        return Success(request.Id, "Listed",
            await worker.ListChatsAsync(normalized, cancellationToken));
    }

    private async Task<ServiceIpcResponse> SendChatMessageAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var submission = request.Payload?.Deserialize<ChatMessageSubmission>()
            ?? throw new InvalidDataException("The chat message body is missing.");
        if (submission.MessageText.Length > ChatLimits.MessageCharacters)
            return new ServiceIpcResponse(request.Id, false, "ChatRejected",
                Message: $"Chat messages are limited to {ChatLimits.MessageCharacters} characters.");
        var normalized = submission with
        {
            Identity = GetCallerIdentity(pipe, submission.Identity.SessionId)
        };
        var receipt = await worker.SendChatMessageAsync(normalized, cancellationToken);
        return receipt.Accepted
            ? Success(request.Id, "Queued", receipt)
            : new ServiceIpcResponse(request.Id, false, "ChatRejected",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    private async Task<ServiceIpcResponse> GetChatDeltaAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var delta = request.Payload?.Deserialize<ChatDeltaRequest>()
            ?? throw new InvalidDataException("The chat synchronization body is missing.");
        var normalized = delta with
        {
            Identity = GetCallerIdentity(pipe, delta.Identity.SessionId)
        };
        return Success(request.Id, "Synchronized",
            await worker.GetChatDeltaAsync(normalized, cancellationToken));
    }

    private async Task<ServiceIpcResponse> RespondToChatActionAsync(
        NamedPipeServerStream pipe, ServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        var decision = request.Payload?.Deserialize<ChatActionDecision>()
            ?? throw new InvalidDataException("The action decision body is missing.");
        var normalized = decision with
        {
            Identity = GetCallerIdentity(pipe, decision.Identity.SessionId)
        };
        var receipt = await worker.RespondToChatActionAsync(normalized, cancellationToken);
        if (receipt.Accepted)
            logger.LogInformation("User {Decision} support action {ActionId}; state is {State}.",
                decision.Approved ? "approved" : "rejected", decision.ActionId,
                receipt.Proposal?.State.ToString() ?? "Unknown");
        else
            logger.LogWarning("Support action decision {ActionId} was not accepted: {State}.",
                decision.ActionId, receipt.Proposal?.State.ToString() ?? "Unknown");
        return Success(request.Id, "Decided", receipt);
    }

    private async Task<ServiceIpcResponse> StopChatRunAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var stop = request.Payload?.Deserialize<ChatRunStopRequest>()
            ?? throw new InvalidDataException("The chat stop body is missing.");
        var normalized = stop with
        {
            Identity = GetCallerIdentity(pipe, stop.Identity.SessionId)
        };
        var receipt = await worker.StopChatRunAsync(normalized, cancellationToken);
        logger.LogInformation("User asked to stop the current chat request; outcome is {Outcome}.",
            receipt.Outcome);
        return receipt.Accepted
            ? Success(request.Id, "Stopped", receipt)
            : new ServiceIpcResponse(request.Id, false, "ChatStopRejected",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    private async Task<ServiceIpcResponse> ChatNotFixedAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var body = request.Payload?.Deserialize<EndpointAgents.Shared.Problems.ChatNotFixedRequest>()
            ?? throw new InvalidDataException("The request body is missing.");
        var receipt = await worker.ReportChatNotFixedAsync(body with
        {
            Identity = GetCallerIdentity(pipe, body.Identity.SessionId)
        }, cancellationToken);
        return receipt.Accepted
            ? Success(request.Id, "Reported", receipt)
            : new ServiceIpcResponse(request.Id, false, "NotFixedRejected",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    /// <summary>
    /// Crashes the Tray saved for itself. A Tray runs as the user, so nothing it says is taken as
    /// it is: the kind and source are fixed here and the text is cleaned again.
    /// </summary>
    private ServiceIpcResponse ReportTrayProblems(ServiceIpcRequest request)
    {
        var reports = request.Payload?.Deserialize<IReadOnlyList<EndpointAgents.Shared.Problems.ProblemReport>>() ?? [];
        var now = DateTimeOffset.UtcNow;
        foreach (var report in reports.Take(10))
            problems.Add(report with
            {
                Kind = EndpointAgents.Shared.Problems.ProblemKinds.IsFromEndpoint(report.Kind)
                    ? report.Kind : EndpointAgents.Shared.Problems.ProblemKinds.LoggedError,
                Source = "Tray",
                Fingerprint = EndpointAgents.Shared.Problems.ProblemText.Limit(report.Fingerprint, 64),
                Title = EndpointAgents.Shared.Problems.ProblemText.Limit(
                    EndpointAgents.Shared.Problems.ProblemText.Scrub(report.Title),
                    EndpointAgents.Shared.Problems.ProblemLimits.MaxTitleCharacters),
                Stack = EndpointAgents.Shared.Problems.ProblemText.ScrubStack(report.Stack),
                Count = Math.Clamp(report.Count, 1, 100_000),
                FirstSeenUtc = report.FirstSeenUtc > now ? now : report.FirstSeenUtc,
                LastSeenUtc = report.LastSeenUtc > now ? now : report.LastSeenUtc
            });
        return Success(request.Id, "Saved", new { saved = reports.Count });
    }

    private async Task<ServiceIpcResponse> CloseChatAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        var close = request.Payload?.Deserialize<ChatConversationCloseRequest>()
            ?? throw new InvalidDataException("The chat close body is missing.");
        var normalized = close with
        {
            Identity = GetCallerIdentity(pipe, close.Identity.SessionId)
        };
        var receipt = await worker.CloseChatAsync(normalized, cancellationToken);
        return receipt.Accepted
            ? Success(request.Id, "Closed", receipt)
            : new ServiceIpcResponse(request.Id, false, "ChatCloseRejected",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    private async Task<ServiceIpcResponse> DiagnoseIbsngAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        if (!IbsngDiagnoseCommandHandler.TryParse(request.Payload,
            ibsngOptions.Value.ServerHost, out var diagnosticRequest, out var error))
            return new ServiceIpcResponse(request.Id, false, "InvalidIbsngRequest",
                Message: error);

        var caller = GetCallerIdentity(pipe, sessionId: 0);
        var scopedRequest = diagnosticRequest! with { UserSid = caller.UserSid };
        var report = await ibsngDiagnostics.DiagnoseAsync(request.Id, scopedRequest,
            cancellationToken);
        return Success(request.Id, "Diagnosed", report);
    }

    private async Task<ServiceIpcResponse> OpenIbsngAsync(ServiceIpcRequest request,
        CancellationToken cancellationToken)
    {
        var snapshot = runtime.GetSnapshot();
        var rpc = RpcRequest.Create(request.Id, "network.ibsng_launch");
        var context = new CommandExecutionContext(snapshot.AgentId, "Local Tray", null,
            DateTimeOffset.UtcNow);
        var response = await dispatcher.DispatchAsync(rpc, context, cancellationToken);
        return response.Error is null
            ? Success(request.Id, "Opened", response.Result)
            : new ServiceIpcResponse(request.Id, false, "OpenFailed",
                response.Error.Data is null ? null : JsonSerializer.SerializeToElement(response.Error.Data),
                response.Error.Message);
    }

    private async Task<ServiceIpcResponse> SubmitSupportAsync(NamedPipeServerStream pipe,
        ServiceIpcRequest request, CancellationToken cancellationToken)
    {
        if (request.Payload is not { } payload)
            return new ServiceIpcResponse(request.Id, false, "InvalidSupportRequest",
                Message: "The support request body is missing.");

        var draft = payload.Deserialize<SupportRequestDraft>();
        if (draft is null || string.IsNullOrWhiteSpace(draft.Subject)
            || string.IsNullOrWhiteSpace(draft.MessageText))
            return new ServiceIpcResponse(request.Id, false, "InvalidSupportRequest",
                Message: "Subject and problem description are required.");
        if (draft.Subject.Length > 120 || draft.MessageText.Length > 8_000)
            return new ServiceIpcResponse(request.Id, false, "SupportRequestTooLarge",
                Message: "The support request exceeds the allowed size.");

        lock (_submissionGate)
        {
            if (DateTimeOffset.UtcNow - _lastSubmissionUtc < TimeSpan.FromSeconds(5))
                return new ServiceIpcResponse(request.Id, false, "RateLimited",
                    Message: "Wait a few seconds before sending another support request.");
            _lastSubmissionUtc = DateTimeOffset.UtcNow;
        }

        var normalized = draft with
        {
            Subject = Normalize(draft.Subject, 120),
            MessageText = Normalize(draft.MessageText, 8_000),
            TextDirection = draft.TextDirection is "rtl" or "ltr" ? draft.TextDirection : "auto",
            LogExcerpts = draft.LogExcerpts.Take(20).Select(item => item with
            {
                Message = Normalize(item.Message, 2_000)
            }).ToArray(),
            IbsngDiagnostics = NormalizeEvidence(draft.IbsngDiagnostics)
        };

        var receipt = await worker.SubmitSupportRequestAsync(normalized, GetCallerName(pipe),
            cancellationToken);
        return receipt.Accepted
            ? Success(request.Id, "Submitted", receipt)
            : new ServiceIpcResponse(request.Id, false, "SupportUnavailable",
                JsonSerializer.SerializeToElement(receipt), receipt.Message);
    }

    private static ServiceIpcResponse Success<T>(string id, string state, T payload) =>
        new(id, true, state, JsonSerializer.SerializeToElement(payload));

    private static ServiceIpcResponse AccessDenied(string id) =>
        new(id, false, "AdministratorRequired",
            Message: "Administrator approval is required for this action.");

    private static bool ShouldReport(string method) => method is not
        (ServiceIpcProtocol.StatusMethod or ServiceIpcProtocol.GetChatDeltaMethod
            or ServiceIpcProtocol.ListChatsMethod);

    private static string Normalize(string value, int maximum) =>
        new string(value.Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(maximum).ToArray()).Trim();

    private static IbsngSupportEvidence? NormalizeEvidence(IbsngSupportEvidence? evidence)
    {
        if (evidence is null) return null;
        return evidence with
        {
            ServerHost = Normalize(evidence.ServerHost, 253),
            Summary = Normalize(evidence.Summary, 500),
            Checks = evidence.Checks.Take(20).Select(check => check with
            {
                Name = Normalize(check.Name, 100),
                Status = Normalize(check.Status, 32),
                Summary = Normalize(check.Summary, 500),
                Detail = check.Detail is null ? null : Normalize(check.Detail, 500)
            }).ToArray(),
            SanitizedLogLines = evidence.SanitizedLogLines.TakeLast(20)
                .Select(line => Normalize(line, 500)).ToArray()
        };
    }

    private static string GetCallerName(NamedPipeServerStream pipe)
    {
        var name = "Local user";
        pipe.RunAsClient(() => name = WindowsIdentity.GetCurrent().Name);
        return name;
    }

    private static ChatIdentity GetCallerIdentity(NamedPipeServerStream pipe, int sessionId)
    {
        ChatIdentity? caller = null;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value
                ?? throw new UnauthorizedAccessException(
                    "The named-pipe caller has no Windows user SID.");
            caller = new ChatIdentity(sid, identity.Name, sessionId);
        });
        return caller ?? throw new UnauthorizedAccessException(
            "The named-pipe caller identity could not be determined.");
    }

    private static bool IsCallerAdministrator(NamedPipeServerStream pipe)
    {
        var isAdministrator = false;
        pipe.RunAsClient(() =>
        {
            using var identity = WindowsIdentity.GetCurrent();
            isAdministrator = new WindowsPrincipal(identity)
                .IsInRole(WindowsBuiltInRole.Administrator);
        });
        return isAdministrator;
    }

    private static async Task<string> ReadBoundedLineAsync(StreamReader reader,
        CancellationToken cancellationToken)
    {
        var buffer = new char[1];
        var value = new StringBuilder();
        while (value.Length <= ServiceIpcProtocol.MaxMessageCharacters)
        {
            var read = await reader.ReadAsync(buffer, cancellationToken);
            if (read == 0 || buffer[0] == '\n') return value.ToString().TrimEnd('\r');
            value.Append(buffer[0]);
        }
        throw new InvalidDataException("The local IPC request exceeded the allowed size.");
    }
}
