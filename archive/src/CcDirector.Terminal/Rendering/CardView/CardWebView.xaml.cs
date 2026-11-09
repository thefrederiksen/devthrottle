using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;
using Microsoft.Web.WebView2.Core;

namespace CcDirector.Terminal.Rendering.CardView;

/// <summary>
/// WebView2-based terminal renderer that displays ANSI output as styled HTML cards.
/// Maintains its own AnsiParser + cell grid (same as TerminalControl) and converts
/// the grid to HTML on each poll tick.
/// </summary>
public partial class CardWebView : UserControl
{

    /// <summary>What every byte from this control says about itself (source logging, 2026-09-05): the desktop
    /// terminal door, the person at the keyboard.</summary>
    private static readonly CcDirector.Core.Sessions.SubmissionProvenance DesktopTerminalDoor =
        CcDirector.Core.Sessions.SubmissionProvenance.Typed(CcDirector.Core.Sessions.SubmissionRoutes.DesktopTerminal, CcDirector.Core.Sessions.SubmissionIdentityKinds.LocalUser);
    private Session? _session;
    private long _bufferPosition;
    private DispatcherTimer? _pollTimer;
    private bool _webViewReady;
    private string _lastHtml = "";

    // Own parser state (independent from TerminalControl)
    private TerminalCell[,] _cells;
    private int _cols;
    private int _rows;
    private readonly List<TerminalCell[]> _scrollback = new();
    private AnsiParser? _parser;
    private const int ScrollbackLines = 5000;
    private const int DefaultCols = 120;
    private const int DefaultRows = 40;
    private const int PollIntervalMs = 50;

    private static string? _cachedTemplate;
    private static string? _cachedCss;

    public CardWebView()
    {
        _cols = DefaultCols;
        _rows = DefaultRows;
        _cells = new TerminalCell[_cols, _rows];

        InitializeComponent();
        Loaded += CardWebView_Loaded;
    }

    private async void CardWebView_Loaded(object sender, RoutedEventArgs e)
    {
        FileLog.Write("[CardWebView] Loaded, initializing WebView2");
        try
        {
            var userDataFolder = CcDirector.Core.Storage.CcStorage.WebView2Card();

            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await WebView.EnsureCoreWebView2Async(env);

            WebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            WebView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            WebView.CoreWebView2.Settings.IsZoomControlEnabled = false;

            WebView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;

            // Build initial HTML document
            string template = LoadEmbeddedResource("CcDirector.Terminal.Rendering.CardView.card-template.html");
            string css = LoadEmbeddedResource("CcDirector.Terminal.Rendering.CardView.card-style.css");

            // Convert current grid state to HTML (may already have content from Attach)
            string bodyHtml = AnsiToHtmlConverter.ConvertToHtml(_scrollback, _cells, _cols, _rows);
            string fullDoc = AnsiToHtmlConverter.BuildDocument(template, css, bodyHtml);
            _lastHtml = bodyHtml;

            // Wait for navigation to complete
            var navTcs = new TaskCompletionSource<bool>();
            void OnNavCompleted(object? s, CoreWebView2NavigationCompletedEventArgs args)
            {
                WebView.CoreWebView2.NavigationCompleted -= OnNavCompleted;
                navTcs.TrySetResult(args.IsSuccess);
            }
            WebView.CoreWebView2.NavigationCompleted += OnNavCompleted;
            WebView.CoreWebView2.NavigateToString(fullDoc);

            bool success = await navTcs.Task;
            if (!success)
            {
                FileLog.Write("[CardWebView] NavigateToString FAILED");
                LoadingText.Text = "Failed to load card view";
                return;
            }

            _webViewReady = true;
            LoadingText.Visibility = Visibility.Collapsed;
            FileLog.Write("[CardWebView] WebView2 initialized and page loaded");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] WebView2 initialization FAILED: {ex.Message}");
            LoadingText.Text = $"WebView2 error: {ex.Message}";
        }
    }

    /// <summary>
    /// Attach to a session and start polling its buffer for ANSI output.
    /// </summary>
    public void Attach(Session session)
    {
        FileLog.Write($"[CardWebView] Attach: sessionId={session.Id}");

        _session = session;
        _bufferPosition = 0;
        _scrollback.Clear();
        _cells = new TerminalCell[_cols, _rows];
        _parser = new AnsiParser(_cells, _cols, _rows, _scrollback, ScrollbackLines);

        // Parse existing buffer content
        if (session.Buffer != null)
        {
            var (initial, pos) = session.Buffer.GetWrittenSince(0);
            _bufferPosition = pos;
            if (initial.Length > 0)
                _parser.Parse(initial);
        }

        _pollTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(PollIntervalMs)
        };
        _pollTimer.Tick += PollTimer_Tick;
        _pollTimer.Start();

        FileLog.Write("[CardWebView] Attach complete, polling started");
    }

    /// <summary>
    /// Stop polling and detach from the session.
    /// </summary>
    public void Detach()
    {
        FileLog.Write($"[CardWebView] Detach: sessionId={_session?.Id}");
        _pollTimer?.Stop();
        _pollTimer = null;
        _session = null;
        _parser = null;
    }

    private void PollTimer_Tick(object? sender, EventArgs e)
    {
        try
        {
            if (_session?.Buffer == null || !_webViewReady || _parser == null) return;

            var (data, newPos) = _session.Buffer.GetWrittenSince(_bufferPosition);
            if (data.Length > 0)
            {
                _bufferPosition = newPos;
                _parser.Parse(data);

                // Convert grid to HTML and push update
                string html = AnsiToHtmlConverter.ConvertToHtml(_scrollback, _cells, _cols, _rows);
                if (html != _lastHtml)
                {
                    _lastHtml = html;
                    _ = PushFullHtml(html);
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] PollTimer_Tick FAILED: {ex.Message}");
        }
    }

    private async Task PushFullHtml(string html)
    {
        try
        {
            // Escape for JavaScript string literal
            string escaped = html
                .Replace("\\", "\\\\")
                .Replace("'", "\\'")
                .Replace("\n", "\\n")
                .Replace("\r", "\\r");

            await WebView.CoreWebView2.ExecuteScriptAsync($"replaceAll('{escaped}')");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] PushFullHtml FAILED: {ex.Message}");
        }
    }

    private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        // Allow initial about:blank and data: navigation
        if (e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
            e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            return;

        // Block external navigation, open in default browser instead
        e.Cancel = true;
        FileLog.Write($"[CardWebView] Link clicked: {e.Uri}");

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = e.Uri,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] Open link FAILED: {ex.Message}");
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        try
        {
            if (_session == null) return;

            bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

            if (ctrl && e.Key == Key.C) { _session.SendInput(new byte[] { 0x03 }, null, DesktopTerminalDoor); e.Handled = true; return; }
            if (ctrl && e.Key == Key.D) { _session.SendInput(new byte[] { 0x04 }, null, DesktopTerminalDoor); e.Handled = true; return; }
            if (ctrl && e.Key == Key.Z) { _session.SendInput(new byte[] { 0x1A }, null, DesktopTerminalDoor); e.Handled = true; return; }
            if (ctrl && e.Key == Key.L) { _session.SendInput(new byte[] { 0x0C }, null, DesktopTerminalDoor); e.Handled = true; return; }
            if (shift && e.Key == Key.Tab) { _session.SendInput("\x1b[Z"u8.ToArray(), null, DesktopTerminalDoor); e.Handled = true; return; }

            byte[]? data = e.Key switch
            {
                Key.Enter => "\r"u8.ToArray(),
                Key.Back => new byte[] { 0x7F },
                Key.Tab => "\t"u8.ToArray(),
                Key.Escape => new byte[] { 0x1B },
                Key.Up => "\x1b[A"u8.ToArray(),
                Key.Down => "\x1b[B"u8.ToArray(),
                Key.Right => "\x1b[C"u8.ToArray(),
                Key.Left => "\x1b[D"u8.ToArray(),
                Key.Home => "\x1b[H"u8.ToArray(),
                Key.End => "\x1b[F"u8.ToArray(),
                Key.Delete => "\x1b[3~"u8.ToArray(),
                Key.PageUp => "\x1b[5~"u8.ToArray(),
                Key.PageDown => "\x1b[6~"u8.ToArray(),
                _ => null
            };

            if (data != null)
            {
                _session.SendInput(data, null, DesktopTerminalDoor);
                e.Handled = true;
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] OnPreviewKeyDown FAILED: {ex.Message}");
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        try
        {
            if (_session == null || string.IsNullOrEmpty(e.Text)) return;
            var bytes = Encoding.UTF8.GetBytes(e.Text);
            _session.SendInput(bytes, null, DesktopTerminalDoor);
            e.Handled = true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[CardWebView] OnTextInput FAILED: {ex.Message}");
        }
    }

    private static string LoadEmbeddedResource(string resourceName)
    {
        if (resourceName.EndsWith(".html", StringComparison.OrdinalIgnoreCase) && _cachedTemplate != null)
            return _cachedTemplate;
        if (resourceName.EndsWith(".css", StringComparison.OrdinalIgnoreCase) && _cachedCss != null)
            return _cachedCss;

        var assembly = typeof(AnsiToHtmlConverter).Assembly;
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null)
            throw new InvalidOperationException($"Embedded resource not found: {resourceName}");

        using var reader = new StreamReader(stream);
        string content = reader.ReadToEnd();

        if (resourceName.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            _cachedTemplate = content;
        else if (resourceName.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
            _cachedCss = content;

        return content;
    }
}
