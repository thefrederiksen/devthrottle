using System.Collections.ObjectModel;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// Phase 5 desktop tab: ask the SessionStatusWingman questions about the
/// currently-bound session. Each ask is one fresh, stateless Haiku call.
/// Conversation history shown here is for the user's benefit only; the wingman
/// itself never has memory between calls.
/// </summary>
public partial class WingmanView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(50) };
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    private static readonly Dictionary<string, IBrush> StatusBrushes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["green"]   = new SolidColorBrush(Color.FromRgb(0x22, 0xC5, 0x5E)),
        ["blue"]    = new SolidColorBrush(Color.FromRgb(0x3B, 0x82, 0xF6)),
        ["yellow"]  = new SolidColorBrush(Color.FromRgb(0xEA, 0xB3, 0x08)),
        ["red"]     = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44)),
        ["unknown"] = new SolidColorBrush(Color.FromRgb(0x6A, 0x6A, 0x6A)),
    };

    private Session? _session;
    private string? _directorBaseUrl;
    private readonly ObservableCollection<AskEntry> _history = new();

    public WingmanView()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _history;
    }

    /// <summary>
    /// Wire to a session + the local Director base URL. The base URL is
    /// <c>http://127.0.0.1:&lt;port&gt;</c> where port is the live Director's Control API
    /// port. Caller (MainWindow) supplies it.
    /// </summary>
    public void Bind(Session? session, string? directorBaseUrl)
    {
        UnbindCurrent();
        _session = session;
        _directorBaseUrl = directorBaseUrl?.TrimEnd('/');
        _history.Clear();
        UpdateEmptyHint();

        if (session is not null)
        {
            session.OnStatusColorChanged += OnStatusColorChanged;
            RefreshBanner();
            QuestionBox.IsEnabled = true;
            AskButton.IsEnabled = true;
        }
        else
        {
            SupReason.Text = "no session selected";
            SupSubtitle.Text = "open a session in the sidebar to use the wingman";
            SupDot.Fill = StatusBrushes["unknown"];
            QuestionBox.IsEnabled = false;
            AskButton.IsEnabled = false;
        }
    }

    private void UpdateEmptyHint()
    {
        EmptyHint.IsVisible = _history.Count == 0;
    }

    private void UnbindCurrent()
    {
        if (_session is not null)
            _session.OnStatusColorChanged -= OnStatusColorChanged;
    }

    private void OnStatusColorChanged(string oldColor, string newColor, string reason)
    {
        Dispatcher.UIThread.Post(RefreshBanner);
    }

    private void RefreshBanner()
    {
        if (_session is null) return;
        SupDot.Fill = StatusBrushes.TryGetValue(_session.StatusColor ?? "", out var b) ? b : StatusBrushes["unknown"];
        SupReason.Text = string.IsNullOrEmpty(_session.LastStatusReason) ? "(no reason set)" : _session.LastStatusReason;
        SupSubtitle.Text = $"session {_session.Id.ToString().Substring(0, 8)} - {_session.RepoPath}";
    }

    private void RefreshButton_Click(object? sender, RoutedEventArgs e) => RefreshBanner();

    private void QuestionBox_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !(e.KeyModifiers.HasFlag(KeyModifiers.Shift)))
        {
            e.Handled = true;
            _ = AskAsync();
        }
    }

    private void AskButton_Click(object? sender, RoutedEventArgs e) => _ = AskAsync();

    private async Task AskAsync()
    {
        var question = QuestionBox.Text?.Trim() ?? "";
        if (string.IsNullOrEmpty(question)) return;
        if (_session is null || string.IsNullOrEmpty(_directorBaseUrl))
        {
            AppendEntry(question, "(wingman is not connected; no session/Director URL)", "");
            return;
        }

        QuestionBox.Text = "";
        AskButton.IsEnabled = false;
        var pending = new AskEntry
        {
            Question = question,
            Answer = "thinking...",
            Footer = "",
            AskedAt = DateTime.UtcNow,
        };
        _history.Add(pending);
        UpdateEmptyHint();
        ScrollToBottom();

        try
        {
            var url = $"{_directorBaseUrl}/sessions/{_session.Id}/wingman/ask";
            var body = new WingmanAskRequest { Question = question };
            using var resp = await Http.PostAsJsonAsync(url, body, JsonOpts);
            if (!resp.IsSuccessStatusCode)
            {
                pending.Answer = $"wingman HTTP {(int)resp.StatusCode}";
                pending.Footer = await resp.Content.ReadAsStringAsync();
            }
            else
            {
                var result = await resp.Content.ReadFromJsonAsync<WingmanAskResult>(JsonOpts);
                if (result is null)
                {
                    pending.Answer = "(empty response)";
                }
                else
                {
                    pending.Answer = string.IsNullOrEmpty(result.Answer) ? "(no answer)" : result.Answer;
                    pending.Footer = $"{result.Model}  -  {result.LatencyMs} ms  -  {result.ContextDigest}";
                    ContextDigestText.Text = string.IsNullOrEmpty(result.ContextDigest) ? "(no answers yet)" : result.ContextDigest;
                }
            }
        }
        catch (Exception ex)
        {
            pending.Answer = "ask failed: " + ex.Message;
            FileLog.Write($"[WingmanView] ask FAILED: {ex.Message}");
        }
        finally
        {
            // Force the items control to redraw the changed pending entry.
            var idx = _history.IndexOf(pending);
            if (idx >= 0)
            {
                _history.RemoveAt(idx);
                _history.Insert(idx, pending);
            }
            AskButton.IsEnabled = true;
            ScrollToBottom();
        }
    }

    private void AppendEntry(string question, string answer, string footer)
    {
        _history.Add(new AskEntry { Question = question, Answer = answer, Footer = footer });
        ScrollToBottom();
    }

    private void ScrollToBottom()
    {
        // Post twice: first to let the ItemsControl realise the new item; second to
        // perform the actual scroll after layout has measured. Single-Post races the
        // measure pass on the first add and ends up scrolling to where the list WAS,
        // not where it IS.
        Dispatcher.UIThread.Post(() =>
        {
            Dispatcher.UIThread.Post(() => HistoryScroller.ScrollToEnd());
        });
    }

    public sealed class AskEntry
    {
        public string Question { get; set; } = "";
        public string Answer { get; set; } = "";
        public string Footer { get; set; } = "";
        public DateTime AskedAt { get; set; } = DateTime.UtcNow;

        /// <summary>Bound to the per-entry "5s ago" / "2m ago" timestamp label.</summary>
        public string TimestampDisplay
        {
            get
            {
                var seconds = (DateTime.UtcNow - AskedAt).TotalSeconds;
                if (seconds < 5) return "just now";
                if (seconds < 60) return $"{(int)seconds}s ago";
                var minutes = seconds / 60;
                if (minutes < 60) return $"{(int)minutes}m ago";
                var hours = minutes / 60;
                if (hours < 24) return $"{(int)hours}h ago";
                return AskedAt.ToLocalTime().ToString("HH:mm");
            }
        }
    }
}
