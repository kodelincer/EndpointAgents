using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Dashboard;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using EndpointAgents.Shared.Ibsng;
using ControlPlane.Api.Problems;
using ControlPlane.Api.Support;
using ControlPlane.Api.Updates;
using EndpointAgents.Shared.Problems;
using EndpointAgents.Shared.Updates;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ControlPlane.Api.Hubs;

/// <summary>SignalR endpoint used exclusively by installed endpoint agents.</summary>
public sealed class AgentHub(AgentRegistry registry, SupportRequestPublisher support,
    ChatStore chatStore, CommandCatalog commandCatalog,
    DashboardPublisher dashboard, IOptions<AgentSecurityOptions> security,
    AgentUpdateStore updateStore, AgentUpdateRepository updateRepository,
    ProblemRecorder problems,
    ILogger<AgentHub> logger) : Hub
{
    private static readonly HashSet<string> ChatActions =
        new(StringComparer.Ordinal)
        {
            "software.office_remediation_run",
            "software.office_bugfix_run",
            "system.windows_hello_sample_run"
        };

    /// <summary>Associates the current SignalR connection with a durable agent identity.</summary>
    public async Task RegisterAgent(AgentRegistration registration)
    {
        var authenticatedAgentId = Context.User?.FindFirst(
            AgentAuthentication.AgentIdClaim)?.Value;
        if (security.Value.RequireAuthentication
            && string.IsNullOrWhiteSpace(authenticatedAgentId))
            throw new HubException("Endpoint device authentication is required.");
        if (!string.IsNullOrWhiteSpace(authenticatedAgentId)
            && !await chatStore.IsDeviceActiveAsync(
                authenticatedAgentId, Context.ConnectionAborted))
            throw new HubException("The endpoint device token was revoked.");
        if (registration is null)
            throw new HubException("The registration is invalid.");
        // Everything in a registration is typed by the PC; keep it short and printable before
        // it reaches the dashboard, the logs and the database.
        registration = registration with
        {
            AgentId = Normalize(registration.AgentId, 64),
            Hostname = Normalize(registration.Hostname, 255),
            MachineName = Normalize(registration.MachineName, 255),
            UserName = Normalize(registration.UserName, 256),
            Version = Normalize(registration.Version, 40)
        };
        if (!string.IsNullOrWhiteSpace(authenticatedAgentId))
            registration = registration with { AgentId = authenticatedAgentId };
        else if (string.IsNullOrWhiteSpace(registration.AgentId))
            throw new HubException("The registration has no agent id.");
        // A connection without a device token may not call itself an enrolled PC: it would
        // receive that PC's commands, and a revoked PC could simply come back without its token.
        else if (await chatStore.IsDeviceEnrolledAsync(registration.AgentId, Context.ConnectionAborted))
            throw new HubException("This device is enrolled; its device token is required.");

        await registry.MarkOnlineAsync(registration, Context.ConnectionId);
        if (!string.IsNullOrWhiteSpace(registration.Version))
            await updateStore.RecordVersionAsync(registration.AgentId, registration.Version,
                isCheck: false, Context.ConnectionAborted);
        logger.LogInformation("Agent {AgentId} registered with connection {ConnectionId}",
            registration.AgentId, Context.ConnectionId);
    }

    /// <summary>
    /// The endpoint asks whether it should install a newer agent. The answer comes only from the
    /// admin's settings and the signed versions in the file repository.
    /// </summary>
    public async Task<AgentUpdateOffer?> CheckForAgentUpdate(AgentUpdateCheck check)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        if (check is null || string.IsNullOrWhiteSpace(check.CurrentVersion)
            || check.CurrentVersion.Length > 40)
            throw new HubException("The update check is invalid.");
        var cancellationToken = Context.ConnectionAborted;
        await updateStore.RecordVersionAsync(agentId, check.CurrentVersion, isCheck: true, cancellationToken);
        var settings = await updateStore.GetSettingsAsync(cancellationToken);
        if (settings.Paused || (settings.TargetVersion is null && settings.PilotVersion is null))
            return null;
        var decision = AgentUpdatePlanner.Decide(agentId,
            check with { FailedVersions = check.FailedVersions ?? [] }, settings,
            await updateRepository.GetAllAsync(cancellationToken),
            await updateStore.IsInConversationAsync(agentId, TimeSpan.FromMinutes(10), cancellationToken),
            DateTimeOffset.Now);
        logger.LogInformation("Agent update check from {AgentId} on {Version}: {Reason}",
            agentId, check.CurrentVersion, decision.Reason);
        return decision.Offer;
    }

    /// <summary>The endpoint reports how an update went (downloading, installed, rolled back...).</summary>
    public async Task ReportAgentUpdate(AgentUpdateReport report)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        if (report is null || !AgentUpdateStatuses.IsKnown(report.Status))
            throw new HubException("The update report is invalid.");
        await updateStore.RecordReportAsync(agentId, report.Status, report.FromVersion ?? "",
            report.ToVersion ?? "", report.Message ?? "", Context.ConnectionAborted);
        logger.LogInformation("Agent {AgentId} update {From} -> {To}: {Status} {Message}",
            agentId, report.FromVersion, report.ToVersion, report.Status, report.Message);
        if (report.Status is AgentUpdateStatuses.RolledBack or AgentUpdateStatuses.Failed)
        {
            registry.TryGetByConnectionId(Context.ConnectionId, out var agent);
            await problems.RecordAsync(agentId, agent?.MachineName, report.ToVersion, ProblemKinds.UpdateRolledBack,
                "Updater", $"Update {report.FromVersion} -> {report.ToVersion}: {report.Status}. {report.Message}",
                $"{report.Status}:{report.ToVersion}");
        }
    }

    /// <summary>
    /// Problem reports from this PC, and every so often an empty call that only says it is alive.
    /// Returns normally when the PC can forget them, also when some were over a limit.
    /// </summary>
    public async Task<int> ReportProblems(ProblemReportBatch batch)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        if (batch?.Device is null || batch.Reports is null || batch.Reports.Count > ProblemLimits.MaxReportsPerHour)
            throw new HubException("The problem report is invalid.");
        registry.TryGetByConnectionId(Context.ConnectionId, out var agent);
        return await problems.AddFromAgentAsync(agentId,
            batch.Device with { MachineName = agent?.MachineName ?? batch.Device.MachineName },
            batch.Reports, Context.ConnectionAborted);
    }

    /// <summary>The user says this chat did not fix the problem. Carries no chat text.</summary>
    public async Task<ChatNotFixedReceipt> ReportChatNotFixed(ChatNotFixedRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var conversationId = Normalize(request.ConversationId, 64);
        if (!await chatStore.EscalateForUserAsync(agentId, request.Identity.UserSid, conversationId,
                "The user pressed: this did not fix it.", Context.ConnectionAborted))
            return new ChatNotFixedReceipt(false, "این گفتگو پیدا نشد.");
        registry.TryGetByConnectionId(Context.ConnectionId, out var agent);
        await problems.RecordAsync(agentId, agent?.MachineName, agent?.Version, ProblemKinds.UserReported, "Chat",
            "The user said this chat did not fix the problem.", conversationId, conversationId);
        await dashboard.PublishHelpdeskAsync(new HelpdeskChange(conversationId, "escalated"), Context.ConnectionAborted);
        return new ChatNotFixedReceipt(true, "به تیم پشتیبانی خبر دادیم.");
    }

    public Task<ChatConversation> OpenChat(ChatIdentity identity)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        return chatStore.OpenConversationAsync(agentId, Normalize(identity),
            Context.ConnectionAborted);
    }

    public Task<IReadOnlyList<ChatConversationSummary>> ListChats(
        ChatConversationListRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        return chatStore.ListConversationsAsync(agentId,
            request with { Identity = Normalize(request.Identity) },
            Context.ConnectionAborted);
    }

    public async Task<ChatMessageReceipt> SendChatMessage(ChatMessageSubmission submission)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var normalized = submission with
        {
            ClientMessageId = Normalize(submission.ClientMessageId, 100),
            MessageText = Normalize(submission.MessageText, ChatLimits.MessageCharacters + 1),
            TextDirection = submission.TextDirection is "rtl" or "ltr"
                ? submission.TextDirection
                : "auto",
            Identity = Normalize(submission.Identity)
        };
        var receipt = await chatStore.SubmitMessageAsync(agentId, normalized,
            Context.ConnectionAborted);
        if (receipt.Accepted)
            await dashboard.PublishHelpdeskAsync(new HelpdeskChange(
                receipt.ConversationId, "message", receipt.Sequence), Context.ConnectionAborted);
        return receipt;
    }

    public async Task<ChatDelta> GetChatDelta(ChatDeltaRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var delta = await chatStore.GetDeltaAsync(agentId,
            request with { Identity = Normalize(request.Identity) },
            Context.ConnectionAborted);
        return delta ?? throw new HubException(
            "این گفتگو متعلق به کاربر فعلی نیست.");
    }

    public async Task<ChatConversationCloseReceipt> CloseChat(
        ChatConversationCloseRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var receipt = await chatStore.CloseConversationAsync(agentId,
            request with { Identity = Normalize(request.Identity) },
            Context.ConnectionAborted);
        if (receipt.Accepted)
            await dashboard.PublishHelpdeskAsync(new HelpdeskChange(
                request.ConversationId, "closed"), Context.ConnectionAborted);
        return receipt;
    }

    public async Task<ChatRunStopReceipt> StopChatRun(ChatRunStopRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var receipt = await chatStore.StopConversationRunAsync(agentId,
            request with { Identity = Normalize(request.Identity) },
            Context.ConnectionAborted);
        if (receipt.Accepted)
            await dashboard.PublishHelpdeskAsync(new HelpdeskChange(
                request.ConversationId, "stopped"), Context.ConnectionAborted);
        return receipt;
    }

    public async Task<ChatActionDecisionReceipt> RespondToChatAction(
        ChatActionDecision decision)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        if (decision.ResponseText is { } responseText
            && (!ChatActionReply.TryParse(responseText, out var approved)
                || approved != decision.Approved))
            return new ChatActionDecisionReceipt(false, null,
                "Reply with an exact yes/no response to decide this action.");
        var result = await chatStore.DecideActionAsync(agentId,
            decision with { Identity = Normalize(decision.Identity) },
            Context.ConnectionAborted);
        if (!result.Accepted || result.Proposal is null)
            return new ChatActionDecisionReceipt(result.Accepted, result.Proposal,
                result.Message, result.DecisionMessage);

        var proposal = result.Proposal;
        if (!decision.Approved)
        {
            await chatStore.WriteActionProgressAsync(proposal.ActionId,
                ChatProgressStage.ActionRejected, ChatProgressOutcome.None,
                Context.ConnectionAborted);
            return new ChatActionDecisionReceipt(true, proposal,
                result.Message, result.DecisionMessage);
        }
        if (!ChatActions.Contains(proposal.Method)
            || !commandCatalog.Contains(proposal.Method)
            || SensitiveCommandParameterGuard.ContainsSensitiveData(proposal.Parameters))
        {
            await chatStore.MarkActionExecutedAsync(proposal.ActionId, false,
                "The stored action is no longer allow-listed.", Context.ConnectionAborted);
            await chatStore.WriteActionProgressAsync(proposal.ActionId,
                ChatProgressStage.ActionCompleted, ChatProgressOutcome.Failure,
                Context.ConnectionAborted);
            return new ChatActionDecisionReceipt(false,
                proposal with { State = ChatActionState.Failed },
                "این اقدام دیگر مجاز نیست.", result.DecisionMessage);
        }

        var rpcParameters = AddUserScope(proposal.Parameters, decision.Identity.UserSid,
            proposal.ActionId);
        var rpc = RpcRequest.Create(proposal.ActionId, proposal.Method, rpcParameters);
        await chatStore.WriteActionProgressAsync(proposal.ActionId,
            ChatProgressStage.ActionStarted, ChatProgressOutcome.None,
            Context.ConnectionAborted);
        RpcResponse response;
        try
        {
            response = await registry.SendCommandAsync(agentId, rpc,
                Context.ConnectionAborted);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            await chatStore.MarkActionExecutedAsync(proposal.ActionId, false,
                ex.Message, Context.ConnectionAborted);
            await chatStore.WriteActionProgressAsync(proposal.ActionId,
                ChatProgressStage.ActionCompleted, ChatProgressOutcome.Failure,
                Context.ConnectionAborted);
            logger.LogWarning(ex, "Approved support action {ActionId} failed to dispatch.",
                proposal.ActionId);
            return new ChatActionDecisionReceipt(false,
                proposal with { State = ChatActionState.Failed },
                "انجام اقدام ناموفق بود. لطفاً با پشتیبانی تماس بگیرید.",
                result.DecisionMessage);
        }
        await chatStore.MarkActionExecutedAsync(proposal.ActionId, response.Error is null,
            response.Error?.Message, Context.ConnectionAborted);
        await chatStore.WriteActionProgressAsync(proposal.ActionId,
            ChatProgressStage.ActionCompleted,
            response.Error is null
                ? ChatProgressOutcome.Success
                : ChatProgressOutcome.Failure,
            Context.ConnectionAborted);
        return new ChatActionDecisionReceipt(response.Error is null,
            proposal with
            {
                State = response.Error is null
                    ? ChatActionState.Executed
                    : ChatActionState.Failed
            }, response.Error is null
                ? "اقدام تأییدشده انجام شد."
                : "انجام اقدام ناموفق بود. لطفاً با پشتیبانی تماس بگیرید.",
            result.DecisionMessage);
    }

    public async Task<SupportRequestReceipt> SubmitSupportRequest(
        SupportRequestSubmission submission)
    {
        if (!registry.TryGetByConnectionId(Context.ConnectionId, out var agent) || agent is null)
            return new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
                "Register the endpoint agent before submitting a helpdesk request.");

        var subject = Normalize(submission.Subject, 120);
        var message = Normalize(submission.MessageText, 8_000);
        if (string.IsNullOrWhiteSpace(subject) || string.IsNullOrWhiteSpace(message))
            return new SupportRequestReceipt(string.Empty, DateTimeOffset.UtcNow, false,
                "Subject and problem description are required.");

        var now = DateTimeOffset.UtcNow;
        var runtime = submission.Runtime with
        {
            AgentId = agent.AgentId,
            MachineName = agent.MachineName,
            Version = agent.Version
        };
        var request = new SupportRequest(Guid.NewGuid().ToString("N"),
            Normalize(submission.ClientRequestId, 100), now, subject, message,
            submission.TextDirection is "rtl" or "ltr" ? submission.TextDirection : "auto",
            agent.AgentId, agent.MachineName, Normalize(submission.UserName, 256),
            submission.SessionId, runtime, submission.LogExcerpts.Take(20).Select(item => item with
            {
                Message = Normalize(item.Message, 2_000)
            }).ToArray(), NormalizeEvidence(submission.IbsngDiagnostics));

        var receipt = await support.PublishAsync(request, Context.ConnectionAborted);
        if (receipt.Accepted)
            logger.LogInformation("Received helpdesk request {RequestId} from agent {AgentId}.",
                request.RequestId, request.AgentId);
        return receipt;
    }

    /// <summary>Removes the disconnected transport without disturbing a newer reconnect.</summary>
    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        await registry.MarkOfflineAsync(Context.ConnectionId);
        logger.LogInformation(exception, "Agent connection disconnected: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    private static string Normalize(string? value, int maximum) =>
        new string((value ?? string.Empty)
            .Where(character => character is '\r' or '\n' or '\t' || !char.IsControl(character))
            .Take(maximum).ToArray()).Trim();

    private static ChatIdentity Normalize(ChatIdentity identity) =>
        identity with { UserName = Normalize(identity.UserName, 256) };

    private static JsonElement AddUserScope(JsonElement? parameters, string userSid,
        string chatApprovalId)
    {
        var scoped = new Dictionary<string, object?>
        {
            ["userSid"] = userSid,
            [ChatActionApproval.ParameterName] = chatApprovalId
        };
        if (parameters is { ValueKind: JsonValueKind.Object } value)
            foreach (var property in value.EnumerateObject())
                scoped[property.Name] = property.Value.Clone();
        return JsonSerializer.SerializeToElement(scoped);
    }

    private string GetAuthenticatedAgentId() =>
        Context.User?.FindFirst(AgentAuthentication.AgentIdClaim)?.Value
        ?? throw new HubException(
            "Enroll this endpoint before opening an isolated support conversation.");

    private void EnsureRegisteredAgent(string agentId)
    {
        if (!registry.TryGetByConnectionId(Context.ConnectionId, out var agent)
            || agent?.AgentId != agentId)
            throw new HubException("Register the authenticated endpoint before using chat.");
    }

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
}
