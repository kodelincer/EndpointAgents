using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using EndpointAgents.Tray;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class SupportWindowStateTests
{
    [Fact]
    public void ConversationUsesServerRollingWindowLimit()
    {
        var model = new SupportViewModel();
        model.ApplyConversationLimit(3);

        for (var index = 0; index < 5; index++)
            model.AddMessage(index % 2 == 0, "پیام " + index);

        Assert.Equal(3, model.Conversation.Count);
        Assert.DoesNotContain(model.Conversation.OfType<MessageItemViewModel>(),
            item => item.Body == "پیام 0");
        Assert.Equal("پیام 4",
            Assert.IsType<MessageItemViewModel>(model.Conversation[^1]).Body);
    }

    [Fact]
    public void ProgressTimelineRecognizesApprovedActionCompletion()
    {
        var model = new SupportViewModel();
        var now = DateTimeOffset.UtcNow;
        var progress = model.AddProgress(new ChatRunProgressEvent(
            "event-1", "conversation-1", "run-1", 1,
            ChatProgressStage.AwaitingApproval, ChatProgressActivity.ProposedAction,
            ChatProgressOutcome.None, now));

        Assert.False(progress.IsTerminal);
        Assert.True(progress.IsRunning);
        Assert.Contains("تأیید", progress.Summary);

        model.AddProgress(new ChatRunProgressEvent(
            "event-2", "conversation-1", "run-1", 2,
            ChatProgressStage.ActionCompleted, ChatProgressActivity.ProposedAction,
            ChatProgressOutcome.Success, now.AddSeconds(1)));

        Assert.True(progress.IsTerminal);
        Assert.False(progress.IsRunning);
        Assert.Contains("انجام شد", progress.Summary);
        Assert.Equal(2, progress.Steps.Count);
        Assert.Equal("\uE73E", progress.Steps[^1].StatusGlyph);
        Assert.Equal("\uE711", new ProgressStepViewModel(
            "failed", "00:00:00", ChatProgressOutcome.Failure).StatusGlyph);
    }

    [Fact]
    public void AutomaticWindowsRepairProgressDoesNotClaimUserApproval()
    {
        var model = new SupportViewModel();
        var progress = model.AddProgress(new ChatRunProgressEvent(
            "event-1", "conversation-1", "run-1", 1,
            ChatProgressStage.ActionStarted,
            ChatProgressActivity.WindowsActivationRepair,
            ChatProgressOutcome.None, DateTimeOffset.UtcNow));

        Assert.Contains("تعمیر فعال‌سازی ویندوز", progress.Summary);
        Assert.DoesNotContain("تأیید", progress.Summary);
    }

    [Fact]
    public void StatusStateControlsAvailableServiceActions()
    {
        var model = new SupportViewModel();
        var runtime = new AgentRuntimeSnapshot(
            "agent-1", Environment.MachineName, "1.0.0",
            AgentConnectionStatus.Connected, AgentFunctionMode.Active,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null,
            0, null, null, null);

        model.ApplyStatus(new TrayStatus(TrayHealthState.Ready,
            EndpointServiceState.Running, runtime, "متصل و آماده", "آماده"));

        Assert.True(model.IsConnected);
        Assert.True(model.CanPause);
        Assert.True(model.CanStop);
        Assert.False(model.CanStart);
        Assert.False(model.CanResume);
    }

    [Fact]
    public void SelectedLogPreservesMixedDirectionEvidence()
    {
        var model = new SupportViewModel();
        var timestamp = DateTimeOffset.UtcNow;
        var selected = new EndpointEventLogEntry(timestamp, "Warning",
            "EndpointAgents.Service", 42, "SHA-256: ABCD · هشدار آزمایشی");
        model.ReplaceLogs([selected]);

        Assert.Null(model.SelectedLog);
        model.SelectedLog = selected;
        model.ReplaceLogs([
            new EndpointEventLogEntry(timestamp, "Warning",
                "EndpointAgents.Service", 42, "SHA-256: ABCD · هشدار آزمایشی")
        ]);

        Assert.NotNull(model.SelectedLog);
        Assert.Contains("هشدار", model.SelectedLogText);
        Assert.Contains("EndpointAgents.Service", model.SelectedLogText);
        Assert.Contains("SHA-256: ABCD", model.SelectedLogText);
    }

    [Theory]
    [InlineData("این متن راست‌به‌چپ است", true)]
    [InlineData("EndpointAgents status", false)]
    public void MessageDirectionIsDerivedFromFirstStrongCharacter(
        string message, bool expectedRightToLeft)
    {
        var item = new MessageItemViewModel(ConversationItemKind.Message,
            "test", message, DateTimeOffset.UtcNow);

        Assert.Equal(expectedRightToLeft, item.IsRightToLeft);
    }

    [Fact]
    public void ConversationListFiltersWithoutLosingTheSelectedConversation()
    {
        var model = new SupportViewModel();
        var now = DateTimeOffset.UtcNow;
        var conversations = new[]
        {
            Summary("active", "Printer problem", now,
                ChatConversationState.Working, true),
            Summary("resolved", "Office repaired", now.AddMinutes(-1),
                ChatConversationState.Resolved, true),
            Summary("closed", "Old network case", now.AddDays(-1),
                ChatConversationState.Closed, false)
        };

        var selected = model.ReplaceConversations(conversations, "closed");
        model.SelectConversation(Assert.IsType<ConversationSummaryViewModel>(selected));
        model.SetConversationFilter(ConversationListFilter.Active);

        Assert.Equal("closed", model.SelectedConversation!.ConversationId);
        Assert.True(model.IsConversationReadOnly);
        Assert.Single(model.ConversationGroups.SelectMany(group => group));
        Assert.Equal("active",
            model.ConversationGroups.SelectMany(group => group).Single().ConversationId);
    }

    [Fact]
    public void ConversationRefreshPreservesRowsAndUpdatesOnlyChangedData()
    {
        var model = new SupportViewModel();
        var now = DateTimeOffset.UtcNow;
        var original = Summary("active", "Original", now,
            ChatConversationState.Working, true);
        model.ReplaceConversations([original], original.ConversationId);
        var group = Assert.Single(model.ConversationGroups);
        var row = Assert.Single(group);

        model.ReplaceConversations([original], original.ConversationId);
        Assert.Same(group, Assert.Single(model.ConversationGroups));
        Assert.Same(row, Assert.Single(group));

        var changed = original with
        {
            Title = "Updated",
            Preview = "New reply",
            State = ChatConversationState.Answered,
            LastSequence = original.LastSequence + 1
        };
        model.ReplaceConversations([changed], changed.ConversationId);

        Assert.Same(row, Assert.Single(group));
        Assert.Equal("Updated", row.Title);
        Assert.Equal("New reply", row.Preview);
        Assert.Equal(ChatConversationState.Answered, row.State);
    }

    [Fact]
    public void DraftAndClosedConversationExposeDifferentComposerModes()
    {
        var model = new SupportViewModel();
        var closed = Summary("closed", "Completed request", DateTimeOffset.UtcNow,
            ChatConversationState.Closed, false);
        var selected = model.ReplaceConversations([closed], closed.ConversationId);

        model.SelectConversation(Assert.IsType<ConversationSummaryViewModel>(selected));
        Assert.False(model.IsDraft);
        Assert.True(model.IsConversationReadOnly);
        Assert.Equal("Completed request", model.ConversationHeading);

        model.BeginConversationDraft();
        Assert.True(model.IsDraft);
        Assert.False(model.IsConversationReadOnly);
        Assert.Null(model.SelectedConversation);
    }

    [Fact]
    public void ConversationListStateDistinguishesLoadingEmptyAndError()
    {
        var model = new SupportViewModel();

        model.BeginConversationListRefresh();
        Assert.True(model.IsConversationListLoading);
        Assert.False(model.IsConversationListEmpty);

        model.CompleteConversationListRefresh();
        Assert.True(model.IsConversationListEmpty);

        model.CompleteConversationListRefresh("failed");
        Assert.True(model.HasConversationListError);
        Assert.False(model.IsConversationListEmpty);
    }

    private static ChatConversationSummary Summary(string id, string title,
        DateTimeOffset updatedAt, ChatConversationState state, bool isActive) =>
        new(id, title, title, "ltr", updatedAt.AddMinutes(-5), updatedAt, 10,
            state, isActive);
}
