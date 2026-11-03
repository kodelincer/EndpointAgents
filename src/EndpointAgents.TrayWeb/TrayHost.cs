using EndpointAgents.Shared.Support;
using EndpointAgents.Tray;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace EndpointAgents.TrayWeb;

/// <summary>
/// Owns the notification-area icon, the status loop, the service channel and the one window.
/// </summary>
public sealed class TrayHost : IDisposable
{
    private readonly System.Windows.Application _app;
    private readonly TrayOptions _options;
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _statusMenuItem;
    private readonly TrayIconFactory _icons = new();
    private readonly WindowsServiceManager _serviceManager = new();
    private readonly ServiceIpcClient _serviceClient = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly EventWaitHandle _showRequested;
    private readonly RegisteredWaitHandle _showRequests;
    private readonly MainWindow _window;
    private readonly DispatcherTimer _chatTimer;
    private readonly ChatNotifier _chatNotifier = new();
    private string? _balloonConversation;
    private bool _checkingChats;
    private AgentRuntimeSnapshot? _lastRuntime;
    private TrayStatus? _lastStatus;
    private int _ipcFailures;
    private bool _refreshing;
    private bool _disposed;

    public TrayHost(System.Windows.Application app, TrayOptions options)
    {
        _app = app;
        _options = options;
        _window = new MainWindow(options, RefreshStatusAsync, Exit);

        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        _statusMenuItem = new Forms.ToolStripMenuItem("در حال بررسی وضعیت دستیار پشتیبانی…")
        {
            Enabled = false
        };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("باز کردن دستیار پشتیبانی", null, (_, _) => _window.ShowSection("chat"));
        menu.Items.Add("وضعیت و کنترل", null, (_, _) => _window.ShowSection("status"));
        menu.Items.Add("مشاهده گزارش‌ها", null, (_, _) => _window.ShowSection("logs"));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("خروج از برنامه", null, (_, _) => Exit());

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "دستیار پشتیبانی - در حال بررسی",
            Icon = _icons.Get(TrayHealthState.Checking),
            Visible = !options.Preview,
            ContextMenuStrip = menu
        };
        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) _window.ShowSection(null);
        };
        _notifyIcon.BalloonTipClicked += (_, _) =>
        {
            if (_balloonConversation is { } conversationId) _window.ShowConversation(conversationId);
        };
        _notifyIcon.BalloonTipClosed += (_, _) => _balloonConversation = null;

        var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
        _showRequested = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName(sessionId));
        var dispatcher = app.Dispatcher;
        _showRequests = ThreadPool.RegisterWaitForSingleObject(_showRequested,
            (_, _) => dispatcher.BeginInvoke(() =>
            {
                if (!_disposed) _window.ShowSection("chat");
            }), null, Timeout.Infinite, executeOnlyOnce: false);

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _chatTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(10)
        };
        _chatTimer.Tick += async (_, _) => await CheckChatsAsync();

        if (options.Preview)
        {
            _window.ShowSection(options.Section ?? "chat");
            return;
        }

        var channel = new TrayServiceChannel(new TrayTaskDispatcher(_window.ConsentPrompt));
        _ = channel.RunAsync(_shutdown.Token).ContinueWith(task =>
            TrayEventLogger.Error("کانال فرمان نشست کاربر متوقف شد: " +
                task.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);

        _statusTimer.Start();
        _chatTimer.Start();
        TrayEventLogger.Information("برنامه ناحیه اعلان دستیار پشتیبانی برای نشست کاربر اجرا شد.");
        _ = RefreshStatusAsync();
        if (options.Section is { } section) _window.ShowSection(section);
        if (options.Smoke)
            _ = Task.Delay(TimeSpan.FromSeconds(3)).ContinueWith(_ =>
                dispatcher.BeginInvoke(Exit));
    }

    private static string ShowEventName(int sessionId) =>
        $@"Local\EndpointAgents.Tray.Show.{sessionId}";

    /// <summary>Called by a second launch in the same session: show the running window.</summary>
    public static void SignalShowWindow(int sessionId)
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName(sessionId), out var handle))
            using (handle) handle.Set();
    }

    private async Task<TrayStatus?> RefreshStatusAsync()
    {
        if (_options.Preview) return null;
        if (_refreshing || _shutdown.IsCancellationRequested) return _lastStatus;
        _refreshing = true;
        try
        {
            var scmState = await Task.Run(_serviceManager.GetStatus, _shutdown.Token);
            var serviceState = scmState;
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
                if (scmState != EndpointServiceState.Running)
                    probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));
                _lastRuntime = await _serviceClient.GetStatusAsync(probeTimeout.Token);
                _ipcFailures = 0;
                serviceState = EndpointServiceStateResolver.ResolveObservedState(scmState,
                    ipcAvailable: true);
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                _lastRuntime = null;
                if (scmState == EndpointServiceState.Running)
                {
                    _ipcFailures++;
                    if (_ipcFailures == 2)
                        TrayEventLogger.Warning(
                            "برنامه ناحیه اعلان نمی‌تواند وضعیت سرویس را بخواند: " + ex.Message);
                }
                else
                {
                    _ipcFailures = 0;
                }
            }
            ApplyStatus(TrayStatusResolver.Resolve(serviceState, _lastRuntime, _ipcFailures,
                DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyStatus(new TrayStatus(TrayHealthState.Error, EndpointServiceState.Unknown,
                _lastRuntime, "وضعیت در دسترس نیست", ex.Message));
        }
        finally
        {
            _refreshing = false;
        }
        return _lastStatus;
    }

    private void ApplyStatus(TrayStatus status)
    {
        _notifyIcon.Icon = _icons.Get(status);
        _notifyIcon.Text = LimitTooltip("دستیار پشتیبانی - " + status.Label);
        _statusMenuItem.Text = status.Label;
        _window.UpdateStatus(status);

        if (_lastStatus is { } previous && previous.Health != status.Health)
        {
            TrayEventLogger.Information(
                $"وضعیت دستیار پشتیبانی از {previous.Health} به {status.Health} تغییر کرد: {status.Label}.");
            if (status.Health is TrayHealthState.Error)
                _notifyIcon.ShowBalloonTip(5_000, "دستیار پشتیبانی نیاز به بررسی دارد",
                    status.Detail, Forms.ToolTipIcon.Error);
            else if (previous.Health is TrayHealthState.Error or TrayHealthState.Warning
                     && status.Health == TrayHealthState.Ready)
                _notifyIcon.ShowBalloonTip(4_000, "دستیار پشتیبانی دوباره متصل شد",
                    "دستیار پشتیبانی دوباره متصل و آماده استفاده است.", Forms.ToolTipIcon.Info);
        }
        _lastStatus = status;
    }

    /// <summary>
    /// While the chat window is closed or minimised, shows a pop-up next to the clock for a new
    /// reply or a question that waits for the user (for example "restart the PC"). A click opens
    /// that conversation. Only reads the user's own chats through the service pipe.
    /// </summary>
    private async Task CheckChatsAsync()
    {
        if (_checkingChats || _disposed || _shutdown.IsCancellationRequested) return;
        if (_lastStatus is not { Health: TrayHealthState.Ready or TrayHealthState.Warning }) return;
        _checkingChats = true;
        try
        {
            var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            var summaries = await _serviceClient.ListChatsAsync(sessionId, _shutdown.Token);
            if (_window.IsVisible && _window.WindowState != System.Windows.WindowState.Minimized)
            {
                _chatNotifier.MarkSeen(summaries);
                return;
            }
            foreach (var (conversationId, after) in _chatNotifier.Changed(summaries))
            {
                var delta = await _serviceClient.GetChatDeltaAsync(new EndpointAgents.Shared.Chat.ChatDeltaRequest(
                    conversationId, after, new EndpointAgents.Shared.Chat.ChatIdentity(
                        "S-1-0-0", Environment.UserName, sessionId)), _shutdown.Token);
                if (_chatNotifier.From(delta) is not { } notification) continue;
                _balloonConversation = notification.ConversationId;
                _notifyIcon.ShowBalloonTip(10_000, notification.Title, notification.Body,
                    notification.NeedsRestart ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            TrayEventLogger.Warning("پیام‌های تازهٔ گفتگو خوانده نشد: " + ex.Message);
        }
        finally
        {
            _checkingChats = false;
        }
    }

    private static string LimitTooltip(string value) => value.Length <= 63 ? value : value[..63];

    private void Exit()
    {
        Dispose();
        _app.Shutdown();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusTimer.Stop();
        _chatTimer.Stop();
        _showRequests.Unregister(null);
        _showRequested.Dispose();
        _shutdown.Cancel();
        _window.ClosePermanently();
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _icons.Dispose();
        _shutdown.Dispose();
        if (!_options.Preview)
            TrayEventLogger.Information("برنامه ناحیه اعلان دستیار پشتیبانی بسته شد.");
    }
}
