using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using CcDirector.Core.Backends;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

/// <summary>
/// A throwaway "Launch preview" popup (issue #436). It starts the configured agent with the
/// exact resolved command line in a disposable ConPTY-backed terminal so the user can watch the
/// agent boot (e.g. Claude printing its permission-bypass banner) before committing to it.
///
/// This session is deliberately NOT a managed session: it never touches the SessionManager, is
/// never added to the roster, and is never persisted. Closing the dialog (Close or Stop and
/// close) gracefully tears the process down and disposes the backend, so no orphan process and
/// no roster entry survive.
/// </summary>
public partial class LaunchPreviewDialog : Window
{
    private readonly string _executable;
    private readonly string _arguments;
    private readonly string _workingDir;
    private readonly string _displayName;

    private ConPtyBackend? _backend;
    private bool _torndown;

    public LaunchPreviewDialog() : this("claude", "", "", "Agent") { }

    /// <param name="executable">Resolved tool executable path (the card's path box value).</param>
    /// <param name="arguments">The exact resolved command-line arguments to launch with.</param>
    /// <param name="workingDir">Working directory for the throwaway process.</param>
    /// <param name="displayName">Friendly tool name for status text.</param>
    public LaunchPreviewDialog(string executable, string arguments, string workingDir, string displayName)
    {
        FileLog.Write($"[LaunchPreviewDialog] Constructor: tool={displayName}, exe={executable}, args='{arguments}'");
        _executable = executable;
        _arguments = arguments;
        _workingDir = workingDir;
        _displayName = displayName;
        InitializeComponent();

        CommandBox.Text = string.IsNullOrEmpty(arguments) ? executable : $"{executable} {arguments}";

        Loaded += (_, _) => StartPreview();
        Closing += async (_, _) => await TearDownAsync();
    }

    /// <summary>
    /// Start the throwaway process and bridge its ConPTY backend to the on-screen terminal:
    /// output bytes feed the TerminalView, keystrokes flow back to the process, and grid resizes
    /// are forwarded to the PTY. Runs on the UI thread from the Loaded lifecycle event.
    /// </summary>
    private void StartPreview()
    {
        FileLog.Write($"[LaunchPreviewDialog] StartPreview: tool={_displayName}");
        try
        {
            var backend = new ConPtyBackend();
            _backend = backend;

            var buffer = backend.Buffer
                ?? throw new InvalidOperationException("ConPtyBackend has no terminal buffer.");

            // Process output -> terminal. The buffer fires on a background drain thread, so the
            // Feed must be marshalled to the UI thread before touching the TerminalView.
            buffer.OnBytesWritten += data =>
                Dispatcher.UIThread.Post(() => Terminal.Feed(data));

            // Keystrokes from the terminal -> process input.
            Terminal.InputReceived += bytes => backend.Write(bytes);

            // Terminal grid resize -> PTY resize so the agent reflows to the visible size.
            Terminal.TerminalSizeChanged += (cols, rows) =>
                backend.Resize((short)cols, (short)rows);

            backend.ProcessExited += code =>
                Dispatcher.UIThread.Post(() => ShowStatus($"{_displayName} exited (code {code})."));

            var workingDir = string.IsNullOrWhiteSpace(_workingDir) ? Environment.CurrentDirectory : _workingDir;
            backend.Start(_executable, _arguments, workingDir, (short)Terminal.Cols, (short)Terminal.Rows);

            ShowStatus($"{_displayName} running (throwaway - not saved).");
            Terminal.Focus();
            FileLog.Write($"[LaunchPreviewDialog] StartPreview: started pid={backend.ProcessId}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LaunchPreviewDialog] StartPreview FAILED: {ex.Message}");
            ShowStatus($"Could not start {_displayName}: {ex.Message}");
        }
    }

    private void BtnStopClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[LaunchPreviewDialog] BtnStopClose_Click");
        Close();
    }

    private void BtnClose_Click(object? sender, RoutedEventArgs e)
    {
        FileLog.Write("[LaunchPreviewDialog] BtnClose_Click");
        Close();
    }

    /// <summary>
    /// Gracefully shut down the throwaway process and dispose the backend so nothing leaks. Safe
    /// to call more than once (the Closing event plus an explicit Close both route here).
    /// </summary>
    private async Task TearDownAsync()
    {
        if (_torndown)
            return;
        _torndown = true;

        FileLog.Write("[LaunchPreviewDialog] TearDownAsync");
        var backend = _backend;
        if (backend is null)
            return;

        var pid = backend.ProcessId;
        await backend.GracefulShutdownAsync();
        backend.Dispose();
        _backend = null;
        FileLog.Write($"[LaunchPreviewDialog] TearDownAsync: throwaway process pid={pid} stopped and disposed");
    }

    private void ShowStatus(string text)
    {
        StatusText.Text = text;
    }
}
