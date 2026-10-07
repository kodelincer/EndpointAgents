using Microsoft.UI.Xaml;

namespace EndpointAgents.Tray;

public sealed partial class App : Microsoft.UI.Xaml.Application
{
    private TrayApplicationController? _controller;
    private SupportWindow? _smokeWindow;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, args) =>
        {
            TrayEventLogger.Error("خطای پیش‌بینی‌نشده رابط کاربری: " + args.Exception.Message);
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var arguments = Environment.GetCommandLineArgs();
        if (arguments.Contains("--ui-smoke",
                StringComparer.OrdinalIgnoreCase))
        {
            _smokeWindow = new SupportWindow(() => Task.CompletedTask);
            _smokeWindow.ShowSection(SupportSection.Helpdesk);
            _smokeWindow.ShowSection(SupportSection.Status);
            _smokeWindow.ShowSection(SupportSection.Logs);
            _smokeWindow.ShowSection(SupportSection.Helpdesk);
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(8));
                Environment.Exit(0);
            });
            return;
        }
#if DEBUG
        var exportIcon = Array.IndexOf(arguments, "--export-icon");
        if (exportIcon > 0 && exportIcon + 1 < arguments.Length)
        {
            TrayIconFactory.WriteIco(arguments[exportIcon + 1], TrayHealthState.Ready);
            foreach (var state in Enum.GetValues<TrayHealthState>())
            {
                using var bitmap = TrayIconFactory.Draw(state, 64);
                bitmap.Save(Path.ChangeExtension(arguments[exportIcon + 1], $"{state}.png"));
            }
            Exit();
            return;
        }
        if (arguments.Contains("--ui-preview", StringComparer.OrdinalIgnoreCase))
        {
            _smokeWindow = new SupportWindow(() => Task.CompletedTask);
            _smokeWindow.LoadPreview(arguments.Contains("--dark"), arguments.Contains("--narrow"));
            _smokeWindow.ShowSection(arguments.Contains("--status") ? SupportSection.Status : SupportSection.Helpdesk);
            if (arguments.Contains("--max")) _smokeWindow.MaximizeForPreview();
            var snapshot = Array.IndexOf(arguments, "--snapshot");
            if (snapshot > 0 && snapshot + 1 < arguments.Length)
            {
                var window = _smokeWindow;
                var path = arguments[snapshot + 1];
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(4));
                    window.DispatcherQueue.TryEnqueue(async () =>
                    {
                        await window.SavePreviewSnapshotAsync(path);
                        Exit();
                    });
                });
            }
            return;
        }
#endif
        _controller = new TrayApplicationController(ExitApplication);
        if (arguments.Contains("--tray-smoke", StringComparer.OrdinalIgnoreCase))
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(TimeSpan.FromSeconds(2));
                Environment.Exit(0);
            });
        }
    }

    private void ExitApplication()
    {
        _controller?.Dispose();
        _controller = null;
        Exit();
    }
}
