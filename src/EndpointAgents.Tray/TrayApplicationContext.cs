using EndpointAgents.Shared.Support;
using Forms = System.Windows.Forms;

namespace EndpointAgents.Tray;

public sealed class TrayApplicationController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private readonly Forms.ToolStripMenuItem _statusMenuItem;
    private readonly TrayIconFactory _icons = new();
    private readonly WindowsServiceManager _serviceManager = new();
    private readonly ServiceIpcClient _serviceClient = new();
    private readonly Forms.Timer _statusTimer = new() { Interval = 2_000 };
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Action _exitApplication;
    private readonly Task _ipcServerTask;
    private readonly EventWaitHandle _showRequested = new(false, EventResetMode.AutoReset,
        ShowEventName(System.Diagnostics.Process.GetCurrentProcess().SessionId));
    private readonly RegisteredWaitHandle _showRequests;
    private SupportWindow? _supportWindow;
    private AgentRuntimeSnapshot? _lastRuntime;
    private TrayStatus? _lastStatus;
    private int _ipcFailures;
    private bool _refreshing;
    private bool _disposed;

    public TrayApplicationController(Action exitApplication)
    {
        _exitApplication = exitApplication;
        var menu = new Forms.ContextMenuStrip { RightToLeft = Forms.RightToLeft.Yes };
        _statusMenuItem = new Forms.ToolStripMenuItem("در حال بررسی وضعیت دستیار پشتیبانی…")
        {
            Enabled = false
        };
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("باز کردن دستیار پشتیبانی", null,
            (_, _) => OpenWindow(SupportSection.Helpdesk));
        menu.Items.Add("مشاهده گزارش‌ها", null,
            (_, _) => OpenWindow(SupportSection.Logs));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("خروج از برنامه", null, (_, _) => _exitApplication());

        var initialIcon = _icons.Get(TrayHealthState.Checking);
        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "دستیار پشتیبانی - در حال بررسی",
            Icon = initialIcon,
            Visible = true,
            ContextMenuStrip = menu
        };
        _notifyIcon.DoubleClick += (_, _) => OpenWindow(SupportSection.Helpdesk);

        var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
        _showRequests = ThreadPool.RegisterWaitForSingleObject(
            _showRequested, (_, _) => dispatcher.TryEnqueue(() =>
            {
                if (!_disposed) OpenWindow(SupportSection.Helpdesk);
            }), null, Timeout.Infinite, executeOnlyOnce: false);

        var server = new TrayIpcServer(System.Diagnostics.Process.GetCurrentProcess().SessionId);
        _ipcServerTask = server.RunAsync(_shutdown.Token);
        _ = _ipcServerTask.ContinueWith(task =>
            TrayEventLogger.Error("کانال فرمان نشست کاربر متوقف شد: " +
                task.Exception?.GetBaseException().Message),
            TaskContinuationOptions.OnlyOnFaulted);

        _statusTimer.Tick += async (_, _) => await RefreshStatusAsync();
        _statusTimer.Start();
        TrayEventLogger.Information(
            "برنامه ناحیه اعلان دستیار پشتیبانی برای نشست کاربر اجرا شد.");
        _ = RefreshStatusAsync();
    }

    private static string ShowEventName(int sessionId) =>
        $@"Local\EndpointAgents.Tray.Show.{sessionId}";

    // Called by a second launch of the tray in the same session.
    public static void SignalShowWindow(int sessionId)
    {
        if (EventWaitHandle.TryOpenExisting(ShowEventName(sessionId), out var handle))
            using (handle) handle.Set();
    }

    private SupportWindow EnsureWindow() =>
        _supportWindow ??= new SupportWindow(RefreshStatusAsync);

    private void OpenWindow(SupportSection section)
    {
        var window = EnsureWindow();
        window.ShowSection(section);
        if (_lastStatus is { } status) window.UpdateStatus(status);
        if (section == SupportSection.Logs) _ = RefreshStatusAsync();
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing || _shutdown.IsCancellationRequested) return;
        _refreshing = true;
        try
        {
            var scmState = await Task.Run(_serviceManager.GetStatus, _shutdown.Token);
            var serviceState = scmState;
            try
            {
                using var probeTimeout = CancellationTokenSource.CreateLinkedTokenSource(
                    _shutdown.Token);
                if (scmState != EndpointServiceState.Running)
                    probeTimeout.CancelAfter(TimeSpan.FromMilliseconds(250));

                _lastRuntime = await _serviceClient.GetStatusAsync(probeTimeout.Token);
                _ipcFailures = 0;
                serviceState = EndpointServiceStateResolver.ResolveObservedState(
                    scmState, ipcAvailable: true);
            }
            catch (Exception ex) when (!_shutdown.IsCancellationRequested)
            {
                _lastRuntime = null;
                if (scmState == EndpointServiceState.Running)
                {
                    _ipcFailures++;
                    if (_ipcFailures == 2)
                        TrayEventLogger.Warning(
                            "برنامه ناحیه اعلان نمی‌تواند وضعیت سرویس را بخواند: " +
                            ex.Message);
                }
                else
                {
                    _ipcFailures = 0;
                }
            }

            ApplyStatus(TrayStatusResolver.Resolve(serviceState, _lastRuntime,
                _ipcFailures, DateTimeOffset.UtcNow));
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested) { }
        catch (Exception ex)
        {
            ApplyStatus(new TrayStatus(TrayHealthState.Error,
                EndpointServiceState.Unknown, _lastRuntime,
                "وضعیت در دسترس نیست", ex.Message));
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void ApplyStatus(TrayStatus status)
    {
        _notifyIcon.Icon = _icons.Get(status.Health);
        _notifyIcon.Text = LimitTooltip("دستیار پشتیبانی - " + status.Label);
        _statusMenuItem.Text = status.Label;
        _supportWindow?.UpdateStatus(status);

        if (_lastStatus is { } previous && previous.Health != status.Health)
        {
            TrayEventLogger.Information(
                $"وضعیت دستیار پشتیبانی از {previous.Health} به {status.Health} تغییر کرد: {status.Label}.");
            if (status.Health is TrayHealthState.Error)
            {
                _notifyIcon.BalloonTipTitle = "دستیار پشتیبانی نیاز به بررسی دارد";
                _notifyIcon.BalloonTipText = status.Detail;
                _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Error;
                _notifyIcon.ShowBalloonTip(5_000);
            }
            else if (previous.Health is TrayHealthState.Error or TrayHealthState.Warning
                     && status.Health == TrayHealthState.Ready)
            {
                _notifyIcon.BalloonTipTitle = "دستیار پشتیبانی دوباره متصل شد";
                _notifyIcon.BalloonTipText = "دستیار پشتیبانی دوباره متصل و آماده استفاده است.";
                _notifyIcon.BalloonTipIcon = Forms.ToolTipIcon.Info;
                _notifyIcon.ShowBalloonTip(4_000);
            }
        }
        _lastStatus = status;
    }

    private static string LimitTooltip(string value) =>
        value.Length <= 63 ? value : value[..63];

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _statusTimer.Stop();
        _showRequests.Unregister(null);
        _showRequested.Dispose();
        _shutdown.Cancel();
        _supportWindow?.ClosePermanently();
        _supportWindow = null;
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
        _statusTimer.Dispose();
        _icons.Dispose();
        _shutdown.Dispose();
        TrayEventLogger.Information(
            "برنامه ناحیه اعلان دستیار پشتیبانی بسته شد.");
    }
}
