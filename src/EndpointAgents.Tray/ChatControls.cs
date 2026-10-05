using EndpointAgents.Shared.Chat;
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

internal sealed class ConversationPanel : Panel
{
    protected override Point ScrollToControl(Control activeControl)
    {
        if (IsHandleCreated && activeControl.IsHandleCreated)
        {
            var activeBounds = RectangleToClient(
                activeControl.RectangleToScreen(activeControl.ClientRectangle));
            if (ClientRectangle.IntersectsWith(activeBounds))
                return AutoScrollPosition;
        }
        return base.ScrollToControl(activeControl);
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
    private readonly RichTextBox _bodyView;

    public event MouseEventHandler? ConversationMouseWheel;

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
        var rtl = BidiText.IsRightToLeft(body);
        _bodyView = new RichTextBox
        {
            Name = "ChatMessageBody",
            BackColor = fromUser ? UserBubble : AssistantBubble,
            BorderStyle = BorderStyle.None,
            Cursor = Cursors.IBeam,
            DetectUrls = false,
            Font = Font,
            ForeColor = Ink,
            ReadOnly = true,
            RightToLeft = rtl ? RightToLeft.Yes : RightToLeft.No,
            ScrollBars = RichTextBoxScrollBars.None,
            ShortcutsEnabled = true,
            TabStop = true,
            Text = body,
            WordWrap = true
        };
        _bodyView.MouseWheel += (_, e) => ConversationMouseWheel?.Invoke(this, e);
        Controls.Add(_bodyView);
        RecalculateHeight();
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        ConversationMouseWheel?.Invoke(this, e);
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        RecalculateHeight();
        if (ClientSize.Width >= 180) _bodyView.Bounds = CalculateLayout().Body;
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
        var headerFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | (rtl ? TextFormatFlags.RightToLeft | TextFormatFlags.Right : TextFormatFlags.Left);
        using var headerFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, _author, headerFont, layout.Header, Ink, headerFlags);

        var timeFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            | (rtl ? TextFormatFlags.Left : TextFormatFlags.Right);
        using var timeFont = new Font("Segoe UI", 8F);
        TextRenderer.DrawText(e.Graphics, _time, timeFont, layout.Time, Muted, timeFlags);
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

internal sealed class ChatRunProgressView : Control
{
    private const int HeaderHeight = 44;
    private const int RowHeight = 28;
    private static readonly Color Surface = Color.White;
    private static readonly Color Ink = Color.FromArgb(37, 43, 52);
    private static readonly Color Muted = Color.FromArgb(91, 99, 111);
    private static readonly Color Line = Color.FromArgb(218, 222, 228);
    private static readonly Color Action = Color.FromArgb(37, 99, 178);
    private static readonly Color Success = Color.FromArgb(28, 125, 76);
    private static readonly Color Warning = Color.FromArgb(142, 91, 16);
    private static readonly Color Danger = Color.FromArgb(178, 48, 48);
    private readonly List<ChatRunProgressEvent> _events = [];
    private bool _expanded = true;
    private bool _terminal;

    public event EventHandler? ExpansionChanging;

    public ChatRunProgressView(string runId)
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.UserPaint
            | ControlStyles.SupportsTransparentBackColor, true);
        Name = "ChatProgress";
        Tag = runId;
        BackColor = Color.Transparent;
        Font = new Font("Segoe UI", 9F);
        Margin = new Padding(0, 0, 0, 10);
        TabStop = false;
        AccessibleRole = AccessibleRole.StaticText;
        UpdateHeight();
    }

    public void AddEvent(ChatRunProgressEvent progress)
    {
        _events.Add(progress);
        _events.Sort(static (left, right) => left.Sequence.CompareTo(right.Sequence));
        _terminal = progress.Stage is ChatProgressStage.Completed
            or ChatProgressStage.Failed or ChatProgressStage.ActionCompleted
            or ChatProgressStage.ActionRejected;
        if (_terminal)
        {
            _expanded = false;
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.PushButton;
        }
        else
        {
            _expanded = true;
            TabStop = false;
            Cursor = Cursors.Default;
            AccessibleRole = AccessibleRole.StaticText;
        }
        AccessibleName = CurrentLabel();
        AccessibleDescription = string.Join("، ", _events.Select(EventLabel));
        UpdateHeight();
        Invalidate();
    }

    public void RefreshElapsed() => Invalidate();

    public bool IsTerminal => _terminal;

    public string Summary => CurrentLabel();

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        if (!_terminal) return;
        ExpansionChanging?.Invoke(this, EventArgs.Empty);
        _expanded = !_expanded;
        UpdateHeight();
        Invalidate();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (_terminal && e.KeyCode is Keys.Enter or Keys.Space)
        {
            OnClick(EventArgs.Empty);
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (_events.Count == 0 || ClientSize.Width < 180) return;

        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var panel = PanelBounds();
        using var path = UiDrawing.CreateRoundedRectangle(panel, 10);
        using var fill = new SolidBrush(Surface);
        using var border = new Pen(Focused ? Action : Line, Focused ? 2F : 1F);
        e.Graphics.FillPath(fill, path);
        e.Graphics.DrawPath(border, path);

        var latest = _events[^1];
        var iconBounds = new Rectangle(panel.Right - 34, panel.Top + 12, 18, 18);
        DrawStatusIcon(e.Graphics, iconBounds, latest, current: true);
        var elapsedBounds = new Rectangle(panel.Left + 12, panel.Top + 9, 118, 24);
        TextRenderer.DrawText(e.Graphics, ElapsedText(), Font, elapsedBounds, Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        var currentBounds = new Rectangle(panel.Left + 136, panel.Top + 8,
            Math.Max(60, panel.Width - 184), 26);
        using var currentFont = new Font(Font, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, CurrentLabel(), currentFont, currentBounds, Ink,
            TextFormatFlags.RightToLeft | TextFormatFlags.Right
            | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | TextFormatFlags.NoPrefix);

        if (_terminal)
        {
            var toggleBounds = new Rectangle(panel.Left + 12, panel.Top + 29, 118, 12);
            using var toggleFont = new Font("Segoe UI", 7.5F);
            TextRenderer.DrawText(e.Graphics,
                _expanded ? "بستن جزئیات ▲" : "نمایش جزئیات ▼",
                toggleFont, toggleBounds, Muted,
                TextFormatFlags.Left | TextFormatFlags.NoPrefix);
        }

        if (!_expanded) return;
        using var line = new Pen(Line);
        using var timeFont = new Font("Segoe UI", 8F);
        e.Graphics.DrawLine(line, panel.Left + 12, panel.Top + HeaderHeight,
            panel.Right - 12, panel.Top + HeaderHeight);
        for (var index = 0; index < _events.Count; index++)
        {
            var progress = _events[index];
            var rowTop = panel.Top + HeaderHeight + 6 + index * RowHeight;
            var rowIcon = new Rectangle(panel.Right - 32, rowTop + 5, 14, 14);
            DrawStatusIcon(e.Graphics, rowIcon, progress,
                current: index == _events.Count - 1);
            var timeBounds = new Rectangle(panel.Left + 12, rowTop, 70, RowHeight);
            TextRenderer.DrawText(e.Graphics,
                ToPersianDigits(progress.CreatedAtUtc.LocalDateTime.ToString("HH:mm:ss")),
                timeFont, timeBounds, Muted,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter
                | TextFormatFlags.NoPrefix);
            var labelBounds = new Rectangle(panel.Left + 88, rowTop,
                Math.Max(80, panel.Width - 132), RowHeight);
            TextRenderer.DrawText(e.Graphics, EventLabel(progress), Font,
                labelBounds, Ink, TextFormatFlags.RightToLeft | TextFormatFlags.Right
                | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
                | TextFormatFlags.NoPrefix);
        }
    }

    private void DrawStatusIcon(Graphics graphics, Rectangle bounds,
        ChatRunProgressEvent progress, bool current)
    {
        var failed = progress.Stage == ChatProgressStage.Failed
            || progress.Outcome == ChatProgressOutcome.Failure;
        var rejected = progress.Stage == ChatProgressStage.ActionRejected;
        var complete = progress.Stage == ChatProgressStage.Completed
            || progress.Stage == ChatProgressStage.ActionCompleted
            || progress.Outcome == ChatProgressOutcome.Success || !current;
        var color = failed ? Danger : rejected ? Warning : complete ? Success : Action;
        var glyph = failed ? "!" : rejected ? "×" : complete ? "✓" : "…";
        using var brush = new SolidBrush(color);
        using var glyphFont = new Font("Segoe UI", bounds.Width <= 14 ? 7F : 9F,
            FontStyle.Bold);
        graphics.FillEllipse(brush, bounds);
        TextRenderer.DrawText(graphics, glyph, glyphFont, bounds, Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
            | TextFormatFlags.NoPrefix);
    }

    private Rectangle PanelBounds()
    {
        var width = Math.Min(620, Math.Max(300, ClientSize.Width - 96));
        return new Rectangle(ClientSize.Width - width - 18, 2,
            width, Math.Max(1, Height - 4));
    }

    private void UpdateHeight()
    {
        var desired = HeaderHeight + 6;
        if (_expanded) desired += 12 + _events.Count * RowHeight;
        if (Height != desired) Height = desired;
    }

    private string CurrentLabel() => _events.Count == 0
        ? "روند بررسی درخواست"
        : EventLabel(_events[^1]);

    private string ElapsedText()
    {
        if (_events.Count == 0) return string.Empty;
        var end = _terminal ? _events[^1].CreatedAtUtc : DateTimeOffset.UtcNow;
        var elapsed = end - _events[0].CreatedAtUtc;
        var seconds = Math.Max(0, (int)elapsed.TotalSeconds);
        var text = seconds < 60
            ? $"{seconds} ثانیه"
            : $"{seconds / 60} دقیقه و {seconds % 60} ثانیه";
        return ToPersianDigits(text);
    }

    private static string EventLabel(ChatRunProgressEvent progress) => progress.Stage switch
    {
        ChatProgressStage.Queued => "درخواست در صف بررسی قرار گرفت",
        ChatProgressStage.AssistantStarted => "درخواست به دستیار پشتیبانی ارسال شد",
        ChatProgressStage.WaitingForModel => "در انتظار پاسخ مدل",
        ChatProgressStage.EndpointCheckStarted => StartLabel(progress.Activity),
        ChatProgressStage.EndpointCheckCompleted => CompleteLabel(
            progress.Activity, progress.Outcome),
        ChatProgressStage.Retrying => "ارتباط با دستیار برقرار نشد؛ تلاش دوباره",
        ChatProgressStage.Completed => "پاسخ آماده شد",
        ChatProgressStage.Failed => "بررسی درخواست کامل نشد",
        ChatProgressStage.AwaitingApproval => "در انتظار تأیید شما برای اقدام پیشنهادی",
        ChatProgressStage.ActionStarted => progress.Activity
            == ChatProgressActivity.OfficeActivationRepair
                ? "تعمیر فعال‌سازی آفیس در حال اجرا است"
                : "اقدام تأییدشده در حال اجرا است",
        ChatProgressStage.ActionCompleted => progress.Outcome == ChatProgressOutcome.Failure
            ? progress.Activity == ChatProgressActivity.OfficeActivationRepair
                ? "تعمیر فعال‌سازی آفیس ناموفق بود"
                : "اجرای اقدام تأییدشده ناموفق بود"
            : progress.Activity == ChatProgressActivity.OfficeActivationRepair
                ? "تعمیر فعال‌سازی آفیس انجام شد"
                : "اقدام تأییدشده انجام شد",
        ChatProgressStage.ActionRejected => "اقدام پیشنهادی با انتخاب شما اجرا نشد",
        _ => "در حال بررسی درخواست"
    };

    private static string StartLabel(ChatProgressActivity activity) => activity switch
    {
        ChatProgressActivity.OfficeInventory =>
            "درخواست بررسی نصب و نسخه آفیس به دستیار پشتیبانی ارسال شد",
        ChatProgressActivity.OfficeActivationStatus =>
            "درخواست بررسی فعال‌سازی آفیس به دستیار پشتیبانی ارسال شد",
        ChatProgressActivity.OfficeRuntimeStatus =>
            "در حال بررسی برنامه‌های باز آفیس",
        ChatProgressActivity.WindowsActivationStatus =>
            "درخواست بررسی فعال‌سازی ویندوز به دستیار پشتیبانی ارسال شد",
        ChatProgressActivity.ProposedAction => "در حال آماده‌سازی اقدام پیشنهادی",
        _ => "درخواست بررسی به دستیار پشتیبانی ارسال شد"
    };

    private static string CompleteLabel(ChatProgressActivity activity,
        ChatProgressOutcome outcome)
    {
        var failed = outcome == ChatProgressOutcome.Failure;
        return activity switch
        {
            ChatProgressActivity.OfficeInventory => failed
                ? "بررسی نصب و نسخه آفیس ناموفق بود"
                : "بررسی نصب و نسخه آفیس کامل شد",
            ChatProgressActivity.OfficeActivationStatus => failed
                ? "بررسی فعال‌سازی آفیس ناموفق بود"
                : "بررسی فعال‌سازی آفیس کامل شد",
            ChatProgressActivity.OfficeRuntimeStatus => failed
                ? "بررسی برنامه‌های باز آفیس ناموفق بود"
                : "بررسی برنامه‌های باز آفیس کامل شد",
            ChatProgressActivity.WindowsActivationStatus => failed
                ? "بررسی فعال‌سازی ویندوز ناموفق بود"
                : "بررسی فعال‌سازی ویندوز کامل شد",
            ChatProgressActivity.ProposedAction => failed
                ? "آماده‌سازی اقدام پیشنهادی ناموفق بود"
                : "اقدام پیشنهادی آماده شد",
            _ => failed ? "بررسی رایانه ناموفق بود" : "بررسی رایانه کامل شد"
        };
    }

    private static string ToPersianDigits(string value)
    {
        const string western = "0123456789";
        const string persian = "۰۱۲۳۴۵۶۷۸۹";
        return string.Concat(value.Select(character =>
        {
            var index = western.IndexOf(character);
            return index >= 0 ? persian[index] : character;
        }));
    }
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
