using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Avalonia.Helpers;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Gemini;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia.Controls;

/// <summary>One rendered message in the History tab (a normalized conversation message).</summary>
public sealed class HistoryMessageVm
{
    public string Speaker { get; init; } = "";
    public string Body { get; init; } = "";
    public IBrush HeaderBrush { get; init; } = Brushes.Gray;
    public IBrush CardBrush { get; init; } = Brushes.Transparent;

    /// <summary>True for raw terminal scrollback (Gemini): render verbatim, not as Markdown.</summary>
    public bool IsRawText { get; init; }

    /// <summary>Link context that makes file paths and URLs in this bubble clickable (#735).</summary>
    public MarkdownRenderContext? LinkContext { get; init; }
}

/// <summary>
/// The History tab: a read-only, agent-agnostic view of a session's conversation, rendered from
/// the canonical <see cref="ConversationHistory"/>. It polls the transcript on a timer (cheaply -
/// it only re-parses when the underlying file actually changes) and shows the same clean thread
/// regardless of which agent produced it. First cut targets Claude; other agents show a notice
/// until their providers land.
/// </summary>
public partial class HistoryView : UserControl
{
    private static readonly IBrush UserHeader = new SolidColorBrush(Color.FromRgb(0x6C, 0xA0, 0xF0));
    private static readonly IBrush AssistantHeader = new SolidColorBrush(Color.FromRgb(0x57, 0xC7, 0x7D));
    private static readonly IBrush ToolHeader = new SolidColorBrush(Color.FromRgb(0xC9, 0xA2, 0x27));
    private static readonly IBrush UserCard = new SolidColorBrush(Color.FromRgb(0x23, 0x2A, 0x38));
    private static readonly IBrush AssistantCard = new SolidColorBrush(Color.FromRgb(0x23, 0x28, 0x26));
    private static readonly IBrush ToolCard = new SolidColorBrush(Color.FromRgb(0x2A, 0x27, 0x1B));

    // Cap for the Gemini raw-terminal-text block (it has no structured turns to truncate per-card).
    // Keep the recent tail; an uncapped multi-hundred-KB body in one wrapping TextBlock janks the UI.
    private const int GeminiBodyMaxChars = 24_000;

    // History-state pill colors (deliberately distinct from the green "live" badge so the
    // transcript-derived label can never be mistaken for the live byte-based status).
    private static readonly IBrush BgRunningPill = new SolidColorBrush(Color.FromRgb(0x3A, 0x2A, 0x4A));
    private static readonly IBrush BgRunningText = new SolidColorBrush(Color.FromRgb(0xC9, 0xA0, 0xF0));
    private static readonly IBrush WorkingPill = new SolidColorBrush(Color.FromRgb(0x24, 0x33, 0x4A));
    private static readonly IBrush WorkingText = new SolidColorBrush(Color.FromRgb(0x6C, 0xA0, 0xF0));
    private static readonly IBrush NeedsYouPill = new SolidColorBrush(Color.FromRgb(0x44, 0x28, 0x28));
    private static readonly IBrush NeedsYouText = new SolidColorBrush(Color.FromRgb(0xE0, 0x8A, 0x8A));
    private static readonly IBrush IdlePill = new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x2A));
    private static readonly IBrush IdleText = new SolidColorBrush(Color.FromRgb(0xAA, 0xAA, 0xAA));

    private readonly ObservableCollection<HistoryMessageVm> _messages = new();
    private DispatcherTimer? _timer;
    private Session? _session;
    private string _lastSignature = "";

    // What the header "Show:" toggles let through (tool calls / tool results / thinking). Loaded
    // from config.json on construction and re-saved whenever a checkbox flips (#760).
    private HistoryFilterConfig _filter = HistoryFilterConfig.Default;

    // The most recently parsed conversation, kept so a filter toggle can re-render instantly from
    // memory without re-reading and re-parsing the transcript. Null for the raw-text (Gemini) path.
    private ConversationHistory? _lastHistory;

    // Whether the History tab is the visible tab (#744). MainWindow drives this via OnShown/OnHidden
    // so the poll timer only runs while the tab is on screen.
    private bool _isShown;

    // Shared per-session link context for clickable paths/URLs in bubbles (#735). Built on Attach
    // because its inputs (the repo path and the routing callbacks) are stable for a session.
    private MarkdownRenderContext? _linkContext;

    /// <summary>Raised when the user picks "View File" on a link in a bubble (resolved path).</summary>
    public event Action<string>? ViewFileRequested;

    /// <summary>Raised when opening a link in a browser fails (human-readable message).</summary>
    public event Action<string>? BrowserLaunchFailed;

    // Cached transcript analysis for the derived history state (Claude only). Re-parsed only when
    // the transcript file changes; re-derived every tick so the process-liveness guard updates fast.
    private HistoryAnalysis? _lastAnalysis;
    private string _analysisSignature = "";

    public HistoryView()
    {
        InitializeComponent();
        Items.ItemsSource = _messages;

        // Apply the saved filter to the checkboxes FIRST, then subscribe. Wiring the handler only
        // after the initial state is set guarantees it fires solely on real user clicks - never
        // during construction, where it could otherwise overwrite the persisted choice (#760).
        LoadFilterIntoCheckboxes();
        ShowToolCallsCheck.IsCheckedChanged += OnFilterChanged;
        ShowToolResultsCheck.IsCheckedChanged += OnFilterChanged;
        ShowThinkingCheck.IsCheckedChanged += OnFilterChanged;
    }

    /// <summary>
    /// Read the saved filter and set the three "Show:" checkboxes to match. A malformed config.json
    /// makes <see cref="HistoryFilterConfig.Get"/> throw (it never silently coerces a bad value). The
    /// History tab is a non-critical view that must still open, so this lifecycle boundary catches and
    /// applies the defaults - but logs the FULL exception (not just the message) so the bad config is
    /// diagnosable, never silently swallowed (#760 review). Called before the change handlers are
    /// wired, so it never fires <see cref="OnFilterChanged"/>.
    /// </summary>
    private void LoadFilterIntoCheckboxes()
    {
        try
        {
            _filter = HistoryFilterConfig.Get();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HistoryView] LoadFilter FAILED (malformed config.json 'history_filter'?), using defaults: {ex}");
            _filter = HistoryFilterConfig.Default;
        }

        ShowToolCallsCheck.IsChecked = _filter.ShowToolCalls;
        ShowToolResultsCheck.IsChecked = _filter.ShowToolResults;
        ShowThinkingCheck.IsChecked = _filter.ShowThinking;
    }

    /// <summary>A "Show:" checkbox flipped: capture the new filter, persist it, and re-render the
    /// already-parsed conversation from memory (no transcript re-read).</summary>
    private void OnFilterChanged(object? sender, RoutedEventArgs e)
    {
        try
        {
            _filter = new HistoryFilterConfig(
                ShowToolCalls: ShowToolCallsCheck.IsChecked ?? true,
                ShowToolResults: ShowToolResultsCheck.IsChecked ?? true,
                ShowThinking: ShowThinkingCheck.IsChecked ?? true);
            _filter.Save();
            ReapplyFilter();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HistoryView] OnFilterChanged failed: {ex.Message}");
        }
    }

    /// <summary>Re-map the cached conversation with the current filter and reconcile the rendered
    /// list. No-op when there is nothing parsed yet or for the raw-text (Gemini) path, where the
    /// single verbatim block has no structured parts to filter.</summary>
    private void ReapplyFilter()
    {
        var session = _session;
        if (session is null || _lastHistory is null || IsRawTextAgent(session.AgentKind))
            return;

        var atBottom = IsNearBottom();
        var vms = Map(_lastHistory, _linkContext, _filter);
        Reconcile(vms);
        CountText.Text = vms.Count == 0 ? "" : $"{vms.Count} messages";
        UpdateEmptyState(vms.Count);
        if (atBottom)
            ScrollToEndDeferred();
    }

    /// <summary>
    /// Show/hide the empty-state label and pick its wording. When nothing rendered but the parsed
    /// conversation actually has messages and a filter is hiding part of it, say so explicitly
    /// ("No messages match the current filters.") rather than the misleading "No messages yet."
    /// (Codex review, finding 2) - this is the common case for a tool-result-only turn with
    /// "Results" switched off.
    /// </summary>
    private void UpdateEmptyState(int renderedCount)
    {
        if (renderedCount > 0)
        {
            EmptyText.IsVisible = false;
            return;
        }

        EmptyText.IsVisible = true;
        var anyFilterOff = !_filter.ShowToolCalls || !_filter.ShowToolResults || !_filter.ShowThinking;
        var historyHadMessages = _lastHistory is { Messages.Count: > 0 };
        EmptyText.Text = anyFilterOff && historyHadMessages
            ? "No messages match the current filters."
            : "No messages yet.";
    }

    /// <summary>Bind the tab to a session and start polling its history.</summary>
    public void Attach(Session session)
    {
        Detach();
        _session = session;
        _linkContext = new MarkdownRenderContext
        {
            RepoPath = session.RepoPath,
            PathExists = static p => File.Exists(p) || Directory.Exists(p),
            OnViewFile = path => ViewFileRequested?.Invoke(path),
            OnBrowserError = message => BrowserLaunchFailed?.Invoke(message),
        };
        FileLog.Write($"[HistoryView] Attach: session={session.Id} agent={session.AgentKind} transcript={session.ClaudeTranscriptPath ?? "(null)"}");
        // Poll only while the History tab is the visible tab (#744). If it is hidden now, polling
        // starts when MainWindow calls OnShown().
        if (_isShown)
            StartPolling();
    }

    /// <summary>Stop polling and clear the view.</summary>
    public void Detach()
    {
        StopPolling();
        _session = null;
        _linkContext = null;
        _lastSignature = "";
        _lastHistory = null;
        _lastAnalysis = null;
        _analysisSignature = "";
        _messages.Clear();
        CountText.Text = "";
        EmptyText.IsVisible = true;
        EmptyText.Text = "No messages yet.";
        HistoryStatePill.IsVisible = false;
    }

    /// <summary>Start the 2.5s poll timer (idempotent) and refresh once immediately.</summary>
    private void StartPolling()
    {
        if (_timer != null || _session is null)
            return;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Stop the poll timer (the session binding is left intact).</summary>
    private void StopPolling()
    {
        _timer?.Stop();
        _timer = null;
    }

    /// <summary>
    /// Called by MainWindow when the History tab becomes the visible tab (#744). Resume polling (an
    /// immediate refresh runs) and snap to the latest message. Gating on visibility avoids parsing
    /// and rendering off-screen and avoids computing scroll geometry against a zero-size viewport.
    /// </summary>
    public void OnShown()
    {
        _isShown = true;
        StartPolling();
        ScrollToEndDeferred();
    }

    /// <summary>Called by MainWindow when the History tab is hidden (#744): stop polling.</summary>
    public void OnHidden()
    {
        _isShown = false;
        StopPolling();
    }

    private async Task RefreshAsync()
    {
        var session = _session;
        if (session is null)
            return;

        try
        {
            // Gemini is the exception: it has no transcript file, so the file-based path below
            // never applies. Its conversation lives only in the session's terminal buffer, so
            // poll the buffer here and re-render the single cleaned-text block when it grows.
            if (IsRawTextAgent(session.AgentKind))
            {
                await RefreshGeminiAsync(session);
                return;
            }

            var path = SessionHistoryReader.ResolveTranscriptPath(session);
            if (path is null || !File.Exists(path))
            {
                _messages.Clear();
                CountText.Text = "";
                EmptyText.IsVisible = true;
                EmptyText.Text = SessionHistoryReader.IsSupported(session)
                    ? "Waiting for the conversation to start..."
                    : "History is not available for this agent yet.";
                _lastSignature = "";
                // Drop the cached conversation too: otherwise a later filter toggle would re-render
                // this now-gone transcript from memory and resurrect history the refresh just hid.
                _lastHistory = null;
                HistoryStatePill.IsVisible = false;
                return;
            }

            // Derived history-state pill (experimental, Claude only). Updated every tick - even when
            // the transcript itself has not changed - so the liveness guard clears a stuck
            // "Background running" promptly once the process exits.
            await UpdateDerivedHistoryStateAsync(session, path);

            // Only re-parse the conversation when the transcript file actually changed.
            var info = new FileInfo(path);
            var signature = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (signature == _lastSignature)
                return;
            _lastSignature = signature;

            var history = await Task.Run(() => SessionHistoryReader.Read(session));
            _lastHistory = history;
            var vms = Map(history, _linkContext, _filter);
            FileLog.Write($"[HistoryView] refresh: path={path} messages={history.Messages.Count} vms={vms.Count}");

            // Was the user parked at the bottom before this update? (Decide before changing the list.)
            var atBottom = IsNearBottom();

            // Incremental render (#745): keep the common leading bubbles, append only what is new -
            // existing bubbles are not re-created, so a scrolled-up reader keeps their position.
            Reconcile(vms);

            CountText.Text = vms.Count == 0 ? "" : $"{vms.Count} messages";
            UpdateEmptyState(vms.Count);

            // Keep the newest message in view if the user was already at the bottom (#744). The list
            // is bottom-anchored, so a short conversation is always pinned without any scrolling.
            if (atBottom)
                ScrollToEndDeferred();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HistoryView] RefreshAsync failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Compute and render the transcript-derived history state (GitHub #736). This is Claude-only:
    /// the background-agent lifecycle signal lives in the Claude transcript format, so other agents
    /// have no derived label (they fall back to today's live heuristic). The cached analysis is
    /// re-parsed only when the file changes; the cheap <see cref="HistoryStateDeriver.Derive"/> runs
    /// every tick with the current process-liveness so a finished session cannot stay "Background
    /// running". This never reads or writes the live byte-based status.
    /// </summary>
    private async Task UpdateDerivedHistoryStateAsync(Session session, string path)
    {
        if (session.AgentKind != AgentKind.ClaudeCode)
        {
            HistoryStatePill.IsVisible = false;
            return;
        }

        var info = new FileInfo(path);
        var signature = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        if (signature != _analysisSignature)
        {
            _lastAnalysis = await Task.Run(() => HistoryStateDeriver.AnalyzeFile(path));
            _analysisSignature = signature;
        }

        var state = HistoryStateDeriver.Derive(_lastAnalysis ?? HistoryAnalysis.Empty, session.Backend.IsRunning);
        RenderHistoryStatePill(state);
    }

    /// <summary>Paint the derived-state pill. Hidden when Idle to keep the header quiet.</summary>
    private void RenderHistoryStatePill(HistoryState state)
    {
        switch (state)
        {
            case HistoryState.BackgroundRunning:
                HistoryStatePill.Background = BgRunningPill;
                HistoryStateText.Foreground = BgRunningText;
                HistoryStateText.Text = "history: Background running";
                HistoryStatePill.IsVisible = true;
                break;
            case HistoryState.Working:
                HistoryStatePill.Background = WorkingPill;
                HistoryStateText.Foreground = WorkingText;
                HistoryStateText.Text = "history: Working";
                HistoryStatePill.IsVisible = true;
                break;
            case HistoryState.NeedsYou:
                HistoryStatePill.Background = NeedsYouPill;
                HistoryStateText.Foreground = NeedsYouText;
                HistoryStateText.Text = "history: Needs you";
                HistoryStatePill.IsVisible = true;
                break;
            default:
                HistoryStatePill.Background = IdlePill;
                HistoryStateText.Foreground = IdleText;
                HistoryStateText.Text = "history: Idle";
                HistoryStatePill.IsVisible = true;
                break;
        }
    }

    /// <summary>
    /// Gemini-only refresh. Gemini has no transcript file, so the conversation is read from the
    /// session's terminal buffer and rendered as a single, clearly-labeled raw-text block. The
    /// buffer's monotonic byte count is the change signal, so we only re-render when it grows.
    /// </summary>
    private async Task RefreshGeminiAsync(Session session)
    {
        var buffer = session.Buffer;
        if (buffer is null)
        {
            _messages.Clear();
            CountText.Text = "";
            EmptyText.IsVisible = true;
            EmptyText.Text = "Waiting for the conversation to start...";
            _lastSignature = "";
            return;
        }

        var signature = "gemini|" + buffer.TotalBytesWritten;
        if (signature == _lastSignature)
            return;
        _lastSignature = signature;

        var history = await Task.Run(() => SessionHistoryReader.Read(session));
        var fullBody = history.Messages.Count == 0
            ? ""
            : string.Join("\n", history.Messages
                .SelectMany(m => m.Parts)
                .Where(p => p.Kind == ConversationPartKind.Text)
                .Select(p => p.Text));

        // Cap the rendered body to its recent tail. Only the recent scrollback is useful, and the
        // raw Gemini buffer can reach the circular buffer's full size (~2 MB); a string that large in
        // a single non-virtualized, wrapping TextBlock would be re-measured on the UI thread on every
        // buffer-growth tick and jank the History pane (the structured path caps each card the same way).
        var body = fullBody.Length <= GeminiBodyMaxChars
            ? fullBody
            : "... [earlier terminal output trimmed]\n" + fullBody.Substring(fullBody.Length - GeminiBodyMaxChars);
        FileLog.Write($"[HistoryView] gemini refresh: bytes={buffer.TotalBytesWritten} bodyLen={body.Length} (full={fullBody.Length})");

        if (body.Length == 0)
        {
            _messages.Clear();
            CountText.Text = "";
            EmptyText.IsVisible = true;
            EmptyText.Text = "Waiting for the conversation to start...";
            return;
        }

        var atBottom = IsNearBottom();
        Reconcile(new List<HistoryMessageVm>
        {
            new HistoryMessageVm
            {
                Speaker = GeminiTerminalHistory.Label,
                Body = body,
                HeaderBrush = ToolHeader,
                CardBrush = ToolCard,
                IsRawText = IsRawTextAgent(session.AgentKind),
            },
        });
        CountText.Text = "raw terminal text";
        EmptyText.IsVisible = false;

        if (atBottom)
            ScrollToEndDeferred();
    }

    /// <summary>
    /// The single source of truth for "this agent's history is raw terminal scrollback, not a
    /// structured transcript" - currently only Gemini, which persists no usable transcript and is
    /// read straight from the session's terminal buffer. Such an agent is rendered verbatim
    /// (<see cref="HistoryMessageVm.IsRawText"/>), never through the Markdown pipeline. Kept as one
    /// named predicate so the buffer-read branch and the raw-render flag can never disagree (the
    /// kind of split that produced the Cockpit IsRawText bug, GitHub #742).
    /// </summary>
    internal static bool IsRawTextAgent(AgentKind agent) => agent == AgentKind.Gemini;

    /// <summary>
    /// Incrementally reconcile the rendered list with the latest view-models (#745). Keeps the common
    /// leading run untouched (so existing bubbles are not re-created and a scrolled-up reader's
    /// position is preserved), removes any diverging tail, then appends the new messages. A handover
    /// that replaces the conversation (e.g. Claude /clear, which repoints the transcript) shares no
    /// prefix, so it naturally rebuilds from scratch.
    /// </summary>
    private void Reconcile(List<HistoryMessageVm> newVms)
    {
        int prefix = 0;
        int min = Math.Min(_messages.Count, newVms.Count);
        while (prefix < min && SameVm(_messages[prefix], newVms[prefix]))
            prefix++;

        for (int i = _messages.Count - 1; i >= prefix; i--)
            _messages.RemoveAt(i);
        for (int i = prefix; i < newVms.Count; i++)
            _messages.Add(newVms[i]);
    }

    // Two rendered messages are the same bubble when their visible content matches; LinkContext is
    // per-session (identical across bubbles) so it is intentionally not compared.
    private static bool SameVm(HistoryMessageVm a, HistoryMessageVm b)
        => a.Speaker == b.Speaker && a.IsRawText == b.IsRawText && a.Body == b.Body;

    /// <summary>Map a parsed conversation to display bubbles, applying the "Show:" filter. Internal
    /// so the filtering behavior (hiding tool calls / results / thinking, and the all-hidden case)
    /// can be unit-tested directly without a live UI (Codex review, test-gap finding).</summary>
    internal static List<HistoryMessageVm> Map(ConversationHistory history, MarkdownRenderContext? linkContext, HistoryFilterConfig filter)
    {
        var list = new List<HistoryMessageVm>(history.Messages.Count);
        foreach (var message in history.Messages)
        {
            var vm = MapMessage(message, linkContext, filter);
            if (vm is not null)
                list.Add(vm);
        }
        return list;
    }

    private static HistoryMessageVm? MapMessage(ConversationMessage message, MarkdownRenderContext? linkContext, HistoryFilterConfig filter)
    {
        var sb = new StringBuilder();

        if (message.Role == ConversationRole.Assistant)
        {
            foreach (var part in message.Parts)
            {
                switch (part.Kind)
                {
                    case ConversationPartKind.Text:
                        Append(sb, part.Text);
                        break;
                    case ConversationPartKind.Thinking:
                        if (filter.ShowThinking && part.Text.Length > 0)
                            Append(sb, "(thinking) " + part.Text);
                        break;
                    case ConversationPartKind.ToolUse:
                        if (filter.ShowToolCalls)
                            Append(sb, "[tool] " + (part.ToolName ?? "?") + ToolInputSuffix(part.Text));
                        break;
                    case ConversationPartKind.ToolResult:
                        if (filter.ShowToolResults)
                            Append(sb, "[result] " + Truncate(part.Text, 400));
                        break;
                }
            }

            var body = sb.ToString().Trim();
            return body.Length == 0
                ? null
                : new HistoryMessageVm { Speaker = "Assistant", Body = Truncate(body, 4000), HeaderBrush = AssistantHeader, CardBrush = AssistantCard, LinkContext = linkContext };
        }

        // User role: either a real prompt, or tool results being fed back to the assistant.
        var onlyToolResults = message.Parts.Count > 0 && message.Parts.All(p => p.Kind == ConversationPartKind.ToolResult);

        // A pure tool-result bubble is hidden entirely when results are filtered out.
        if (onlyToolResults && !filter.ShowToolResults)
            return null;

        foreach (var part in message.Parts)
        {
            switch (part.Kind)
            {
                case ConversationPartKind.Text:
                    Append(sb, part.Text);
                    break;
                case ConversationPartKind.ToolResult:
                    if (filter.ShowToolResults)
                        Append(sb, Truncate(part.Text, 600));
                    break;
            }
        }

        var userBody = sb.ToString().Trim();
        if (userBody.Length == 0)
            return null;

        return onlyToolResults
            ? new HistoryMessageVm { Speaker = "Tool result", Body = Truncate(userBody, 2000), HeaderBrush = ToolHeader, CardBrush = ToolCard, LinkContext = linkContext }
            : new HistoryMessageVm { Speaker = "You", Body = Truncate(userBody, 4000), HeaderBrush = UserHeader, CardBrush = UserCard, LinkContext = linkContext };
    }

    private static void Append(StringBuilder sb, string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        if (sb.Length > 0)
            sb.Append('\n');
        sb.Append(text);
    }

    private static string ToolInputSuffix(string inputJson)
    {
        var trimmed = inputJson.Trim();
        if (trimmed.Length == 0 || trimmed == "{}")
            return "";
        return "  " + Truncate(trimmed, 160);
    }

    private static string Truncate(string text, int max)
        => text.Length <= max ? text : text.Substring(0, max) + " ...";

    /// <summary>True when the user is parked at (or within a line of) the bottom of the thread, or
    /// the content is short enough that there is nothing to scroll.</summary>
    private bool IsNearBottom()
    {
        var max = Scroller.Extent.Height - Scroller.Viewport.Height;
        return max <= 0 || Scroller.Offset.Y >= max - 60;
    }

    /// <summary>Scroll to the newest message after the pending layout pass. With the bottom-anchored
    /// layout a short conversation is already pinned to the bottom; this handles the case where the
    /// thread is taller than the viewport and a new message has been appended.</summary>
    private void ScrollToEndDeferred()
        => Dispatcher.UIThread.Post(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);
}
