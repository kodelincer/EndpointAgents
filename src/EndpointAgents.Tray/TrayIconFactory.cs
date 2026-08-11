using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

using System.Drawing;

namespace EndpointAgents.Tray;

public sealed class TrayIconFactory : IDisposable
{
    private readonly Dictionary<TrayHealthState, Icon> _icons = new();

    public Icon Get(TrayHealthState state)
    {
        if (_icons.TryGetValue(state, out var icon)) return icon;
        icon = Create(state);
        _icons[state] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }

    private static Icon Create(TrayHealthState state)
    {
        using var bitmap = new Bitmap(32, 32, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var graphite = Color.FromArgb(62, 68, 78);
        using var background = new SolidBrush(graphite);
        graphics.FillEllipse(background, 3, 3, 26, 26);
        using var white = new SolidBrush(Color.White);
        using var whitePen = new Pen(Color.White, 2.4f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round
        };

        switch (state)
        {
            case TrayHealthState.Ready:
            case TrayHealthState.Warning:
                graphics.FillPolygon(white, [new PointF(12, 9), new PointF(12, 23), new PointF(23, 16)]);
                break;
            case TrayHealthState.Paused:
                graphics.FillRectangle(white, 11, 9, 4, 14);
                graphics.FillRectangle(white, 18, 9, 4, 14);
                break;
            case TrayHealthState.Stopped:
                graphics.FillRectangle(white, 11, 11, 11, 11);
                break;
            case TrayHealthState.Error:
                graphics.DrawLine(whitePen, 16, 9, 16, 18);
                graphics.FillEllipse(white, 14.5f, 21, 3, 3);
                break;
            default:
                graphics.DrawEllipse(whitePen, 10, 10, 12, 12);
                graphics.DrawLine(whitePen, 16, 12, 16, 16);
                graphics.DrawLine(whitePen, 16, 16, 20, 18);
                break;
        }

        var badge = state switch
        {
            TrayHealthState.Ready => Color.FromArgb(38, 153, 96),
            TrayHealthState.Paused or TrayHealthState.Warning => Color.FromArgb(218, 149, 32),
            TrayHealthState.Error => Color.FromArgb(205, 62, 62),
            _ => Color.FromArgb(126, 132, 142)
        };
        if (state is TrayHealthState.Ready or TrayHealthState.Paused
            or TrayHealthState.Warning or TrayHealthState.Error)
        {
            using var badgeBrush = new SolidBrush(badge);
            using var rim = new Pen(Color.White, 1.5f);
            graphics.FillEllipse(badgeBrush, 22, 22, 8, 8);
            graphics.DrawEllipse(rim, 22, 22, 8, 8);
        }

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            NativeMethods.DestroyIcon(handle);
        }
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyIcon(IntPtr handle);
    }
}
