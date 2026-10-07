#if DEBUG
using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using Microsoft.UI.Xaml;

namespace EndpointAgents.Tray;

// Development-only: `EndpointAgents.Tray.exe --ui-preview` shows the window with sample
// conversations so the layout can be reviewed without the service or control plane.
public sealed partial class SupportWindow
{
    public void MaximizeForPreview() =>
        (_appWindow.Presenter as Microsoft.UI.Windowing.OverlappedPresenter)?.Maximize();

    public void LoadPreview(bool dark, bool narrow)
    {
        _previewMode = true;
        if (dark) WindowRoot.RequestedTheme = ElementTheme.Dark;
        if (narrow) ResizeWindowInDips(MinimumWidthDips, MinimumHeightDips);
        var now = DateTimeOffset.Now;
        UpdateStatus(new TrayStatus(TrayHealthState.Ready, EndpointServiceState.Running,
            new AgentRuntimeSnapshot("preview", Environment.MachineName, "dev",
                AgentConnectionStatus.Connected, AgentFunctionMode.Active, now.AddHours(-2),
                now.AddMinutes(-1), now.AddMinutes(-3), "office.activation.status", true, 0,
                null, null, null),
            "متصل و آماده", "سرویس پس‌زمینه آماده دریافت عملکردهای تأییدشده پشتیبانی است."));

        ChatConversationSummary Summary(string id, string title, string preview,
            TimeSpan age, ChatConversationState state, bool active = true) =>
            new(id, title, preview, "rtl", now - age - TimeSpan.FromMinutes(20), now - age, 1,
                state, active);
        var selected = ViewModel.ReplaceConversations([
            Summary("c1", "آفیس پیام فعال‌سازی می‌دهد", "فعال‌سازی آفیس با موفقیت انجام شد.",
                TimeSpan.FromMinutes(2), ChatConversationState.Answered),
            Summary("c2", "پرینتر طبقه دوم چاپ نمی‌کند", "برای راه‌اندازی دوباره سرویس چاپ تأیید می‌دهید؟",
                TimeSpan.FromMinutes(40), ChatConversationState.AwaitingApproval),
            Summary("c3", "VPN وصل نمی‌شود", "در حال بررسی اتصال شبکه…",
                TimeSpan.FromHours(3), ChatConversationState.Working),
            Summary("c4", "اینترنت قطع و وصل می‌شود", "مشکل برطرف شد.",
                TimeSpan.FromDays(1), ChatConversationState.Resolved, false),
            Summary("c5", "فعال‌سازی ویندوز", "ویندوز فعال است.",
                TimeSpan.FromDays(3), ChatConversationState.Closed, false)
        ], "c1");
        ViewModel.SelectConversation(selected!);
        _conversationId = "c1";
        _changingConversation = true;
        ConversationIndex.SelectedItem = selected;
        _changingConversation = false;

        ViewModel.ClearConversationTimeline();
        ViewModel.AddMessage(true, "سلام، وقتی ورد را باز می‌کنم پیام می‌دهد آفیس فعال نیست.",
            timestamp: now.AddMinutes(-6));
        ViewModel.AddMessage(false, "سلام، الان وضعیت آفیس روی رایانه شما را بررسی می‌کنم.",
            timestamp: now.AddMinutes(-6));
        var stages = new[]
        {
            (ChatProgressStage.AssistantStarted, ChatProgressActivity.None),
            (ChatProgressStage.EndpointCheckStarted, ChatProgressActivity.OfficeActivationStatus),
            (ChatProgressStage.ActionStarted, ChatProgressActivity.OfficeActivationRepair),
            (ChatProgressStage.ActionCompleted, ChatProgressActivity.OfficeActivationRepair)
        };
        for (var index = 0; index < stages.Length; index++)
            ViewModel.AddProgress(new ChatRunProgressEvent($"e{index}", "c1", "r1", index,
                stages[index].Item1, stages[index].Item2, ChatProgressOutcome.Success,
                now.AddMinutes(-5 + index)));
        ViewModel.AddMessage(false,
            "فعال‌سازی آفیس دوباره انجام شد و الان فعال است. لطفاً ورد را یک بار ببندید و دوباره باز کنید.",
            timestamp: now.AddMinutes(-2));
        ViewModel.AddMessage(true, "درست شد، ممنون 🙏", timestamp: now.AddMinutes(-1));
        ViewModel.ChatState = "متصل به پشتیبانی";
        UpdateSubmitState();
    }

    // Saves what the window renders, for reviews on machines where the screen cannot be captured.
    public async Task SavePreviewSnapshotAsync(string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(WindowRoot);
        var pixels = await bitmap.GetPixelsAsync();
        using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
            Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
        var scale = WindowRoot.XamlRoot.RasterizationScale;
        encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96 * scale, 96 * scale,
            System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions.ToArray(pixels));
        await encoder.FlushAsync();
        await using var file = File.Create(path);
        await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream.GetInputStreamAt(0))
            .CopyToAsync(file);
    }
}
#endif
