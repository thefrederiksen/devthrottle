using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Avalonia.DevReports;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Microsoft.Web.WebView2.Core;

namespace CcDirector.Avalonia.Controls;

/// <summary>
/// The Director's reports pane (issue #3019): one session's dev reports, read and answered in a document tab
/// beside that session.
///
/// THERE IS ONE IMPLEMENTATION OF THE LIST, THE VIEWER, THE FRAME HOST AND THE NOTES, AND IT IS THE WEB ONE
/// (<c>packages/client-core</c>). This control hosts it; it reimplements none of it in C#. What it adds is
/// the three things only a host can do:
///
///  1. Ask the Gateway where the page is and navigate to exactly that address - it never composes one.
///  2. Hand the page the Gateway key this Director already holds, in memory, over the WebView2 message
///     bridge, and nowhere else.
///  3. Refuse everything else: only the top-level document at the pane address is answered, only the one
///     message kind earns an answer, and a top-level navigation anywhere else is cancelled.
///
/// The report itself lives in the page's sandboxed frame (CONTRACT.md section 4) and has no path to this
/// bridge: <c>WebMessageReceived</c> fires for the TOP-LEVEL document only, this control never subscribes to
/// <c>FrameCreated</c>, and it never adds a host object to script.
///
/// Every decision above is a pure function in <see cref="DevReportPaneBridge"/>, so it is proven without a
/// user interface and without a web view. This file is the wiring.
/// </summary>
public partial class DevReportsPaneControl : UserControl, IFileViewer
{
    /// <summary>
    /// The synthetic document-tab key for a session's reports. It is not a file and never opens one - it
    /// exists so the tab machinery, which keys tabs on a path, can tell two sessions' panes apart and re-use
    /// one that is already open.
    /// </summary>
    public static string TabKeyFor(Guid sessionId) => $"dev-reports:{sessionId:D}";

    private readonly Guid _sessionId;
    private readonly string _sessionName;

    /// <summary>The address the Gateway handed back; null until it has. Nothing is answered without it.</summary>
    private string? _paneUrl;

    /// <summary>
    /// Whether the document now in the web view is the page we were handed. Set when a document STARTS
    /// loading, so it is settled before any of that document's script runs - a check made after the page
    /// had loaded would race the page's own first message. A document at any other address closes the
    /// bridge, and only loading the pane address again opens it.
    /// </summary>
    private bool _bridgeOpen;

    private bool _handlersWired;

    public DevReportsPaneControl(Guid sessionId, string sessionName)
    {
        InitializeComponent();

        _sessionId = sessionId;
        _sessionName = string.IsNullOrWhiteSpace(sessionName) ? sessionId.ToString("D") : sessionName.Trim();

        SessionText.Text = GetDisplayName();
        ToolTip.SetTip(SessionText, $"Dev reports published by session {sessionId:D}");
        StatusText.IsVisible = true;

        WebViewHost.CoreReady += OnCoreReady;
        WebViewHost.NavigationCompleted += OnNavigationCompleted;

        FileLog.Write($"[DevReportsPane] created: session={_sessionId:D}");
    }

    // ---------------------------------------------------------------- IFileViewer

    public string? FilePath => TabKeyFor(_sessionId);

    public bool IsDirty => false;

    public event Action? DisplayNameChanged;

    public string GetDisplayName() => $"Reports - {_sessionName}";

    public Task SaveAsync() => Task.CompletedTask;

    /// <summary>
    /// Load the pane. The argument is the synthetic tab key, not a file, and is ignored - this control knows
    /// its own session. The Gateway call runs off the user interface thread; the pane is already showing
    /// "Loading..." before it starts.
    /// </summary>
    public Task LoadFileAsync(string filePath)
    {
        FileLog.Write($"[DevReportsPane] LoadFileAsync: session={_sessionId:D}");
        _ = LoadPaneAsync();
        return Task.CompletedTask;
    }

    public void ShowLoadError(string message)
    {
        StatusText.Text = message;
        StatusText.IsVisible = true;
    }

    // ---------------------------------------------------------------- loading

    /// <summary>
    /// Ask the Gateway for the page address, then navigate to exactly that address. It is started and not
    /// awaited, so it is an entry point of its own and carries the try/catch.
    /// </summary>
    private async Task LoadPaneAsync()
    {
        try
        {
            var config = GatewayConfig.Load();
            var gatewayBase = CockpitUrlResolver.ResolveCockpitBase(config);
            FileLog.Write($"[DevReportsPane] LoadPaneAsync: asking {gatewayBase} where session {_sessionId:D} reports are");

            // Off the user interface thread, always: the whole call, not only the wait.
            var url = await Task.Run(async () =>
            {
                using var http = DevReportPaneUrlClient.NewClient();
                return await DevReportPaneUrlClient.FetchAsync(http, gatewayBase, config.Token, _sessionId.ToString("D"));
            }).ConfigureAwait(true);

            _paneUrl = url;
            FileLog.Write($"[DevReportsPane] LoadPaneAsync: navigating to {url}");
            WebViewHost.Navigate(url);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] LoadPaneAsync FAILED: {ex.Message}");
            Dispatcher.UIThread.Post(() => ShowLoadError(
                $"The reports for this session could not be opened: {ex.Message}"));
        }
    }

    private void OnNavigationCompleted(bool isSuccess)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (isSuccess) StatusText.IsVisible = false;
            else ShowLoadError("The reports page did not load. Use Reload to try again.");
        });
    }

    private void ReloadButton_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            FileLog.Write($"[DevReportsPane] ReloadButton_Click: session={_sessionId:D}");
            StatusText.Text = "Loading...";
            StatusText.IsVisible = true;
            _ = LoadPaneAsync();
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] ReloadButton_Click FAILED: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- the bridge

    /// <summary>
    /// Wire the bridge as soon as the web view exists. Deliberately NOT wired: <c>FrameCreated</c>, and any
    /// host object on script. The report's frame must have no path to this bridge at all.
    /// </summary>
    private void OnCoreReady()
    {
        try
        {
            if (_handlersWired) return;
            if (WebViewHost.CoreWebView2 is not { } core) return;
            _handlersWired = true;

            core.NavigationStarting += OnNavigationStarting;
            core.ContentLoading += OnContentLoading;
            core.WebMessageReceived += OnWebMessageReceived;

            FileLog.Write($"[DevReportsPane] OnCoreReady: bridge wired for session={_sessionId:D}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] OnCoreReady FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancel any top-level navigation that is not the page we were handed. A report can carry a plain link
    /// or a meta refresh; the pane is not a browser, and it holds the owner's Gateway key.
    /// </summary>
    private void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        try
        {
            if (_paneUrl is not { } pane) return;
            if (DevReportPaneBridge.NavigationIsAllowed(e.Uri, pane)) return;

            e.Cancel = true;
            FileLog.Write($"[DevReportsPane] navigation CANCELLED (not the pane address): {e.Uri}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] OnNavigationStarting FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Open or close the bridge for the document that is now loading, BEFORE any of its script runs. A
    /// document at any address but the pane's gets nothing, and only the pane address opens it again.
    /// </summary>
    private void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
    {
        try
        {
            var source = WebViewHost.CoreWebView2?.Source;
            var open = _paneUrl is { } pane && DevReportPaneBridge.SameAddress(source, pane);
            if (open != _bridgeOpen)
                FileLog.Write($"[DevReportsPane] bridge {(open ? "OPEN" : "CLOSED")} for the document now loading");
            _bridgeOpen = open;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] OnContentLoading FAILED: {ex.Message}");
        }
    }

    /// <summary>
    /// Answer the page's one question. Fires for the TOP-LEVEL document only - a frame's message never
    /// reaches here - and is checked again against the pane address before anything is handed over.
    /// </summary>
    private void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            if (_paneUrl is not { } pane)
            {
                FileLog.Write("[DevReportsPane] web message ignored: no pane address has been resolved yet");
                return;
            }

            if (!_bridgeOpen)
            {
                FileLog.Write("[DevReportsPane] web message ignored: the bridge is closed for the document in the pane");
                return;
            }

            if (!DevReportPaneBridge.EarnsTheKey(e.Source, pane, e.WebMessageAsJson, out var refusal))
            {
                FileLog.Write($"[DevReportsPane] web message ignored: {refusal}");
                return;
            }

            var token = GatewayConfig.Load().Token;
            if (string.IsNullOrWhiteSpace(token))
            {
                FileLog.Write("[DevReportsPane] the page asked for a Gateway key and this Director holds none");
                Dispatcher.UIThread.Post(() => ShowLoadError(
                    "This Director holds no Gateway key, so the reports page has nothing to read with. " +
                    "Connect it to a Gateway and open the reports again."));
                return;
            }

            // The key is composed into the one message and posted to the top-level document. It reaches no
            // log line, no file and no address - this method writes that a key was handed over, never the key.
            WebViewHost.CoreWebView2!.PostWebMessageAsJson(
                DevReportPaneBridge.ComposeKeyMessage(token, _sessionId.ToString("D")));
            FileLog.Write($"[DevReportsPane] handed the Gateway key to the reports page for session={_sessionId:D}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[DevReportsPane] OnWebMessageReceived FAILED: {ex.Message}");
        }
    }
}
