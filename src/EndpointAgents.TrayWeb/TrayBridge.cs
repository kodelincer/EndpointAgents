using EndpointAgents.Shared.Chat;
using EndpointAgents.Tray;
using Microsoft.Win32;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EndpointAgents.TrayWeb;

/// <summary>
/// The only way the page can reach the computer: a fixed list of named methods, each mapped to an
/// existing Tray operation. There is no generic "run" or file access; unknown methods are refused.
/// </summary>
public sealed class TrayBridge(MainWindow window, Func<Task<TrayStatus?>> refreshStatus, Action exit)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private const int MaxRequestCharacters = 64 * 1024;
    private const int MaxClipboardCharacters = 512 * 1024;
    private readonly ServiceIpcClient _service = new();
    private readonly EndpointEventLogReader _logReader = new();
    private readonly int _sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
    private IReadOnlyList<EndpointEventLogEntry> _lastLogs = [];
    private string? _pendingSection;

    private sealed record Request(string Id, string Method, JsonElement? Params);

    public void PostEvent(string name, object data)
    {
        if (name == "navigate" && !window.PageReady.IsCompleted)
            _pendingSection = (string?)data.GetType().GetProperty("section")?.GetValue(data);
        Post(new { type = "event", name, data });
    }

    public async Task HandleAsync(string json)
    {
        Request? request;
        try
        {
            if (json.Length > MaxRequestCharacters) return;
            request = JsonSerializer.Deserialize<Request>(json, Json);
        }
        catch (JsonException)
        {
            return;
        }
        if (request is null || string.IsNullOrEmpty(request.Id)) return;

        try
        {
            var result = await DispatchAsync(request.Method, request.Params ?? default);
            Post(new { type = "response", id = request.Id, ok = true, result });
        }
        catch (Exception ex)
        {
            Post(new
            {
                type = "response",
                id = request.Id,
                ok = false,
                error = new { code = ex.GetType().Name, message = ex.Message }
            });
        }
    }

    private async Task<object?> DispatchAsync(string method, JsonElement p)
    {
        switch (method)
        {
            case "app.ready":
                window.MarkPageReady();
                var section = _pendingSection;
                _pendingSection = null;
                return new
                {
                    preview = window.Options.Preview,
                    userName = Environment.UserName,
                    machineName = Environment.MachineName,
                    section,
                    status = window.LastStatus is { } last ? StatusDto(last) : null,
                    maximized = window.WindowState == System.Windows.WindowState.Maximized,
                    consents = ((WebConsentPrompt)window.ConsentPrompt).Pending,
                    messageLimit = ChatLimits.MessageCharacters
                };

            case "status.refresh":
                return await refreshStatus() is { } status ? StatusDto(status) : null;

            case "chat.list":
                return await _service.ListChatsAsync(_sessionId, CancellationToken.None);

            case "chat.sync":
                return await _service.GetChatDeltaAsync(new ChatDeltaRequest(
                    Text(p, "conversationId", 64), Long(p, "afterSequence"), Identity()),
                    CancellationToken.None);

            case "chat.send":
            {
                var text = Text(p, "text", ChatLimits.MessageCharacters).Trim();
                if (text.Length == 0) throw new ArgumentException("پیام خالی است.");
                var conversationId = OptionalText(p, "conversationId", 64);
                return await _service.SendChatMessageAsync(new ChatMessageSubmission(
                    Guid.NewGuid().ToString("N"), conversationId, text,
                    IsRightToLeft(text) ? "rtl" : "ltr", Identity()), CancellationToken.None);
            }

            case "chat.close":
                return await _service.CloseChatAsync(new ChatConversationCloseRequest(
                    Text(p, "conversationId", 64), Identity()), CancellationToken.None);

            case "chat.not_fixed":
                return await _service.ReportChatNotFixedAsync(new EndpointAgents.Shared.Problems.ChatNotFixedRequest(
                    Text(p, "conversationId", 64), Identity()), CancellationToken.None);

            case "chat.stop":
                return await _service.StopChatRunAsync(new ChatRunStopRequest(
                    Text(p, "conversationId", 64), Identity()), CancellationToken.None);

            case "chat.action.respond":
            {
                var approved = p.TryGetProperty("approved", out var value) && value.ValueKind == JsonValueKind.True;
                var reply = OptionalText(p, "text", ChatLimits.MessageCharacters)
                    ?? (approved ? "بله" : "نه");
                return await _service.RespondToChatActionAsync(new ChatActionDecision(
                    Text(p, "conversationId", 64), Text(p, "actionId", 64), approved, Identity(),
                    reply), CancellationToken.None);
            }

            case "service.control":
            {
                var action = Text(p, "action", 16);
                if (action is not ("start" or "stop" or "pause" or "resume"))
                    throw new ArgumentException("عملیات سرویس شناخته‌شده نیست.");
                var result = await ServiceControlLauncher.LaunchAsync(action, CancellationToken.None);
                await Task.Delay(500);
                await refreshStatus();
                return result;
            }

            case "logs.read":
            {
                var hours = p.TryGetProperty("hours", out var h) && h.TryGetInt32(out var value)
                    ? Math.Clamp(value, 1, 24 * 7) : 6;
                var level = OptionalText(p, "level", 16);
                if (level is not (null or "Error" or "Warning" or "Information")) level = null;
                _lastLogs = await _logReader.ReadAsync(TimeSpan.FromHours(hours), level,
                    OptionalText(p, "search", 200)?.Trim() is { Length: > 0 } search ? search : null,
                    CancellationToken.None);
                return _lastLogs;
            }

            case "logs.export":
                return ExportLogs(OptionalText(p, "format", 8) == "json");

            case "clipboard.write":
                System.Windows.Clipboard.SetText(Text(p, "text", MaxClipboardCharacters));
                return true;

            case "consent.answer":
                return ((WebConsentPrompt)window.ConsentPrompt).Answer(Text(p, "id", 64),
                    p.TryGetProperty("approved", out var answer) && answer.ValueKind == JsonValueKind.True);

            case "keyboard.persian":
                SwitchToPersianKeyboard();
                return true;

            case "window.minimize":
                window.WindowState = System.Windows.WindowState.Minimized;
                return true;

            case "window.toggleMaximize":
                window.WindowState = window.WindowState == System.Windows.WindowState.Maximized
                    ? System.Windows.WindowState.Normal
                    : System.Windows.WindowState.Maximized;
                return true;

            case "window.close":
                window.Close();
                return true;

            case "window.drag":
                window.StartDrag();
                return true;

            case "app.exit":
                exit();
                return true;

            default:
                throw new NotSupportedException("این درخواست پشتیبانی نمی‌شود.");
        }
    }

    internal static object StatusDto(TrayStatus status) => new
    {
        health = status.Health,
        service = status.ServiceState,
        label = status.Label,
        detail = status.Detail,
        runtime = status.Runtime
    };

    private string? ExportLogs(bool json)
    {
        var dialog = new SaveFileDialog
        {
            FileName = $"EndpointAgent-report-{DateTime.Now:yyyyMMdd-HHmm}",
            Filter = json ? "فایل JSON|*.json" : "فایل متنی|*.txt",
            DefaultExt = json ? ".json" : ".txt",
            AddExtension = true
        };
        if (dialog.ShowDialog(window) != true) return null;
        var text = json
            ? JsonSerializer.Serialize(_lastLogs, new JsonSerializerOptions { WriteIndented = true })
            : string.Join(Environment.NewLine + Environment.NewLine, _lastLogs.Select(FormatLog));
        File.WriteAllText(dialog.FileName, text, Encoding.UTF8);
        return dialog.FileName;
    }

    private void SwitchToPersianKeyboard()
    {
        // Persian is the default typing language, applied once per time the window is shown.
        if (window.PersianKeyboardApplied) return;
        window.PersianKeyboardApplied = true;
        var layout = LoadKeyboardLayout("00000429", 0);
        if (layout != IntPtr.Zero) PostMessage(window.Handle, 0x0050, IntPtr.Zero, layout);
    }

    internal static string FormatLog(EndpointEventLogEntry item) =>
        $"[{item.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {item.Level} · {item.Source} · " +
        $"{(item.EventId == 0 ? "—" : item.EventId)}\r\n{item.Message}";

    private ChatIdentity Identity() => new("S-1-0-0", Environment.UserName, _sessionId);

    private static string Text(JsonElement p, string name, int max) =>
        OptionalText(p, name, max) ?? throw new ArgumentException($"'{name}' is required.");

    private static string? OptionalText(JsonElement p, string name, int max)
    {
        if (p.ValueKind != JsonValueKind.Object || !p.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString() ?? string.Empty;
        if (text.Length > max) throw new ArgumentException($"'{name}' is too long.");
        return text;
    }

    private static long Long(JsonElement p, string name) =>
        p.ValueKind == JsonValueKind.Object && p.TryGetProperty(name, out var value)
        && value.TryGetInt64(out var number) ? Math.Max(0, number) : 0;

    internal static bool IsRightToLeft(string text)
    {
        foreach (var character in text)
        {
            if (character is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= 'ﻼ') return true;
            if (char.IsLetter(character)) return false;
        }
        return false;
    }

    private void Post(object message)
    {
        var json = JsonSerializer.Serialize(message, Json);
        if (window.Dispatcher.CheckAccess()) window.PostToPage(json);
        else window.Dispatcher.BeginInvoke(() => window.PostToPage(json));
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadKeyboardLayout(string layoutId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);
}
