using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using System.Runtime.InteropServices;
using System.Text;

namespace EndpointAgents.Tray;

public enum SupportFormTab
{
    Helpdesk,
    Status,
    Logs
}

public sealed class SupportForm : Form
{
    private const int MessageCharacterLimit = 1_000;
    private static readonly Color Canvas = Color.FromArgb(247, 248, 250);
    private static readonly Color Surface = Color.White;
    private static readonly Color Ink = Color.FromArgb(37, 43, 52);
    private static readonly Color Muted = Color.FromArgb(91, 99, 111);
    private static readonly Color Line = Color.FromArgb(218, 222, 228);
    private static readonly Color Action = Color.FromArgb(37, 99, 178);
    private readonly Func<Task> _refreshStatus;
    private readonly ServiceIpcClient _serviceClient = new();
    private readonly EndpointEventLogReader _logReader = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TabControl _tabs = new();
    private readonly TabPage _helpdeskTab = new("درخواست پشتیبانی");
    private readonly TabPage _statusTab = new("وضعیت و کنترل");
    private readonly TabPage _logsTab = new("گزارش‌ها");
    private readonly PictureBox _statusIcon = new();
    private readonly Label _statusLabel = new();
    private readonly Label _statusDetail = new();
    private readonly Panel _conversation = new();
    private readonly Label _chatConnectionState = new();
    private readonly RtlMessageBox _message = new();
    private readonly Label _characterCount = new();
    private readonly Label _submitResult = new();
    private readonly Button _submitButton = new();
    private readonly Dictionary<string, Label> _statusValues = new();
    private readonly Button _startButton = new();
    private readonly Button _pauseButton = new();
    private readonly Button _resumeButton = new();
    private readonly Button _stopButton = new();
    private readonly Label _actionResult = new();
    private readonly ComboBox _logAge = new();
    private readonly ComboBox _logLevel = new();
    private readonly TextBox _logSearch = new();
    private readonly ListView _logList = new();
    private readonly RichTextBox _logDetail = new();
    private readonly Label _logState = new();
    private readonly System.Windows.Forms.Timer _searchTimer = new() { Interval = 450 };
    private readonly System.Windows.Forms.Timer _chatTimer = new() { Interval = 2_000 };
    private readonly System.Windows.Forms.Timer _reportTimer = new() { Interval = 5_000 };
    private readonly HashSet<string> _renderedMessageIds = [];
    private readonly HashSet<string> _seenActionIds = [];
    private readonly HashSet<string> _seenProgressEventIds = [];
    private readonly Dictionary<string, ChatRunProgressView> _progressViews = [];
    private IReadOnlyList<EndpointEventLogEntry> _logs = [];
    private TrayStatus _currentStatus = new(TrayHealthState.Checking,
        EndpointServiceState.Unknown, null, "در حال بررسی عامل", "وضعیت فعلی در حال دریافت است.");
    private bool _allowClose;
    private bool _loadingLogs;
    private bool _submittingSupport;
    private bool _layingOutConversation;
    private bool _syncingChat;
    private string? _conversationId;
    private long _lastChatSequence;

    public SupportForm(Func<Task> refreshStatus)
    {
        _refreshStatus = refreshStatus;
        Text = "عامل نقطه پایانی";
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(880, 720);
        MinimumSize = new Size(760, 640);
        BackColor = Canvas;
        ForeColor = Ink;
        Font = new Font("Segoe UI", 9F);
        AutoScaleMode = AutoScaleMode.Dpi;
        KeyPreview = true;
        RightToLeft = RightToLeft.Yes;
        RightToLeftLayout = true;

        Controls.Add(BuildTabs());
        Controls.Add(BuildHeader());
        FormClosing += OnFormClosing;
        Resize += (_, _) =>
        {
            if (WindowState == FormWindowState.Minimized) Hide();
        };
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedTab == _logsTab) await RefreshLogsAsync();
            UpdateChatPolling();
            UpdateReportPolling();
            if (_tabs.SelectedTab == _helpdeskTab) await SyncChatAsync();
        };
        _searchTimer.Tick += async (_, _) =>
        {
            _searchTimer.Stop();
            await RefreshLogsAsync();
        };
        Shown += async (_, _) =>
        {
            await _refreshStatus();
            UpdateChatPolling();
            UpdateReportPolling();
            if (_tabs.SelectedTab == _helpdeskTab) await SyncChatAsync();
        };
        VisibleChanged += (_, _) =>
        {
            UpdateChatPolling();
            UpdateReportPolling();
        };
        _chatTimer.Tick += async (_, _) => await SyncChatAsync();
        _reportTimer.Tick += async (_, _) => await RefreshLogsAsync();
    }

    public void ShowTab(SupportFormTab tab)
    {
        _tabs.SelectedTab = tab switch
        {
            SupportFormTab.Status => _statusTab,
            SupportFormTab.Logs => _logsTab,
            _ => _helpdeskTab
        };
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        BringToFront();
        UpdateChatPolling();
        UpdateReportPolling();
        if (tab == SupportFormTab.Helpdesk) _ = SyncChatAsync();
    }

    public void UpdateStatus(TrayStatus status, Icon icon)
    {
        if (InvokeRequired)
        {
            BeginInvoke(new Action(() => UpdateStatus(status, icon)));
            return;
        }

        _currentStatus = status;
        _statusIcon.Image?.Dispose();
        _statusIcon.Image = icon.ToBitmap();
        Icon = icon;
        _statusLabel.Text = status.Label;
        _statusDetail.Text = status.Detail;
        var runtime = status.Runtime;
        SetStatusValue("سرویس", Friendly(status.ServiceState));
        SetStatusValue("اتصال", runtime is null ? "در دسترس نیست" : Friendly(runtime.ConnectionStatus));
        SetStatusValue("عملکردها", runtime is null ? "ناشناخته" : Friendly(runtime.FunctionMode));
        SetStatusValue("آخرین اتصال", FormatTime(runtime?.LastConnectedAtUtc));
        SetStatusValue("آخرین عملکرد", runtime?.LastCommandMethod is null
            ? "عملکردی ثبت نشده است"
            : $"{runtime.LastCommandMethod} · {FormatTime(runtime.LastCommandAtUtc)}");
        SetStatusValue("آخرین مشکل", runtime?.LastProblemMessage ?? "مشکلی گزارش نشده است");

        var running = EndpointServiceStateResolver.IsOperational(status.ServiceState);
        var pending = status.ServiceState is EndpointServiceState.StartPending
            or EndpointServiceState.StopPending;
        _startButton.Enabled = !pending && status.ServiceState == EndpointServiceState.Stopped;
        _stopButton.Enabled = !pending && status.ServiceState == EndpointServiceState.Running;
        _pauseButton.Enabled = running && runtime is { FunctionMode: AgentFunctionMode.Active };
        _resumeButton.Enabled = running && runtime is { FunctionMode: AgentFunctionMode.Paused };
        UpdateSubmitState();
    }

    public void ClosePermanently()
    {
        _allowClose = true;
        _lifetime.Cancel();
        _searchTimer.Dispose();
        _chatTimer.Dispose();
        _reportTimer.Dispose();
        Close();
    }

    private Control BuildHeader()
    {
        var panel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 82,
            BackColor = Surface,
            Padding = new Padding(18, 15, 22, 14),
            RightToLeft = RightToLeft.Yes
        };
        panel.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, panel.Height - 1, panel.Width, panel.Height - 1);
        };
        _statusIcon.Size = new Size(44, 44);
        _statusIcon.SizeMode = PictureBoxSizeMode.CenterImage;
        _statusIcon.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        _statusIcon.Location = new Point(814, 18);
        _statusLabel.AutoSize = false;
        _statusLabel.Font = new Font(Font, FontStyle.Bold);
        _statusLabel.Location = new Point(120, 13);
        _statusLabel.Size = new Size(678, 24);
        _statusLabel.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _statusLabel.TextAlign = ContentAlignment.MiddleRight;
        _statusLabel.Text = "در حال بررسی عامل";
        _statusDetail.AutoEllipsis = true;
        _statusDetail.ForeColor = Muted;
        _statusDetail.Location = new Point(120, 40);
        _statusDetail.Size = new Size(678, 24);
        _statusDetail.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        _statusDetail.TextAlign = ContentAlignment.MiddleRight;
        _statusDetail.Text = "وضعیت سرویس و اتصال در حال دریافت است.";
        var refresh = SecondaryButton("به‌روزرسانی");
        refresh.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        refresh.Location = new Point(18, 23);
        refresh.Size = new Size(90, 34);
        refresh.Click += async (_, _) => await _refreshStatus();
        panel.Resize += (_, _) => _statusIcon.Left = panel.ClientSize.Width - _statusIcon.Width - 22;
        panel.Controls.AddRange([_statusIcon, _statusLabel, _statusDetail, refresh]);
        return panel;
    }

    private Control BuildTabs()
    {
        _tabs.Dock = DockStyle.Fill;
        _tabs.Padding = new Point(18, 7);
        _tabs.Font = new Font("Segoe UI", 9.25F);
        _tabs.RightToLeft = RightToLeft.Yes;
        _tabs.RightToLeftLayout = true;
        _tabs.TabPages.AddRange([_helpdeskTab, _statusTab, _logsTab]);
        _helpdeskTab.BackColor = Canvas;
        _statusTab.BackColor = Canvas;
        _logsTab.BackColor = Canvas;
        BuildHelpdeskTab();
        BuildStatusTab();
        BuildLogsTab();
        return _tabs;
    }

    private void BuildHelpdeskTab()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = Padding.Empty,
            ColumnCount = 1,
            RowCount = 3,
            RightToLeft = RightToLeft.No
        };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 170));

        var chatHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(24, 8, 24, 7),
            ColumnCount = 2,
            RowCount = 1,
            RightToLeft = RightToLeft.No
        };
        chatHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 154));
        chatHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        chatHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        chatHeader.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, chatHeader.Height - 1,
                chatHeader.Width, chatHeader.Height - 1);
        };
        var titleBlock = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            RightToLeft = RightToLeft.Yes,
            Margin = Padding.Empty
        };
        titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        titleBlock.RowStyles.Add(new RowStyle(SizeType.Absolute, 19));
        titleBlock.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "دستیار پشتیبانی هوشمند",
            Font = new Font("Segoe UI", 11.5F, FontStyle.Bold),
            ForeColor = Ink,
            TextAlign = ContentAlignment.MiddleLeft
        }, 0, 0);
        titleBlock.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "مشکل را بنویسید؛ دستیار اطلاعات لازم را مرحله‌به‌مرحله در همین گفتگو از شما می‌پرسد.",
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleLeft,
            AutoEllipsis = true
        }, 0, 1);
        _chatConnectionState.Dock = DockStyle.Fill;
        _chatConnectionState.Text = "○ در انتظار اتصال";
        _chatConnectionState.ForeColor = Muted;
        _chatConnectionState.TextAlign = ContentAlignment.MiddleRight;
        _chatConnectionState.RightToLeft = RightToLeft.Yes;
        chatHeader.Controls.Add(_chatConnectionState, 0, 0);
        chatHeader.Controls.Add(titleBlock, 1, 0);

        _conversation.Dock = DockStyle.Fill;
        _conversation.AutoScroll = true;
        _conversation.BackColor = Canvas;
        _conversation.Padding = new Padding(24, 18, 24, 10);
        _conversation.RightToLeft = RightToLeft.No;
        _conversation.Resize += (_, _) => LayoutConversationMessages();

        var composer = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(24, 10, 24, 8),
            ColumnCount = 1,
            RowCount = 3,
            RightToLeft = RightToLeft.No
        };
        composer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 82));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        composer.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        composer.Paint += (_, e) =>
        {
            using var pen = new Pen(Line);
            e.Graphics.DrawLine(pen, 0, 0, composer.Width, 0);
        };

        var inputFrame = new RoundedPanel(Line, 10)
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(12, 8, 12, 8),
            Margin = new Padding(0, 0, 0, 4),
            RightToLeft = RightToLeft.Yes
        };
        _message.Dock = DockStyle.Fill;
        _message.Name = "SupportMessageInput";
        _message.BorderStyle = BorderStyle.None;
        _message.Multiline = true;
        _message.AcceptsTab = false;
        _message.ScrollBars = RichTextBoxScrollBars.None;
        _message.Font = new Font("Segoe UI", 10.5F);
        _message.MaxLength = MessageCharacterLimit;
        _message.RightToLeft = RightToLeft.Yes;
        _message.SelectionAlignment = HorizontalAlignment.Right;
        _message.BackColor = Surface;
        _message.ForeColor = Ink;
        var messagePlaceholder = new Label
        {
            Dock = DockStyle.Fill,
            Text = "مثلاً: اتصال IBSng من برقرار نمی‌شود و پیام Timeout دریافت می‌کنم…",
            Font = _message.Font,
            ForeColor = Muted,
            BackColor = Surface,
            RightToLeft = RightToLeft.Yes,
            TextAlign = ContentAlignment.TopRight,
            Padding = new Padding(2, 1, 2, 0),
            Cursor = Cursors.IBeam,
            TabStop = false
        };
        messagePlaceholder.Click += (_, _) => _message.Focus();
        _message.Enter += (_, _) => messagePlaceholder.Visible = false;
        _message.Leave += (_, _) =>
            messagePlaceholder.Visible = string.IsNullOrEmpty(_message.Text);
        _message.TextChanged += (_, _) =>
        {
            messagePlaceholder.Visible = string.IsNullOrEmpty(_message.Text) && !_message.Focused;
            _characterCount.Text = $"{_message.TextLength:N0} از ۱٬۰۰۰";
            UpdateSubmitState();
        };
        _message.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter || e.Shift || !_submitButton.Enabled) return;
            e.SuppressKeyPress = true;
            e.Handled = true;
            _ = SubmitSupportAsync();
        };
        inputFrame.Controls.Add(_message);
        inputFrame.Controls.Add(messagePlaceholder);
        messagePlaceholder.BringToFront();

        _submitButton.Text = "ارسال";
        _submitButton.Dock = DockStyle.None;
        _submitButton.Size = new Size(88, 36);
        _submitButton.Margin = Padding.Empty;
        _submitButton.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        StylePrimary(_submitButton);
        _submitButton.Click += async (_, _) => await SubmitSupportAsync();

        _characterCount.Dock = DockStyle.None;
        _characterCount.TextAlign = ContentAlignment.MiddleCenter;
        _characterCount.ForeColor = Muted;
        _characterCount.Text = "۰ از ۱٬۰۰۰";
        _characterCount.RightToLeft = RightToLeft.Yes;
        _characterCount.Margin = Padding.Empty;
        _characterCount.Anchor = AnchorStyles.Top | AnchorStyles.Left;

        var composerActions = new Panel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Margin = Padding.Empty,
            RightToLeft = RightToLeft.No
        };
        void LayoutComposerActions()
        {
            const int gap = 10;
            var submitLeft = Math.Max(0, composerActions.ClientSize.Width - 88);
            _submitButton.SetBounds(submitLeft, 2, 88, 36);
            var characterCountLeft = Math.Max(0, submitLeft - gap - 108);
            _characterCount.SetBounds(characterCountLeft, 2, 108, 36);
        }
        composerActions.Resize += (_, _) => LayoutComposerActions();
        composerActions.Controls.Add(_submitButton);
        composerActions.Controls.Add(_characterCount);

        _submitResult.AutoEllipsis = true;
        _submitResult.ForeColor = Muted;
        _submitResult.Dock = DockStyle.Fill;
        _submitResult.TextAlign = ContentAlignment.MiddleRight;
        _submitResult.RightToLeft = RightToLeft.Yes;
        _submitResult.Margin = Padding.Empty;

        composer.Controls.Add(inputFrame, 0, 0);
        composer.Controls.Add(composerActions, 0, 1);
        composer.Controls.Add(_submitResult, 0, 2);

        layout.Controls.Add(chatHeader, 0, 0);
        layout.Controls.Add(_conversation, 0, 1);
        layout.Controls.Add(composer, 0, 2);
        _helpdeskTab.Controls.Add(layout);
    }

    private void BuildStatusTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(28, 24, 28, 22),
            ColumnCount = 1,
            RowCount = 4,
            RightToLeft = RightToLeft.Yes
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 53));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        root.Controls.Add(new Label
        {
            Text = "وضعیت عامل و کنترل‌های محلی",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Ink,
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleRight
        }, 0, 0);

        var statusGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Surface,
            Padding = new Padding(18, 12, 18, 12),
            ColumnCount = 2,
            RowCount = 6,
            CellBorderStyle = TableLayoutPanelCellBorderStyle.Single,
            RightToLeft = RightToLeft.Yes
        };
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 160));
        statusGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var statusNames = new[] { "سرویس", "اتصال", "عملکردها", "آخرین اتصال", "آخرین عملکرد", "آخرین مشکل" };
        for (var row = 0; row < statusNames.Length; row++)
        {
            var name = statusNames[row];
            statusGrid.RowStyles.Add(new RowStyle(SizeType.Percent, 16.666F));
            var key = new Label { Text = name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Muted, Padding = new Padding(0, 0, 7, 0) };
            var value = new Label { Text = "در حال بررسی…", Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleRight, ForeColor = Ink, AutoEllipsis = true, Padding = new Padding(0, 0, 7, 0) };
            _statusValues[name] = value;
            statusGrid.Controls.Add(key, 0, row);
            statusGrid.Controls.Add(value, 1, row);
        }
        root.Controls.Add(statusGrid, 0, 1);

        var actions = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = new Padding(0, 14, 0, 10),
            WrapContents = false,
            RightToLeft = RightToLeft.Yes
        };
        ConfigureActionButton(_startButton, "راه‌اندازی سرویس", "start");
        ConfigureActionButton(_pauseButton, "توقف موقت عملکردها", "pause");
        ConfigureActionButton(_resumeButton, "ادامه عملکردها", "resume");
        ConfigureActionButton(_stopButton, "توقف سرویس", "stop", danger: true);
        actions.Controls.AddRange([_startButton, _pauseButton, _resumeButton, _stopButton]);
        root.Controls.Add(actions, 0, 2);
        _actionResult.Dock = DockStyle.Fill;
        _actionResult.ForeColor = Muted;
        _actionResult.TextAlign = ContentAlignment.MiddleRight;
        _actionResult.Text = "برای تغییر وضعیت سرویس، ویندوز تأیید مدیر سیستم را درخواست می‌کند.";
        root.Controls.Add(_actionResult, 0, 3);
        _statusTab.Controls.Add(root);
    }

    private void BuildLogsTab()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(18, 16, 18, 16),
            ColumnCount = 1,
            RowCount = 5,
            RightToLeft = RightToLeft.Yes
        };
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 58));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 44));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 64));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 36));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        var heading = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty,
            RightToLeft = RightToLeft.Yes
        };
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        heading.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        heading.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "گزارش فعالیت عامل",
            Font = new Font("Segoe UI", 13F, FontStyle.Bold),
            ForeColor = Ink,
            TextAlign = ContentAlignment.MiddleRight
        }, 0, 0);
        heading.Controls.Add(new Label
        {
            Dock = DockStyle.Fill,
            Text = "اتصال‌ها، فرمان‌ها، نتیجه اجرا و اقدامات محلی این رایانه",
            ForeColor = Muted,
            TextAlign = ContentAlignment.MiddleRight
        }, 0, 1);
        root.Controls.Add(heading, 0, 0);
        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.RightToLeft,
            RightToLeft = RightToLeft.Yes
        };
        _logAge.DropDownStyle = ComboBoxStyle.DropDownList;
        _logAge.Items.AddRange(["یک ساعت اخیر", "۶ ساعت اخیر", "۲۴ ساعت اخیر", "۷ روز اخیر"]);
        _logAge.SelectedIndex = 1;
        _logAge.Width = 120;
        _logAge.SelectedIndexChanged += async (_, _) => await RefreshLogsAsync();
        _logLevel.DropDownStyle = ComboBoxStyle.DropDownList;
        _logLevel.Items.AddRange(["همه سطح‌ها", "اطلاعات", "هشدار", "خطا"]);
        _logLevel.SelectedIndex = 0;
        _logLevel.Width = 115;
        _logLevel.SelectedIndexChanged += async (_, _) => await RefreshLogsAsync();
        _logSearch.PlaceholderText = "جست‌وجو در گزارش‌ها";
        _logSearch.RightToLeft = RightToLeft.Yes;
        _logSearch.Width = 210;
        _logSearch.TextChanged += (_, _) => { _searchTimer.Stop(); _searchTimer.Start(); };
        var refresh = SecondaryButton("به‌روزرسانی");
        refresh.Click += async (_, _) => await RefreshLogsAsync();
        var copy = SecondaryButton("کپی");
        copy.Click += async (_, _) => await CopyLogAsync();
        var export = SecondaryButton("خروجی…");
        export.Click += async (_, _) => await ExportLogsAsync();
        filters.Controls.AddRange([_logAge, _logLevel, _logSearch, refresh, copy, export]);
        root.Controls.Add(filters, 0, 1);

        _logList.Dock = DockStyle.Fill;
        _logList.View = View.Details;
        _logList.FullRowSelect = true;
        _logList.HideSelection = false;
        _logList.GridLines = true;
        _logList.RightToLeft = RightToLeft.Yes;
        _logList.RightToLeftLayout = true;
        _logList.Name = "EndpointActivityReport";
        _logList.Columns.Add("زمان", 145);
        _logList.Columns.Add("سطح", 90);
        _logList.Columns.Add("منبع", 170);
        _logList.Columns.Add("رویداد", 65);
        _logList.Columns.Add("پیام", 360);
        _logList.SelectedIndexChanged += (_, _) => ShowSelectedLog();
        root.Controls.Add(_logList, 0, 2);
        _logDetail.Dock = DockStyle.Fill;
        _logDetail.ReadOnly = true;
        _logDetail.BackColor = Surface;
        _logDetail.BorderStyle = BorderStyle.FixedSingle;
        _logDetail.Font = new Font("Segoe UI", 9F);
        _logDetail.RightToLeft = RightToLeft.Yes;
        _logDetail.Text = "برای مشاهده متن کامل، یک رویداد را انتخاب کنید.";
        root.Controls.Add(_logDetail, 0, 3);
        _logState.Dock = DockStyle.Fill;
        _logState.ForeColor = Muted;
        _logState.TextAlign = ContentAlignment.MiddleRight;
        root.Controls.Add(_logState, 0, 4);
        _logsTab.Controls.Add(root);
    }

    private async Task SubmitSupportAsync()
    {
        if (!_submitButton.Enabled || _submittingSupport) return;
        _submittingSupport = true;
        _message.ReadOnly = true;
        _submitButton.Enabled = false;
        _submitButton.Text = "ارسال…";
        _submitResult.ForeColor = Muted;
        _submitResult.Text = "پیام در حال ارسال به دستیار پشتیبانی است…";
        try
        {
            var messageText = GetSafeMessageText();
            var isRightToLeft = BidiText.IsRightToLeft(messageText);
            await EnsureChatOpenedAsync();
            var submission = new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
                _conversationId, messageText, isRightToLeft ? "rtl" : "ltr",
                LocalChatIdentity());
            var receipt = await _serviceClient.SendChatMessageAsync(
                submission, _lifetime.Token);
            if (!receipt.Accepted) throw new InvalidOperationException(receipt.Message);
            _conversationId = receipt.ConversationId;
            _renderedMessageIds.Add(receipt.MessageId);
            AddChatMessage(true, messageText);
            _message.Clear();
            _submitResult.Text = receipt.Message;
            TrayEventLogger.Information($"درخواست پشتیبانی {receipt.MessageId} ارسال شد.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _submitResult.ForeColor = Color.FromArgb(178, 48, 48);
            _submitResult.Text = ex.Message;
            TrayEventLogger.Warning($"ارسال درخواست پشتیبانی ناموفق بود: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _submittingSupport = false;
            _message.ReadOnly = false;
            _submitButton.Text = "ارسال";
            UpdateSubmitState();
            _message.Focus();
        }
    }

    private async Task RunServiceActionAsync(string action)
    {
        if (action == "stop" && MessageBox.Show(
            "با توقف سرویس، این رایانه تا زمان راه‌اندازی دوباره از پشتیبانی قطع می‌شود. ادامه می‌دهید؟",
            "توقف سرویس عامل نقطه پایانی", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2, MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign)
            != DialogResult.Yes) return;
        SetActionButtonsEnabled(false);
        _actionResult.ForeColor = Muted;
        _actionResult.Text = "در انتظار تأیید مدیر سیستم…";
        var result = await ServiceControlLauncher.LaunchAsync(action, _lifetime.Token);
        _actionResult.ForeColor = result.Success ? Color.FromArgb(28, 125, 76)
            : result.Cancelled ? Muted : Color.FromArgb(178, 48, 48);
        _actionResult.Text = result.Message;
        await Task.Delay(500);
        await _refreshStatus();
    }

    private async Task RefreshLogsAsync()
    {
        if (_loadingLogs || IsDisposed) return;
        _loadingLogs = true;
        _logState.Text = "در حال خواندن گزارش رویدادهای ویندوز…";
        try
        {
            _logs = await _logReader.ReadAsync(GetSelectedAge(),
                GetSelectedLogLevel(),
                string.IsNullOrWhiteSpace(_logSearch.Text) ? null : _logSearch.Text.Trim(),
                _lifetime.Token);
            _logList.BeginUpdate();
            _logList.Items.Clear();
            foreach (var item in _logs)
            {
                var summary = item.Message.Replace("\r", " ").Replace("\n", " ");
                if (summary.Length > 180) summary = summary[..180] + "…";
                var row = new ListViewItem(item.TimestampUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"));
                row.SubItems.Add(PersianLevel(item.Level));
                row.SubItems.Add(item.Source);
                row.SubItems.Add(item.EventId.ToString());
                row.SubItems.Add(summary);
                row.Tag = item;
                if (item.Level == "Error") row.ForeColor = Color.FromArgb(168, 45, 45);
                else if (item.Level == "Warning") row.ForeColor = Color.FromArgb(142, 91, 16);
                _logList.Items.Add(row);
            }
            _logList.EndUpdate();
            _logState.Text = _logs.Count == 0
                ? "رویدادی مطابق این فیلترها پیدا نشد."
                : $"{_logs.Count:N0} رویداد نمایش داده شد · جدیدترین در ابتدا";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logState.Text = "گزارش‌ها در دسترس نیستند: " + ex.Message;
        }
        finally { _loadingLogs = false; }
    }

    private void ShowSelectedLog()
    {
        if (_logList.SelectedItems.Count == 0
            || _logList.SelectedItems[0].Tag is not EndpointEventLogEntry item) return;
        _logDetail.Text = $"زمان: {item.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}\r\n" +
            $"سطح: {PersianLevel(item.Level)}\r\nمنبع: {item.Source}\r\nرویداد: {item.EventId}\r\n\r\n{item.Message}";
    }

    private async Task CopyLogAsync()
    {
        var text = _logList.SelectedItems.Count > 0
            && _logList.SelectedItems[0].Tag is EndpointEventLogEntry selected
            ? FormatLog(selected)
            : string.Join(Environment.NewLine + Environment.NewLine, _logs.Select(FormatLog));
        if (!string.IsNullOrWhiteSpace(text))
        {
            await Task.Yield();
            Clipboard.SetText(text);
            _logState.Text = "متن گزارش در حافظه موقت کپی شد.";
        }
    }

    private async Task ExportLogsAsync()
    {
        using var dialog = new SaveFileDialog
        {
            Title = "خروجی گزارش‌های عامل نقطه پایانی",
            Filter = "فایل متنی (*.txt)|*.txt|فایل JSON (*.json)|*.json",
            FileName = $"EndpointAgent-report-{DateTime.Now:yyyyMMdd-HHmm}.txt"
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        var text = Path.GetExtension(dialog.FileName).Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? System.Text.Json.JsonSerializer.Serialize(_logs,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true })
            : string.Join(Environment.NewLine + Environment.NewLine, _logs.Select(FormatLog));
        await File.WriteAllTextAsync(dialog.FileName, text, Encoding.UTF8, _lifetime.Token);
        _logState.Text = "گزارش‌ها در این مسیر ذخیره شدند: " + dialog.FileName;
    }

    private ChatIdentity LocalChatIdentity() => new("S-1-0-0", Environment.UserName,
        System.Diagnostics.Process.GetCurrentProcess().SessionId);

    private void UpdateChatPolling()
    {
        var shouldPoll = Visible && _tabs.SelectedTab == _helpdeskTab
            && !_lifetime.IsCancellationRequested;
        if (shouldPoll && !_chatTimer.Enabled) _chatTimer.Start();
        else if (!shouldPoll && _chatTimer.Enabled) _chatTimer.Stop();
    }

    private void UpdateReportPolling()
    {
        var shouldPoll = Visible && _tabs.SelectedTab == _logsTab
            && !_lifetime.IsCancellationRequested;
        if (shouldPoll && !_reportTimer.Enabled) _reportTimer.Start();
        else if (!shouldPoll && _reportTimer.Enabled) _reportTimer.Stop();
    }

    private async Task EnsureChatOpenedAsync()
    {
        if (!string.IsNullOrWhiteSpace(_conversationId)) return;
        var conversation = await _serviceClient.OpenChatAsync(
            System.Diagnostics.Process.GetCurrentProcess().SessionId, _lifetime.Token);
        _conversationId = conversation.ConversationId;
    }

    private async Task SyncChatAsync()
    {
        if (_syncingChat || IsDisposed || !Visible || _tabs.SelectedTab != _helpdeskTab)
            return;
        if (_currentStatus.Runtime?.ConnectionStatus != AgentConnectionStatus.Connected)
            return;

        _syncingChat = true;
        try
        {
            await EnsureChatOpenedAsync();
            var request = new ChatDeltaRequest(_conversationId!, _lastChatSequence,
                LocalChatIdentity());
            var delta = await _serviceClient.GetChatDeltaAsync(request, _lifetime.Token);
            var entries = new List<(long Sequence, ChatMessage? Message,
                ChatActionProposal? Action, ChatRunProgressEvent? Progress)>();
            foreach (var message in delta.Messages)
                entries.Add((message.Sequence, message, null, null));
            foreach (var action in delta.Actions)
                entries.Add((action.Sequence, null, action, null));
            foreach (var progress in delta.ProgressEvents ?? [])
                entries.Add((progress.Sequence, null, null, progress));
            foreach (var entry in entries.OrderBy(entry => entry.Sequence))
            {
                if (entry.Message is { } message)
                {
                    if (_renderedMessageIds.Add(message.MessageId))
                    {
                        var fromUser = message.Role == ChatMessageRole.User;
                        AddChatMessage(fromUser, message.Body,
                            message.Role == ChatMessageRole.System ? "سیستم" : null);
                    }
                    UpdateChatMessageState(message);
                }
                else if (entry.Action is { } action
                         && _seenActionIds.Add(action.ActionId))
                {
                    await HandleActionProposalAsync(action);
                }
                else if (entry.Progress is { } progress
                         && _seenProgressEventIds.Add(progress.EventId))
                {
                    AddProgressEvent(progress);
                }
                _lastChatSequence = Math.Max(_lastChatSequence, entry.Sequence);
            }
            foreach (var progress in _progressViews.Values)
                progress.RefreshElapsed();
            _conversationId = delta.Conversation.ConversationId;
            _lastChatSequence = Math.Max(_lastChatSequence,
                delta.Conversation.LastSequence);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _submitResult.ForeColor = Color.FromArgb(178, 48, 48);
            _submitResult.Text = ex.Message;
            TrayEventLogger.Warning(
                $"همگام‌سازی گفتگوی پشتیبانی ناموفق بود: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _syncingChat = false;
        }
    }

    private async Task HandleActionProposalAsync(ChatActionProposal proposal)
    {
        if (proposal.State != ChatActionState.Pending)
        {
            AddChatMessage(false,
                $"{proposal.DisplayName}\r\nوضعیت: {PersianActionState(proposal.State)}",
                "اقدام پیشنهادی");
            return;
        }

        var rtl = BidiText.IsRightToLeft(proposal.Description);
        var options = rtl
            ? MessageBoxOptions.RtlReading | MessageBoxOptions.RightAlign
            : 0;
        var approved = MessageBox.Show(this,
            $"{proposal.DisplayName}\r\n\r\n{proposal.Description}\r\n\r\n" +
            "آیا اجرای دقیق این اقدام را تأیید می‌کنید؟",
            "تأیید اقدام پشتیبانی", MessageBoxButtons.YesNo, MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button2, options) == DialogResult.Yes;
        var decision = new ChatActionDecision(proposal.ConversationId,
            proposal.ActionId, approved, LocalChatIdentity());
        var receipt = await _serviceClient.RespondToChatActionAsync(
            decision, _lifetime.Token);
        AddChatMessage(false, receipt.Message, "اقدام پیشنهادی");
    }

    private string GetSafeMessageText() => _message.Text.Trim();

    private void UpdateChatMessageState(ChatMessage message)
    {
        if (message.Role == ChatMessageRole.User)
        {
            _submitResult.ForeColor = message.State == ChatMessageState.Failed
                ? Color.FromArgb(178, 48, 48)
                : Muted;
            _submitResult.Text = message.State switch
            {
                ChatMessageState.Queued => "پیام در صف دستیار پشتیبانی است.",
                ChatMessageState.Working => "دستیار پشتیبانی در حال بررسی پیام است…",
                ChatMessageState.Failed => "بررسی این پیام ناموفق بود.",
                _ => _submitResult.Text
            };
            return;
        }

        if (message.Role == ChatMessageRole.Assistant)
        {
            _submitResult.ForeColor = message.State == ChatMessageState.Failed
                ? Color.FromArgb(178, 48, 48)
                : Muted;
            _submitResult.Text = message.State == ChatMessageState.Failed
                ? "دستیار نتوانست این درخواست را کامل کند."
                : "پاسخ دستیار دریافت شد.";
        }
    }

    private void AddChatMessage(bool fromUser, string body, string? author = null)
    {
        var message = new ChatMessageView(author ?? (fromUser ? "شما" : "دستیار پشتیبانی"),
            body, fromUser, DateTime.Now);
        _conversation.Controls.Add(message);
        LayoutConversationMessages();
        _conversation.ScrollControlIntoView(message);
    }

    private void AddProgressEvent(ChatRunProgressEvent progress)
    {
        var added = false;
        if (!_progressViews.TryGetValue(progress.RunId, out var view))
        {
            view = new ChatRunProgressView(progress.RunId);
            view.SizeChanged += (_, _) => LayoutConversationMessages();
            _progressViews.Add(progress.RunId, view);
            _conversation.Controls.Add(view);
            added = true;
        }
        view.AddEvent(progress);
        LayoutConversationMessages();
        if (added) _conversation.ScrollControlIntoView(view);
    }

    private void LayoutConversationMessages()
    {
        if (_layingOutConversation) return;
        _layingOutConversation = true;
        var previousScroll = -_conversation.AutoScrollPosition.Y;
        try
        {
            _conversation.SuspendLayout();
            try
            {
                _conversation.AutoScroll = false;
                _conversation.AutoScrollMinSize = Size.Empty;
                var width = GetConversationMessageWidth();
                var top = _conversation.Padding.Top;
                foreach (Control control in _conversation.Controls)
                {
                    control.Width = width;
                    control.Location = new Point(_conversation.Padding.Left, top);
                    top += control.Height + 10;
                }
                _conversation.AutoScroll = true;
                _conversation.AutoScrollMinSize = new Size(0, top + _conversation.Padding.Bottom);
            }
            finally
            {
                _conversation.ResumeLayout(performLayout: true);
            }
            if (previousScroll > 0)
                _conversation.AutoScrollPosition = new Point(0, previousScroll);
        }
        finally
        {
            _layingOutConversation = false;
        }
    }

    private int GetConversationMessageWidth()
    {
        return Math.Max(300, _conversation.ClientSize.Width
            - _conversation.Padding.Horizontal - SystemInformation.VerticalScrollBarWidth - 2);
    }

    private static string BuildAutomaticSubject(string message)
    {
        var firstLine = message.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim().TrimStart('•', '-', '*').Trim())
            .FirstOrDefault(line => !string.IsNullOrWhiteSpace(line));
        if (string.IsNullOrWhiteSpace(firstLine)) return "درخواست پشتیبانی کاربر";
        var subject = string.Join(' ', firstLine.Split((char[]?)null,
            StringSplitOptions.RemoveEmptyEntries));
        return subject.Length <= 120 ? subject : subject[..120].TrimEnd();
    }

    private void ConfigureActionButton(Button button, string text, string action, bool danger = false)
    {
        button.Text = text;
        button.AutoSize = true;
        button.Height = 36;
        button.Padding = new Padding(12, 0, 12, 0);
        button.Margin = new Padding(0, 0, 10, 0);
        button.FlatStyle = FlatStyle.System;
        button.Click += async (_, _) => await RunServiceActionAsync(action);
        button.HandleCreated += (_, _) => NativeMethods.SendMessage(button.Handle,
            0x160C, IntPtr.Zero, new IntPtr(1));
        if (danger) button.ForeColor = Color.FromArgb(158, 42, 42);
    }

    private void SetActionButtonsEnabled(bool enabled)
    {
        if (!enabled)
        {
            _startButton.Enabled = _pauseButton.Enabled = _resumeButton.Enabled = _stopButton.Enabled = false;
            return;
        }
        UpdateStatus(_currentStatus, Icon ?? SystemIcons.Application);
    }

    private void UpdateSubmitState()
    {
        var connected = _currentStatus.Runtime?.ConnectionStatus == AgentConnectionStatus.Connected;
        _chatConnectionState.Text = connected ? "● متصل به پشتیبانی" : "○ در انتظار اتصال";
        _chatConnectionState.ForeColor = connected ? Color.FromArgb(28, 125, 76) : Muted;
        _submitButton.Enabled = !_submittingSupport && connected && !string.IsNullOrWhiteSpace(_message.Text)
            && _message.TextLength <= MessageCharacterLimit;
        if (!connected && string.IsNullOrWhiteSpace(_submitResult.Text))
            _submitResult.Text = "برای ارسال درخواست، عامل نقطه پایانی باید به سرور متصل باشد.";
        else if (connected && _submitResult.Text == "برای ارسال درخواست، عامل نقطه پایانی باید به سرور متصل باشد.")
            _submitResult.Text = string.Empty;
    }

    private void SetStatusValue(string name, string value)
    {
        if (_statusValues.TryGetValue(name, out var label)) label.Text = value;
    }

    private TimeSpan GetSelectedAge() => _logAge.SelectedIndex switch
    {
        0 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(24),
        3 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(6)
    };

    private string? GetSelectedLogLevel() => _logLevel.SelectedIndex switch
    {
        1 => "Information",
        2 => "Warning",
        3 => "Error",
        _ => null
    };

    private static string Friendly(Enum value) => value.ToString() switch
    {
        "Unknown" => "ناشناخته",
        "NotInstalled" => "نصب نشده",
        "Stopped" => "متوقف",
        "StartPending" => "در حال راه‌اندازی",
        "StopPending" => "در حال توقف",
        "Running" => "در حال اجرا",
        "Interactive" => "در حال اجرا (توسعه)",
        "Starting" => "در حال شروع",
        "Connecting" => "در حال اتصال",
        "Connected" => "متصل",
        "Reconnecting" => "در حال اتصال مجدد",
        "Disconnected" => "قطع‌شده",
        "Faulted" => "دارای خطا",
        "Active" => "فعال",
        "Paused" => "متوقف موقت",
        _ => "ناشناخته"
    };

    private static string FormatTime(DateTimeOffset? value) => value is null
        ? "ثبت نشده" : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");

    private static string PersianLevel(string level) => level switch
    {
        "Information" => "اطلاعات",
        "Warning" => "هشدار",
        "Error" => "خطا",
        _ => level
    };

    private static string PersianActionState(ChatActionState state) => state switch
    {
        ChatActionState.Pending => "در انتظار تأیید",
        ChatActionState.Approved => "تأییدشده",
        ChatActionState.Rejected => "ردشده",
        ChatActionState.Expired => "منقضی‌شده",
        ChatActionState.Executed => "اجراشده",
        ChatActionState.Failed => "ناموفق",
        _ => "نامشخص"
    };

    private static string FormatLog(EndpointEventLogEntry item) =>
        $"[{item.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}] {PersianLevel(item.Level)} · " +
        $"{item.Source} · {item.EventId}\r\n{item.Message}";

    private static Button SecondaryButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, Height = 32, FlatStyle = FlatStyle.Flat, BackColor = Surface, ForeColor = Ink, Margin = new Padding(0, 0, 7, 0), Padding = new Padding(8, 0, 8, 0) };
        button.FlatAppearance.BorderColor = Line;
        return button;
    }

    private static void StylePrimary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.FlatAppearance.BorderSize = 0;
        button.BackColor = Action;
        button.ForeColor = Color.White;
        button.Font = new Font("Segoe UI", 9F, FontStyle.Bold);
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_allowClose || e.CloseReason is CloseReason.WindowsShutDown
            or CloseReason.ApplicationExitCall or CloseReason.TaskManagerClosing) return;
        e.Cancel = true;
        Hide();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        internal static extern IntPtr SendMessage(IntPtr handle, int message,
            IntPtr wParam, IntPtr lParam);
    }
}
