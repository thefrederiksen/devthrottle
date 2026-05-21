using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Rich card-based view of Claude Code session output.
/// Parses JSONL streaming output and renders each tool call as a styled widget card.
/// Follows Attach/Detach pattern.
/// </summary>
public partial class CleanView : UserControl
{
    private Session? _session;
    private DispatcherTimer? _pollTimer;
    private int _lastLineCount;
    private string? _jsonlPath;
    private bool _parsing;

    private readonly ObservableCollection<CleanWidgetViewModel> _allWidgets = new();
    private readonly ObservableCollection<CleanWidgetViewModel> _filteredWidgets = new();
    private string? _pendingInjection;
    private string _filterMode = "All"; // "All", "UserOnly", "Conversation"

    // The orange question card pinned at the end of the feed whenever the
    // supervisor says the session is in "red" status with a distilled question.
    // We hold one instance and add/remove it from the collections directly so
    // the existing scroll-to-bottom behavior just works.
    private CleanWidgetViewModel? _pendingQuestionWidget;

    // Phase 5.1: high-water mark of how many widgets we've already written to
    // <session-logs>/<sid>/agent-view.jsonl. Only grows; if a rewind shrinks
    // _allWidgets we keep the old high water and skip duplicates on the next
    // expansion. Reset on Detach.
    private int _persistedWidgetCount;

    /// <summary>Fired when user requests a rewind. Args: (session, snapshotEntryNumber).</summary>
    public event Action<Session, int>? RewindRequested;

    public CleanView()
    {
        InitializeComponent();

        WidgetItems.ItemsSource = _filteredWidgets;
        FilterCombo.SelectionChanged += FilterCombo_SelectionChanged;
    }

    /// <summary>Attach to a session and start monitoring its JSONL output.</summary>
    public void Attach(Session session)
    {
        FileLog.Write($"[CleanView] Attach: session={session.Id}, backendType={session.BackendType}");
        Detach();

        _session = session;
        _lastLineCount = 0;

        // Subscribe to activity state changes
        session.OnActivityStateChanged += OnActivityStateChanged;

        // Subscribe to supervisor status changes so we can show/hide the
        // pending-question card inline at the end of the feed.
        session.OnStatusColorChanged += OnStatusColorChanged;
        SyncPendingQuestionWidget();

        if (session.Backend is StudioBackend studio)
        {
            // Studio mode: subscribe to live stream events, no file polling
            FileLog.Write("[CleanView] Attach: Studio mode -- subscribing to StreamMessageReceived");
            studio.StreamMessageReceived += OnStreamMessageReceived;
            LoadingText.IsVisible = true;
            EmptyText.IsVisible = false;

            // Load any messages already received
            var existing = studio.GetMessages();
            if (existing.Count > 0)
            {
                var studioSnapshotCount = session.History?.SnapshotCount ?? 0;
                var widgets = CleanWidgetViewModel.BuildFromMessages(existing, studioSnapshotCount);
                ReplaceAllWidgets(widgets);
                UpdateEmptyState();
                ScrollToBottom();
            }
        }
        else
        {
            // Terminal mode: file-based polling
            _jsonlPath = ResolveJsonlPath(session);

            if (_jsonlPath == null)
            {
                // Brand new session - no Claude output yet. Show the inviting
                // empty state, not the "Loading..." spinner. The poll timer
                // will pick the JSONL up the moment Claude starts writing.
                FileLog.Write("[CleanView] Attach: no JSONL path available yet, will poll");
                LoadingText.IsVisible = false;
                EmptyText.IsVisible = true;
            }
            else
            {
                LoadingText.IsVisible = true;
                EmptyText.IsVisible = false;
                Dispatcher.UIThread.Post(() => ParseAndUpdate(), DispatcherPriority.Background);
            }

            // Subscribe to metadata changes (ClaudeSessionId may arrive later)
            session.OnClaudeMetadataChanged += OnClaudeMetadataChanged;

            // Start polling timer (2 second interval for incremental updates)
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _pollTimer.Tick += PollTimer_Tick;
            _pollTimer.Start();
        }

        // Show progress if already working
        if (session.ActivityState == ActivityState.Working)
        {
            ProgressArea.IsVisible = true;
        }
    }

    /// <summary>Detach from the current session and clean up.</summary>
    public void Detach()
    {
        if (_session != null)
        {
            FileLog.Write($"[CleanView] Detach: session={_session.Id}");
            _session.OnActivityStateChanged -= OnActivityStateChanged;
            _session.OnClaudeMetadataChanged -= OnClaudeMetadataChanged;
            _session.OnStatusColorChanged -= OnStatusColorChanged;

            // Unsubscribe from StudioBackend events
            if (_session.Backend is StudioBackend studio)
                studio.StreamMessageReceived -= OnStreamMessageReceived;
        }

        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _jsonlPath = null;
        _lastLineCount = 0;
        _parsing = false;
        _pendingInjection = null;
        _filterMode = "All";
        FilterCombo.SelectedIndex = 0;
        _allWidgets.Clear();
        _filteredWidgets.Clear();
        _pendingQuestionWidget = null;
        _persistedWidgetCount = 0;
        ProgressArea.IsVisible = false;
        LoadingText.IsVisible = false;
        EmptyText.IsVisible = true;
    }

    private string? ResolveJsonlPath(Session session)
    {
        if (string.IsNullOrEmpty(session.ClaudeSessionId))
            return null;

        var path = ClaudeSessionReader.GetJsonlPath(session.ClaudeSessionId, session.RepoPath);
        if (!System.IO.File.Exists(path))
        {
            FileLog.Write($"[CleanView] ResolveJsonlPath: file not found: {path}");
            return null;
        }

        FileLog.Write($"[CleanView] ResolveJsonlPath: {path}");
        return path;
    }

    private void OnClaudeMetadataChanged(ClaudeSessionMetadata? metadata)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_session == null)
                return;

            // Try to resolve JSONL path now that metadata may have updated
            if (_jsonlPath == null)
            {
                _jsonlPath = ResolveJsonlPath(_session);
                if (_jsonlPath != null)
                {
                    FileLog.Write("[CleanView] OnClaudeMetadataChanged: JSONL path resolved, parsing");
                    ParseAndUpdate();
                }
            }
        });
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
    {
        Dispatcher.UIThread.Post(SyncPendingQuestionWidget);
    }

    // Ensure the orange question card is present at the tail of the feed when
    // the supervisor flags a pending question (color=red + non-empty reason),
    // and gone otherwise. Idempotent; safe to call repeatedly.
    private void SyncPendingQuestionWidget()
    {
        if (_session == null) return;

        bool shouldShow = string.Equals(_session.StatusColor, "red", StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(_session.LastStatusReason);

        if (shouldShow)
        {
            var text = _session.LastStatusReason ?? "";

            if (_pendingQuestionWidget == null)
            {
                _pendingQuestionWidget = new CleanWidgetViewModel
                {
                    Kind = WidgetKind.PendingQuestion,
                    Header = "Claude is waiting on your answer",
                    Content = text,
                };
                _allWidgets.Add(_pendingQuestionWidget);
                if (PassesFilter(_pendingQuestionWidget))
                    _filteredWidgets.Add(_pendingQuestionWidget);
                ScrollToBottom();
            }
            else if (!string.Equals(_pendingQuestionWidget.Content, text, StringComparison.Ordinal))
            {
                // Question text changed (supervisor reran the summary). Swap the
                // widget for a new one with the updated text; the binding is
                // init-only so we cannot mutate in place.
                var idxAll = _allWidgets.IndexOf(_pendingQuestionWidget);
                var idxFiltered = _filteredWidgets.IndexOf(_pendingQuestionWidget);
                var replacement = new CleanWidgetViewModel
                {
                    Kind = WidgetKind.PendingQuestion,
                    Header = "Claude is waiting on your answer",
                    Content = text,
                };
                if (idxAll >= 0) _allWidgets[idxAll] = replacement;
                if (idxFiltered >= 0) _filteredWidgets[idxFiltered] = replacement;
                _pendingQuestionWidget = replacement;
            }
        }
        else
        {
            if (_pendingQuestionWidget != null)
            {
                _allWidgets.Remove(_pendingQuestionWidget);
                _filteredWidgets.Remove(_pendingQuestionWidget);
                _pendingQuestionWidget = null;
            }
        }
    }

    private void OnActivityStateChanged(ActivityState oldState, ActivityState newState)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (newState == ActivityState.Working)
            {
                ProgressArea.IsVisible = true;
                ProgressText.Text = "Claude is working...";
                YourTurnText.IsVisible = false;
            }
            else
            {
                ProgressArea.IsVisible = false;

                // Show "Your Turn" when Claude finishes or needs input, and there are widgets visible
                if ((newState == ActivityState.WaitingForInput || oldState == ActivityState.Working)
                    && _allWidgets.Count > 0)
                {
                    YourTurnText.IsVisible = true;
                }

                // Do a final parse when Claude finishes a turn
                if (oldState == ActivityState.Working)
                {
                    ParseAndUpdate();
                }
            }
        });
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        if (_session == null || _parsing)
            return;

        // Try to resolve path if we don't have it yet
        if (_jsonlPath == null)
        {
            _jsonlPath = ResolveJsonlPath(_session);
            if (_jsonlPath == null)
                return;
        }

        ParseAndUpdate();
    }

    private void ParseAndUpdate()
    {
        if (_jsonlPath == null || _parsing)
            return;

        _parsing = true;

        try
        {
            var (newMessages, newLineCount) = StreamMessageParser.ParseFileFrom(_jsonlPath, _lastLineCount);

            if (newMessages.Count == 0 && _lastLineCount > 0)
            {
                // No new messages - nothing to do
                return;
            }

            var snapshotCount = _session?.History?.SnapshotCount ?? 0;

            if (_lastLineCount == 0)
            {
                // Full initial load
                var allMessages = StreamMessageParser.ParseFile(_jsonlPath);
                var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages, snapshotCount);

                ReplaceAllWidgets(allWidgets);
                _lastLineCount = newLineCount > 0 ? newLineCount : CountLines(_jsonlPath);
            }
            else if (newMessages.Count > 0)
            {
                // Incremental update - rebuild all widgets from scratch
                // (needed because tool results reference earlier tool_use blocks)
                var allMessages = StreamMessageParser.ParseFile(_jsonlPath);
                var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages, snapshotCount);

                ReplaceAllWidgets(allWidgets);
                _lastLineCount = newLineCount;
            }

            // If we injected a user prompt that isn't in the JSONL yet, re-append it
            if (_pendingInjection != null)
            {
                var lastUserWidget = _allWidgets.LastOrDefault(w => w.Kind == WidgetKind.UserMessage);
                if (lastUserWidget == null || lastUserWidget.Content != _pendingInjection)
                {
                    var injected = new CleanWidgetViewModel
                    {
                        Kind = WidgetKind.UserMessage,
                        Header = "You",
                        Content = _pendingInjection
                    };
                    _allWidgets.Add(injected);
                    if (PassesFilter(injected))
                        _filteredWidgets.Add(injected);
                }
                else
                {
                    // JSONL caught up -- clear the pending injection
                    _pendingInjection = null;
                }
            }

            UpdateEmptyState();
            ScrollToBottom();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CleanView] ParseAndUpdate FAILED: {ex.Message}");
        }
        finally
        {
            _parsing = false;
        }
    }

    private static int CountLines(string path)
    {
        using var fs = new System.IO.FileStream(path, System.IO.FileMode.Open,
            System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite);
        using var reader = new System.IO.StreamReader(fs);

        int count = 0;
        while (reader.ReadLine() != null)
            count++;
        return count;
    }

    private void UpdateEmptyState()
    {
        LoadingText.IsVisible = false;
        EmptyText.IsVisible = _filteredWidgets.Count == 0;
    }

    private void ScrollToBottom()
    {
        Dispatcher.UIThread.Post(() =>
        {
            WidgetScroller.ScrollToEnd();
        }, DispatcherPriority.Loaded);
    }

    /// <summary>Inject a user prompt immediately so it appears before the JSONL poll picks it up.</summary>
    public void InjectUserPrompt(string text)
    {
        FileLog.Write($"[CleanView] InjectUserPrompt: {text.Length} chars");
        if (string.IsNullOrWhiteSpace(text))
            return;

        _pendingInjection = text;
        YourTurnText.IsVisible = false;

        var widget = new CleanWidgetViewModel
        {
            Kind = WidgetKind.UserMessage,
            Header = "You",
            Content = text
        };

        _allWidgets.Add(widget);
        if (PassesFilter(widget))
            _filteredWidgets.Add(widget);

        UpdateEmptyState();
        ScrollToBottom();
    }

    // ==================== FILTERING ====================

    private bool PassesFilter(CleanWidgetViewModel vm)
    {
        // Pending-question card always shows through, regardless of filter mode.
        // It's the most important thing on the screen when present.
        if (vm.Kind == WidgetKind.PendingQuestion)
            return true;
        if (_filterMode == "All")
            return true;
        if (_filterMode == "UserOnly")
            return vm.Kind == WidgetKind.UserMessage;
        // "Conversation" mode: user messages + Claude text responses only
        return vm.Kind == WidgetKind.UserMessage || vm.Kind == WidgetKind.Text;
    }

    private void ReplaceAllWidgets(List<CleanWidgetViewModel> widgets)
    {
        _allWidgets.Clear();
        _filteredWidgets.Clear();

        foreach (var w in widgets)
        {
            _allWidgets.Add(w);
            if (PassesFilter(w))
                _filteredWidgets.Add(w);
        }

        PersistNewWidgetsToDisk(widgets);

        // ReplaceAllWidgets just cleared the pending-question widget; reinsert
        // it at the tail if the supervisor still says there is one.
        _pendingQuestionWidget = null;
        SyncPendingQuestionWidget();
    }

    /// <summary>
    /// Phase 5.1: append any widgets we haven't already persisted to the session's
    /// <c>agent-view.jsonl</c>. <see cref="_persistedWidgetCount"/> only grows -
    /// rewinds (shorter list) are no-ops, so we never re-write history.
    /// </summary>
    private void PersistNewWidgetsToDisk(List<CleanWidgetViewModel> widgets)
    {
        if (_session is null) return;
        if (widgets.Count <= _persistedWidgetCount) return;
        var manager = (global::Avalonia.Application.Current as App)?.ControlApiHost?.SessionLogManager;
        if (manager is null) return;

        for (int i = _persistedWidgetCount; i < widgets.Count; i++)
        {
            var w = widgets[i];
            try
            {
                manager.WriteAgentViewWidget(_session.Id, new
                {
                    ts = DateTime.UtcNow,
                    index = i,
                    kind = w.Kind.ToString(),
                    header = w.Header,
                    content = w.Content,
                    result = w.Result,
                    isPending = w.IsPending,
                });
            }
            catch (Exception ex)
            {
                FileLog.Write($"[CleanView] persist widget #{i} failed: {ex.Message}");
            }
        }
        _persistedWidgetCount = widgets.Count;
    }

    private void ApplyFilter()
    {
        _filteredWidgets.Clear();
        foreach (var w in _allWidgets)
        {
            if (PassesFilter(w))
                _filteredWidgets.Add(w);
        }

        UpdateEmptyState();
        ScrollToBottom();
    }

    private void FilterCombo_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (FilterCombo.SelectedItem is not ComboBoxItem item)
            return;

        var newMode = item.Tag?.ToString() ?? "All";
        if (newMode == _filterMode)
            return;

        _filterMode = newMode;
        FileLog.Write($"[CleanView] FilterCombo_SelectionChanged: filterMode={_filterMode}");
        ApplyFilter();
    }

    // ==================== REWIND ====================

    private void RewindButton_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[CleanView] RewindButton_Click");
        if (sender is not Button button || button.Tag is not int entryNumber)
            return;

        // Open context menu for confirmation
        var menu = new ContextMenu();
        var resetItem = new MenuItem { Header = "Reset to here", Tag = entryNumber };
        resetItem.Click += RewindMenuItem_Click;
        menu.Items.Add(resetItem);
        menu.Open(button);
    }

    private void RewindMenuItem_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not int entryNumber)
            return;

        if (_session == null)
        {
            FileLog.Write("[CleanView] RewindMenuItem_Click: no session");
            return;
        }

        FileLog.Write($"[CleanView] RewindMenuItem_Click: entryNumber={entryNumber}, session={_session.Id}");
        RewindRequested?.Invoke(_session, entryNumber);
    }

    /// <summary>Handle live stream messages from StudioBackend.</summary>
    private void OnStreamMessageReceived(StreamMessage msg)
    {
        // Called from background thread -- dispatch to UI
        Dispatcher.UIThread.Post(() =>
        {
            if (_session == null || _session.Backend is not StudioBackend studio)
                return;

            // Rebuild all widgets from the accumulated messages
            var allMessages = studio.GetMessages();
            var snapshotCount = _session?.History?.SnapshotCount ?? 0;
            var allWidgets = CleanWidgetViewModel.BuildFromMessages(allMessages, snapshotCount);

            ReplaceAllWidgets(allWidgets);
            UpdateEmptyState();
            ScrollToBottom();
        });
    }
}
