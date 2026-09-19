using ControlPlane.Api.Chat;
using ControlPlane.Api.Commands;
using ControlPlane.Api.Files;
using ControlPlane.Api.Tools;
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Office;
using EndpointAgents.Shared.Rpc;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Security.Cryptography;
using System.Text.Json;

namespace ControlPlane.Api.Tests;

public sealed class ChatStoreTests : IClassFixture<ControlPlaneApiFactory>
{
    private readonly ChatStore _store;
    private readonly FileArtifactRepository _files;
    private readonly string _databasePath;
    private static CancellationToken TestCancellation =>
        TestContext.Current.CancellationToken;

    public ChatStoreTests(ControlPlaneApiFactory factory)
    {
        _ = factory.CreateClient();
        _store = factory.Services.GetRequiredService<ChatStore>();
        _files = factory.Services.GetRequiredService<FileArtifactRepository>();
        _databasePath = factory.DatabasePath;
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
        Assert.DoesNotMatch("[A-Za-z]", first.Message);
        Assert.DoesNotMatch("[A-Za-z]", duplicate.Message);
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
        Assert.Single(ownerDelta.ProgressEvents!);
        Assert.Null(endpointForgery);
        Assert.Null(userForgery);
    }

    [Fact]
    public async Task DeliversSafeOrderedProgressWithoutAddingItToModelHistory()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-110-220-330-1001", "DOMAIN\\progress-user", 5);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        var receipt = await _store.SubmitMessageAsync(agentId,
            new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
                conversation.ConversationId, "check Office activation", "ltr", identity),
            TestCancellation);

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
        Assert.Single(lease.History);

        await _store.WriteProgressAsync(lease.RunId,
            ChatProgressStage.WaitingForModel, ChatProgressActivity.None,
            ChatProgressOutcome.None, TestCancellation);
        await _store.WriteProgressAsync(lease.RunId,
            ChatProgressStage.EndpointCheckStarted,
            ChatProgressActivity.OfficeActivationStatus,
            ChatProgressOutcome.None, TestCancellation);
        await _store.WriteProgressAsync(lease.RunId,
            ChatProgressStage.EndpointCheckCompleted,
            ChatProgressActivity.OfficeActivationStatus,
            ChatProgressOutcome.Success, TestCancellation);
        await _store.CompleteRunAsync(lease.RunId, "done", TestCancellation);

        var delta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, receipt.Sequence, identity),
            TestCancellation);
        Assert.NotNull(delta);
        var progressEvents = Assert.IsAssignableFrom<IReadOnlyList<ChatRunProgressEvent>>(
            delta!.ProgressEvents);
        Assert.Equal(
            [
                ChatProgressStage.Queued,
                ChatProgressStage.AssistantStarted,
                ChatProgressStage.WaitingForModel,
                ChatProgressStage.EndpointCheckStarted,
                ChatProgressStage.EndpointCheckCompleted,
                ChatProgressStage.Completed
            ],
            progressEvents.Select(item => item.Stage));
        Assert.True(progressEvents.Select(item => item.Sequence)
            .SequenceEqual(progressEvents.Select(item => item.Sequence).Order()));
        var assistant = Assert.Single(delta.Messages,
            message => message.Role == ChatMessageRole.Assistant);
        Assert.True(progressEvents[^1].Sequence < assistant.Sequence);
        Assert.Equal(lease.RunId, assistant.RunId);
    }

    [Fact]
    public async Task DeltaBoundsVisibleHistoryAndKeepsPendingActionsAvailable()
    {
        var testRoot = Path.Combine(Path.GetTempPath(),
            $"EndpointAgents-chat-window-{Guid.NewGuid():N}");
        Directory.CreateDirectory(testRoot);
        try
        {
            var store = new ChatStore(Options.Create(new ChatOptions
            {
                DatabasePath = Path.Combine(testRoot, "chat.db"),
                MaxVisibleItems = 3
            }));
            await store.InitializeAsync(TestCancellation);
            var agentId = Guid.NewGuid().ToString("D");
            var identity = new ChatIdentity(
                "S-1-5-21-123-456-789-1001", "DOMAIN\\window-user", 21);
            var conversation = await store.OpenConversationAsync(
                agentId, identity, TestCancellation);
            ChatActionProposal? pendingAction = null;

            for (var turn = 1; turn <= 5; turn++)
            {
                await store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
                    Guid.NewGuid().ToString("N"), conversation.ConversationId,
                    $"user-{turn}", "ltr", identity), TestCancellation);
                var lease = Assert.IsType<ChatRunLease>(await store.LeaseNextRunAsync(
                    10, TimeSpan.FromMinutes(5), TestCancellation));
                await store.WriteProgressAsync(lease.RunId,
                    ChatProgressStage.WaitingForModel, ChatProgressActivity.None,
                    ChatProgressOutcome.None, TestCancellation);
                if (turn == 1)
                    pendingAction = await store.CreateActionProposalAsync(lease.Capability,
                        "system.windows_activation_bugfix_run", "Pending action",
                        "Pending action remains actionable", parameters: null,
                        TimeSpan.FromMinutes(30), TestCancellation);
                await store.CompleteRunAsync(lease.RunId,
                    $"assistant-{turn}", TestCancellation);
            }

            var delta = Assert.IsType<ChatDelta>(await store.GetDeltaAsync(agentId,
                new ChatDeltaRequest(conversation.ConversationId, 0, identity),
                TestCancellation));
            Assert.Equal(3, delta.VisibleItemLimit);
            Assert.Equal(["assistant-4", "user-5", "assistant-5"],
                delta.Messages.Select(message => message.Body));
            var progressEvents = Assert.IsAssignableFrom<
                IReadOnlyList<ChatRunProgressEvent>>(delta.ProgressEvents);
            Assert.Equal(3, progressEvents.Select(item => item.RunId).Distinct().Count());
            Assert.True(delta.Messages.Select(item => item.Sequence).SequenceEqual(
                delta.Messages.Select(item => item.Sequence).Order()));
            Assert.True(progressEvents.Select(item => item.Sequence).SequenceEqual(
                progressEvents.Select(item => item.Sequence).Order()));

            var pendingOnly = Assert.IsType<ChatDelta>(await store.GetDeltaAsync(agentId,
                new ChatDeltaRequest(conversation.ConversationId, long.MaxValue, identity),
                TestCancellation));
            Assert.Equal(pendingAction!.ActionId, Assert.Single(pendingOnly.Actions).ActionId);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(testRoot)) Directory.Delete(testRoot, recursive: true);
        }
    }

    [Fact]
    public async Task HermesHistoryContainsCurrentRequestAndFourPreviousMessages()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-123-456-789-1002", "DOMAIN\\history-user", 22);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);

        for (var turn = 1; turn <= 3; turn++)
        {
            await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
                Guid.NewGuid().ToString("N"), conversation.ConversationId,
                $"user-{turn}", "ltr", identity), TestCancellation);
            var completed = await LeaseRunForAgentAsync(agentId);
            await _store.CompleteRunAsync(completed.RunId,
                $"assistant-{turn}", TestCancellation);
        }
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "current-request", "ltr", identity), TestCancellation);

        var lease = await LeaseRunForAgentAsync(agentId, historyLimit: 5);
        Assert.Equal(
            ["user-2", "assistant-2", "user-3", "assistant-3", "current-request"],
            lease.History.Select(message => message.Body));
        Assert.Equal(ChatMessageRole.User, lease.History[^1].Role);
        await _store.FailRunAsync(lease.RunId, "expected test cleanup", TestCancellation);
    }

    [Fact]
    public void DeserializesLegacyDeltaWithoutProgressEvents()
    {
        var conversation = new ChatConversation("conversation", "agent", "S-1-0-0",
            "user", 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 0);
        var json = JsonSerializer.Serialize(new
        {
            Conversation = conversation,
            Messages = Array.Empty<ChatMessage>(),
            Actions = Array.Empty<ChatActionProposal>()
        });

        var delta = JsonSerializer.Deserialize<ChatDelta>(json);

        Assert.NotNull(delta);
        Assert.Null(delta.ProgressEvents);
        Assert.Equal(30, delta.VisibleItemLimit);
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

        var rejectedIbsng = await EndpointSupportTools.ProposeEndpointAction(
            _store, new CommandCatalog(), _files,
            lease.Capability, "network.ibsng_launch",
            "اجرای برنامه اتصال", new EndpointActionParameters("ignored"),
            TestCancellation);
        Assert.Contains("not allow-listed", rejectedIbsng, StringComparison.Ordinal);

        var rejectedWindowsProposal = await EndpointSupportTools.ProposeEndpointAction(
            _store, new CommandCatalog(), _files, lease.Capability,
            "system.windows_activation_bugfix_run", "Repair Windows activation",
            new EndpointActionParameters("windows-activation-repair"), TestCancellation);
        Assert.Contains("not allow-listed", rejectedWindowsProposal,
            StringComparison.Ordinal);

        var parameters = JsonSerializer.SerializeToElement(
            new { artifactId = "approved-fix" });
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_remediation_run", "Run approved fix",
            "Activation diagnosis found a compatible approved fix.", parameters,
            TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(proposal);
        Assert.True(await _store.HasActionProposalAsync(lease.RunId,
            "software.office_remediation_run", TestCancellation));
        Assert.False(await _store.HasActionProposalAsync(lease.RunId,
            "software.office_bugfix_run", TestCancellation));

        var decision = new ChatActionDecision(conversation.ConversationId,
            proposal!.ActionId, true, identity, "yes");
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
        Assert.Equal("yes", firstDecision.DecisionMessage?.Body);
        Assert.False(reusedDecision.Accepted);
        Assert.DoesNotMatch("[A-Za-z]", firstDecision.Message);
        Assert.DoesNotMatch("[A-Za-z]", reusedDecision.Message);

        await _store.CompleteRunAsync(lease.RunId, "done", TestCancellation);
        await _store.WriteActionProgressAsync(proposal.ActionId,
            ChatProgressStage.ActionStarted, ChatProgressOutcome.None,
            TestCancellation);
        await _store.WriteActionProgressAsync(proposal.ActionId,
            ChatProgressStage.ActionCompleted, ChatProgressOutcome.Success,
            TestCancellation);
        var actionDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, 0, identity),
            TestCancellation);
        Assert.Equal(
            [ChatProgressStage.ActionStarted, ChatProgressStage.ActionCompleted],
            actionDelta!.ProgressEvents!.TakeLast(2).Select(item => item.Stage));
        Assert.Null(await _store.ResolveCapabilityAsync(
            lease.Capability, TestCancellation));
    }

    [Fact]
    public async Task ProposedArtifactActionPinsTheApprovedCatalogRevision()
    {
        const string artifactId = "pin-test";
        const string fileName = "pin-test.bat";
        var bytes = "@echo off\r\nexit /b 0\r\n"u8.ToArray();
        var sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        var playbook = new BatchPlaybook(1,
        [
            new BatchPlaybookStep("Select an item", "1", "line", 30),
            new BatchPlaybookStep("Continue", "Y", "key", 30)
        ]);
        Directory.CreateDirectory(_files.RootPath);
        await File.WriteAllBytesAsync(Path.Combine(_files.RootPath, fileName), bytes,
            TestCancellation);
        await using (var stream = File.Create(_files.OfficeBugfixManifestPath))
            await JsonSerializer.SerializeAsync(stream, new OfficeBugfixManifest
            {
                Version = 1,
                Artifacts =
                [
                    new OfficeBugfixManifestEntry
                    {
                        ArtifactId = artifactId,
                        DisplayName = "Pinned Office repair",
                        Description = "Approved test repair",
                        FileName = fileName,
                        Sha256 = sha256,
                        MinOfficeVersion = "16.0",
                        MaxOfficeVersionExclusive = "17.0",
                        Playbook = playbook
                    }
                ]
            }, cancellationToken: TestCancellation);

        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity("S-1-5-21-111-222-333-7002",
            "DOMAIN\\pin-user", 14);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "Office activation failed", "ltr", identity), TestCancellation);
        var lease = await LeaseRunForAgentAsync(agentId);

        var result = await EndpointSupportTools.ProposeEndpointAction(
            _store, new CommandCatalog(), _files, lease.Capability,
            "software.office_bugfix_run", "Run the approved repair",
            new EndpointActionParameters(artifactId), TestCancellation);

        Assert.Contains("\"ok\":true", result, StringComparison.Ordinal);
        var delta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, 0, identity),
            TestCancellation);
        var proposal = Assert.Single(delta!.Actions);
        Assert.Equal(fileName, proposal.Parameters!.Value.GetProperty(
            ChatActionApproval.ArtifactFileNameParameterName).GetString());
        Assert.Equal(sha256, proposal.Parameters.Value.GetProperty(
            ChatActionApproval.ArtifactSha256ParameterName).GetString());
        Assert.Equal(BatchPlaybookPolicy.ComputeSha256(playbook),
            proposal.Parameters.Value.GetProperty(
                ChatActionApproval.PlaybookSha256ParameterName).GetString());

        await _store.CompleteRunAsync(lease.RunId, "ready", TestCancellation);
        await _store.DecideActionAsync(agentId, new ChatActionDecision(
            conversation.ConversationId, proposal.ActionId, false, identity, "no"),
            TestCancellation);
    }

    [Fact]
    public async Task RejectsChatMessagesLongerThanTwoHundredCharacters()
    {
        var identity = new ChatIdentity("S-1-5-21-111-222-333-1001",
            "DOMAIN\\limit", 8);
        var receipt = await _store.SubmitMessageAsync(Guid.NewGuid().ToString("D"),
            new ChatMessageSubmission(Guid.NewGuid().ToString("N"), null,
                new string('x', ChatLimits.MessageCharacters + 1), "ltr", identity),
            TestCancellation);

        Assert.False(receipt.Accepted);
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
        var retryDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, receipt.Sequence, identity),
            TestCancellation);
        Assert.Contains(retryDelta!.ProgressEvents!,
            item => item.Stage == ChatProgressStage.Retrying);

        var recovered = await _store.LeaseNextRunAsync(
            20, TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(recovered);
        Assert.Equal(lease.RunId, recovered!.RunId);
        Assert.NotEqual(lease.Capability, recovered.Capability);
        await _store.CompleteRunAsync(recovered.RunId, "recovered", TestCancellation);
    }

    [Fact]
    public async Task MissingOfficeActionGetsExactlyOneCorrectiveRetry()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-910-920-930-1001", "DOMAIN\\retry-user", 10);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "Office activation failed", "ltr", identity), TestCancellation);

        ChatRunLease first;
        while (true)
        {
            first = (await _store.LeaseNextRunAsync(20, TimeSpan.FromMinutes(5),
                TestCancellation))!;
            Assert.NotNull(first);
            if (first.AgentId == agentId) break;
            await _store.CompleteRunAsync(first.RunId, "drained", TestCancellation);
        }
        Assert.Equal(1, first.Attempt);
        Assert.True(await _store.TryRequeueMissingActionAsync(first.RunId,
            TestCancellation));

        var second = await _store.LeaseNextRunAsync(
            20, TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(second);
        Assert.Equal(first.RunId, second!.RunId);
        Assert.Equal(2, second.Attempt);
        Assert.NotEqual(first.Capability, second.Capability);
        Assert.False(await _store.TryRequeueMissingActionAsync(second.RunId,
            TestCancellation));
        await _store.FailRunAsync(second.RunId, "expected test failure",
            TestCancellation);
    }

    [Fact]
    public async Task ClosingConversationRequiresTerminalWorkAndRetainsOwnedHistory()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-111-222-333-7001", "DOMAIN\\close-user", 12);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "Office activation failed", "ltr", identity), TestCancellation);

        var wrongEndpoint = await _store.CloseConversationAsync(
            Guid.NewGuid().ToString("D"),
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation);
        var wrongUser = await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId,
                identity with { UserSid = "S-1-5-21-111-222-333-7999" }),
            TestCancellation);
        Assert.False(wrongEndpoint.Accepted);
        Assert.False(wrongUser.Accepted);

        var lease = await LeaseRunForAgentAsync(agentId);
        var runningClose = await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation);
        Assert.False(runningClose.Accepted);

        var scope = await _store.ResolveCapabilityAsync(lease.Capability, TestCancellation);
        Assert.NotNull(scope);
        await _store.WriteToolAuditAsync(scope!, "test_tool", "ok",
            "private diagnostic detail", TestCancellation);
        var parameters = JsonSerializer.SerializeToElement(
            new { artifactId = "office-activation-repair" });
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "software.office_bugfix_run", "Repair Office", "private description",
            parameters, TimeSpan.FromMinutes(5), TestCancellation);
        Assert.NotNull(proposal);
        await _store.CompleteRunAsync(lease.RunId, "private assistant reply",
            TestCancellation);

        var pendingClose = await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation);
        Assert.False(pendingClose.Accepted);
        var rejected = await _store.DecideActionAsync(agentId,
            new ChatActionDecision(conversation.ConversationId, proposal!.ActionId,
                false, identity, "no"), TestCancellation);
        Assert.True(rejected.Accepted);

        var closed = await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation);
        var repeated = await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation);
        Assert.True(closed.Accepted);
        Assert.True(repeated.Accepted);
        var retainedDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, 0, identity),
            TestCancellation);
        Assert.NotNull(retainedDelta);
        Assert.Contains(retainedDelta!.Messages,
            message => message.Body == "Office activation failed");
        Assert.Contains(retainedDelta.Messages,
            message => message.Body == "private assistant reply");
        Assert.Contains(retainedDelta.Actions,
            action => action.ActionId == proposal.ActionId);
        Assert.Null(await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(conversation.ConversationId, 0,
                identity with { UserSid = "S-1-5-21-111-222-333-7999" }),
            TestCancellation));
        Assert.False((await _store.SubmitMessageAsync(agentId,
            new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
                conversation.ConversationId, "old case", "ltr", identity),
            TestCancellation)).Accepted);

        var summaries = await _store.ListConversationsAsync(agentId,
            new ChatConversationListRequest(identity), TestCancellation);
        var closedSummary = Assert.Single(summaries);
        Assert.Equal("Office activation failed", closedSummary.Title);
        Assert.Equal(ChatConversationState.Closed, closedSummary.State);
        Assert.False(closedSummary.IsActive);
        Assert.True(closedSummary.IsReadOnly);
        Assert.Empty(await _store.ListConversationsAsync(agentId,
            new ChatConversationListRequest(
                identity with { UserSid = "S-1-5-21-111-222-333-7999" }),
            TestCancellation));

        var freshReceipt = await _store.SubmitMessageAsync(agentId,
            new ChatMessageSubmission(Guid.NewGuid().ToString("N"), null,
                "A separate new issue", "ltr", identity), TestCancellation);
        Assert.True(freshReceipt.Accepted);
        Assert.NotEqual(conversation.ConversationId, freshReceipt.ConversationId);
        var freshDelta = await _store.GetDeltaAsync(agentId,
            new ChatDeltaRequest(freshReceipt.ConversationId!, 0, identity),
            TestCancellation);
        Assert.Contains(freshDelta!.Messages,
            message => message.Body == "A separate new issue");
        Assert.Empty(freshDelta.Actions);

        await using var connection = new SqliteConnection(
            $"Data Source={_databasePath};Mode=ReadWrite");
        await connection.OpenAsync(TestCancellation);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT c.is_active, c.user_name, c.session_id,
                (SELECT COUNT(*) FROM messages m
                    WHERE m.conversation_id = c.conversation_id AND m.body <> ''),
                (SELECT COUNT(*) FROM action_proposals p
                    WHERE p.conversation_id = c.conversation_id
                      AND (p.display_name <> '' OR p.description <> ''
                           OR p.execution_error IS NOT NULL)),
                (SELECT COUNT(*) FROM tool_audit t
                    WHERE t.conversation_id = c.conversation_id AND t.detail IS NOT NULL),
                (SELECT COUNT(*) FROM action_proposals p
                    WHERE p.conversation_id = c.conversation_id
                      AND p.method = 'software.office_bugfix_run'
                      AND p.parameters_json IS NOT NULL AND p.state = 'Rejected')
            FROM conversations c WHERE c.conversation_id = $conversation_id;
            """;
        command.Parameters.AddWithValue("$conversation_id", conversation.ConversationId);
        await using var reader = await command.ExecuteReaderAsync(TestCancellation);
        Assert.True(await reader.ReadAsync(TestCancellation));
        Assert.Equal(0, reader.GetInt64(0));
        Assert.Equal(string.Empty, reader.GetString(1));
        Assert.Equal(0, reader.GetInt64(2));
        Assert.True(reader.GetInt64(3) > 0);
        Assert.True(reader.GetInt64(4) > 0);
        Assert.Equal(0, reader.GetInt64(5));
        Assert.Equal(1, reader.GetInt64(6));
        await reader.DisposeAsync();

        await _store.PurgeExpiredDataAsync(DateTimeOffset.UtcNow.AddSeconds(1),
            TestCancellation);
        command.CommandText =
            "SELECT COUNT(*) FROM conversations WHERE conversation_id = $conversation_id;";
        Assert.Equal(0L, Convert.ToInt64(
            await command.ExecuteScalarAsync(TestCancellation)));
        command.CommandText =
            "SELECT COUNT(*) FROM tool_audit WHERE conversation_id = $conversation_id;";
        Assert.Equal(0L, Convert.ToInt64(
            await command.ExecuteScalarAsync(TestCancellation)));
    }

    [Fact]
    public async Task ConversationListNormalizesTitlesSortsLimitsAndExcludesEmptyDrafts()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-111-222-333-7101", "DOMAIN\\history-user", 14);
        var titles = new[]
        {
            "  Printer is offline\nPlease help  ",
            "Office update failed",
            "VPN cannot connect"
        };

        foreach (var title in titles)
        {
            var receipt = await _store.SubmitMessageAsync(agentId,
                new ChatMessageSubmission(Guid.NewGuid().ToString("N"), null,
                    title, "ltr", identity), TestCancellation);
            Assert.True(receipt.Accepted);
            var lease = await LeaseRunForAgentAsync(agentId);
            await _store.CompleteRunAsync(lease.RunId, "answered", TestCancellation);
            Assert.True((await _store.CloseConversationAsync(agentId,
                new ChatConversationCloseRequest(receipt.ConversationId!, identity),
                TestCancellation)).Accepted);
            await Task.Delay(10, TestCancellation);
        }

        await _store.OpenConversationAsync(agentId, identity, TestCancellation);
        var all = await _store.ListConversationsAsync(agentId,
            new ChatConversationListRequest(identity, 50), TestCancellation);
        Assert.Equal(3, all.Count);
        Assert.Equal("VPN cannot connect", all[0].Title);
        Assert.Equal("Printer is offline Please help", all[2].Title);
        Assert.All(all, summary => Assert.Equal(ChatConversationState.Closed,
            summary.State));
        Assert.True(all[0].UpdatedAtUtc >= all[1].UpdatedAtUtc);
        Assert.True(all[1].UpdatedAtUtc >= all[2].UpdatedAtUtc);

        var limited = await _store.ListConversationsAsync(agentId,
            new ChatConversationListRequest(identity, 2), TestCancellation);
        Assert.Equal(2, limited.Count);
        Assert.Equal(all.Take(2).Select(item => item.ConversationId),
            limited.Select(item => item.ConversationId));
    }

    [Fact]
    public async Task ClosingConversationBlocksApprovedActionsAndExpiresStaleOnes()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-111-222-333-8001", "DOMAIN\\action-user", 13);
        var conversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "Windows activation failed", "ltr", identity), TestCancellation);
        var lease = await LeaseRunForAgentAsync(agentId);
        var proposal = await _store.CreateActionProposalAsync(lease.Capability,
            "system.windows_activation_bugfix_run", "Repair Windows", "description",
            parameters: null, TimeSpan.FromMinutes(5), TestCancellation);
        await _store.CompleteRunAsync(lease.RunId, "ready", TestCancellation);
        Assert.True((await _store.DecideActionAsync(agentId,
            new ChatActionDecision(conversation.ConversationId, proposal!.ActionId,
                true, identity, "yes"), TestCancellation)).Accepted);
        Assert.False((await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation)).Accepted);
        await _store.MarkActionExecutedAsync(proposal.ActionId, true, null,
            TestCancellation);
        Assert.True((await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation)).Accepted);

        var staleConversation = await _store.OpenConversationAsync(
            agentId, identity, TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), staleConversation.ConversationId,
            "Office activation failed", "ltr", identity), TestCancellation);
        var staleLease = await LeaseRunForAgentAsync(agentId);
        var staleProposal = await _store.CreateActionProposalAsync(staleLease.Capability,
            "software.office_bugfix_run", "Repair Office", "description",
            parameters: null, TimeSpan.FromSeconds(-1), TestCancellation);
        await _store.CompleteRunAsync(staleLease.RunId, "ready", TestCancellation);
        Assert.True((await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(staleConversation.ConversationId, identity),
            TestCancellation)).Accepted);

        await using var connection = new SqliteConnection(
            $"Data Source={_databasePath};Mode=ReadWrite");
        await connection.OpenAsync(TestCancellation);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT state FROM action_proposals WHERE action_id = $action_id;";
        command.Parameters.AddWithValue("$action_id", staleProposal!.ActionId);
        Assert.Equal(ChatActionState.Expired.ToString(),
            await command.ExecuteScalarAsync(TestCancellation));
    }

    [Fact]
    public async Task HumanTakeoverCancelsHermesAndSupportsUnreadResolveAndPrivacyFlow()
    {
        var agentId = Guid.NewGuid().ToString("D");
        var identity = new ChatIdentity(
            "S-1-5-21-111-222-333-9001", "DOMAIN\\helpdesk-user", 14);
        var conversation = await _store.OpenConversationAsync(agentId, identity,
            TestCancellation);
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "I need help with Office", "ltr", identity), TestCancellation);
        var running = await LeaseRunForAgentAsync(agentId);

        var initial = Assert.Single((await _store.GetHelpdeskConversationsAsync(
            200, null, null, TestCancellation)),
            item => item.ConversationId == conversation.ConversationId);
        Assert.Equal(HelpdeskStatus.New, initial.Status);
        Assert.Equal(1, initial.UnreadCount);

        Assert.True(await _store.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(null, true, null), TestCancellation));
        await _store.CompleteRunAsync(running.RunId, "late assistant output",
            TestCancellation);
        var reply = await _store.SubmitOperatorMessageAsync(conversation.ConversationId,
            new HelpdeskOperatorMessageRequest(Guid.NewGuid().ToString("N"),
                "I am checking this for you.", "ltr"), TestCancellation);
        Assert.True(reply.Accepted);

        var takenOver = await _store.GetHelpdeskConversationAsync(
            conversation.ConversationId, 0, TestCancellation);
        Assert.NotNull(takenOver);
        Assert.Equal(HelpdeskResponder.Operator, takenOver!.Conversation.ResponderMode);
        Assert.Equal(HelpdeskStatus.Open, takenOver.Conversation.Status);
        Assert.Contains(takenOver.Messages, message => message.Role == ChatMessageRole.Operator);
        Assert.DoesNotContain(takenOver.Messages,
            message => message.Role == ChatMessageRole.Assistant
                && message.Body == "late assistant output");

        Assert.True(await _store.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(null, null, takenOver.Conversation.LastSequence),
            TestCancellation));
        var read = Assert.Single((await _store.GetHelpdeskConversationsAsync(
            200, null, null, TestCancellation)),
            item => item.ConversationId == conversation.ConversationId);
        Assert.Equal(0, read.UnreadCount);

        var diagnosticRequest = RpcRequest.Create(Guid.NewGuid().ToString("N"),
            "endpoint.get_info");
        await _store.AppendDiagnosticTraceAsync(conversation.ConversationId, agentId,
            diagnosticRequest, RpcResponse.Success(diagnosticRequest.Id, new { ok = true }),
            TestCancellation);
        Assert.True(await _store.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(HelpdeskStatus.Resolved, null, null),
            TestCancellation));
        await _store.SubmitMessageAsync(agentId, new ChatMessageSubmission(
            Guid.NewGuid().ToString("N"), conversation.ConversationId,
            "The problem returned", "ltr", identity), TestCancellation);
        var reopened = await _store.GetHelpdeskConversationAsync(
            conversation.ConversationId, 0, TestCancellation);
        Assert.Equal(HelpdeskStatus.New, reopened!.Conversation.Status);
        Assert.Equal(HelpdeskResponder.Hermes, reopened.Conversation.ResponderMode);

        Assert.True(await _store.PatchHelpdeskConversationAsync(conversation.ConversationId,
            new HelpdeskConversationPatch(null, true, null), TestCancellation));
        Assert.True((await _store.CloseConversationAsync(agentId,
            new ChatConversationCloseRequest(conversation.ConversationId, identity),
            TestCancellation)).Accepted);
        Assert.Null(await _store.GetHelpdeskConversationAsync(
            conversation.ConversationId, 0, TestCancellation));
        await using var connection = new SqliteConnection(
            $"Data Source={_databasePath};Mode=ReadWrite");
        await connection.OpenAsync(TestCancellation);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM helpdesk_trace_events WHERE conversation_id = $id;";
        command.Parameters.AddWithValue("$id", conversation.ConversationId);
        Assert.Equal(0L, Convert.ToInt64(
            await command.ExecuteScalarAsync(TestCancellation)));
    }

    private async Task<ChatRunLease> LeaseRunForAgentAsync(
        string agentId, int historyLimit = 20)
    {
        while (true)
        {
            var lease = await _store.LeaseNextRunAsync(
                historyLimit, TimeSpan.FromMinutes(5), TestCancellation);
            Assert.NotNull(lease);
            if (lease!.AgentId == agentId) return lease;
            await _store.CompleteRunAsync(lease.RunId, "drained", TestCancellation);
        }
    }
}
