using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Agents;
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

    private readonly ObservableCollection<HistoryMessageVm> _messages = new();
    private DispatcherTimer? _timer;
    private Session? _session;
    private string _lastSignature = "";

    public HistoryView()
    {
        InitializeComponent();
        Items.ItemsSource = _messages;
    }

    /// <summary>Bind the tab to a session and start polling its history.</summary>
    public void Attach(Session session)
    {
        Detach();
        _session = session;
        FileLog.Write($"[HistoryView] Attach: session={session.Id} agent={session.AgentKind} transcript={session.ClaudeTranscriptPath ?? "(null)"}");
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _timer.Tick += async (_, _) => await RefreshAsync();
        _timer.Start();
        _ = RefreshAsync();
    }

    /// <summary>Stop polling and clear the view.</summary>
    public void Detach()
    {
        _timer?.Stop();
        _timer = null;
        _session = null;
        _lastSignature = "";
        _messages.Clear();
        CountText.Text = "";
        EmptyText.IsVisible = true;
        EmptyText.Text = "No messages yet.";
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
            if (session.AgentKind == AgentKind.Gemini)
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
                return;
            }

            // Only re-parse when the transcript file actually changed.
            var info = new FileInfo(path);
            var signature = info.Length + "|" + info.LastWriteTimeUtc.Ticks;
            if (signature == _lastSignature)
                return;
            _lastSignature = signature;

            var history = await Task.Run(() => SessionHistoryReader.Read(session));
            var vms = Map(history);
            FileLog.Write($"[HistoryView] refresh: path={path} messages={history.Messages.Count} vms={vms.Count}");

            var atBottom = IsNearBottom();
            _messages.Clear();
            foreach (var vm in vms)
                _messages.Add(vm);

            CountText.Text = vms.Count == 0 ? "" : $"{vms.Count} messages";
            EmptyText.IsVisible = vms.Count == 0;
            if (vms.Count == 0)
                EmptyText.Text = "No messages yet.";

            if (atBottom)
                ScrollToEndDeferred();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HistoryView] RefreshAsync failed: {ex.Message}");
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

        var atBottom = IsNearBottom();
        _messages.Clear();
        if (body.Length == 0)
        {
            CountText.Text = "";
            EmptyText.IsVisible = true;
            EmptyText.Text = "Waiting for the conversation to start...";
            return;
        }

        _messages.Add(new HistoryMessageVm
        {
            Speaker = GeminiTerminalHistory.Label,
            Body = body,
            HeaderBrush = ToolHeader,
            CardBrush = ToolCard,
        });
        CountText.Text = "raw terminal text";
        EmptyText.IsVisible = false;

        if (atBottom)
            ScrollToEndDeferred();
    }

    private static List<HistoryMessageVm> Map(ConversationHistory history)
    {
        var list = new List<HistoryMessageVm>(history.Messages.Count);
        foreach (var message in history.Messages)
        {
            var vm = MapMessage(message);
            if (vm is not null)
                list.Add(vm);
        }
        return list;
    }

    private static HistoryMessageVm? MapMessage(ConversationMessage message)
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
                        if (part.Text.Length > 0)
                            Append(sb, "(thinking) " + part.Text);
                        break;
                    case ConversationPartKind.ToolUse:
                        Append(sb, "[tool] " + (part.ToolName ?? "?") + ToolInputSuffix(part.Text));
                        break;
                    case ConversationPartKind.ToolResult:
                        Append(sb, "[result] " + Truncate(part.Text, 400));
                        break;
                }
            }

            var body = sb.ToString().Trim();
            return body.Length == 0
                ? null
                : new HistoryMessageVm { Speaker = "Assistant", Body = Truncate(body, 4000), HeaderBrush = AssistantHeader, CardBrush = AssistantCard };
        }

        // User role: either a real prompt, or tool results being fed back to the assistant.
        var onlyToolResults = message.Parts.Count > 0 && message.Parts.All(p => p.Kind == ConversationPartKind.ToolResult);
        foreach (var part in message.Parts)
        {
            switch (part.Kind)
            {
                case ConversationPartKind.Text:
                    Append(sb, part.Text);
                    break;
                case ConversationPartKind.ToolResult:
                    Append(sb, Truncate(part.Text, 600));
                    break;
            }
        }

        var userBody = sb.ToString().Trim();
        if (userBody.Length == 0)
            return null;

        return onlyToolResults
            ? new HistoryMessageVm { Speaker = "Tool result", Body = Truncate(userBody, 2000), HeaderBrush = ToolHeader, CardBrush = ToolCard }
            : new HistoryMessageVm { Speaker = "You", Body = Truncate(userBody, 4000), HeaderBrush = UserHeader, CardBrush = UserCard };
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

    private bool IsNearBottom()
    {
        var max = Scroller.Extent.Height - Scroller.Viewport.Height;
        return max <= 0 || Scroller.Offset.Y >= max - 60;
    }

    private void ScrollToEndDeferred()
    {
        Dispatcher.UIThread.Post(
            () => Scroller.Offset = new Vector(0, Scroller.Extent.Height),
            DispatcherPriority.Background);
    }
}
