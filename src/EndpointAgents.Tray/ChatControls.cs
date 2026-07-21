using System.Drawing.Drawing2D;

namespace EndpointAgents.Tray;

internal static class BidiText
{
    public static bool IsRightToLeft(string? text, bool defaultRightToLeft = true)
    {
        if (string.IsNullOrWhiteSpace(text)) return defaultRightToLeft;

        foreach (var character in text)
        {
            if (IsRightToLeftCharacter(character)) return true;
            if (char.IsLetter(character)) return false;
        }

        return defaultRightToLeft;
    }

    private static bool IsRightToLeftCharacter(char character) =>
        character is >= '\u0590' and <= '\u08ff'
            or >= '\ufb1d' and <= '\ufdff'
            or >= '\ufe70' and <= '\ufeff';
}

internal sealed class RoundedPanel : Panel
{
    private readonly Color _borderColor;
    private readonly int _cornerRadius;

    public RoundedPanel(Color borderColor, int cornerRadius = 10)
    {
        _borderColor = borderColor;
        _cornerRadius = cornerRadius;
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width <= 1 || ClientSize.Height <= 1) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = UiDrawing.CreateRoundedRectangle(
            new Rectangle(0, 0, ClientSize.Width - 1, ClientSize.Height - 1), _cornerRadius);
        using var pen = new Pen(_borderColor);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class RtlMessageBox : RichTextBox
{
    private bool _applyingDirection;

    public RtlMessageBox()
    {
        RightToLeft = RightToLeft.Yes;
        DetectUrls = false;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyRightToLeftParagraphs(formatAll: true);
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        ApplyRightToLeftParagraphs(formatAll: true);
    }

    protected override void OnSelectionChanged(EventArgs e)
    {
        base.OnSelectionChanged(e);
        ApplyRightToLeftParagraphs(formatAll: false);
    }

    private void ApplyRightToLeftParagraphs(bool formatAll)
    {
        if (_applyingDirection || !IsHandleCreated) return;
        _applyingDirection = true;
        try
        {
            RightToLeft = RightToLeft.Yes;
            var selectionStart = SelectionStart;
            var selectionLength = SelectionLength;
            if (formatAll) SelectAll();
            SelectionAlignment = HorizontalAlignment.Right;
            if (formatAll)
            {
                Select(selectionStart, selectionLength);
                SelectionAlignment = HorizontalAlignment.Right;
            }
        }
        finally
        {
            _applyingDirection = false;
        }
    }
}

internal sealed class ChatMessageView : Control
{
    private static readonly Color AssistantBubble = Color.FromArgb(238, 241, 245);
    private static readonly Color UserBubble = Color.FromArgb(218, 232, 250);
    private static readonly Color UserLine = Color.FromArgb(174, 199, 232);
    private static readonly Color Ink = Color.FromArgb(37, 43, 52);
    private static readonly Color Muted = Color.FromArgb(91, 99, 111);
    private static readonly Color Line = Color.FromArgb(218, 222, 228);
    private static readonly Color Action = Color.FromArgb(37, 99, 178);
    private readonly string _body;
    private readonly string _author;
    private readonly string _time;
    private readonly bool _fromUser;

    public ChatMessageView(string author, string body, bool fromUser, DateTime timestamp)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor, true);
        _author = author;
        _body = body;
        _fromUser = fromUser;
        _time = timestamp.ToString("HH:mm");
        Font = new Font("Segoe UI", 10F);
        BackColor = Color.Transparent;
        Margin = new Padding(0, 0, 0, 10);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        AccessibleName = author;
        AccessibleDescription = body;
        RecalculateHeight();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateHeight();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (ClientSize.Width < 180) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var layout = CalculateLayout();
        using var path = UiDrawing.CreateRoundedRectangle(layout.Bubble, 12);
        using var fill = new SolidBrush(_fromUser ? UserBubble : AssistantBubble);
        e.Graphics.FillPath(fill, path);
        using var border = new Pen(_fromUser ? UserLine : Line);
        e.Graphics.DrawPath(border, path);

        if (!_fromUser)
        {
            var avatar = new Rectangle(18, layout.Bubble.Top + 2, 30, 30);
            using var avatarBrush = new SolidBrush(Action);
            using var avatarFont = new Font("Segoe UI", 8F, FontStyle.Bold);
            e.Graphics.FillEllipse(avatarBrush, avatar);
            TextRenderer.DrawText(e.Graphics, "AI", avatarFont,
                avatar, Color.White, TextFormatFlags.HorizontalCenter
                | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        }

        var rtl = BidiText.IsRightToLeft(_body);
        var bodyFlags = BodyFlags(rtl);
        var headerFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | (rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);
        using var headerFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, _author, headerFont, layout.Header, Ink, headerFlags);

        var timeFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | (rtl ? TextFormatFlags.Left : TextFormatFlags.Right);
        using var timeFont = new Font("Segoe UI", 8F);
        TextRenderer.DrawText(e.Graphics, _time, timeFont, layout.Time, Muted, timeFlags);
        TextRenderer.DrawText(e.Graphics, _body, Font, layout.Body, Ink, bodyFlags);
    }

    private void RecalculateHeight()
    {
        if (ClientSize.Width < 180) return;
        var desiredHeight = CalculateLayout().Bubble.Bottom + 4;
        if (Height != desiredHeight) Height = desiredHeight;
    }

    private MessageLayout CalculateLayout()
    {
        var maximumBubbleWidth = Math.Max(220, Math.Min(620, ClientSize.Width - 96));
        var textWidth = maximumBubbleWidth - 30;
        var rtl = BidiText.IsRightToLeft(_body);
        var measured = TextRenderer.MeasureText(_body, Font,
            new Size(textWidth, int.MaxValue), BodyFlags(rtl));
        var bubbleWidth = Math.Min(maximumBubbleWidth, Math.Max(210, measured.Width + 30));
        var bubbleHeight = Math.Max(78, measured.Height + 49);
        var bubbleX = _fromUser
            ? ClientSize.Width - bubbleWidth - 18
            : 56;
        var bubble = new Rectangle(bubbleX, 3, bubbleWidth, bubbleHeight);
        var header = new Rectangle(bubble.Left + 15, bubble.Top + 10,
            bubble.Width - 80, 19);
        var time = rtl
            ? new Rectangle(bubble.Left + 15, bubble.Top + 11, 43, 18)
            : new Rectangle(bubble.Right - 58, bubble.Top + 11, 43, 18);
        var body = new Rectangle(bubble.Left + 15, bubble.Top + 34,
            bubble.Width - 30, bubble.Height - 44);
        return new MessageLayout(bubble, header, time, body);
    }

    private static TextFormatFlags BodyFlags(bool rightToLeft) =>
        TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix
        | (rightToLeft ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);

    private sealed record MessageLayout(Rectangle Bubble, Rectangle Header,
        Rectangle Time, Rectangle Body);
}

internal static class UiDrawing
{
    public static GraphicsPath CreateRoundedRectangle(Rectangle bounds, int radius)
    {
        var diameter = Math.Min(Math.Min(radius * 2, bounds.Width), bounds.Height);
        var arc = new Rectangle(bounds.Location, new Size(diameter, diameter));
        var path = new GraphicsPath();
        path.AddArc(arc, 180, 90);
        arc.X = bounds.Right - diameter;
        path.AddArc(arc, 270, 90);
        arc.Y = bounds.Bottom - diameter;
        path.AddArc(arc, 0, 90);
        arc.X = bounds.Left;
        path.AddArc(arc, 90, 90);
        path.CloseFigure();
        return path;
    }
}
