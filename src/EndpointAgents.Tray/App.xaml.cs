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
