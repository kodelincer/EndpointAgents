using ControlPlane.Api.Agents;
using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Security;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using EndpointAgents.Shared.Support;
using EndpointAgents.Shared.Ibsng;
using ControlPlane.Api.Support;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace ControlPlane.Api.Hubs;

/// <summary>SignalR endpoint used exclusively by installed endpoint agents.</summary>
public sealed class AgentHub(AgentRegistry registry, SupportRequestPublisher support,
    ChatStore chatStore, CommandCatalog commandCatalog,
    IOptions<AgentSecurityOptions> security, ILogger<AgentHub> logger) : Hub
{
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
        if (!string.IsNullOrWhiteSpace(authenticatedAgentId))
            registration = registration with { AgentId = authenticatedAgentId };

        await registry.MarkOnlineAsync(registration, Context.ConnectionId);
        logger.LogInformation("Agent {AgentId} registered with connection {ConnectionId}",
            registration.AgentId, Context.ConnectionId);
    }

    public Task<ChatConversation> OpenChat(ChatIdentity identity)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        return chatStore.OpenConversationAsync(agentId, Normalize(identity),
            Context.ConnectionAborted);
    }

    public Task<ChatMessageReceipt> SendChatMessage(ChatMessageSubmission submission)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var normalized = submission with
        {
            ClientMessageId = Normalize(submission.ClientMessageId, 100),
            MessageText = Normalize(submission.MessageText, 8_000),
            TextDirection = submission.TextDirection is "rtl" or "ltr"
                ? submission.TextDirection
                : "auto",
            Identity = Normalize(submission.Identity)
        };
        return chatStore.SubmitMessageAsync(agentId, normalized, Context.ConnectionAborted);
    }

    public async Task<ChatDelta> GetChatDelta(ChatDeltaRequest request)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var delta = await chatStore.GetDeltaAsync(agentId,
            request with { Identity = Normalize(request.Identity) },
            Context.ConnectionAborted);
        return delta ?? throw new HubException(
            "The conversation does not belong to this endpoint user.");
    }

    public async Task<ChatActionDecisionReceipt> RespondToChatAction(
        ChatActionDecision decision)
    {
        var agentId = GetAuthenticatedAgentId();
        EnsureRegisteredAgent(agentId);
        var result = await chatStore.DecideActionAsync(agentId,
            decision with { Identity = Normalize(decision.Identity) },
            Context.ConnectionAborted);
        if (!result.Accepted || result.Proposal is null || !decision.Approved)
            return new ChatActionDecisionReceipt(result.Accepted, result.Proposal,
                result.Message);

        var proposal = result.Proposal;
        if (!commandCatalog.Contains(proposal.Method)
            || SensitiveCommandParameterGuard.ContainsSensitiveData(proposal.Parameters))
        {
            await chatStore.MarkActionExecutedAsync(proposal.ActionId, false,
                "The stored action is no longer allow-listed.", Context.ConnectionAborted);
            return new ChatActionDecisionReceipt(false,
                proposal with { State = ChatActionState.Failed },
                "The stored action is no longer allow-listed.");
        }

        var rpcParameters = AddUserScope(proposal.Parameters, decision.Identity.UserSid);
        var rpc = RpcRequest.Create(proposal.ActionId, proposal.Method, rpcParameters);
        var response = await registry.SendCommandAsync(agentId, rpc, Context.ConnectionAborted);
        await chatStore.MarkActionExecutedAsync(proposal.ActionId, response.Error is null,
            response.Error?.Message, Context.ConnectionAborted);
        return new ChatActionDecisionReceipt(response.Error is null,
            proposal with
            {
                State = response.Error is null
                    ? ChatActionState.Executed
                    : ChatActionState.Failed
            }, response.Error?.Message ?? "The approved endpoint action completed.");
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

    private static JsonElement AddUserScope(JsonElement? parameters, string userSid)
    {
        var scoped = new Dictionary<string, object?>
        {
            ["userSid"] = userSid
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
