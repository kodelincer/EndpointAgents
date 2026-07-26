using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Chat;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ChatStoreTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ChatStore _store;
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    public ChatStoreTests(ControlPlaneApiFactory factory)
    {
        _ = factory.CreateClient();
        _store = factory.Services.GetRequiredService<ChatStore>();
    }

    [Fact]
    public async Task IsolatesEndpointsAndUsersAndDeduplicatesClientMessages()
    {
        var agentA = Guid.NewGuid().ToString("D");
        var agentB = Guid.NewGuid().ToString("D");
        var userA = new ChatIdentity("S-1-5-21-100-200-300-1001", "DOMAIN\\alice", 1);
        var userB = new ChatIdentity("S-1-5-21-100-200-300-1002", "DOMAIN\\bob", 2);
        var conversationA = await _store.OpenConversationAsync(
            agentA, userA, TestCancellation);
        var conversationB = await _store.OpenConversationAsync(
            agentB, userA, TestCancellation);
        var conversationSecondUser = await _store.OpenConversationAsync(
            agentA, userB, TestCancellation);

        Assert.NotEqual(conversationA.ConversationId, conversationB.ConversationId);
        Assert.NotEqual(conversationA.ConversationId,
            conversationSecondUser.ConversationId);

        var submission = new ChatMessageSubmission("same-client-id",
            conversationA.ConversationId, "same prompt", "ltr", userA);
        var first = await _store.SubmitMessageAsync(agentA, submission, TestCancellation);
        var duplicate = await _store.SubmitMessageAsync(agentA, submission, TestCancellation);
        Assert.Equal(first.MessageId, duplicate.MessageId);
        Assert.Equal(first.Sequence, duplicate.Sequence);

        var ownerDelta = await _store.GetDeltaAsync(agentA,
            new ChatDeltaRequest(conversationA.ConversationId, 0, userA),
            TestCancellation);
        var endpointForgery = await _store.GetDeltaAsync(agentB,
            new ChatDeltaRequest(conversationA.ConversationId, 0, userA),
            TestCancellation);
        var userForgery = await _store.GetDeltaAsync(agentA,
            new ChatDeltaRequest(conversationA.ConversationId, 0, userB),
            TestCancellation);

        Assert.Single(ownerDelta!.Messages);
        Assert.Null(endpointForgery);
        Assert.Null(userForgery);
    }

    [Fact]
    public async Task RunCapabilitiesExpireAndActionsAreSingleUse()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-400-500-600-1001", "DOMAIN\\operator", 3);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "diagnose Office", "ltr", identity), TestCancellation);

        ChatRunLease? lease;
        do
        {
            lease = await _store.LeaseNextRunAsync(
                20, TimeSpan.FromMinutes(5), TestCancellation);
            Assert.NotNull(lease);
            if (lease!.AgentId != agentId)
                await _store.CompleteRunAsync(lease.RunId, "drained", TestCancellation);
        } while (lease.AgentId != agentId);
        Assert.NotNull(lease);
        Assert.Equal(agentId, (await _store.ResolveCapabilityAsync(
            lease!.Capability, TestCancellation))!.AgentId);

        var parameters = JsonSerializer.SerializeToElement(
            new { artifactId = "approved-fix" });
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_remediation_run", "Run approved fix",
            "Activation diagnosis found a compatible approved fix.", parameters,
            TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(proposal);

        var decision = new ChatActionDecision(conversation.ConversationId,
            proposal!.ActionId, true, identity);
        var wrongEndpoint = await _store.DecideActionAsync(
            Guid.NewGuid().ToString("D"), decision, TestCancellation);
        var wrongUser = await _store.DecideActionAsync(
            agentId, decision with
            {
                Identity = new ChatIdentity(
                    "S-1-5-21-400-500-600-9999", "DOMAIN\\mallory", 9)
            }, TestCancellation);
        var firstDecision = await _store.DecideActionAsync(
            agentId, decision, TestCancellation);
        var reusedDecision = await _store.DecideActionAsync(
            agentId, decision, TestCancellation);
        Assert.False(wrongEndpoint.Accepted);
        Assert.False(wrongUser.Accepted);
        Assert.True(firstDecision.Accepted);
        Assert.False(reusedDecision.Accepted);

        await _store.CompleteRunAsync(lease.RunId, "done", TestCancellation);
        Assert.Null(await _store.ResolveCapabilityAsync(
            lease.Capability, TestCancellation));
    }

    [Fact]
    public async Task RequeueInvalidatesCapabilityAndPreservesTheQueuedTurn()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-700-800-900-1001", "DOMAIN\\restart-user", 4);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        var receipt = await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "check after restart", "ltr", identity), TestCancellation);
        var queuedDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, receipt.Sequence, identity),
            TestCancellation);
        Assert.Equal(ChatMessageState.Queued, Assert.Single(queuedDelta!.Messages).State);

        ChatRunLease lease;
        while (true)
        {
            var candidate = await _store.LeaseNextRunAsync(
                20, TimeSpan.FromMinutes(5), TestCancellation);
            Assert.NotNull(candidate);
            lease = candidate!;
            if (lease.AgentId == agentId) break;
            await _store.CompleteRunAsync(lease.RunId, "drained", TestCancellation);
        }
        var workingDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, receipt.Sequence, identity),
            TestCancellation);
        Assert.Equal(ChatMessageState.Working,
            Assert.Single(workingDelta!.Messages).State);

        await _store.RequeueRunAsync(lease.RunId, TestCancellation);
        Assert.Null(await _store.ResolveCapabilityAsync(
            lease.Capability, TestCancellation));

        var recovered = await _store.LeaseNextRunAsync(
            20, TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(recovered);
        Assert.Equal(lease.RunId, recovered!.RunId);
        Assert.NotEqual(lease.Capability, recovered.Capability);
        await _store.CompleteRunAsync(recovered.RunId, "recovered", TestCancellation);
    }
}
