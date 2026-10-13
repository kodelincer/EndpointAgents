using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace EndpointAgents.Tray;

/// <summary>How the assistant looks. The face carries the mood, its colour carries the severity.</summary>
public enum TrayFace
{
    /// <summary>Service running and connected to the support server (blue).</summary>
    Happy,
    /// <summary>Still checking or starting (blue-grey).</summary>
    Neutral,
    /// <summary>Functions paused by the user (blue-grey, eyes closed).</summary>
    Sleepy,
    /// <summary>Service is up but the support server is not reachable, or the last task failed (strong yellow).</summary>
    Sad,
    /// <summary>The background service is stopped, missing or not answering (red).</summary>
    Angry
}

// One mark everywhere (tray, taskbar, window, exe): the head of the assistant robot.
// Its face and colour follow the agent's state: happy blue, sad yellow, angry red.
public sealed class TrayIconFactory : IDisposable
{
    private static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 256];
    private readonly Dictionary<(TrayFace, int), Icon> _icons = new();

    public static TrayFace FaceFor(TrayHealthState health) => health switch
    {
        TrayHealthState.Ready => TrayFace.Happy,
        TrayHealthState.Paused => TrayFace.Sleepy,
        TrayHealthState.Warning => TrayFace.Sad,
        TrayHealthState.Error or TrayHealthState.Stopped => TrayFace.Angry,
        _ => TrayFace.Neutral
    };

    /// <summary>
    /// Sad when the service itself is fine but the support server is not; angry when the service
    /// cannot be reached at all.
    /// </summary>
    public static TrayFace FaceFor(TrayStatus status) => status.Health switch
    {
        TrayHealthState.Error when status.Runtime is not null
            && status.ServiceState is EndpointServiceState.Running or EndpointServiceState.Interactive
            => TrayFace.Sad,
        _ => FaceFor(status.Health)
    };

    public Icon Get(TrayHealthState state, int size = 0) => Get(FaceFor(state), size);

    public Icon Get(TrayStatus status, int size = 0) => Get(FaceFor(status), size);

    public Icon Get(TrayFace face, int size = 0)
    {
        if (size <= 0) size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
        if (_icons.TryGetValue((face, size), out var icon)) return icon;
        icon = Create(face, size);
        _icons[(face, size)] = icon;
        return icon;
    }

    public void Dispose()
    {
        foreach (var icon in _icons.Values) icon.Dispose();
        _icons.Clear();
    }

    public static Color FaceColor(TrayFace face) => face switch
    {
        TrayFace.Happy => Color.FromArgb(0x2F, 0x6B, 0xFF),
        TrayFace.Sad => Color.FromArgb(0xFF, 0xB4, 0x00),
        TrayFace.Angry => Color.FromArgb(0xEF, 0x3B, 0x3B),
        _ => Color.FromArgb(0x7F, 0x93, 0xBD)
    };

    public static Color StateColor(TrayHealthState state) => FaceColor(FaceFor(state));

    private static Color Mix(Color color, Color other, float amount) => Color.FromArgb(
        (int)(color.R + (other.R - color.R) * amount),
        (int)(color.G + (other.G - color.G) * amount),
        (int)(color.B + (other.B - color.B) * amount));

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        var d = r * 2;
        path.AddArc(x, y, d, d, 180, 90);
        path.AddArc(x + w - d, y, d, d, 270, 90);
        path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
        path.AddArc(x, y + h - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Quadratic curve start -> end through a control point, as GDI+ draws cubics.
    private static void Quad(Graphics g, Pen pen, float x0, float y0, float cx, float cy, float x1, float y1) =>
        g.DrawBezier(pen, x0, y0, x0 + 2f / 3 * (cx - x0), y0 + 2f / 3 * (cy - y0),
            x1 + 2f / 3 * (cx - x1), y1 + 2f / 3 * (cy - y1), x1, y1);

    public static Bitmap Draw(TrayHealthState state, int size) => Draw(FaceFor(state), size);

    public static Bitmap Draw(TrayFace face, int size)
    {
        var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        g.ScaleTransform(size / 64f, size / 64f);

        var accent = FaceColor(face);
        var navy = Color.FromArgb(0x16, 0x21, 0x3F);
        var dark = Mix(accent, navy, 0.25f);
        var eye = Mix(accent, Color.White, 0.12f);

        Pen Round(Color c, float w) => new(c, w)
        {
            StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round
        };

        using (var stem = Round(dark, 2.6f))
        using (var ball = new SolidBrush(accent))
        {
            g.DrawLine(stem, 20, 14, 17, 6);
            g.DrawLine(stem, 44, 14, 47, 6);
            g.FillEllipse(ball, 16.5f - 3.8f, 5 - 3.8f, 7.6f, 7.6f);
            g.FillEllipse(ball, 47.5f - 3.8f, 5 - 3.8f, 7.6f, 7.6f);
            using var left = RoundedRect(1.5f, 26, 8.5f, 18, 4.2f);
            using var right = RoundedRect(54, 26, 8.5f, 18, 4.2f);
            g.FillPath(ball, left);
            g.FillPath(ball, right);
            g.FillEllipse(ball, 36 - 3.8f, 57 - 3.8f, 7.6f, 7.6f);
        }
        using (var boom = Round(accent, 3.6f))
            g.DrawBezier(boom, 58, 42, 58, 53, 50, 57, 39, 57);

        using (var head = RoundedRect(7, 13, 50, 42, 18))
        using (var shell = new LinearGradientBrush(new PointF(0, 13), new PointF(0, 55),
            Mix(accent, Color.White, 0.88f), Mix(accent, Color.White, 0.55f)))
            g.FillPath(shell, head);
        using (var visor = RoundedRect(13, 19, 38, 30, 13))
        using (var screen = new SolidBrush(navy))
            g.FillPath(screen, visor);

        using var line = Round(eye, 3.2f);
        using var fill = new SolidBrush(eye);
        void Eye(float cx, float rx, float ry) => g.FillEllipse(fill, cx - rx, 32 - ry, rx * 2, ry * 2);
        switch (face)
        {
            case TrayFace.Happy:
                Quad(g, line, 20.5f, 33.5f, 24f, 27.5f, 27.5f, 33.5f);
                Quad(g, line, 36.5f, 33.5f, 40f, 27.5f, 43.5f, 33.5f);
                Quad(g, line, 24.5f, 40f, 32f, 48f, 39.5f, 40f);
                break;
            case TrayFace.Neutral:
                Eye(24, 3.6f, 4.2f);
                Eye(40, 3.6f, 4.2f);
                g.DrawLine(line, 27, 41.5f, 37, 41.5f);
                break;
            case TrayFace.Sleepy:
                Quad(g, line, 20.5f, 32f, 24f, 35.5f, 27.5f, 32f);
                Quad(g, line, 36.5f, 32f, 40f, 35.5f, 43.5f, 32f);
                Quad(g, line, 28f, 41.5f, 32f, 43.3f, 36f, 41.5f);
                break;
            case TrayFace.Sad:
                Eye(24.5f, 3.6f, 4.2f);
                Eye(39.5f, 3.6f, 4.2f);
                g.DrawLine(line, 19.5f, 27f, 28.5f, 23.5f);
                g.DrawLine(line, 44.5f, 27f, 35.5f, 23.5f);
                Quad(g, line, 24.5f, 44f, 32f, 36f, 39.5f, 44f);
                using (var tear = new SolidBrush(Color.FromArgb(0x8F, 0xD0, 0xFF)))
                    g.FillEllipse(tear, 42f, 38f, 6f, 7f);
                break;
            case TrayFace.Angry:
                Eye(24.5f, 3.6f, 2.8f);
                Eye(39.5f, 3.6f, 2.8f);
                g.DrawLine(line, 19f, 24.5f, 29f, 29f);
                g.DrawLine(line, 45f, 24.5f, 35f, 29f);
                g.DrawLines(line, new PointF[]
                {
                    new(24f, 44.5f), new(28f, 42f), new(32f, 44.5f), new(36f, 42f), new(40f, 44.5f)
                });
                break;
        }
        return bitmap;
    }

    // Writes a multi-size PNG-compressed .ico (the exe icon in Assets/AppIcon.ico).
    public static void WriteIco(string path, TrayHealthState state) => WriteIco(path, FaceFor(state));

    public static void WriteIco(string path, TrayFace face)
    {
        var images = IcoSizes.Select(size =>
        {
            using var bitmap = Draw(face, size);
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

    private static Icon Create(TrayFace face, int size)
    {
        using var bitmap = Draw(face, size);
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
