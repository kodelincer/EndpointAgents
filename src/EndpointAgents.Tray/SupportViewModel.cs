using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text;

namespace EndpointAgents.Tray;

public enum SupportSection
{
    Helpdesk,
    Status,
    Logs
}

public enum ConversationListFilter
{
    All,
    Active,
    Resolved,
    Closed
}

public sealed class SupportViewModel : INotifyPropertyChanged
{
    private const int DefaultConversationLimit = 30;
    private TrayStatus _status = new(TrayHealthState.Checking,
        EndpointServiceState.Unknown, null, "در حال بررسی عامل",
        "وضعیت فعلی در حال دریافت است.");
    private string _chatState = "در انتظار اتصال";
    private string _submitResult = string.Empty;
    private string _actionResult = string.Empty;
    private string _logState = string.Empty;
    private EndpointEventLogEntry? _selectedLog;
    private bool _submitting;
    private bool _runningAction;
    private int _conversationLimit = DefaultConversationLimit;
    private readonly List<ConversationSummaryViewModel> _allConversations = [];
    private ConversationSummaryViewModel? _selectedConversation;
    private ConversationListFilter _conversationFilter;
    private bool _isDraft = true;
    private bool _conversationListLoading;
    private bool _conversationListLoaded;
    private string _conversationListError = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ConversationItemViewModel> Conversation { get; } = [];
    public ObservableCollection<ConversationDateGroup> ConversationGroups { get; } = [];
    public ObservableCollection<EndpointEventLogEntry> Logs { get; } = [];

    public TrayStatus Status => _status;
    public string StatusLabel => _status.Label;
    public string StatusDetail => _status.Detail;
    public string Service => Friendly(_status.ServiceState);
    public string Connection => _status.Runtime is null
        ? "در دسترس نیست" : Friendly(_status.Runtime.ConnectionStatus);
    public string FunctionMode => _status.Runtime is null
        ? "ناشناخته" : Friendly(_status.Runtime.FunctionMode);
    public string LastConnected => FormatTime(_status.Runtime?.LastConnectedAtUtc);
    public string LastCommand => _status.Runtime?.LastCommandMethod is null
        ? "عملکردی ثبت نشده است"
        : $"{_status.Runtime.LastCommandMethod} · {FormatTime(_status.Runtime.LastCommandAtUtc)}";
    public string LastProblem => _status.Runtime?.LastProblemMessage ?? "مشکلی گزارش نشده است";
    public bool IsConnected => _status.Runtime?.ConnectionStatus == AgentConnectionStatus.Connected;
    public ConversationSummaryViewModel? SelectedConversation
    {
        get => _selectedConversation;
        private set
        {
            if (!Set(ref _selectedConversation, value)) return;
            OnPropertyChanged(nameof(ConversationHeading));
            OnPropertyChanged(nameof(ConversationSubheading));
            OnPropertyChanged(nameof(IsConversationReadOnly));
        }
    }
    public bool IsDraft => _isDraft;
    public bool IsConversationReadOnly => !_isDraft && SelectedConversation is { IsActive: false };
    public bool IsConversationListLoading => _conversationListLoading;
    public string ConversationListError => _conversationListError;
    public bool HasConversationListError => !string.IsNullOrWhiteSpace(_conversationListError);
    public bool IsConversationListEmpty => _conversationListLoaded
        && !_conversationListLoading
        && !HasConversationListError
        && ConversationGroups.Count == 0;
    public string ConversationHeading => _isDraft
        ? "گفتگوی جدید"
        : SelectedConversation?.Title ?? "درخواست پشتیبانی";
    public string ConversationSubheading => _isDraft
        ? "پیام نخست، گفتگوی تازه را ایجاد می‌کند."
        : SelectedConversation?.StatusLabel ?? string.Empty;
    public bool CanStart => !_runningAction && _status.ServiceState == EndpointServiceState.Stopped;
    public bool CanStop => !_runningAction && _status.ServiceState == EndpointServiceState.Running;
    public bool CanPause => !_runningAction
        && EndpointServiceStateResolver.IsOperational(_status.ServiceState)
        && _status.Runtime is { FunctionMode: AgentFunctionMode.Active };
    public bool CanResume => !_runningAction
        && EndpointServiceStateResolver.IsOperational(_status.ServiceState)
        && _status.Runtime is { FunctionMode: AgentFunctionMode.Paused };

    public string ChatState
    {
        get => _chatState;
        set => Set(ref _chatState, value);
    }

    public string SubmitResult
    {
        get => _submitResult;
        set => Set(ref _submitResult, value);
    }

    public string ActionResult
    {
        get => _actionResult;
        set => Set(ref _actionResult, value);
    }

    public string LogState
    {
        get => _logState;
        set => Set(ref _logState, value);
    }

    public EndpointEventLogEntry? SelectedLog
    {
        get => _selectedLog;
        set
        {
            if (!Set(ref _selectedLog, value)) return;
            OnPropertyChanged(nameof(SelectedLogText));
        }
    }

    public string SelectedLogText => _selectedLog is null
        ? "برای مشاهده متن کامل، یک رویداد را انتخاب کنید."
        : $"زمان: {BidiText.IsolateLeftToRight($"{_selectedLog.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss zzz}")}\r\n" +
          $"سطح: {PersianLevel(_selectedLog.Level)}\r\n" +
          $"منبع: {BidiText.IsolateLeftToRight(_selectedLog.Source)}\r\n" +
          $"رویداد: {BidiText.IsolateLeftToRight(_selectedLog.EventId == 0 ? "—" : _selectedLog.EventId.ToString())}\r\n\r\n" +
          _selectedLog.Message;

    public bool IsSubmitting
    {
        get => _submitting;
        set => Set(ref _submitting, value);
    }

    public void ApplyStatus(TrayStatus status)
    {
        _status = status;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusLabel));
        OnPropertyChanged(nameof(StatusDetail));
        OnPropertyChanged(nameof(Service));
        OnPropertyChanged(nameof(Connection));
        OnPropertyChanged(nameof(FunctionMode));
        OnPropertyChanged(nameof(LastConnected));
        OnPropertyChanged(nameof(LastCommand));
        OnPropertyChanged(nameof(LastProblem));
        OnPropertyChanged(nameof(IsConnected));
        NotifyActions();
    }

    public ConversationSummaryViewModel? ReplaceConversations(
        IEnumerable<ChatConversationSummary> conversations, string? preferredId)
    {
        _allConversations.Clear();
        _allConversations.AddRange(conversations.Select(item =>
            new ConversationSummaryViewModel(item)));
        RefreshConversationGroups();
        NotifyConversationListState();
        var selected = string.IsNullOrWhiteSpace(preferredId)
            ? null
            : _allConversations.FirstOrDefault(item =>
                item.ConversationId == preferredId);
        if (!_isDraft) SelectedConversation = selected;
        return selected;
    }

    public void SetConversationFilter(ConversationListFilter filter)
    {
        if (_conversationFilter == filter) return;
        _conversationFilter = filter;
        RefreshConversationGroups();
        NotifyConversationListState();
    }

    public void BeginConversationListRefresh()
    {
        _conversationListLoading = true;
        _conversationListError = string.Empty;
        NotifyConversationListState();
    }

    public void CompleteConversationListRefresh(string? error = null)
    {
        _conversationListLoading = false;
        _conversationListLoaded = true;
        _conversationListError = error ?? string.Empty;
        NotifyConversationListState();
    }

    public void SelectConversation(ConversationSummaryViewModel conversation)
    {
        _isDraft = false;
        SelectedConversation = conversation;
        NotifyConversationMode();
    }

    public void BeginConversationDraft()
    {
        _isDraft = true;
        SelectedConversation = null;
        NotifyConversationMode();
    }

    public void ClearConversationTimeline() => Conversation.Clear();

    public void SetActionRunning(bool value)
    {
        _runningAction = value;
        NotifyActions();
    }

    public void ReplaceLogs(IEnumerable<EndpointEventLogEntry> entries)
    {
        var selected = SelectedLog;
        Logs.Clear();
        foreach (var entry in entries) Logs.Add(entry);
        SelectedLog = selected is null ? null : Logs.FirstOrDefault(entry =>
            entry.TimestampUtc == selected.TimestampUtc &&
            entry.EventId == selected.EventId &&
            string.Equals(entry.Source, selected.Source, StringComparison.Ordinal) &&
            string.Equals(entry.Message, selected.Message, StringComparison.Ordinal));
    }

    public MessageItemViewModel AddMessage(bool fromUser, string body,
        string? author = null, DateTimeOffset? timestamp = null)
    {
        var item = new MessageItemViewModel(
            fromUser ? ConversationItemKind.UserMessage : ConversationItemKind.Message,
            author ?? (fromUser ? "شما" : "دستیار پشتیبانی"), body,
            timestamp ?? DateTimeOffset.Now);
        AddConversationItem(item);
        return item;
    }

    public ProgressItemViewModel AddProgress(ChatRunProgressEvent progress)
    {
        var item = Conversation.OfType<ProgressItemViewModel>()
            .FirstOrDefault(candidate => candidate.RunId == progress.RunId);
        if (item is null)
        {
            item = new ProgressItemViewModel(progress.RunId);
            AddConversationItem(item);
        }
        item.Add(progress);
        return item;
    }

    public void ApplyConversationLimit(int limit)
    {
        _conversationLimit = Math.Clamp(limit, 1, 200);
        TrimConversation();
    }

    private void AddConversationItem(ConversationItemViewModel item)
    {
        Conversation.Add(item);
        TrimConversation();
    }

    private void TrimConversation()
    {
        while (Conversation.Count > _conversationLimit)
            Conversation.RemoveAt(0);
    }

    private void RefreshConversationGroups()
    {
        ConversationGroups.Clear();
        var visible = _allConversations.Where(item => _conversationFilter switch
        {
            ConversationListFilter.Active => item.IsActive
                && item.State != ChatConversationState.Resolved,
            ConversationListFilter.Resolved => item.State == ChatConversationState.Resolved,
            ConversationListFilter.Closed => item.State == ChatConversationState.Closed,
            _ => true
        });
        foreach (var group in visible.GroupBy(item => item.DateGroup))
            ConversationGroups.Add(new ConversationDateGroup(group.Key, group));
    }

    private void NotifyConversationMode()
    {
        OnPropertyChanged(nameof(IsDraft));
        OnPropertyChanged(nameof(IsConversationReadOnly));
        OnPropertyChanged(nameof(ConversationHeading));
        OnPropertyChanged(nameof(ConversationSubheading));
    }

    private void NotifyConversationListState()
    {
        OnPropertyChanged(nameof(IsConversationListLoading));
        OnPropertyChanged(nameof(ConversationListError));
        OnPropertyChanged(nameof(HasConversationListError));
        OnPropertyChanged(nameof(IsConversationListEmpty));
    }

    private void NotifyActions()
    {
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanPause));
        OnPropertyChanged(nameof(CanResume));
    }

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static string Friendly(Enum value) => value.ToString() switch
    {
        "Running" => "در حال اجرا",
        "Interactive" => "اجرای تعاملی",
        "Stopped" => "متوقف",
        "StartPending" => "در حال شروع",
        "StopPending" => "در حال توقف",
        "NotInstalled" => "نصب نشده",
        "Connected" => "متصل",
        "Connecting" => "در حال اتصال",
        "Reconnecting" => "در حال اتصال مجدد",
        "Disconnected" => "قطع",
        "Faulted" => "خطا",
        "Active" => "فعال",
        "Paused" => "مکث",
        _ => "نامشخص"
    };

    public static string PersianLevel(string level) => level switch
    {
        "Error" => "خطا",
        "Warning" => "هشدار",
        "Information" => "اطلاعات",
        _ => level
    };

    public static string FormatTime(DateTimeOffset? value) => value is null
        ? "ثبت نشده" : value.Value.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class ConversationDateGroup : ObservableCollection<ConversationSummaryViewModel>
{
    public ConversationDateGroup(string label,
        IEnumerable<ConversationSummaryViewModel> conversations) : base(conversations) =>
        Label = label;

    public string Label { get; }
}

public sealed class ConversationSummaryViewModel
{
    public ConversationSummaryViewModel(ChatConversationSummary summary)
    {
        Summary = summary;
        var local = summary.UpdatedAtUtc.LocalDateTime;
        UpdatedTime = local.ToString("HH:mm");
        var days = (DateTime.Today - local.Date).Days;
        DateGroup = days switch
        {
            <= 0 => "امروز",
            1 => "دیروز",
            < 7 => $"{days} روز پیش",
            _ => local.ToString("yyyy/MM/dd")
        };
        (StatusLabel, StatusGlyph) = summary.State switch
        {
            ChatConversationState.Working => ("در حال پاسخ‌گویی", "\uE895"),
            ChatConversationState.AwaitingApproval => ("در انتظار تأیید شما", "\uE7BA"),
            ChatConversationState.Answered => ("پاسخ داده شد", "\uE73E"),
            ChatConversationState.Resolved => ("حل شده", "\uE73E"),
            ChatConversationState.Closed => ("بسته شده", "\uE711"),
            _ => ("باز", "\uE8BD")
        };
    }

    public ChatConversationSummary Summary { get; }
    public string ConversationId => Summary.ConversationId;
    public string Title => Summary.Title;
    public string Preview => Summary.Preview;
    public ChatConversationState State => Summary.State;
    public bool IsActive => Summary.IsActive;
    public string UpdatedTime { get; }
    public string DateGroup { get; }
    public string StatusLabel { get; }
    public string StatusGlyph { get; }
}

public enum ConversationItemKind
{
    Message,
    UserMessage,
    Progress
}

public abstract class ConversationItemViewModel
{
    protected ConversationItemViewModel(ConversationItemKind kind) => Kind = kind;
    public ConversationItemKind Kind { get; }
}

public sealed class MessageItemViewModel : ConversationItemViewModel
{
    public MessageItemViewModel(ConversationItemKind kind, string author, string body,
        DateTimeOffset timestamp) : base(kind)
    {
        Author = author;
        Body = body;
        Timestamp = timestamp.LocalDateTime.ToString("HH:mm");
        IsRightToLeft = BidiText.IsRightToLeft(body);
    }

    public string Author { get; }
    public string Body { get; }
    public string Timestamp { get; }
    public bool IsRightToLeft { get; }
}

public sealed class ProgressItemViewModel : ConversationItemViewModel, INotifyPropertyChanged
{
    private string _summary = "در حال بررسی درخواست";
    private bool _terminal;

    public ProgressItemViewModel(string runId) : base(ConversationItemKind.Progress) =>
        RunId = runId;

    public event PropertyChangedEventHandler? PropertyChanged;
    public string RunId { get; }
    public string Summary
    {
        get => _summary;
        private set
        {
            if (_summary == value) return;
            _summary = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Summary)));
        }
    }
    public bool IsTerminal
    {
        get => _terminal;
        private set
        {
            if (_terminal == value) return;
            _terminal = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsTerminal)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsRunning)));
        }
    }
    public bool IsRunning => !IsTerminal;
    public ObservableCollection<ProgressStepViewModel> Steps { get; } = [];

    public void Add(ChatRunProgressEvent progress)
    {
        Summary = ProgressLabel(progress);
        IsTerminal = progress.Stage is ChatProgressStage.Completed
            or ChatProgressStage.Failed or ChatProgressStage.ActionCompleted
            or ChatProgressStage.ActionRejected;
        Steps.Add(new ProgressStepViewModel(Summary,
            progress.CreatedAtUtc.LocalDateTime.ToString("HH:mm:ss"),
            progress.Outcome));
    }

    private static string ProgressLabel(ChatRunProgressEvent progress) => progress.Stage switch
    {
        ChatProgressStage.Queued => "درخواست در صف بررسی است",
        ChatProgressStage.AssistantStarted => "دستیار بررسی را آغاز کرد",
        ChatProgressStage.WaitingForModel => "در انتظار پاسخ دستیار",
        ChatProgressStage.EndpointCheckStarted => ActivityLabel(progress.Activity),
        ChatProgressStage.EndpointCheckCompleted => progress.Outcome == ChatProgressOutcome.Failure
            ? "بررسی عامل ناموفق بود" : "بررسی عامل کامل شد",
        ChatProgressStage.Retrying => "ارتباط برقرار نشد؛ تلاش دوباره",
        ChatProgressStage.Completed => "پاسخ آماده شد",
        ChatProgressStage.Failed => "بررسی درخواست کامل نشد",
        ChatProgressStage.AwaitingApproval => "در انتظار تأیید اقدام پیشنهادی",
        ChatProgressStage.ActionStarted => "اقدام تأییدشده در حال اجرا است",
        ChatProgressStage.ActionCompleted => progress.Outcome == ChatProgressOutcome.Failure
            ? "اجرای اقدام ناموفق بود" : "اقدام تأییدشده انجام شد",
        ChatProgressStage.ActionRejected => "اقدام پیشنهادی اجرا نشد",
        _ => "در حال بررسی درخواست"
    };

    private static string ActivityLabel(ChatProgressActivity activity) => activity switch
    {
        ChatProgressActivity.OfficeInventory => "در حال بررسی نصب و نسخه آفیس",
        ChatProgressActivity.OfficeActivationStatus => "در حال بررسی فعال‌سازی آفیس",
        ChatProgressActivity.OfficeRuntimeStatus => "در حال بررسی برنامه‌های باز آفیس",
        ChatProgressActivity.WindowsActivationStatus => "در حال بررسی فعال‌سازی ویندوز",
        ChatProgressActivity.ProposedAction => "در حال آماده‌سازی اقدام پیشنهادی",
        _ => "در حال بررسی عامل"
    };
}

public sealed record ProgressStepViewModel(
    string Label, string Time, ChatProgressOutcome Outcome)
{
    public string StatusGlyph => Outcome == ChatProgressOutcome.Failure
        ? "\uE711"
        : "\uE73E";
}

internal static class BidiText
{
    public static string IsolateLeftToRight(string value) => "\u2066" + value + "\u2069";

    public static bool IsRightToLeft(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if ((rune.Value >= 0x0590 && rune.Value <= 0x08FF)
                || (rune.Value >= 0xFB1D && rune.Value <= 0xFDFF)
                || (rune.Value >= 0xFE70 && rune.Value <= 0xFEFF))
                return true;
            if (char.IsLetterOrDigit((char)rune.Value)) return false;
        }
        return true;
    }
}
