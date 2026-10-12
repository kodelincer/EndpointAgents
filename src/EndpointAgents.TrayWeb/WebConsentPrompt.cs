using EndpointAgents.Tray;
using System.Collections.Concurrent;

namespace EndpointAgents.TrayWeb;

/// <summary>
/// Shows service consent requests as an approval sheet inside the Tray window. Falls back to a
/// Windows message box when the window cannot render. Anything other than an explicit approval is "no".
/// </summary>
public sealed class WebConsentPrompt(MainWindow window) : IUserConsentPrompt
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> _pending = new();
    private readonly ConcurrentDictionary<string, object> _payloads = new();
    private readonly MessageBoxConsentPrompt _fallback = new();

    public async Task<bool> ConfirmAsync(UserConsentPrompt prompt, CancellationToken cancellationToken)
    {
        await window.Dispatcher.InvokeAsync(() => window.ShowSection(null));
        try
        {
            await window.PageReady.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken);
        }
        catch (TimeoutException) { }
        if (window.WebUnavailable || !window.PageReady.IsCompleted)
            return await _fallback.ConfirmAsync(prompt, cancellationToken);

        var id = Guid.NewGuid().ToString("N");
        var answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var payload = new
        {
            id,
            kind = prompt.Kind,
            title = prompt.Title,
            body = prompt.Body,
            detail = prompt.Detail,
            approveText = prompt.ApproveText,
            declineText = prompt.DeclineText,
            caution = prompt.Caution
        };
        _pending[id] = answer;
        _payloads[id] = payload;
        window.Dispatcher.Invoke(() =>
        {
            window.ShowSection(null);
            window.PostEvent("consent", payload);
        });
        try
        {
            return await answer.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
            _payloads.TryRemove(id, out _);
            if (!answer.Task.IsCompleted)
                window.Dispatcher.Invoke(() =>
                    window.PostEvent("consent.withdrawn", new { id }));
        }
    }

    /// <summary>Prompts still waiting, re-sent when the page (re)loads.</summary>
    internal IReadOnlyList<object> Pending => _payloads.Values.ToArray();

    internal bool Answer(string id, bool approved)
    {
        _payloads.TryRemove(id, out _);
        if (!_pending.TryRemove(id, out var answer)) return false;
        TrayEventLogger.Information(approved
            ? "کاربر اقدام درخواستی سرویس را در پنجره دستیار تأیید کرد."
            : "کاربر اقدام درخواستی سرویس را در پنجره دستیار رد کرد.");
        return answer.TrySetResult(approved);
    }
}
