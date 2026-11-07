using EndpointAgents.Shared.Chat;

namespace EndpointAgents.Tray;

/// <summary>One pop-up next to the clock while the chat window is closed.</summary>
public sealed record ChatNotification(string ConversationId, string Title, string Body, bool NeedsRestart);

/// <summary>
/// Decides which pop-ups to show while the chat window is closed: a new reply from the
/// assistant or the helpdesk, and a question that waits for the user. Pure logic; the Tray
/// host polls the service and shows the result. No AI runs here.
/// </summary>
public sealed class ChatNotifier
{
    public const int BodyCharacters = 180;
    private static readonly string[] RestartWords =
        ["ری‌استارت", "ریستارت", "راه‌اندازی مجدد", "راه‌اندازی دوباره", "راه اندازی مجدد", "راه اندازی دوباره", "restart"];

    private readonly Dictionary<string, long> _seen = new(StringComparer.Ordinal);
    private readonly HashSet<string> _announcedActions = new(StringComparer.Ordinal);
    private bool _initialized;

    /// <summary>
    /// Compares the newest list with what was seen and returns the conversations that changed
    /// with the sequence to read after. The first list only records where each conversation is.
    /// </summary>
    public IReadOnlyList<(string ConversationId, long AfterSequence)> Changed(
        IReadOnlyList<ChatConversationSummary> summaries)
    {
        var changed = new List<(string, long)>();
        foreach (var summary in summaries)
        {
            var known = _seen.TryGetValue(summary.ConversationId, out var sequence);
            if (_initialized && summary.IsActive && (!known || summary.LastSequence > sequence))
                changed.Add((summary.ConversationId, known ? sequence : 0));
            _seen[summary.ConversationId] = Math.Max(summary.LastSequence, known ? sequence : 0);
        }
        _initialized = true;
        return changed;
    }

    /// <summary>Marks everything up to now as seen, for example while the window is open.</summary>
    public void MarkSeen(IReadOnlyList<ChatConversationSummary> summaries)
    {
        foreach (var summary in summaries) _seen[summary.ConversationId] = summary.LastSequence;
        _initialized = true;
    }

    /// <summary>The pop-up for new messages and waiting questions in one conversation, or null.</summary>
    public ChatNotification? From(ChatDelta delta)
    {
        var waiting = delta.Actions.FirstOrDefault(action =>
            action.State == ChatActionState.Pending && _announcedActions.Add(action.ActionId));
        var reply = delta.Messages.LastOrDefault(message =>
            message.Role is ChatMessageRole.Assistant or ChatMessageRole.Operator
            && message.State == ChatMessageState.Completed
            && !string.IsNullOrWhiteSpace(message.Body));
        var conversationId = delta.Conversation.ConversationId;
        if (waiting is not null)
            return new(conversationId, "دستیار پشتیبانی منتظر پاسخ شماست",
                Shorten(waiting.DisplayName), false);
        if (reply is null) return null;
        var restart = NeedsRestart(reply.Body);
        var title = restart ? "لطفاً کامپیوتر را دوباره راه‌اندازی کنید"
            : reply.Role == ChatMessageRole.Operator ? "پاسخ کارشناس پشتیبانی"
            : "پیام تازه از دستیار پشتیبانی";
        return new(conversationId, title, Shorten(reply.Body), restart);
    }

    public static bool NeedsRestart(string text) =>
        RestartWords.Any(word => text.Contains(word, StringComparison.OrdinalIgnoreCase));

    /// <summary>One line, without links or markup, short enough for a Windows pop-up.</summary>
    public static string Shorten(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .Replace("**", "", StringComparison.Ordinal).Replace("`", "", StringComparison.Ordinal);
        return line.Length <= BodyCharacters ? line : line[..(BodyCharacters - 1)].TrimEnd() + "…";
    }
}
