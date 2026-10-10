using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EndpointAgents.Tray;

// One mark everywhere (tray, taskbar, window, exe): a white chat bubble on a disc whose
// colour is the agent's health. Blue = connected, red = disconnected or failing,
// amber = paused by the user, grey = still checking.
public sealed class TrayIconFactory : IDisposable
{
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private readonly Dictionary<(TrayHealthState, int), Icon> _icons = new();

    public Icon Get(TrayHealthState state, int size = 0)
    {
        if (size <= 0) size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (_icons.TryGetValue((state, size), out var icon)) return icon;
        icon = Create(state, size);
        _icons[(state, size)] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }

    public static Color StateColor(TrayHealthState state) => state switch
    {
        TrayHealthState.Ready => Color.FromArgb(0x33, 0x90, 0xEC),
        TrayHealthState.Paused => Color.FromArgb(0xF0, 0x8C, 0x00),
        TrayHealthState.Warning or TrayHealthState.Error or TrayHealthState.Stopped
            => Color.FromArgb(0xE5, 0x39, 0x35),
        _ => Color.FromArgb(0x8A, 0x96, 0xA3)
    };

    public static Bitmap Draw(TrayHealthState state, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
        graphics.Clear(Color.Transparent);
        graphics.ScaleTransform(size / 32f, size / 32f);

        var color = StateColor(state);
        static int Lighten(int channel) => channel + (255 - channel) * 3 / 10;
        var light = Color.FromArgb(Lighten(color.R), Lighten(color.G), Lighten(color.B));
        using (var disc = new LinearGradientBrush(new PointF(0, 0), new PointF(0, 32), light, color))
            graphics.FillEllipse(disc, 0.5f, 0.5f, 31, 31);

        // Bubble body with a tail at the bottom-left.
        using var bubble = new GraphicsPath();
        const float left = 7.5f, top = 8.5f, right = 24.5f, bottom = 20.5f, radius = 4.5f;
        bubble.AddArc(left, top, radius * 2, radius * 2, 180, 90);
        bubble.AddArc(right - radius * 2, top, radius * 2, radius * 2, 270, 90);
        bubble.AddArc(right - radius * 2, bottom - radius * 2, radius * 2, radius * 2, 0, 90);
        bubble.AddLine(right - radius, bottom, 15f, bottom);
        bubble.AddLine(15f, bottom, 9.5f, 25f);
        bubble.AddLine(9.5f, 25f, 10.5f, bottom - 0.5f);
        bubble.AddArc(left, bottom - radius * 2, radius * 2, radius * 2, 90, 90);
        bubble.CloseFigure();
        using (var white = new SolidBrush(Color.White))
            graphics.FillPath(white, bubble);

        using var dots = new SolidBrush(color);
        foreach (var x in new[] { 12f, 16f, 20f })
            graphics.FillEllipse(dots, x - 1.6f, 14.5f - 1.6f, 3.2f, 3.2f);
        return bitmap;
    }

    // Writes a multi-size PNG-compressed .ico (the exe icon in Assets/AppIcon.ico).
    public static void WriteIco(string path, TrayHealthState state)
    {
        var images = IcoSizes.Select(size =>
        {
            using var bitmap = Draw(state, size);
            using var stream = new MemoryStream();
            bitmap.Save(stream, ImageFormat.Png);
            return (size, data: stream.ToArray());
        }).ToList();

        using var writer = new BinaryWriter(File.Create(path));
        writer.Write((short)0);
        writer.Write((short)1);
        writer.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, data) in images)
        {
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)(size >= 256 ? 0 : size));
            writer.Write((byte)0);
            writer.Write((byte)0);
            writer.Write((short)1);
            writer.Write((short)32);
            writer.Write(data.Length);
            writer.Write(offset);
            offset += data.Length;
        }
        foreach (var (_, data) in images) writer.Write(data);
    }

    private static Icon Create(TrayHealthState state, int size)
    {
        using var bitmap = Draw(state, size);
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
