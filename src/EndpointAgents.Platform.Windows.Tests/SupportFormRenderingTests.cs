using EndpointAgents.Tray;
using System.Drawing;
using System.Windows.Forms;

namespace EndpointAgents.Platform.Windows.Tests;

public sealed class SupportFormRenderingTests
{
    [Fact]
    public void TextOnlyConversationRendersOnAnStaThread()
    {
        Exception? renderingError = null;
        var thread = new Thread(() =>
        {
            try
            {
                Application.EnableVisualStyles();
                using var form = new SupportForm(() => Task.CompletedTask);
                form.Show();
                Application.DoEvents();
                var messageInput = Assert.Single(form.Controls
                    .Find("SupportMessageInput", searchAllChildren: true)
                    .OfType<RichTextBox>());
                messageInput.Focus();
                messageInput.Text = "این متن آزمایشی باید از سمت راست نوشته شود.";
                messageInput.Select(messageInput.TextLength, 0);
                Application.DoEvents();
                Assert.Equal(RightToLeft.Yes, messageInput.RightToLeft);
                Assert.Equal(HorizontalAlignment.Right, messageInput.SelectionAlignment);
                using var bitmap = new Bitmap(form.Width, form.Height);
                form.DrawToBitmap(bitmap, new Rectangle(Point.Empty, form.Size));
                var snapshotPath = Environment.GetEnvironmentVariable(
                    "ENDPOINTAGENTS_UI_SNAPSHOT_PATH");
                if (!string.IsNullOrWhiteSpace(snapshotPath))
                    bitmap.Save(snapshotPath);
                form.ClosePermanently();
            }
            catch (Exception ex)
            {
                renderingError = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(10)),
            "The Helpdesk form did not finish rendering before the timeout.");
        Assert.Null(renderingError);
    }
}
