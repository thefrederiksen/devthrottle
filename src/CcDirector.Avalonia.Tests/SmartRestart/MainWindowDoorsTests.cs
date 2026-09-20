using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Platform;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using CcDirector.Avalonia.SmartRestart;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests.SmartRestart;

/// <summary>
/// THE TWO DOORS IN THE REAL MAIN WINDOW (issue #3167, phase 5). Everything behind the doors is proved
/// with fakes in <see cref="SmartShutdownCoordinatorTests"/>, on a stand-in window. The fifteen lines
/// that JOIN the real window to that flow were proved only by reading MainWindow.axaml.cs as text, and
/// a text match would pass just as happily if the menu item were wired to nothing.
///
/// So these open the real <see cref="MainWindow"/> in the headless application and press its own doors:
/// its own File menu item, built by its own BuildNativeMenu and clicked through the same interface
/// Avalonia's Windows backend calls when the owner clicks it; and its own OnClosing, given each close
/// reason that matters. What is read back is what the real window's own controls say.
///
/// WHAT THESE DO NOT COVER, said plainly because the owner is deciding how much of his own time this
/// saves:
///
/// * THE ENGINE IS ALWAYS NULL HERE. The window's engine lambda is
///   <c>(Application.Current as App)?.ControlApiHost?.CreateSmartShutdown()</c>, and the headless test
///   application is not <c>App</c>, so it answers null and cannot be made to answer anything else
///   without starting a real Director. Every path that needs an engine - the smart shutdown itself, the
///   progress screen, the restart, the close the flow asks for - is therefore reached only in
///   SmartShutdownCoordinatorTests, never through the real window.
/// * THE REAL DIALOG IS NEVER SHOWN OVER THE REAL WINDOW. Showing this window raises its Loaded
///   handler, whose first line casts Application.Current to the real App and so throws here; the window
///   is built but never shown, and a modal dialog has no visible owner to open over. What the dialog
///   draws is SmartShutdownDialogTests; that the coordinator opens it is SmartShutdownCoordinatorTests.
/// * NOTHING PRESSES A REAL MOUSE ON THE REAL FILE MENU. The native menu is drawn by Windows, not by
///   Avalonia, so there is no frame to click in a headless test.
/// </summary>
public class MainWindowDoorsTests
{
    // ===== Door one: File, Smart Restart =====

    /// <summary>
    /// The File menu really has the item, and clicking it really reaches the coordinator. The sentence
    /// read back is written in exactly one place in the product - the no-engine branch of
    /// <see cref="SmartShutdownCoordinator.OpenFromFileMenuAsync"/> - so the window's own notification
    /// bar carrying it says both that the click arrived there and that the answer came back out through
    /// the window's own ShowNotification.
    /// </summary>
    [AvaloniaFact]
    public void FileMenu_SmartRestartClicked_ReachesTheCoordinator_AndItsAnswerReachesTheNotificationBar()
    {
        var window = new MainWindow();

        var item = SmartRestartMenuItem(window);
        Assert.NotNull(item);
        Assert.False(window.NotificationBar.IsVisible);

        Click(item!);

        Assert.True(window.NotificationBar.IsVisible);
        Assert.Equal(
            "Smart Restart is not available: this Director's control service did not start. The log says why.",
            window.NotificationText.Text);
    }

    /// <summary>
    /// The same door with sessions on the rail: still the coordinator, still through this window's own
    /// notification bar. A Director with sessions and no engine is told the engine is the problem, which
    /// is the coordinator's own order of checks rather than anything the window decides.
    /// </summary>
    [AvaloniaFact]
    public void FileMenu_SmartRestartClicked_WithSessionsOnTheRail_StillReachesTheCoordinator()
    {
        var window = new MainWindow();
        AddSession(window, ActivityState.Working, "builder");
        AddSession(window, ActivityState.Idle, "reviewer");

        Click(SmartRestartMenuItem(window)!);

        Assert.True(window.NotificationBar.IsVisible);
        Assert.Contains("Smart Restart is not available", window.NotificationText.Text);
    }

    /// <summary>The File menu names the item once, and names neither window this feature replaced.</summary>
    [AvaloniaFact]
    public void FileMenu_NamesSmartRestartOnce_AndNamesNeitherOldWindow()
    {
        var window = new MainWindow();
        var headers = FileMenuHeaders(window);

        Assert.Single(headers, h => h == "Smart Restart");
        Assert.DoesNotContain(headers, h => h.Contains("Drain", StringComparison.Ordinal));
    }

    // ===== Door two: the window closing =====

    /// <summary>
    /// A user close with a session running: the window's own OnClosing asks the coordinator, the
    /// coordinator says cancel, and the window CANCELS its own close and returns. Without that branch
    /// the Director would close with the session still running, which is the defect this feature exists
    /// to fix.
    /// </summary>
    [AvaloniaFact]
    public void OnClosing_UserCloseWithASessionRunning_IsCancelledByTheWindow()
    {
        var window = new MainWindow();
        AddSession(window, ActivityState.Working, "builder");

        var closing = RaiseOnClosing(window, WindowCloseReason.WindowClosing);

        Assert.True(closing.Args.Cancel);
        // Cancelled means RETURNED: none of the window's own teardown below the branch ran, which is
        // what the throw in the two tests below shows happening when the close IS let through.
        Assert.Null(closing.Error);
    }

    /// <summary>
    /// The same window with the same session, and the other reason. The operating system shutting down
    /// is neither asked about nor cancelled - there is no ten minutes to spend, and a dialog nobody can
    /// answer would only hold the machine up. This is the test that proves OnClosing hands over the REAL
    /// close reason rather than a constant: one rig, two reasons, two different answers.
    /// </summary>
    [AvaloniaFact]
    public void OnClosing_OperatingSystemShutdownWithASessionRunning_IsNotCancelled_AndTheCloseCarriesOn()
    {
        var window = new MainWindow();
        AddSession(window, ActivityState.Working, "builder");

        var closing = RaiseOnClosing(window, WindowCloseReason.OSShutdown);

        Assert.False(closing.Args.Cancel);
        Assert.IsType<InvalidCastException>(closing.Error);
    }

    /// <summary>
    /// A user close with nothing running is not cancelled either: the Director closes as it always has.
    /// The same contrast again, this time on the sessions the window hands over rather than on the
    /// reason.
    /// </summary>
    [AvaloniaFact]
    public void OnClosing_UserCloseWithNoSessions_IsNotCancelled_AndTheCloseCarriesOn()
    {
        var window = new MainWindow();

        var closing = RaiseOnClosing(window, WindowCloseReason.WindowClosing);

        Assert.False(closing.Args.Cancel);
        Assert.IsType<InvalidCastException>(closing.Error);
    }

    // ===== The things the window hands the coordinator when it builds it =====

    /// <summary>
    /// THE SWAP, ON THE REAL WINDOW'S OWN CONTROLS. The coordinator is handed two actions: one puts a
    /// control in place of the session view, the other takes it away again. Here they are taken off the
    /// real window's own coordinator and run, and what is read back is the real <c>SmartShutdownHost</c>
    /// and the real <c>SessionViewGrid</c> of MainWindow.axaml.
    /// </summary>
    [AvaloniaFact]
    public void TheSwapLambdas_PutTheSurfaceInPlaceOfTheSessionView_AndTakeItAwayAgain()
    {
        var window = new MainWindow();
        var coordinator = CoordinatorOf(window);

        // As the window starts: the session view is what is on screen.
        Assert.False(window.SmartShutdownHost.IsVisible);
        Assert.Null(window.SmartShutdownHost.Content);
        Assert.True(window.SessionViewGrid.IsVisible);

        var surface = new SmartShutdownSurface();
        Lambda<Action<Control>>(coordinator, "_showInPlaceOfSessionView")(surface);

        Assert.Same(surface, window.SmartShutdownHost.Content);
        Assert.True(window.SmartShutdownHost.IsVisible);
        Assert.False(window.SessionViewGrid.IsVisible);

        Lambda<Action>(coordinator, "_restoreSessionView")();

        Assert.Null(window.SmartShutdownHost.Content);
        Assert.False(window.SmartShutdownHost.IsVisible);
        Assert.True(window.SessionViewGrid.IsVisible);
    }

    /// <summary>
    /// The sessions lambda hands over the window's OWN rail, live: a session added after the coordinator
    /// was built is in the next answer, because the lambda reads the roster each time rather than
    /// copying it once.
    /// </summary>
    [AvaloniaFact]
    public void TheSessionsLambda_HandsOverTheWindowsOwnRail_AsItIsWhenItIsAsked()
    {
        var window = new MainWindow();
        var sessions = Lambda<Func<IEnumerable<Session>>>(CoordinatorOf(window), "_sessions");

        Assert.Empty(sessions());

        var builder = AddSession(window, ActivityState.Working, "builder");
        Assert.Equal(new[] { builder }, sessions().ToArray());
    }

    /// <summary>
    /// The engine lambda answers null here rather than throwing, and that is the whole reason the
    /// engine-driven paths cannot be reached through the real window in a test: the headless application
    /// is not the Director's App, so there is no control service and no engine. Said as a test rather
    /// than only as prose, so that a later change which DOES make it reachable turns this red and gets
    /// the deeper tests written.
    /// </summary>
    [AvaloniaFact]
    public void TheEngineLambda_UnderTheHeadlessTestApplication_AnswersNoEngineRatherThanThrowing()
    {
        var window = new MainWindow();

        Assert.Null(Lambda<Func<ISmartShutdown?>>(CoordinatorOf(window), "_engine")());
    }

    /// <summary>
    /// The remaining two are the window's own methods, handed over as method groups. Nothing is run
    /// here: showing a notification is proved by the File menu tests above, and closing the application
    /// needs an engine, so it is reached only in SmartShutdownCoordinatorTests. What is asserted is that
    /// the coordinator holds THIS window's Close and THIS window's ShowNotification.
    /// </summary>
    [AvaloniaFact]
    public void TheCloseAndMessageLambdas_AreThisWindowsOwnCloseAndItsOwnNotificationBar()
    {
        var window = new MainWindow();
        var coordinator = CoordinatorOf(window);

        var close = Lambda<Action>(coordinator, "_closeApplication");
        Assert.Same(window, close.Target);
        Assert.Equal("Close", close.Method.Name);

        var message = Lambda<Action<string>>(coordinator, "_showMessage");
        Assert.Same(window, message.Target);
        Assert.Equal("ShowNotification", message.Method.Name);
    }

    /// <summary>
    /// The dialog lambda belongs to this window. It is not run (this window is never shown, see the note
    /// at the top), so what is asserted is that the coordinator would ask THIS window to own the dialog
    /// rather than some other window.
    /// </summary>
    [AvaloniaFact]
    public void TheDialogLambda_BelongsToThisWindow()
    {
        var window = new MainWindow();

        var showDialog = Lambda<Func<SmartShutdownViewModel, Task<SmartShutdownChoice>>>(
            CoordinatorOf(window), "_showDialog");

        Assert.Same(window, showDialog.Target);
    }

    // ===== The rig =====

    private sealed record ClosingRun(WindowClosingEventArgs Args, Exception? Error);

    /// <summary>
    /// Raises the real window's own OnClosing with the given reason and gives back what it did: whether
    /// it cancelled, and anything that came out of it afterwards.
    ///
    /// Avalonia builds <see cref="WindowClosingEventArgs"/> itself and keeps the constructor to its own
    /// assembly, and the only other way in is Show() then Close() - which this window cannot survive
    /// here, because its Loaded handler casts Application.Current to the real App. So the arguments are
    /// built the way Avalonia builds them and handed to the window's own override.
    ///
    /// A close that is LET THROUGH runs the window's own teardown, and the first thing that teardown
    /// does is reach for the real application, which the headless test application is not. That throw is
    /// reported here rather than hidden, because it is what tells a test that the close carried on.
    /// Nothing is written to the owner's files by it: the cast is the first statement of
    /// UpdateAllSessionHistoryTimestamps, before any load or save.
    /// </summary>
    private static ClosingRun RaiseOnClosing(MainWindow window, WindowCloseReason reason)
    {
        var args = (WindowClosingEventArgs)Activator.CreateInstance(
            typeof(WindowClosingEventArgs),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
            binder: null,
            args: new object?[] { reason, false },
            culture: null)!;
        Assert.Equal(reason, args.CloseReason);
        Assert.False(args.Cancel);

        var onClosing = typeof(MainWindow).GetMethod(
            "OnClosing", BindingFlags.Instance | BindingFlags.NonPublic, binder: null,
            types: new[] { typeof(WindowClosingEventArgs) }, modifiers: null)
            ?? throw new InvalidOperationException("MainWindow no longer overrides OnClosing(WindowClosingEventArgs)");

        Exception? error = null;
        try
        {
            onClosing.Invoke(window, new object[] { args });
        }
        catch (TargetInvocationException ex)
        {
            error = ex.InnerException;
        }
        Settle();
        return new ClosingRun(args, error);
    }

    /// <summary>The coordinator the real window built for its own two doors.</summary>
    private static SmartShutdownCoordinator CoordinatorOf(MainWindow window) =>
        (SmartShutdownCoordinator)(typeof(MainWindow)
            .GetProperty("SmartShutdown", BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(window)
            ?? throw new InvalidOperationException("MainWindow no longer has a SmartShutdown coordinator"));

    /// <summary>One of the things the window handed the coordinator when it built it.</summary>
    private static T Lambda<T>(SmartShutdownCoordinator coordinator, string field) where T : Delegate =>
        (T)(typeof(SmartShutdownCoordinator)
            .GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.GetValue(coordinator)
            ?? throw new InvalidOperationException($"SmartShutdownCoordinator no longer holds {field}"));

    /// <summary>The File menu item headed "Smart Restart", as BuildNativeMenu built it.</summary>
    private static NativeMenuItem? SmartRestartMenuItem(MainWindow window) =>
        FileMenuItems(window).FirstOrDefault(i => i.Header == "Smart Restart");

    private static string[] FileMenuHeaders(MainWindow window) =>
        FileMenuItems(window).Select(i => i.Header ?? string.Empty).ToArray();

    private static IEnumerable<NativeMenuItem> FileMenuItems(MainWindow window)
    {
        var menu = NativeMenu.GetMenu(window)
            ?? throw new InvalidOperationException("MainWindow set no native menu");
        var file = menu.Items.OfType<NativeMenuItem>().FirstOrDefault(i => i.Header == "File")
            ?? throw new InvalidOperationException("the native menu has no File menu");
        var items = file.Menu?.Items
            ?? throw new InvalidOperationException("the File menu is empty");
        return items.OfType<NativeMenuItem>();
    }

    /// <summary>Clicks a native menu item the way Avalonia's own platform backend clicks it.</summary>
    private static void Click(NativeMenuItem item)
    {
        ((INativeMenuItemExporterEventsImplBridge)item).RaiseClicked();
        Settle();
    }

    private static Session AddSession(MainWindow window, ActivityState state, string name)
    {
        const string repo = @"C:\test\repo";
        var session = new Session(Guid.NewGuid(), repo, repo, null, new InertBackend(), null, state,
            DateTimeOffset.UtcNow, name, null);
        window._sessions.Add(new SessionViewModel(session));
        return session;
    }

    /// <summary>Runs everything posted to the interface thread, and what that posted in turn.</summary>
    private static void Settle()
    {
        for (var i = 0; i < 5; i++)
            Dispatcher.UIThread.RunJobs();
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
