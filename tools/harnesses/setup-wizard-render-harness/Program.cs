using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CcDirectorSetup;
using CcDirectorSetup.Services;
using IoPath = System.IO.Path;

namespace SetupWizardRenderHarness;

// Headless + Skia driver for the REAL setup wizard. It runs the actual MainWindow - the real
// steps, the real styles, the real engine install - clicks through the whole flow with
// synthesized mouse input (so hit-testing is exercised, dead buttons fail the run), and captures
// each step to a PNG. This is how the wizard is verified end to end on a machine where screen
// capture and interactive UI driving are not available to agents, and the PNGs are the visual
// proof attached to pull requests.
//
// Usage:
//   cc-setup-wizard-render-harness --release-dir <dir> --out <dir> [--home <dir>]
//
//   --release-dir   A local directory acting as a full release (release-manifest.json + assets).
//                   The install step installs from it - no GitHub, hermetic.
//   --out           Where the PNGs go.
//   --home          Sandbox HOME/CC_DIRECTOR_ROOT so the run installs into a scratch area
//                   instead of the real user profile (default: a temp directory).
//   --no-sandbox    Install into the REAL user profile - the Director in ~/Applications, the launcher
//                   and its launch agent where the shipped wizard puts them. For a disposable Mac
//                   (a continuous-integration runner) only: this is how the shipped flow is proven
//                   against the machine's own launchd.
//
// After the Complete screen is shown the run prints what it says, one fact per line, so a proof can
// read the screen without a person: COMPLETE-HEADING, COMPLETE-DESCRIPTION, COMPLETE-WARNING (the text
// of the warning panel, or "(none)") and COMPLETE-NEXT (the Next button's label).
internal static class Program
{
    private static string _outDir = "";
    private static int _shot;

    [STAThread]
    private static int Main(string[] args)
    {
        var releaseDir = Arg(args, "--release-dir");
        _outDir = Arg(args, "--out") ?? IoPath.Combine(AppContext.BaseDirectory, "out");
        var home = Arg(args, "--home")
            ?? IoPath.Combine(IoPath.GetTempPath(), $"wizard-harness-home-{Guid.NewGuid():N}");

        Directory.CreateDirectory(_outDir);

        var noSandbox = Array.Exists(args, a => string.Equals(a, "--no-sandbox", StringComparison.OrdinalIgnoreCase));
        if (noSandbox)
        {
            Console.WriteLine("installing into the REAL user profile (--no-sandbox)");
        }
        else
        {
            Directory.CreateDirectory(home);
            // Sandbox the install: InstallLayout reads HOME (macOS user profile) and CC_DIRECTOR_ROOT.
            // Must happen BEFORE any wizard type constructs its InstallLayout.
            Environment.SetEnvironmentVariable("HOME", home);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", IoPath.Combine(home, "cc-director"));
        }

        if (releaseDir is not null)
            EngineInstallRunner.ReleaseDirectoryOverride = releaseDir;

        AppBuilder.Configure<App>()
            .UseSkia()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
            .SetupWithoutStarting();

        // Uninstall-UI mode: drive the three uninstall views with a FAKE runner (the step's
        // injection seam) - the real engine Apply would boot the machine's real launchd agent out,
        // which is correct for a genuine uninstall and exactly what a UI capture must never do.
        if (Array.Exists(args, a => string.Equals(a, "--uninstall-ui", StringComparison.OrdinalIgnoreCase)))
            return RenderUninstallUi();

        var screensOnly = Array.Exists(args, a => string.Equals(a, "--screens", StringComparison.OrdinalIgnoreCase));

        var window = new MainWindow { Width = 900, Height = 640 };
        window.Show();
        Pump();

        Capture(window, "welcome");
        HoverAndCapture(window, FindButton(window, "NextButton"), "welcome-next-hover");

        // The wizard is three steps: Welcome, Install, Complete. There is no Prerequisites screen -
        // the executables carry their own .NET runtime, so nothing this installer places needs
        // anything already on the machine and there is nothing left to gate on. This harness used to
        // click Next expecting that screen and then wait for a Re-check button that cannot exist.
        //
        // --screens stops here: it proves every screen CONSTRUCTS AND RENDERS without touching the
        // network or installing anything. That is the pre-release check a unit test cannot make - a
        // wizard that throws on a renamed element passes every test and fails on the first
        // double-click. The full run (no --screens) still drives the real install end to end.
        if (screensOnly)
        {
            ClickNext(window);                   // -> Install (begins the real install; we only render)
            Pump();
            Capture(window, "install-screen");
            RenderCompleteStates();
            Console.WriteLine($"RENDER OK (screens only) -> {_outDir}");
            return 0;
        }

        ClickNext(window);                       // -> Install (the REAL engine install runs now)
        Pump();
        Capture(window, "install-running");
        PumpUntil(() => NextLabel(window) is "Next" or "Retry", TimeSpan.FromMinutes(15),
            "the install to finish");
        Capture(window, "install-done");
        if (NextLabel(window) == "Retry")
        {
            Console.WriteLine("FAIL: install ended in Retry - see install-done.png and the setup log");
            return 1;
        }

        ClickNext(window);                       // -> Complete
        Pump();
        Capture(window, "complete");
        PrintCompleteFacts(window);

        var launch = FindDescendantButton(window, "LaunchButton");
        HoverAndCapture(window, launch, "complete-launch-hover");

        Console.WriteLine($"RENDER OK -> {_outDir}");
        return 0;
    }

    /// <summary>What the Complete screen says, one fact per line, for a proof that has no eyes.</summary>
    private static void PrintCompleteFacts(MainWindow window)
    {
        string Text(string name) => FindDescendant<TextBlock>(window, name) is { IsVisible: true } t ? (t.Text ?? "") : "";
        var warning = FindDescendant<Border>(window, "WarningPanel") is { IsVisible: true } ? Text("WarningText") : "(none)";
        Console.WriteLine($"COMPLETE-HEADING: {Text("HeadingText")}");
        Console.WriteLine($"COMPLETE-DESCRIPTION: {Text("DescriptionText").Replace('\n', ' ')}");
        Console.WriteLine($"COMPLETE-WARNING: {warning.Replace('\n', ' ')}");
        Console.WriteLine($"COMPLETE-NEXT: {NextLabel(window) ?? "(disabled)"}");
    }

    /// <summary>
    /// The Complete screen in every state it can render, built directly. Reaching these through a
    /// real install would need four installs; the states are pure functions of their arguments, so
    /// this proves the markup and the code-behind agree for all of them in one pass.
    /// </summary>
    private static void RenderCompleteStates()
    {
        var noAgent = "No coding agent is set up yet, so your board has nothing to run. "
                      + "DevThrottle checks your tools when it opens and can add the ones it finds.";

        var states = new (string Name, CcDirectorSetup.Steps.CompleteStep Step)[]
        {
            ("complete-installed", new CcDirectorSetup.Steps.CompleteStep(
                installed: 2, skipped: 0, installPath: "/Users/you/Applications/DevThrottle.app",
                isUpdate: false, alreadyUpToDate: false, version: "1.8.5")),
            ("complete-one-thing-left", new CcDirectorSetup.Steps.CompleteStep(
                installed: 2, skipped: 0, installPath: "/Users/you/Applications/DevThrottle.app",
                isUpdate: false, alreadyUpToDate: false, version: "1.8.5",
                agentNotice: noAgent, skippedNames: null, readyToGo: false)),
            ("complete-already-current", new CcDirectorSetup.Steps.CompleteStep(
                installed: 0, skipped: 0, installPath: "/Users/you/Applications/DevThrottle.app",
                isUpdate: true, alreadyUpToDate: true, version: "1.8.5")),
            ("complete-problems", new CcDirectorSetup.Steps.CompleteStep(
                installed: 1, skipped: 1, installPath: "/Users/you/Applications/DevThrottle.app",
                isUpdate: false, alreadyUpToDate: false, version: "1.8.5",
                // The DISPLAY name, which is what MainWindow passes (ComponentDisplayName maps the
                // internal "cc-launcher" id). Passing the raw id here made the screenshot show a name
                // the product no longer renders.
                agentNotice: null, skippedNames: ["Director"],
                skippedReasons: ["Director: cc-director-mac-arm64.zip SHA-256 mismatch; download rejected."])),
            // The launcher could not be started (launchd refused it): the Director IS installed, the green
            // button is still the next step, and the amber panel says what is lost and what happens next.
            ("complete-launcher-warning", new CcDirectorSetup.Steps.CompleteStep(
                installed: 1, skipped: 0, installPath: "/Users/you/Applications/DevThrottle.app",
                isUpdate: false, alreadyUpToDate: false, version: "1.8.5",
                agentNotice: null, skippedNames: null, readyToGo: true, skippedReasons: null,
                warnings: ["Launcher: macOS could not start the launcher at all: launchd reports 'spawn failed' (exit code 78: EX_CONFIG). A report of this failure was sent to DevThrottle."])),
        };

        foreach (var (name, step) in states)
        {
            var w = new Window { Width = 900, Height = 640, Content = step };
            w.Show();
            Pump();
            Capture(w, name);
            w.Close();
            Pump();
        }
    }

    private static int RenderUninstallUi()
    {
        var layout = CcDirector.Setup.Engine.InstallLayout.Default();
        var step = new CcDirectorSetup.Steps.UninstallStep(layout, CcDirector.Setup.Engine.InstallRole.Workstation,
            progress =>
            {
                foreach (var phase in new[]
                {
                    "Stopping the Launcher launch agent",
                    "Removing the app and CLI tools",
                    "Removing the shell PATH entries and shims",
                })
                {
                    progress.Report(phase);
                    Thread.Sleep(30);
                }
                return new CcDirector.Setup.Engine.UninstallReport(
                    Success: true,
                    Steps: ["Removed Director app bundle", "Removed CLI tools", "Removed the Launcher launch agent (launchd)"],
                    Errors: []);
            });

        // The same content margin MainWindow's StepContent gives every step.
        var window = new Window
        {
            Width = 900, Height = 640,
            Content = new Border { Child = step, Margin = new Thickness(32, 24) },
            Background = Avalonia.Media.Brush.Parse("#1E1E1E"),
        };
        window.Show();
        Pump();
        Capture(window, "uninstall-confirm");

        var box = FindDescendant<CheckBox>(window, "DeleteDataCheckbox")!;
        box.IsChecked = true;
        Pump();
        Capture(window, "uninstall-confirm-delete-data");
        box.IsChecked = false;
        Pump();

        Click(window, FindDescendantButton(window, "ConfirmUninstallButton")!);
        PumpUntil(() => FindDescendant<DockPanel>(window, "CompleteView") is { IsVisible: true },
            TimeSpan.FromSeconds(30), "the fake uninstall to finish");
        Capture(window, "uninstall-complete");

        Console.WriteLine($"RENDER OK -> {_outDir}");
        return 0;
    }

    private static T? FindDescendant<T>(Visual root, string name) where T : Control
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is T c && c.Name == name) return c;
            if (child is Visual v && FindDescendant<T>(v, name) is { } found) return found;
        }
        return null;
    }

    // ---- Flow helpers ------------------------------------------------------

    private static string? NextLabel(MainWindow w) =>
        FindButton(w, "NextButton") is { } b && b.IsEnabled ? b.Content?.ToString() : null;

    private static void ClickNext(MainWindow w)
    {
        var next = FindButton(w, "NextButton")
            ?? throw new InvalidOperationException("NextButton not found");
        Click(w, next);
    }

    /// <summary>Click via synthesized mouse input at the control's center, so the run fails when a
    /// button cannot actually be hit (covered, collapsed, or zero-sized).</summary>
    private static void Click(Window w, Button button)
    {
        var p = Center(w, button);
        w.MouseMove(p);
        w.MouseDown(p, MouseButton.Left);
        w.MouseUp(p, MouseButton.Left);
        Pump();
    }

    private static void HoverAndCapture(Window w, Button? button, string name)
    {
        if (button is null || !button.IsVisible)
        {
            Console.WriteLine($"note: skipping {name} (button not present on this run)");
            return;
        }
        w.MouseMove(Center(w, button));
        Pump();
        Capture(w, name);
        w.MouseMove(new Point(0, 0));           // leave hover state clean for the next scene
        Pump();
    }

    private static Point Center(Window w, Button button)
    {
        var topLeft = button.TranslatePoint(new Point(0, 0), w)
            ?? throw new InvalidOperationException($"cannot locate {button.Name} in the window");
        return new Point(topLeft.X + button.Bounds.Width / 2, topLeft.Y + button.Bounds.Height / 2);
    }

    private static Button? FindButton(Window w, string name) =>
        w.FindControl<Button>(name) ?? FindDescendantButton(w, name);

    /// <summary>Find a named button anywhere in the tree (step controls are swapped into a
    /// ContentControl, so window-level FindControl cannot see their children).</summary>
    private static Button? FindDescendantButton(Visual root, string name)
    {
        foreach (var child in root.GetVisualChildren())
        {
            if (child is Button b && b.Name == name) return b;
            if (child is Visual v && FindDescendantButton(v, name) is { } found) return found;
        }
        return null;
    }

    // ---- Pump + capture ----------------------------------------------------

    private static void Pump(int ticks = 5)
    {
        for (var i = 0; i < ticks; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void PumpUntil(Func<bool> done, TimeSpan timeout, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!done())
        {
            if (DateTime.UtcNow > deadline)
                throw new TimeoutException($"timed out waiting for {what}");
            Pump();
            Thread.Sleep(50);
        }
        Pump();
    }

    private static void Capture(Window w, string name)
    {
        var frame = w.CaptureRenderedFrame();
        if (frame is null) { Console.WriteLine($"WARN: no frame for {name}"); return; }
        _shot++;
        var path = IoPath.Combine(_outDir, $"{_shot:D2}-{name}.png");
        frame.Save(path);
        Console.WriteLine($"saved {IoPath.GetFileName(path)}");
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }
}
