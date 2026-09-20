using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Styling;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// The engine as the coordinator sees it, stepped by hand. Every call is written down with what it was
/// handed, and nothing answers until the test says so: the check and the ignore-all each wait on a
/// source the test completes, so a test can look at the screen WHILE the engine has not answered.
/// </summary>
internal sealed class FakeSmartShutdown : ISmartShutdown
{
    public static readonly SmartShutdownAvailability May = new(true, null, true, null);

    public List<SmartShutdownPurpose> Checks { get; } = new();

    public TaskCompletionSource<SmartShutdownAvailability> CheckAnswer { get; } = new();

    public List<SmartShutdownRequest> Starts { get; } = new();

    /// <summary>When set, Start throws it instead of handing back the run.</summary>
    public Exception? StartThrows { get; set; }

    public FakeSmartShutdownRun Run { get; } = new(Shutdown.Snapshot(
        SmartShutdownPhase.Collecting, "Waiting for handovers", "0 of 1 shut down",
        [Shutdown.Row("builder", SmartShutdownSessionState.Asked, "asked")]));

    public List<SmartShutdownRequest> IgnoreAlls { get; } = new();

    public TaskCompletionSource<IgnoreAllResult> IgnoreAllAnswer { get; } = new();

    public int Records { get; private set; }

    public Func<CancellationToken, Task<IgnoreAllResult>> RecordAnswer { get; set; } =
        _ => Task.FromResult(new IgnoreAllResult(true, "workspace-os", null, 0));

    public Task<SmartShutdownAvailability> CheckAsync(SmartShutdownPurpose purpose, CancellationToken ct)
    {
        Checks.Add(purpose);
        return CheckAnswer.Task;
    }

    public ISmartShutdownRun Start(SmartShutdownRequest request)
    {
        Starts.Add(request);
        if (StartThrows is not null) throw StartThrows;
        return Run;
    }

    public Task<IgnoreAllResult> ShutDownIgnoringAllAsync(SmartShutdownRequest request, CancellationToken ct)
    {
        IgnoreAlls.Add(request);
        return IgnoreAllAnswer.Task;
    }

    public Task<IgnoreAllResult> RecordAndLetEndAsync(CancellationToken ct)
    {
        Records++;
        return RecordAnswer(ct);
    }
}

/// <summary>
/// Everything the coordinator is handed, REAL where a window is involved: a real owner window standing
/// for the main window, holding a stand-in for the session view and the place the coordinator's surface
/// goes; the real dialog, shown over that window; and counters for the two things that must happen
/// exactly so often - the application closing, and a message being shown.
/// </summary>
internal sealed class CoordinatorRig
{
    public CoordinatorRig(bool withEngine = true, TimeSpan? operatingSystemShutdownLimit = null)
    {
        SessionView = new TextBlock { Text = "the session view" };
        Host = new ContentControl { IsVisible = false };
        Main = new Window
        {
            Width = 900,
            Height = 600,
            RequestedThemeVariant = ThemeVariant.Dark,
            Content = new Panel { Children = { SessionView, Host } },
        };
        Main.Show();

        Coordinator = new SmartShutdownCoordinator(
            () => withEngine ? Engine : null,
            () => Sessions.ToList(),
            viewModel =>
            {
                Dialog = new SmartShutdownDialog(viewModel) { RequestedThemeVariant = ThemeVariant.Dark };
                DialogsOpened++;
                return Dialog.ShowForResultAsync(Main);
            },
            surface =>
            {
                Host.Content = surface;
                Host.IsVisible = true;
                SessionView.IsVisible = false;
            },
            () =>
            {
                SessionView.IsVisible = true;
                Host.IsVisible = false;
                Host.Content = null;
            },
            () => ApplicationCloses++,
            Messages.Add,
            new FakeShutdownClock(new DateTimeOffset(Shutdown.StartedUtc)),
            operatingSystemShutdownLimit);
        Dispatcher.UIThread.RunJobs();
    }

    public FakeSmartShutdown Engine { get; } = new();

    public List<Session> Sessions { get; } = new();

    public Window Main { get; }

    public TextBlock SessionView { get; }

    public ContentControl Host { get; }

    public SmartShutdownCoordinator Coordinator { get; }

    public SmartShutdownDialog? Dialog { get; private set; }

    public int DialogsOpened { get; private set; }

    public int ApplicationCloses { get; private set; }

    public List<string> Messages { get; } = new();

    /// <summary>What stands in place of the session view right now, or null.</summary>
    public SmartShutdownSurface? Surface => Host.IsVisible ? Host.Content as SmartShutdownSurface : null;

    public bool SessionViewIsShown => SessionView.IsVisible && !Host.IsVisible;

    public Session AddSession(ActivityState state, string name)
    {
        const string repo = @"C:\test\repo";
        var session = new Session(Guid.NewGuid(), repo, repo, null, new InertBackend(), null, state,
            DateTimeOffset.UtcNow, name, null);
        Sessions.Add(session);
        return session;
    }

    /// <summary>Runs everything posted to the interface thread, and what that posted in turn.</summary>
    public static void Settle()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
    }

    public static void Click(Button button)
    {
        button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Settle();
    }

    /// <summary>The engine says it may, and the real dialog's confirm comes alive.</summary>
    public void EngineSaysItMay()
    {
        Engine.CheckAnswer.SetResult(FakeSmartShutdown.May);
        Settle();
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
