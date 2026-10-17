using ControlPlane.Api.Chat;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Rpc;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ChatStopTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ChatStore _store;
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    public ChatStopTests(ControlPlaneApiFactory factory)
    {
        _ = factory.CreateClient();
        _store = factory.Services.GetRequiredService<ChatStore>();
    }

    private async Task<(string AgentId, ChatIdentity Identity, string ConversationId)> StartAsync(
        string sid, string text = "office is not activated")
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(sid, "DOMAIN\\stop-user", 7);
        var conversation = await _store.OpenConversationAsync(agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId, text, "ltr", identity),
            TestCancellation);
        return (agentId, identity, conversation.ConversationId);
    }

    private async Task<ChatRunLease> LeaseForAsync(string agentId)
    {
        while (true)
        {
            var lease = await _store.LeaseNextRunAsync(20, TimeSpan.FromMinutes(5), TestCancellation);
            Assert.NotNull(lease);
            if (lease!.AgentId == agentId) return lease;
            await _store.CompleteRunAsync(lease.RunId, "drained", TestCancellation);
        }
    }

    private Task<ChatDelta?> DeltaAsync(string agentId, ChatIdentity identity, string conversationId) =>
        _store.GetDeltaAsync(agentId, new ChatDeltaRequest(conversationId, 0, identity), TestCancellation);

    [Fact]
    public async Task StopsAQueuedRequestBeforeTheAssistantStartsIt()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-910-920-930-1001");

        var receipt = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.True(receipt.Accepted);
        Assert.Equal(ChatRunStopOutcome.Stopped, receipt.Outcome);
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatMessageState.Cancelled,
            Assert.Single(delta!.Messages, m => m.Role == ChatMessageRole.User).State);
        Assert.Contains(delta.Messages, m => m.Role == ChatMessageRole.Assistant
            && m.Body == ChatStore.RunStoppedMessage);
        Assert.Contains(delta.ProgressEvents!, e => e.Stage == ChatProgressStage.Cancelled);

        var again = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);
        Assert.Equal(ChatRunStopOutcome.NothingToStop, again.Outcome);
    }

    [Fact]
    public async Task OnlyTheOwnerCanStopARequest()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-911-921-931-1001");
        var stranger = new ChatIdentity("S-1-5-21-911-921-931-9999", "DOMAIN\\mallory", 9);

        var otherUser = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, stranger), TestCancellation);
        var otherDevice = await _store.StopConversationRunAsync(Guid.NewGuid().ToString("D"),
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.False(otherUser.Accepted);
        Assert.False(otherDevice.Accepted);
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatMessageState.Queued,
            Assert.Single(delta!.Messages, m => m.Role == ChatMessageRole.User).State);
    }

    [Fact]
    public async Task StopsARunningRequestAndEndsItAsCancelled()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-912-922-932-1001");
        var lease = await LeaseForAsync(agentId);
        var handle = _store.Runs.Get(lease.RunId)!;
        Assert.False(handle.Token.IsCancellationRequested);

        var receipt = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.Equal(ChatRunStopOutcome.Stopped, receipt.Outcome);
        Assert.True(handle.Token.IsCancellationRequested);
        // Nothing new may start for a stopped request.
        Assert.Null(await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_remediation_run", "Run approved fix", "reason",
            JsonSerializer.SerializeToElement(new { artifactId = "approved-fix" }),
            TimeSpan.FromMinutes(5), TestCancellation));

        await _store.FinishCancelledRunAsync(lease.RunId, TestCancellation);

        Assert.Null(await _store.ResolveCapabilityAsync(lease.Capability, TestCancellation));
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatMessageState.Cancelled,
            Assert.Single(delta!.Messages, m => m.Role == ChatMessageRole.User).State);
        Assert.Equal(1, delta.Messages.Count(m => m.Body == ChatStore.RunStoppedMessage));
        Assert.Equal(ChatProgressStage.Cancelled, delta.ProgressEvents!.Last().Stage);
    }

    [Fact]
    public async Task ARepairThatAlreadyStartedIsNotInterruptedAndTheRunStopsAfterIt()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-913-923-933-1001");
        var lease = await LeaseForAsync(agentId);
        var handle = _store.Runs.Get(lease.RunId)!;
        Assert.True(handle.TryPin(out var pin));

        var receipt = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.Equal(ChatRunStopOutcome.FinishingOperation, receipt.Outcome);
        Assert.True(handle.StopRequested);
        Assert.False(handle.Token.IsCancellationRequested);
        Assert.False(handle.TryPin(out _));

        pin!.Dispose();
        Assert.True(handle.Token.IsCancellationRequested);
        await _store.FinishCancelledRunAsync(lease.RunId, TestCancellation);
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatMessageState.Cancelled,
            Assert.Single(delta!.Messages, m => m.Role == ChatMessageRole.User).State);
    }

    [Fact]
    public async Task CancelsAProposalThatIsStillWaitingForApproval()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-914-924-934-1001");
        var lease = await LeaseForAsync(agentId);
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_remediation_run", "Run approved fix", "reason",
            JsonSerializer.SerializeToElement(new { artifactId = "approved-fix" }),
            TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(proposal);
        await _store.CompleteRunAsync(lease.RunId, "may I run the fix?", TestCancellation);

        var receipt = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.Equal(ChatRunStopOutcome.Stopped, receipt.Outcome);
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatActionState.Cancelled, Assert.Single(delta!.Actions).State);
        var late = await _store.DecideActionAsync(agentId,
            new ChatActionDecision(conversationId, proposal!.ActionId, true, identity, "yes"),
            TestCancellation);
        Assert.False(late.Accepted);
    }

    [Fact]
    public async Task AnApprovedActionThatIsExecutingIsLeftToFinish()
    {
        var (agentId, identity, conversationId) = await StartAsync("S-1-5-21-915-925-935-1001");
        var lease = await LeaseForAsync(agentId);
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_remediation_run", "Run approved fix", "reason",
            JsonSerializer.SerializeToElement(new { artifactId = "approved-fix" }),
            TimeSpan.FromMinutes(5), TestCancellation);
        await _store.CompleteRunAsync(lease.RunId, "may I run the fix?", TestCancellation);
        var decided = await _store.DecideActionAsync(agentId,
            new ChatActionDecision(conversationId, proposal!.ActionId, true, identity, "yes"),
            TestCancellation);
        Assert.True(decided.Accepted);

        var receipt = await _store.StopConversationRunAsync(agentId,
            new ChatRunStopRequest(conversationId, identity), TestCancellation);

        Assert.Equal(ChatRunStopOutcome.FinishingOperation, receipt.Outcome);
        var delta = await DeltaAsync(agentId, identity, conversationId);
        Assert.Equal(ChatActionState.Approved, Assert.Single(delta!.Actions).State);
    }

    [Theory]
    [InlineData("endpoint.get_info", true)]
    [InlineData("system.windows_activation_status", true)]
    [InlineData("software.office_inventory", true)]
    [InlineData("network.ibsng_diagnose", true)]
    [InlineData("system.package_run", false)]
    [InlineData("software.office_bugfix_run", false)]
    [InlineData("software.office_remediation_run", false)]
    [InlineData("system.windows_activation_bugfix_run", false)]
    [InlineData("network.ibsng_ensure_dialer", false)]
    [InlineData("anything.else", false)]
    public void OnlyReadOnlyChecksMayBeInterrupted(string method, bool interruptible) =>
        Assert.Equal(interruptible, RpcInterruptPolicy.CanInterrupt(method));
}
