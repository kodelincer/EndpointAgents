using EndpointAgents.Tray;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Microsoft.Win32;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace EndpointAgents.TrayWeb;

/// <summary>
/// A frameless window whose whole client area is the local web UI. The page draws its own
/// title bar; this class only maps window commands and keeps the WebView2 locked to local content.
/// </summary>
public sealed class MainWindow : Window
{
    public const string AppHost = "tray.endpointagents.internal";
    private static readonly string AppOrigin = $"https://{AppHost}/";

    private readonly TrayOptions _options;
    private readonly WebView2 _web = new();
    private readonly TrayBridge _bridge;
    private readonly Func<Task<TrayStatus?>> _refreshStatus;
    private readonly TrayIconFactory _windowIcons = new();
    private readonly TaskCompletionSource _pageReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Task? _initialization;
    private TrayFace? _iconFace;
    private bool _allowClose;
    private bool _webUnavailable;

    public MainWindow(TrayOptions options, Func<Task<TrayStatus?>> refreshStatus, Action exit)
    {
        _options = options;
        _refreshStatus = refreshStatus;
        _bridge = new TrayBridge(this, refreshStatus, exit);
        ConsentPrompt = new WebConsentPrompt(this);

        Title = "دستیار پشتیبانی";
        Width = options.Width;
        Height = options.Height;
        MinWidth = 760;
        MinHeight = 640;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.CanResize;
        ShowInTaskbar = true;
        FlowDirection = FlowDirection.LeftToRight;
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(6),
            GlassFrameThickness = new Thickness(0, 0, 0, 1),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false
        });

        var dark = options.Dark || (!options.Light && SystemUsesDarkTheme());
        var ground = dark ? Color.FromRgb(0x14, 0x18, 0x21) : Color.FromRgb(0xF4, 0xF6, 0xF9);
        Background = new SolidColorBrush(ground);
        _web.DefaultBackgroundColor = System.Drawing.Color.FromArgb(ground.R, ground.G, ground.B);
        Content = _web;
        SetWindowIcon(TrayFace.Neutral);

        SourceInitialized += (_, _) => ApplyRoundedCorners();
        StateChanged += (_, _) =>
        {
            // A maximized frameless window overhangs the screen by the resize border.
            BorderThickness = WindowState == WindowState.Maximized
                ? SystemParameters.WindowResizeBorderThickness
                : new Thickness(0);
            _bridge.PostEvent("window", new { maximized = WindowState == WindowState.Maximized });
        };
        IsVisibleChanged += (_, _) => _bridge.PostEvent("window", new
        {
            visible = IsVisible,
            maximized = WindowState == WindowState.Maximized
        });
    }

    public IUserConsentPrompt ConsentPrompt { get; }
    public TrayOptions Options => _options;
    public TrayStatus? LastStatus { get; private set; }
    internal Task PageReady => _pageReady.Task;
    internal bool WebUnavailable => _webUnavailable;
    internal bool PersianKeyboardApplied { get; set; }

    /// <summary>Shows and focuses the window, optionally switching section ("chat", "status", "logs").</summary>
    public void ShowSection(string? section)
    {
        _initialization ??= InitializeWebAsync();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        PersianKeyboardApplied = false;
        if (section is not null) _bridge.PostEvent("navigate", new { section });
    }

    /// <summary>Opens the chat page on one conversation, for a click on a pop-up.</summary>
    public void ShowConversation(string conversationId)
    {
        ShowSection("chat");
        _bridge.PostEvent("navigate", new { section = "chat", conversationId });
    }

    public void UpdateStatus(TrayStatus status)
    {
        LastStatus = status;
        SetWindowIcon(TrayIconFactory.FaceFor(status));
        _bridge.PostEvent("status", TrayBridge.StatusDto(status));
    }

    public void PostEvent(string name, object data) => _bridge.PostEvent(name, data);

    internal void MarkPageReady() => _pageReady.TrySetResult();

    internal void PostToPage(string json)
    {
        if (_web.CoreWebView2 is { } core) core.PostWebMessageAsJson(json);
    }

    internal IntPtr Handle => new WindowInteropHelper(this).Handle;

    public void ClosePermanently()
    {
        _allowClose = true;
        Close();
        _windowIcons.Dispose();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);
        if (_allowClose) return;
        // Closing hides to the notification area; the Tray keeps running.
        e.Cancel = true;
        if (_options.Preview && _options.SnapshotPath is null)
        {
            System.Windows.Application.Current.Shutdown();
            return;
        }
        Hide();
    }

    private async Task InitializeWebAsync()
    {
        try
        {
            var dataFolder = Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData), "EndpointAgents", "Tray", _options.Preview ? "WebView2-preview" : "WebView2");
            var environment = await CoreWebView2Environment.CreateAsync(null, dataFolder);
            await _web.EnsureCoreWebView2Async(environment);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            FailWeb("اجزای نمایش ویندوز (WebView2) روی این رایانه نصب نیست. " +
                "لطفاً برنامه دستیار پشتیبانی را دوباره نصب کنید یا با پشتیبانی تماس بگیرید.");
            return;
        }
        catch (Exception ex)
        {
            FailWeb("پنجره دستیار پشتیبانی باز نشد: " + ex.Message);
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        var development = _options.Preview || Environment.GetEnvironmentVariable(
            "ENDPOINTAGENTS_TRAY_DEVTOOLS") == "1";
        settings.AreDevToolsEnabled = development;
        settings.AreDefaultContextMenusEnabled = development;
        settings.AreBrowserAcceleratorKeysEnabled = development;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = true;
        // The profile remembers this value, so set it on every start.
        core.Profile.PreferredColorScheme = _options.Dark ? CoreWebView2PreferredColorScheme.Dark
            : _options.Light ? CoreWebView2PreferredColorScheme.Light
            : CoreWebView2PreferredColorScheme.Auto;

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        core.SetVirtualHostNameToFolderMapping(AppHost, wwwroot,
            CoreWebView2HostResourceAccessKind.Deny);
        // Only the bundled page may load; nothing navigates, opens windows, downloads or asks permissions.
        core.NavigationStarting += (_, e) =>
        {
            if (!e.Uri.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) e.Cancel = true;
        };
        core.FrameNavigationStarting += (_, e) => e.Cancel = true;
        core.NewWindowRequested += (_, e) => e.Handled = true;
        core.DownloadStarting += (_, e) => e.Cancel = true;
        core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
        core.WebMessageReceived += (_, e) =>
        {
            if (!e.Source.StartsWith(AppOrigin, StringComparison.OrdinalIgnoreCase)) return;
            string? json;
            try { json = e.WebMessageAsJson; }
            catch { return; }
            _ = _bridge.HandleAsync(json);
        };
        core.ProcessFailed += (_, e) =>
        {
            TrayEventLogger.Warning($"نمایشگر پنجره دستیار متوقف شد ({e.ProcessFailedKind}); دوباره بارگذاری می‌شود.");
            if (e.ProcessFailedKind is CoreWebView2ProcessFailedKind.RenderProcessExited
                or CoreWebView2ProcessFailedKind.RenderProcessUnresponsive)
                core.Reload();
        };

        var query = _options.Preview
            ? "?preview=1" + (_options.Section is { } section ? "&section=" + Uri.EscapeDataString(section) : "")
            : string.Empty;
        _web.Source = new Uri($"{AppOrigin}index.html{query}");

        if (_options.SnapshotPath is { } snapshot)
            _ = SaveSnapshotAsync(core, snapshot);
    }

    private async Task SaveSnapshotAsync(CoreWebView2 core, string path)
    {
        await PageReady.WaitAsync(TimeSpan.FromSeconds(20));
        await Task.Delay(TimeSpan.FromSeconds(2));
        await using (var file = File.Create(path))
            await core.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, file);
        System.Windows.Application.Current.Shutdown();
    }

    private void FailWeb(string message)
    {
        _webUnavailable = true;
        _pageReady.TrySetResult();
        TrayEventLogger.Error(message);
        Hide();
        MessageBox.Show(message, "دستیار پشتیبانی", MessageBoxButton.OK, MessageBoxImage.Error,
            MessageBoxResult.OK, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign);
        _initialization = null;
    }

    private void SetWindowIcon(TrayFace face)
    {
        if (_iconFace == face) return;
        _iconFace = face;
        var icon = _windowIcons.Get(face, 32);
        Icon = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private void ApplyRoundedCorners()
    {
        // Windows 11 rounds frameless windows on request; Windows 10 ignores the attribute.
        var preference = 2;
        _ = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int));
    }

    internal void StartDrag()
    {
        if (WindowState == WindowState.Maximized) return;
        ReleaseCapture();
        SendMessage(Handle, 0x00A1, (IntPtr)2, IntPtr.Zero);
    }

    internal static bool SystemUsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern bool ReleaseCapture();

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);
}
