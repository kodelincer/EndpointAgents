namespace EndpointAgents.Tray;

public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _notifyIcon;

    public TrayApplicationContext()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Open Support", null, (_, _) => OpenSupportForm());
        menu.Items.Add("Connection Status", null, (_, _) => ShowConnectionStatus());
        menu.Items.Add("Exit Tray", null, (_, _) => ExitThread());

        _notifyIcon = new NotifyIcon
        {
            Text = "Endpoint Agent",
            Icon = SystemIcons.Application,
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => OpenSupportForm();
    }

    private static void OpenSupportForm()
    {
        using var form = new SupportForm();
        form.ShowDialog();
    }

    private static void ShowConnectionStatus()
    {
        MessageBox.Show(
            "Connection status will be read from the background service in the next phase.",
            "Endpoint Agent",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    protected override void ExitThreadCore()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();

        base.ExitThreadCore();
    }
}