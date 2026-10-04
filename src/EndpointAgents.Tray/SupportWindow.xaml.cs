using EndpointAgents.Shared.Chat;
using EndpointAgents.Shared.Support;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;
using WinRT.Interop;

namespace EndpointAgents.Tray;

public sealed partial class SupportWindow : Window
{
    private const int MessageCharacterLimit = ChatLimits.MessageCharacters;
    private const int InitialWidthDips = 1240;
    private const int InitialHeightDips = 760;
    private const int MinimumWidthDips = 760;
    private const int MinimumHeightDips = 640;

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private readonly Func<Task> _refreshStatus;
    private readonly ServiceIpcClient _serviceClient = new();
    private readonly EndpointEventLogReader _logReader = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<string> _renderedMessageIds = [];
    private readonly HashSet<string> _seenActionIds = [];
    private readonly HashSet<string> _seenProgressEventIds = [];
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _chatTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _conversationListTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _reportTimer;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _searchTimer;
    private readonly AppWindow _appWindow;
    private readonly SemaphoreSlim _chatSyncGate = new(1, 1);
    private readonly Dictionary<string, double> _conversationScrollOffsets = [];
    private readonly Dictionary<string, string> _conversationDrafts = [];
    private IReadOnlyList<EndpointEventLogEntry> _logs = [];
    private ChatActionProposal? _pendingAction;
    private string? _conversationId;
    private string? _activeConversationId;
    private string? _activeProgressLabel;
    private long _lastChatSequence;
    private bool _allowClose;
    private bool _refreshingConversations;
    private bool _conversationListInitialized;
    private bool _changingConversation;
    private bool _startingConversation;
    private bool _loadingLogs;
    private bool _scrollToEndAfterSync;
    private bool _helpdeskPaneIsInline = true;
    private bool _detailsRequested = true;
    private bool _previewMode;
    private long _conversationVersion;
    private double? _restoreConversationOffset;

    public SupportViewModel ViewModel { get; } = new();

    public SupportWindow(Func<Task> refreshStatus)
    {
        _refreshStatus = refreshStatus;
        InitializeComponent();
        WindowRoot.DataContext = ViewModel;
        ConversationList.ItemTemplateSelector = new ConversationTemplateSelector
        {
            AssistantTemplate = (DataTemplate)WindowRoot.Resources["AssistantMessageTemplate"],
            UserTemplate = (DataTemplate)WindowRoot.Resources["UserMessageTemplate"],
            ProgressTemplate = (DataTemplate)WindowRoot.Resources["ProgressTemplate"]
        };
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarDragRegion);
        Title = "عامل نقطه پایانی";

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        ResizeWindowInDips(InitialWidthDips, InitialHeightDips);
        UpdateTitleBarInsets();
        _appWindow.Title = Title;
        _appWindow.Closing += AppWindow_Closing;
        _appWindow.Changed += AppWindow_Changed;

        _chatTimer = DispatcherQueue.CreateTimer();
        _chatTimer.Interval = TimeSpan.FromSeconds(1);
        _chatTimer.Tick += async (_, _) => await SyncChatAsync();
        _conversationListTimer = DispatcherQueue.CreateTimer();
        _conversationListTimer.Interval = TimeSpan.FromSeconds(3);
        _conversationListTimer.Tick += async (_, _) => await RefreshConversationsAsync();
        _reportTimer = DispatcherQueue.CreateTimer();
        _reportTimer.Interval = TimeSpan.FromSeconds(5);
        _reportTimer.Tick += async (_, _) => await RefreshLogsAsync();
        _searchTimer = DispatcherQueue.CreateTimer();
        _searchTimer.Interval = TimeSpan.FromMilliseconds(450);
        _searchTimer.IsRepeating = false;
        _searchTimer.Tick += async (_, _) => await RefreshLogsAsync();

        ViewModel.Conversation.CollectionChanged += Conversation_CollectionChanged;
        WindowRoot.Loaded += WindowRoot_Loaded;
        Navigation.SelectedItem = Navigation.MenuItems[0];
        UpdateSubmitState();
    }

    private double WindowScale() =>
        GetDpiForWindow(WindowNative.GetWindowHandle(this)) / 96.0;

    private void ResizeWindowInDips(int width, int height)
    {
        var scale = WindowScale();
        _appWindow.Resize(new SizeInt32((int)Math.Ceiling(width * scale),
            (int)Math.Ceiling(height * scale)));
    }

    private void UpdateTitleBarInsets()
    {
        // Insets are physical, but the RTL title bar mirrors Padding: its Left is the
        // physical right, where the caption buttons sit.
        var scale = WindowScale();
        TitleBarDragRegion.Padding = new Thickness(
            16 + _appWindow.TitleBar.RightInset / scale,
            0,
            16 + _appWindow.TitleBar.LeftInset / scale,
            0);
    }

    public void ShowSection(SupportSection section)
    {
        foreach (var item in Navigation.MenuItems.OfType<NavigationViewItem>())
        {
            if (string.Equals(item.Tag?.ToString(), section.ToString(),
                    StringComparison.Ordinal))
            {
                Navigation.SelectedItem = item;
                break;
            }
        }

        if (_appWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Minimized } presenter)
            presenter.Restore();
        _appWindow.Show(activateWindow: true);
        Activate();
        if (section == SupportSection.Helpdesk)
        {
            _scrollToEndAfterSync = true;
            _ = RefreshConversationsAsync();
            _ = SyncChatAsync();
        }
        else if (section == SupportSection.Logs)
        {
            _ = RefreshLogsAsync();
        }
        UpdatePolling();
    }

    public void UpdateStatus(TrayStatus status)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => UpdateStatus(status));
            return;
        }

        ViewModel.ApplyStatus(status);
        var dot = new SolidColorBrush(status.Health switch
        {
            TrayHealthState.Ready => ColorHelper.FromArgb(255, 0x2E, 0xB8, 0x72),
            TrayHealthState.Error => ColorHelper.FromArgb(255, 0xE5, 0x39, 0x35),
            TrayHealthState.Warning or TrayHealthState.Paused or TrayHealthState.Stopped
                => ColorHelper.FromArgb(255, 0xF2, 0xA1, 0x00),
            _ => ColorHelper.FromArgb(255, 0x8A, 0x96, 0xA3)
        });
        TitleStatusDot.Fill = dot;
        DetailsStatusDot.Fill = dot;
        StatusPageDot.Fill = dot;
        UpdateChatState();
        UpdateSubmitState();
    }

    public void ClosePermanently()
    {
        if (_allowClose) return;
        _allowClose = true;
        _lifetime.Cancel();
        _chatTimer.Stop();
        _conversationListTimer.Stop();
        _reportTimer.Stop();
        _searchTimer.Stop();
        ViewModel.Conversation.CollectionChanged -= Conversation_CollectionChanged;
        Close();
        _lifetime.Dispose();
    }

    private async void WindowRoot_Loaded(object sender, RoutedEventArgs e)
    {
        UpdateTitleBarInsets();
        await _refreshStatus();
        await RefreshConversationsAsync();
        UpdatePolling();
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowClose) return;
        if (_previewMode)
        {
            Application.Current.Exit();
            return;
        }
        args.Cancel = true;
        _appWindow.Hide();
        UpdatePolling();
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (args.DidSizeChange)
        {
            var size = _appWindow.Size;
            var scale = WindowScale();
            var minimumWidth = (int)Math.Ceiling(MinimumWidthDips * scale);
            var minimumHeight = (int)Math.Ceiling(MinimumHeightDips * scale);
            if (size.Width < minimumWidth || size.Height < minimumHeight)
                _appWindow.Resize(new SizeInt32(Math.Max(minimumWidth, size.Width),
                    Math.Max(minimumHeight, size.Height)));
        }
        if (args.DidPositionChange || args.DidSizeChange) UpdateTitleBarInsets();
        if (args.DidPresenterChange
            && _appWindow.Presenter is OverlappedPresenter
            { State: OverlappedPresenterState.Minimized })
        {
            _appWindow.Hide();
            UpdatePolling();
        }
    }

    private void Navigation_SelectionChanged(NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItemContainer as NavigationViewItem)?.Tag?.ToString();
        HelpdeskPage.Visibility = tag == nameof(SupportSection.Helpdesk)
            ? Visibility.Visible : Visibility.Collapsed;
        StatusPage.Visibility = tag == nameof(SupportSection.Status)
            ? Visibility.Visible : Visibility.Collapsed;
        LogsPage.Visibility = tag == nameof(SupportSection.Logs)
            ? Visibility.Visible : Visibility.Collapsed;
        if (tag == nameof(SupportSection.Helpdesk))
        {
            _scrollToEndAfterSync = true;
            _ = RefreshConversationsAsync();
            _ = SyncChatAsync();
        }
        else if (tag == nameof(SupportSection.Logs))
        {
            _ = RefreshLogsAsync();
        }
        UpdatePolling();
    }

    private async void RefreshStatus_Click(object sender, RoutedEventArgs e) =>
        await _refreshStatus();

    private void HelpdeskPage_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        UpdateDetailsPane(e.NewSize.Width);
        var inline = e.NewSize.Width >= 720;
        if (inline != _helpdeskPaneIsInline)
        {
            _helpdeskPaneIsInline = inline;
            ConversationSplitView.DisplayMode = inline
                ? SplitViewDisplayMode.Inline
                : SplitViewDisplayMode.Overlay;
            ConversationSplitView.IsPaneOpen = inline;
        }

        ConversationPaneButton.Visibility = inline
            ? Visibility.Collapsed
            : Visibility.Visible;
    }

    private void ConversationPaneButton_Click(object sender, RoutedEventArgs e)
    {
        ConversationSplitView.IsPaneOpen = !ConversationSplitView.IsPaneOpen;
        if (ConversationSplitView.IsPaneOpen)
            DispatcherQueue.TryEnqueue(() => ConversationIndex.Focus(FocusState.Keyboard));
        else
            ConversationPaneButton.Focus(FocusState.Keyboard);
    }

    private void ConversationFilter_Changed(SelectorBar sender,
        SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem is not { Tag: string tag }
            || !Enum.TryParse<ConversationListFilter>(tag, out var filter)) return;
        ViewModel.SetConversationFilter(filter);
    }

    private void ConversationSearch_TextChanged(AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args) =>
        ViewModel.SetConversationSearch(sender.Text);

    private void DetailsButton_Click(object sender, RoutedEventArgs e)
    {
        _detailsRequested = !_detailsRequested;
        UpdateDetailsPane(HelpdeskPage.ActualWidth);
    }

    private void UpdateDetailsPane(double pageWidth)
    {
        // Details only fit beside the list and a readable chat on wide windows.
        var fits = pageWidth >= 1040;
        var show = _detailsRequested && fits;
        DetailsPane.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        DetailsColumn.Width = show ? new GridLength(300) : new GridLength(0);
        DetailsButton.Visibility = fits ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void RefreshConversations_Click(object sender, RoutedEventArgs e) =>
        await RefreshConversationsAsync(showLoading: true);

    private async void ConversationIndex_SelectionChanged(object sender,
        SelectionChangedEventArgs e)
    {
        if (_changingConversation
            || ConversationIndex.SelectedItem is not ConversationSummaryViewModel selected
            || selected.ConversationId == _conversationId) return;
        await SelectConversationAsync(selected);
    }

    private async void NewConversation_Click(object sender, RoutedEventArgs e)
    {
        if (_startingConversation) return;
        _startingConversation = true;
        NewConversationButton.IsEnabled = false;
        ClosedConversationButton.IsEnabled = false;
        try
        {
            if (!string.IsNullOrWhiteSpace(_activeConversationId))
            {
                await _serviceClient.CloseChatAsync(new ChatConversationCloseRequest(
                    _activeConversationId, LocalChatIdentity()), _lifetime.Token);
                _activeConversationId = null;
            }
            BeginConversationDraft(clearDraft: true);
            await RefreshConversationsAsync();
            MessageInput.Focus(FocusState.Programmatic);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "گفتگوی جدید ایجاد نشد",
                Content = ex.Message,
                CloseButtonText = "بستن",
                DefaultButton = ContentDialogButton.Close
            };
            await dialog.ShowAsync();
        }
        finally
        {
            _startingConversation = false;
            NewConversationButton.IsEnabled = true;
            ClosedConversationButton.IsEnabled = true;
            UpdateSubmitState();
        }
    }

    private async Task RefreshConversationsAsync(string? selectConversationId = null,
        bool showLoading = false)
    {
        if (_previewMode || _refreshingConversations || !ViewModel.IsConnected
            || HelpdeskPage.Visibility != Visibility.Visible) return;
        _refreshingConversations = true;
        if (showLoading || !_conversationListInitialized)
            ViewModel.BeginConversationListRefresh();
        try
        {
            var summaries = await _serviceClient.ListChatsAsync(
                System.Diagnostics.Process.GetCurrentProcess().SessionId, _lifetime.Token);
            _activeConversationId = summaries.FirstOrDefault(item => item.IsActive)
                ?.ConversationId;
            var targetId = selectConversationId ?? _conversationId
                ?? (ViewModel.IsDraft ? _activeConversationId : null);
            _changingConversation = true;
            var selected = ViewModel.ReplaceConversations(summaries, targetId);
            if (selected is not null && selected.ConversationId == _conversationId)
            {
                ViewModel.SelectConversation(selected);
                ConversationIndex.SelectedItem = selected;
            }
            _changingConversation = false;

            if (selected is not null && selected.ConversationId != _conversationId)
                await SelectConversationAsync(selected);
            else if (!_conversationListInitialized && selected is null)
                BeginConversationDraft(clearDraft: false);
            else if (_conversationId is not null && selected is null)
                BeginConversationDraft(clearDraft: false);
            _conversationListInitialized = true;
            ViewModel.CompleteConversationListRefresh();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (!_conversationListInitialized)
            {
                BeginConversationDraft(clearDraft: false);
                _conversationListInitialized = true;
            }
            ViewModel.CompleteConversationListRefresh(
                "ارتباط محلی با سرویس کامل نشد. سرویس عامل را به‌روزرسانی یا دوباره راه‌اندازی کنید.");
        }
        finally
        {
            _changingConversation = false;
            _refreshingConversations = false;
            UpdateSubmitState();
        }
    }

    private async Task SelectConversationAsync(ConversationSummaryViewModel selected)
    {
        if (selected.ConversationId == _conversationId)
        {
            ViewModel.SelectConversation(selected);
            UpdateSubmitState();
            return;
        }

        SaveConversationUiState();
        _conversationVersion++;
        _conversationId = selected.ConversationId;
        ViewModel.SelectConversation(selected);
        ResetConversationTimeline();
        MessageInput.Text = _conversationDrafts.GetValueOrDefault(_conversationId, string.Empty);
        _restoreConversationOffset = _conversationScrollOffsets.GetValueOrDefault(
            _conversationId, double.NaN);
        if (double.IsNaN(_restoreConversationOffset.Value))
        {
            _restoreConversationOffset = null;
            _scrollToEndAfterSync = true;
        }
        _changingConversation = true;
        ConversationIndex.SelectedItem = selected;
        _changingConversation = false;
        if (ConversationSplitView.DisplayMode == SplitViewDisplayMode.Overlay)
        {
            ConversationSplitView.IsPaneOpen = false;
            DispatcherQueue.TryEnqueue(() => ConversationPaneButton.Focus(FocusState.Keyboard));
        }
        UpdateSubmitState();
        await SyncChatAsync(waitForGate: true);
    }

    private void BeginConversationDraft(bool clearDraft)
    {
        SaveConversationUiState();
        _conversationVersion++;
        _conversationId = null;
        ViewModel.BeginConversationDraft();
        ResetConversationTimeline();
        ViewModel.AddMessage(false,
            "سلام، من دستیار پشتیبانی هستم. مشکل را توضیح دهید تا بررسی را شروع کنم.");
        if (clearDraft) _conversationDrafts.Remove("__new");
        MessageInput.Text = _conversationDrafts.GetValueOrDefault("__new", string.Empty);
        _changingConversation = true;
        ConversationIndex.SelectedItem = null;
        _changingConversation = false;
        ViewModel.SubmitResult = string.Empty;
        UpdateChatState();
        UpdateSubmitState();
    }

    private void SaveConversationUiState()
    {
        var key = _conversationId ?? "__new";
        _conversationDrafts[key] = MessageInput.Text;
        if (_conversationId is not null
            && FindDescendant<ScrollViewer>(ConversationList) is { } viewer)
            _conversationScrollOffsets[_conversationId] = viewer.VerticalOffset;
    }

    private void ResetConversationTimeline()
    {
        ViewModel.ClearConversationTimeline();
        _renderedMessageIds.Clear();
        _seenActionIds.Clear();
        _seenProgressEventIds.Clear();
        _pendingAction = null;
        _activeProgressLabel = null;
        _lastChatSequence = 0;
        ViewModel.SubmitResult = string.Empty;
    }

    private void MessageInput_TextChanged(object sender, TextChangedEventArgs e)
    {
        CharacterCount.Text = $"{MessageInput.Text.Length:N0} از {MessageCharacterLimit:N0}";
        UpdateSubmitState();
    }

    private async void MessageInput_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(
            Windows.System.VirtualKey.Shift);
        if (state.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        e.Handled = true;
        await SubmitSupportAsync();
    }

    private async void Submit_Click(object sender, RoutedEventArgs e) =>
        await SubmitSupportAsync();

    private async Task SubmitSupportAsync()
    {
        var messageText = MessageInput.Text.Trim();
        if (ViewModel.IsSubmitting || !ViewModel.IsConnected
            || ViewModel.IsConversationReadOnly
            || string.IsNullOrWhiteSpace(messageText)
            || messageText.Length > MessageCharacterLimit) return;

        bool? actionApproval = null;
        if (_pendingAction is not null)
        {
            if (!ChatActionReply.TryParse(messageText, out var approved))
            {
                ViewModel.SubmitResult =
                    "برای پاسخ به اقدام پیشنهادی فقط بله/yes یا نه/no بنویسید.";
                return;
            }
            actionApproval = approved;
        }

        ViewModel.IsSubmitting = true;
        MessageInput.IsReadOnly = true;
        ViewModel.SubmitResult = "پیام در حال ارسال به دستیار پشتیبانی است…";
        UpdateSubmitState();
        try
        {
            if (_pendingAction is { } pending && actionApproval is { } approved)
            {
                _activeProgressLabel = approved
                    ? "در حال آغاز اقدام تأییدشده" : "در حال ثبت تصمیم شما";
                UpdateChatState();
                var decision = new ChatActionDecision(pending.ConversationId,
                    pending.ActionId, approved, LocalChatIdentity(), messageText);
                var actionReceipt = await _serviceClient.RespondToChatActionAsync(
                    decision, _lifetime.Token);
                if (actionReceipt.DecisionMessage is { } decisionMessage)
                {
                    _renderedMessageIds.Add(decisionMessage.MessageId);
                    ViewModel.AddMessage(true, decisionMessage.Body);
                }
                if (actionReceipt.Proposal is not { State: ChatActionState.Pending })
                {
                    _pendingAction = null;
                    MessageInput.Text = string.Empty;
                }
                ViewModel.AddMessage(false, actionReceipt.Message, "اقدام پیشنهادی");
                ViewModel.SubmitResult = actionReceipt.Message;
                await SyncChatAsync(waitForGate: true);
                return;
            }

            _activeProgressLabel = "در حال ثبت درخواست";
            UpdateChatState();
            var submission = new ChatMessageSubmission(Guid.NewGuid().ToString("N"),
                _conversationId, messageText,
                BidiText.IsRightToLeft(messageText) ? "rtl" : "ltr",
                LocalChatIdentity());
            var receipt = await _serviceClient.SendChatMessageAsync(
                submission, _lifetime.Token);
            if (!receipt.Accepted) throw new InvalidOperationException(receipt.Message);
            _conversationId = receipt.ConversationId;
            _activeConversationId = receipt.ConversationId;
            _renderedMessageIds.Add(receipt.MessageId);
            ViewModel.AddMessage(true, messageText);
            MessageInput.Text = string.Empty;
            ViewModel.SubmitResult = receipt.Message;
            TrayEventLogger.Information($"درخواست پشتیبانی {receipt.MessageId} ارسال شد.");
            await RefreshConversationsAsync(receipt.ConversationId);
            await SyncChatAsync(waitForGate: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _activeProgressLabel = null;
            UpdateChatState();
            ViewModel.SubmitResult = ex.Message;
            TrayEventLogger.Warning(
                $"ارسال درخواست پشتیبانی ناموفق بود: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ViewModel.IsSubmitting = false;
            MessageInput.IsReadOnly = false;
            UpdateSubmitState();
            MessageInput.Focus(FocusState.Programmatic);
        }
    }

    private async void ServiceAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string action }) return;
        if (action == "stop")
        {
            var dialog = new ContentDialog
            {
                XamlRoot = WindowRoot.XamlRoot,
                Title = "توقف سرویس عامل نقطه پایانی",
                Content = "با توقف سرویس، این رایانه تا راه‌اندازی دوباره از پشتیبانی قطع می‌شود.",
                PrimaryButtonText = "توقف سرویس",
                CloseButtonText = "انصراف",
                DefaultButton = ContentDialogButton.Close
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        ViewModel.SetActionRunning(true);
        ViewModel.ActionResult = "در انتظار تأیید مدیر سیستم…";
        try
        {
            var result = await ServiceControlLauncher.LaunchAsync(action, _lifetime.Token);
            ViewModel.ActionResult = result.Message;
            await Task.Delay(500, _lifetime.Token);
            await _refreshStatus();
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        finally
        {
            ViewModel.SetActionRunning(false);
        }
    }

    private void LogSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private async void LogFilter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (WindowRoot.XamlRoot is not null) await RefreshLogsAsync();
    }

    private async void RefreshLogs_Click(object sender, RoutedEventArgs e) =>
        await RefreshLogsAsync();

    private async Task RefreshLogsAsync()
    {
        if (_loadingLogs || !_appWindow.IsVisible
            || LogsPage.Visibility != Visibility.Visible) return;
        _loadingLogs = true;
        ViewModel.LogState = "در حال خواندن گزارش فعالیت عامل…";
        try
        {
            _logs = await _logReader.ReadAsync(GetSelectedAge(), GetSelectedLogLevel(),
                string.IsNullOrWhiteSpace(LogSearch.Text) ? null : LogSearch.Text.Trim(),
                _lifetime.Token);
            ViewModel.ReplaceLogs(_logs);
            ViewModel.LogState = _logs.Count == 0
                ? "رویدادی مطابق این فیلترها پیدا نشد."
                : $"{_logs.Count:N0} رویداد نمایش داده شد · جدیدترین در ابتدا";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ViewModel.LogState = "گزارش‌ها در دسترس نیستند: " + ex.Message;
        }
        finally
        {
            _loadingLogs = false;
        }
    }

    private void CopyLogs_Click(object sender, RoutedEventArgs e)
    {
        var text = ViewModel.SelectedLog is { } selected
            ? FormatLog(selected)
            : string.Join(Environment.NewLine + Environment.NewLine, _logs.Select(FormatLog));
        if (string.IsNullOrWhiteSpace(text)) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        ViewModel.LogState = "متن گزارش در حافظه موقت کپی شد.";
    }

    private async void ExportLogs_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileSavePicker
        {
            SuggestedFileName = $"EndpointAgent-report-{DateTime.Now:yyyyMMdd-HHmm}"
        };
        picker.FileTypeChoices.Add("فایل متنی", [".txt"]);
        picker.FileTypeChoices.Add("فایل JSON", [".json"]);
        InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;
        var text = file.FileType.Equals(".json", StringComparison.OrdinalIgnoreCase)
            ? JsonSerializer.Serialize(_logs, new JsonSerializerOptions { WriteIndented = true })
            : string.Join(Environment.NewLine + Environment.NewLine, _logs.Select(FormatLog));
        await File.WriteAllTextAsync(file.Path, text, Encoding.UTF8, _lifetime.Token);
        ViewModel.LogState = "گزارش‌ها ذخیره شدند: " + file.Path;
    }

    private async Task SyncChatAsync(bool waitForGate = false)
    {
        if (_previewMode || !_appWindow.IsVisible || HelpdeskPage.Visibility != Visibility.Visible
            || !ViewModel.IsConnected || string.IsNullOrWhiteSpace(_conversationId)) return;
        try
        {
            if (waitForGate)
                await _chatSyncGate.WaitAsync(_lifetime.Token);
            else if (!await _chatSyncGate.WaitAsync(0, _lifetime.Token))
                return;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
        {
            return;
        }
        var conversationId = _conversationId;
        var conversationVersion = _conversationVersion;
        try
        {
            var request = new ChatDeltaRequest(conversationId, _lastChatSequence,
                LocalChatIdentity());
            var delta = await _serviceClient.GetChatDeltaAsync(request, _lifetime.Token);
            if (conversationVersion != _conversationVersion
                || conversationId != _conversationId) return;
            ViewModel.ApplyConversationLimit(delta.VisibleItemLimit);
            var entries = new List<(long Sequence, ChatMessage? Message,
                ChatActionProposal? Action, ChatRunProgressEvent? Progress)>();
            entries.AddRange(delta.Messages.Select(item =>
                (item.Sequence, (ChatMessage?)item, (ChatActionProposal?)null,
                    (ChatRunProgressEvent?)null)));
            entries.AddRange(delta.Actions.Select(item =>
                (item.Sequence, (ChatMessage?)null, (ChatActionProposal?)item,
                    (ChatRunProgressEvent?)null)));
            entries.AddRange((delta.ProgressEvents ?? []).Select(item =>
                (item.Sequence, (ChatMessage?)null, (ChatActionProposal?)null,
                    (ChatRunProgressEvent?)item)));
            foreach (var entry in entries.OrderBy(item => item.Sequence))
            {
                if (entry.Message is { } message)
                {
                    if (_renderedMessageIds.Add(message.MessageId))
                    {
                        ViewModel.AddMessage(message.Role == ChatMessageRole.User,
                            message.Body, message.Role switch
                            {
                                ChatMessageRole.System => "سیستم",
                                ChatMessageRole.Operator => "کارشناس پشتیبانی",
                                _ => null
                            }, message.CreatedAtUtc);
                    }
                    UpdateChatMessageState(message);
                }
                else if (entry.Action is { } action && _seenActionIds.Add(action.ActionId))
                {
                    HandleActionProposal(action);
                }
                else if (entry.Progress is { } progress
                         && _seenProgressEventIds.Add(progress.EventId))
                {
                    var item = ViewModel.AddProgress(progress);
                    _activeProgressLabel = item.IsTerminal ? null : item.Summary;
                    UpdateChatState();
                    TrayEventLogger.Activity(progress.CreatedAtUtc,
                        $"روند درخواست {progress.RunId}: {item.Summary}.");
                }
                _lastChatSequence = Math.Max(_lastChatSequence, entry.Sequence);
            }
            _conversationId = delta.Conversation.ConversationId;
            _lastChatSequence = Math.Max(_lastChatSequence, delta.Conversation.LastSequence);
            if (_restoreConversationOffset is { } offset)
            {
                _restoreConversationOffset = null;
                DispatcherQueue.TryEnqueue(() =>
                    FindDescendant<ScrollViewer>(ConversationList)?.ChangeView(
                        null, offset, null, disableAnimation: true));
            }
            else if (_scrollToEndAfterSync)
            {
                _scrollToEndAfterSync = false;
                ScrollConversationToEnd();
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ViewModel.SubmitResult = ex.Message;
            TrayEventLogger.Warning(
                $"همگام‌سازی گفتگوی پشتیبانی ناموفق بود: {ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            _chatSyncGate.Release();
            UpdateSubmitState();
        }
    }

    private void HandleActionProposal(ChatActionProposal proposal)
    {
        if (proposal.State != ChatActionState.Pending)
        {
            ViewModel.AddMessage(false,
                $"{proposal.DisplayName}\r\nوضعیت: {PersianActionState(proposal.State)}",
                "اقدام پیشنهادی");
            return;
        }
        _pendingAction = proposal;
        ViewModel.AddMessage(false,
            $"{proposal.DisplayName}\r\n{proposal.Description}{FormatApprovedArtifact(proposal.Parameters)}" +
            "\r\n\r\nآیا انجامش دهم؟ بله/yes یا نه/no بنویسید.",
            "اقدام پیشنهادی");
    }

    private void UpdateChatMessageState(ChatMessage message)
    {
        if (message.State == ChatMessageState.Failed)
        {
            ViewModel.SubmitResult = message.Role == ChatMessageRole.Assistant
                ? "دستیار نتوانست این درخواست را کامل کند."
                : "بررسی این پیام ناموفق بود.";
        }
        else if (message.State == ChatMessageState.Queued)
            ViewModel.SubmitResult = "پیام در صف دستیار پشتیبانی است.";
        else if (message.State == ChatMessageState.Working)
            ViewModel.SubmitResult = "دستیار پشتیبانی در حال بررسی پیام است…";
    }

    private void Conversation_CollectionChanged(object? sender,
        NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add && IsConversationNearEnd())
            DispatcherQueue.TryEnqueue(ScrollConversationToEnd);
    }

    private bool IsConversationNearEnd()
    {
        var viewer = FindDescendant<ScrollViewer>(ConversationList);
        return viewer is null || viewer.ScrollableHeight - viewer.VerticalOffset <= 56;
    }

    private void ScrollConversationToEnd()
    {
        if (ViewModel.Conversation.LastOrDefault() is { } last)
            ConversationList.ScrollIntoView(last, ScrollIntoViewAlignment.Leading);
    }

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match) return match;
            if (FindDescendant<T>(child) is { } nested) return nested;
        }
        return null;
    }

    private void UpdatePolling()
    {
        var visible = _appWindow.IsVisible && !_lifetime.IsCancellationRequested;
        if (visible && HelpdeskPage.Visibility == Visibility.Visible)
        {
            _chatTimer.Start();
            _conversationListTimer.Start();
        }
        else
        {
            _chatTimer.Stop();
            _conversationListTimer.Stop();
        }
        if (visible && LogsPage.Visibility == Visibility.Visible) _reportTimer.Start();
        else _reportTimer.Stop();
    }

    private void UpdateSubmitState()
    {
        var readOnly = ViewModel.IsConversationReadOnly;
        MessageInput.IsReadOnly = ViewModel.IsSubmitting || readOnly;
        MessageInput.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        SubmitButton.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        ClosedConversationButton.Visibility = readOnly
            ? Visibility.Visible
            : Visibility.Collapsed;
        CharacterCount.Visibility = readOnly ? Visibility.Collapsed : Visibility.Visible;
        SubmitButton.IsEnabled = !ViewModel.IsSubmitting && ViewModel.IsConnected
            && !readOnly
            && !string.IsNullOrWhiteSpace(MessageInput.Text)
            && MessageInput.Text.Length <= MessageCharacterLimit;
        SendIcon.Visibility = ViewModel.IsSubmitting ? Visibility.Collapsed : Visibility.Visible;
        SendingRing.IsActive = ViewModel.IsSubmitting;
        NewConversationButton.IsEnabled = ViewModel.IsConnected && !_startingConversation;
        ClosedConversationButton.IsEnabled = ViewModel.IsConnected && !_startingConversation;
        MessageInput.PlaceholderText = readOnly
            ? "این گفتگو بسته شده و فقط برای مشاهده است."
            : "پیام خود را بنویسید…";
        if (readOnly && string.IsNullOrWhiteSpace(ViewModel.SubmitResult))
            ViewModel.SubmitResult =
                "این گفتگو بسته شده است؛ برای درخواست تازه، گفتگوی جدید بسازید.";
        const string disconnected =
            "برای ارسال درخواست، عامل نقطه پایانی باید به سرور متصل باشد.";
        if (!ViewModel.IsConnected && string.IsNullOrWhiteSpace(ViewModel.SubmitResult))
            ViewModel.SubmitResult = disconnected;
        else if (ViewModel.IsConnected && ViewModel.SubmitResult == disconnected)
            ViewModel.SubmitResult = string.Empty;
    }

    private void UpdateChatState()
    {
        ViewModel.ChatState = !ViewModel.IsConnected ? "در انتظار اتصال"
            : !string.IsNullOrWhiteSpace(_activeProgressLabel) ? _activeProgressLabel
            : "متصل به پشتیبانی";
    }

    private ChatIdentity LocalChatIdentity() => new("S-1-0-0", Environment.UserName,
        System.Diagnostics.Process.GetCurrentProcess().SessionId);

    private TimeSpan GetSelectedAge() => LogAge.SelectedIndex switch
    {
        0 => TimeSpan.FromHours(1),
        2 => TimeSpan.FromHours(24),
        3 => TimeSpan.FromDays(7),
        _ => TimeSpan.FromHours(6)
    };

    private string? GetSelectedLogLevel() => LogLevel.SelectedIndex switch
    {
        1 => "Error",
        2 => "Warning",
        3 => "Information",
        _ => null
    };

    private static string PersianActionState(ChatActionState state) => state switch
    {
        ChatActionState.Approved => "تأییدشده",
        ChatActionState.Rejected => "ردشده",
        ChatActionState.Expired => "منقضی",
        ChatActionState.Executed => "اجراشده",
        ChatActionState.Failed => "ناموفق",
        _ => "در انتظار"
    };

    private static string FormatApprovedArtifact(JsonElement? parameters)
    {
        if (parameters is not { ValueKind: JsonValueKind.Object } value
            || !value.TryGetProperty(ChatActionApproval.ArtifactFileNameParameterName,
                out var fileName) || fileName.ValueKind != JsonValueKind.String
            || !value.TryGetProperty(ChatActionApproval.ArtifactSha256ParameterName,
                out var sha256) || sha256.ValueKind != JsonValueKind.String)
            return string.Empty;
        return $"\r\n\r\nFile: {fileName.GetString()}\r\nSHA-256: {sha256.GetString()}";
    }

    private static string FormatLog(EndpointEventLogEntry item) =>
        $"[{item.TimestampUtc.LocalDateTime:yyyy-MM-dd HH:mm:ss}] " +
        $"{SupportViewModel.PersianLevel(item.Level)} · {item.Source} · " +
        $"{(item.EventId == 0 ? "—" : item.EventId)}\r\n{item.Message}";
}

public sealed class ConversationTemplateSelector : DataTemplateSelector
{
    public DataTemplate? AssistantTemplate { get; set; }
    public DataTemplate? UserTemplate { get; set; }
    public DataTemplate? ProgressTemplate { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item) =>
        item is ConversationItemViewModel conversation
            ? conversation.Kind switch
            {
                ConversationItemKind.UserMessage => UserTemplate,
                ConversationItemKind.Progress => ProgressTemplate,
                _ => AssistantTemplate
            }
            : base.SelectTemplateCore(item);
}

public sealed class ConversationStateBrushConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        string language) => new SolidColorBrush(value switch
        {
            ChatConversationState.Working => ColorHelper.FromArgb(255, 0x8E, 0x6C, 0xEF),
            ChatConversationState.AwaitingApproval => ColorHelper.FromArgb(255, 0xF0, 0x8C, 0x00),
            ChatConversationState.Answered => ColorHelper.FromArgb(255, 0x1F, 0xA8, 0x9F),
            ChatConversationState.Resolved => ColorHelper.FromArgb(255, 0x4C, 0xAF, 0x50),
            ChatConversationState.Closed => ColorHelper.FromArgb(255, 0x8A, 0x96, 0xA3),
            _ => ColorHelper.FromArgb(255, 0x33, 0x90, 0xEC)
        });

    public object ConvertBack(object value, Type targetType, object parameter,
        string language) => throw new NotSupportedException();
}

public sealed class BoolVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        string language) => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object parameter,
        string language) => throw new NotSupportedException();
}

public sealed class BidiFlowDirectionConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter,
        string language) => value is true
            ? FlowDirection.RightToLeft
            : FlowDirection.LeftToRight;

    public object ConvertBack(object value, Type targetType, object parameter,
        string language) => throw new NotSupportedException();
}
