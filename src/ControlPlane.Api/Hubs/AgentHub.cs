using ControlPlane.Api.Agents;
using EndpointAgents.Shared.Agents;
using EndpointAgents.Shared.Support;
using EndpointAgents.Shared.Ibsng;
using ControlPlane.Api.Support;
using Microsoft.AspNetCore.SignalR;

namespace ControlPlane.Api.Hubs;

/// <summary>SignalR endpoint used exclusively by installed endpoint agents.</summary>
public sealed class AgentHub(AgentRegistry registry, SupportRequestPublisher support,
    ILogger<AgentHub> logger) : Hub
{
    /// <summary>Associates the current SignalR connection with a durable agent identity.</summary>
    public async Task RegisterAgent(AgentRegistration registration)
    {
        await registry.MarkOnlineAsync(registration, Context.ConnectionId);
        logger.LogInformation("Agent {AgentId} registered with connection {ConnectionId}",
            registration.AgentId, Context.ConnectionId);
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
