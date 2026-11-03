using EndpointAgents.Shared.Chat;
using EndpointAgents.Tray;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class ChatNotifierTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch;

    private static ChatConversationSummary Summary(string id, long sequence, bool active = true) =>
        new(id, "t", "p", "rtl", Now, Now, sequence, ChatConversationState.Open, active);

    private static ChatDelta Delta(string id, params ChatMessage[] messages) =>
        new(new ChatConversation(id, "a", "S-1-0-0", "u", 1, Now, Now, 0), messages, []);

    private static ChatMessage Message(ChatMessageRole role, string body, ChatMessageState state = ChatMessageState.Completed) =>
        new(Guid.NewGuid().ToString("N"), "c1", 1, role, body, "rtl", state, Now);

    [Fact]
    public void TheFirstListOnlyRecordsWhereEachConversationIs()
    {
        var notifier = new ChatNotifier();
        Assert.Empty(notifier.Changed([Summary("c1", 4)]));
        Assert.Equal([("c1", 4L)], notifier.Changed([Summary("c1", 6)]));
        Assert.Empty(notifier.Changed([Summary("c1", 6)]));
    }

    [Fact]
    public void WhatTheUserSawInTheOpenWindowIsNotAnnounced()
    {
        var notifier = new ChatNotifier();
        notifier.MarkSeen([Summary("c1", 9)]);
        Assert.Empty(notifier.Changed([Summary("c1", 9), Summary("old", 3, active: false)]));
        Assert.Equal([("c2", 0L)], notifier.Changed([Summary("c1", 9), Summary("c2", 2)]));
    }

    [Fact]
    public void OnlyFinishedRepliesFromTheAssistantOrHelpdeskPopUp()
    {
        var notifier = new ChatNotifier();
        Assert.Null(notifier.From(Delta("c1", Message(ChatMessageRole.User, "سلام"))));
        Assert.Null(notifier.From(Delta("c1", Message(ChatMessageRole.Assistant, "در حال بررسی", ChatMessageState.Working))));

        var operatorReply = notifier.From(Delta("c1", Message(ChatMessageRole.Operator, "حساب شما باز شد.")))!;
        Assert.Equal("پاسخ کارشناس پشتیبانی", operatorReply.Title);
        Assert.False(operatorReply.NeedsRestart);
    }

    [Fact]
    public void ARestartRequestGetsItsOwnTitle()
    {
        var notification = new ChatNotifier().From(Delta("c1",
            Message(ChatMessageRole.Assistant, "نصب انجام شد. لطفاً کامپیوتر را ری‌استارت کنید.")))!;
        Assert.True(notification.NeedsRestart);
        Assert.Equal("لطفاً کامپیوتر را دوباره راه‌اندازی کنید", notification.Title);
        Assert.Equal("c1", notification.ConversationId);
    }

    [Fact]
    public void LongTextIsShortenedToOneLine()
    {
        var text = ChatNotifier.Shorten("**خط اول**\n\n" + new string('ا', 400));
        Assert.DoesNotContain("\n", text);
        Assert.DoesNotContain("**", text);
        Assert.Equal(ChatNotifier.BodyCharacters, text.Length);
        Assert.EndsWith("…", text);
    }
}
