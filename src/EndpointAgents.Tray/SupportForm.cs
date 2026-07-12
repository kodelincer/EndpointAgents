namespace EndpointAgents.Tray;

public sealed class SupportForm : Form
{
    private readonly TextBox _problemTextBox;
    private readonly Button _sendButton;

    public SupportForm()
    {
        Text = "Endpoint Agent Support";
        Width = 600;
        Height = 400;
        StartPosition = FormStartPosition.CenterScreen;

        _problemTextBox = new TextBox
        {
            Multiline = true,
            Dock = DockStyle.Fill,
            PlaceholderText = "Describe your IT problem here..."
        };

        _sendButton = new Button
        {
            Text = "Send to Helpdesk Agent",
            Dock = DockStyle.Bottom,
            Height = 40
        };

        _sendButton.Click += (_, _) =>
        {
            MessageBox.Show(
                "In the next phase, this message will be sent to the background service and then to the control plane.",
                "Endpoint Agent",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
        };

        Controls.Add(_problemTextBox);
        Controls.Add(_sendButton);
    }
}