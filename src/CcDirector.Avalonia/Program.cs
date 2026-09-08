using System.Runtime.InteropServices;
using Avalonia;
using CcDirector.ControlApi;
using CcDirector.Core.Instances;
using CcDirector.Core.Storage;
using CcDirector.Core.Update;
using CcDirector.Core.Utilities;

namespace CcDirector.Avalonia;

internal static class Program
{
    /// <summary>
    /// The process id this build was asked to wait for, from <c>--wait-for-exit &lt;pid&gt;</c>, or null.
    /// Parsed rather than assumed: an unreadable value means NO wait, because refusing to start over a
    /// malformed argument would be a Director that does not come back - which is the failure every part
    /// of this ordering exists to avoid.
    /// </summary>
    private static int? TryReadWaitForExit(string[] args)
    {
        for (var i = 0; i + 1 < args.Length; i++)
            if (args[i] == "--wait-for-exit" && int.TryParse(args[i + 1], out var pid) && pid > 0)
                return pid;
        return null;
    }

    [STAThread]
    public static int Main(string[] args)
    {
        // Resolve which named instance this process runs as BEFORE FileLog/CcStorage,
        // so logs and the whole data tree redirect to this instance's isolated home
        // (every instance, default included). Skipped for the hidden --apply-update
        // relauncher, which must run outside any instance context.
        var isApplyUpdate = args.Length >= 1 && args[0] == "--apply-update";
        if (!isApplyUpdate)
            ResolveInstance(args);

        // Start logging first so the pre-startup update steps below (which run
        // before App initializes its own logging) are actually recorded.
        FileLog.Start();

        FileLog.Write($"[Program] Instance: slug={InstanceContext.Slug}, isDefault={InstanceContext.IsDefault}, " +
                      $"explicit={InstanceContext.WasExplicitlySelected}, home={InstanceContext.InstanceHome}");

        // Catch anything that escapes a background thread so a crash is at least
        // recorded to a findable file rather than vanishing silently (issue #242).
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            FileLog.Write($"[Program] UNHANDLED ({(e.IsTerminating ? "terminating" : "non-terminating")}): {ex}");
            if (e.IsTerminating && ex is not null) WriteCrashFile("unhandled", ex);
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            FileLog.Write($"[Program] UNOBSERVED TASK: {e.Exception}");
            e.SetObserved();
        };

        // Hidden auto-update relauncher mode, handled BEFORE the single-instance
        // guard: a freshly downloaded build is invoked as
        //   cc-director --apply-update <installTarget> <parentPid> [instanceSlug]
        // to wait for the old process to exit, swap itself into place, and
        // relaunch. It must not acquire the guard or run any normal startup.
        // The instance slug is optional: a build older than that argument does not send one.
        if (args.Length >= 3 && args[0] == "--apply-update")
        {
            try
            {
                return UpdateInstaller.ApplyUpdate(args[1], int.Parse(args[2]), args.Length >= 4 ? args[3] : null);
            }
            catch (Exception ex)
            {
                // A failed apply must NOT be silent (issue #242): show the user why and
                // exit non-zero. The old build's bounded-apply logic will give up after a
                // couple of these and boot the working version with its own notice.
                FileLog.Write($"[Program] ApplyUpdate FAILED: {ex}");
                WriteCrashFile("apply-update", ex);
                MessageBoxW(IntPtr.Zero,
                    $"Director could not apply an update:\n\n{ex.Message}\n\n" +
                    "It will continue on the current version.",
                    "Director - Update failed", MB_OK | MB_ICONWARNING | MB_TOPMOST);
                FileLog.Stop();
                return 1;
            }
        }

        // WAIT FOR THE PROCESS THAT STARTED US, when it asked us to. A relaunch that hands over to a new
        // build has to let the outgoing one finish exiting first, or the incoming one claims the
        // single-instance mutex against a process that is on its way out and refuses to start. The
        // staged-update path has always done this (LaunchRelauncher passes the parent process id and
        // ApplyUpdate waits on it); the ROLLBACK relaunch did not, and that omission is the only reason
        // the guard below could not be taken earlier.
        if (TryReadWaitForExit(args) is { } waitForPid)
        {
            FileLog.Write($"[Program] waiting for process {waitForPid} to exit before claiming this instance");
            UpdateInstaller.WaitForProcessExit(waitForPid, TimeSpan.FromSeconds(30));
        }

        // THE SINGLE-INSTANCE GUARD IS TAKEN BEFORE ANY UPDATE ACTION, AND THAT IS THE WHOLE POINT OF
        // THIS BLOCK BEING HERE RATHER THAN NINETY LINES DOWN.
        //
        // It used to sit after five update actions - RecoverHalfAppliedSwap, TryRollBackFailedUpdate,
        // the relaunch, CleanupAfterUpdate and TryApplyStagedUpdateAtStartup - so a second copy of the
        // Director started against a live one would run all five before finding out it was not wanted.
        // The guard prevented a duplicate DIRECTOR; it did not prevent duplicate UPDATER WORK, because
        // the updater ran first. That mattered because a launcher that cannot fully read an instance
        // home still starts a Director there (deliberately - refusing strands a working one), so an
        // update could proceed over a Director nobody had accounted for.
        //
        // The comment above the staged-update call said the apply happens "before any session exists,
        // so no running work is ever lost". That is true of THIS process and false of the OTHER live
        // Director, which is the one whose work is at stake - a reassurance written from the wrong
        // process's point of view.
        //
        // WHY IT COULD NOT SIMPLY BE MOVED. The rollback relaunch above started the restored build and
        // exited without any handshake, so an early guard would have made the incoming build find the
        // mutex still held by its dying parent and exit - leaving NO Director at all, worse than either
        // problem being solved. It now carries the parent process id and the wait handled above, which
        // is the same handshake its sibling has always had, so the guard can come first.
        using var guard = SingleInstanceGuard.TryAcquire();
        if (guard is null)
        {
            // A second launch must NOT vanish silently -- "clicking does nothing" is exactly the
            // failure this issue targets (issue #242). Bring the already-running window to the
            // foreground; only if no window can be raised do we fall back to an explanatory dialog.
            if (TryRaiseExistingWindow())
            {
                FileLog.Write("[Program] Second launch: raised the already-running window and exited.");
                return 0;
            }

            var busyExe = Environment.ProcessPath ?? "(unknown)";
            var busyMessage =
                "Director is already running." + Environment.NewLine + Environment.NewLine +
                $"Exe: {busyExe}" + Environment.NewLine + Environment.NewLine +
                "Only one instance per install location can run at a time. " +
                "Identity, ports, and state files are keyed by the exe path -- " +
                "running a second copy would collide with the existing one.";
            ShowStartupNotice(busyMessage, "Director", MB_ICONWARNING);
            FileLog.Write("[Program] Second launch refused: another instance holds this exe path.");
            FileLog.Stop();
            return 1;
        }

        // Self-heal a broken install BEFORE cleanup deletes the ".old" backup we recover from
        // (issue #242). Two distinct failure modes are handled here:
        //
        //  1. A half-completed swap left the install exe missing or zero-length -- restore it
        //     from the ".old" backup so we can boot at all instead of dying silently.
        //  2. A previously-applied update produced a build that never came up healthy -- roll
        //     back to the ".old" backup, pin the bad version, relaunch the restored build, and
        //     exit so the working version runs.
        var recoveryNotice = UpdateInstaller.RecoverHalfAppliedSwap();

        var rollbackNotice = UpdateInstaller.TryRollBackFailedUpdate();
        if (rollbackNotice is not null)
        {
            FileLog.Write("[Program] Rolled back a failed update; relaunching the restored build.");
            MessageBoxW(IntPtr.Zero, rollbackNotice, "Director - Update rolled back", MB_OK | MB_ICONWARNING | MB_TOPMOST);
            try
            {
                // Started the same way an update relaunch is: no inherited CC_DIRECTOR_ROOT (which would
                // make the restored build nest a new, empty data home inside this instance's one) and the
                // instance carried explicitly.
                // THE HANDSHAKE, which this path never had. Without it the restored build starts while
                // this process is still exiting, and - now that the single-instance guard is claimed
                // first - it would find the mutex held by a dying parent and refuse to start, leaving
                // the machine with no Director at all. Its sibling LaunchRelauncher has always passed
                // this; the correct pattern was a hundred lines away in the same file.
                System.Diagnostics.Process.Start(
                    UpdateInstaller.BuildRelaunchStartInfo(Environment.ProcessPath ?? "", InstanceContext.Slug,
                        waitForProcessId: Environment.ProcessId));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[Program] Relaunch after rollback FAILED: {ex.Message}");
            }
            FileLog.Stop();
            return 0;
        }

        // Remove the prior build's leftover ".old" file and prune stale staging
        // directories before normal startup. (Runs AFTER recovery/rollback above, which
        // depend on the ".old" backup still being present.)
        UpdateInstaller.CleanupAfterUpdate();

        if (recoveryNotice is not null)
            MessageBoxW(IntPtr.Zero, recoveryNotice, "Director - Update recovered", MB_OK | MB_ICONWARNING | MB_TOPMOST);

        // Apply a staged update at startup -- before any session exists, so no
        // running work is ever lost. If one is pending, the relauncher takes over
        // and we exit immediately so it can swap us out. If a staged update has
        // failed to apply too many times, we get a notice instead of looping forever
        // (issue #242) and continue booting the current build.
        if (UpdateInstaller.TryApplyStagedUpdateAtStartup(out var updateNotice))
            return 0;
        if (updateNotice is not null)
            ShowStartupNotice(updateNotice, "Director - Update", MB_ICONWARNING);

        // THE GUARD IS ALREADY HELD. It was claimed near the top of Main, before any update action -
        // see the block there for why it moved and what had to change first. It is NOT re-taken here:
        // the same process claiming its own mutex twice would deadlock on a non-reentrant wait, and a
        // second claim would in any case answer a question that was already settled.

        // Never let a startup exception exit with no window and no message (issue #242).
        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
            return 0;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] FATAL startup error: {ex}");
            var crashPath = WriteCrashFile("startup", ex);
            ShowStartupNotice(
                $"Director failed to start:\n\n{ex.Message}\n\n" +
                (crashPath is null ? "" : $"Details written to:\n{crashPath}"),
                "Director - Startup error", MB_ICONERROR);
            return 1;
        }
    }

    /// <summary>
    /// Resolve the named instance for this process from <c>--instance &lt;slug&gt;</c> and the
    /// registry, then redirect the whole data tree to that instance's isolated home via the
    /// <c>CC_DIRECTOR_ROOT</c> override. Runs before FileLog/CcStorage.
    ///
    /// EVERY instance - default included - gets its own isolated home. There is no migration
    /// and no shared-root fallback: we never carry old flat data forward. Never throws; on any
    /// failure it still isolates the default home rather than dropping to the shared root.
    /// </summary>
    private static void ResolveInstance(string[] args)
    {
        try
        {
            var slug = ParseInstanceArg(args);
            var wasExplicit = slug is not null;
            InstanceContext.Initialize(slug, wasExplicit);

            // Guarantee the default entry exists (seeded from the hostname) so the picker and
            // launcher always have at least one instance to show.
            NamedInstanceRegistry.EnsureDefault(Environment.MachineName);

            // Resolve this process's instance; an unknown slug falls back to the default entry.
            var inst = NamedInstanceRegistry.Get(InstanceContext.Slug);
            if (inst is null)
            {
                wasExplicit = false;
                inst = NamedInstanceRegistry.Get(InstanceContext.DefaultSlug);
            }
            if (inst is not null)
                InstanceContext.Initialize(inst.Name, wasExplicit, inst.DisplayName);

            // Isolate this instance's home and redirect all storage into it - default too.
            Directory.CreateDirectory(InstanceContext.InstanceHome);
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", InstanceContext.InstanceHome);
        }
        catch
        {
            // Instance resolution must never stop the app from starting - but still isolate the
            // default home; never fall back to the shared root.
            InstanceContext.Initialize(null, wasExplicit: false);
            try
            {
                Directory.CreateDirectory(InstanceContext.InstanceHome);
                Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", InstanceContext.InstanceHome);
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Extract the value of <c>--instance &lt;slug&gt;</c> or <c>--instance=&lt;slug&gt;</c>; null if absent.</summary>
    private static string? ParseInstanceArg(string[] args)
    {
        const string flag = "--instance";
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (a.StartsWith(flag + "=", StringComparison.OrdinalIgnoreCase))
                return a.Substring(flag.Length + 1);
            if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    /// <summary>
    /// Write a crash report to the director log directory so a startup failure leaves a
    /// findable trail even when the UI never came up. Best-effort; returns the path or null.
    /// </summary>
    private static string? WriteCrashFile(string kind, Exception ex)
    {
        try
        {
            var dir = CcStorage.ToolLogs("director");
            Directory.CreateDirectory(dir);
            // No Date.Now in a reproducible context is fine here -- this is a real crash path.
            var path = Path.Combine(dir, $"crash-{kind}-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
            File.WriteAllText(path, $"[{kind}] {DateTime.Now:o}\n\n{ex}\n");
            return path;
        }
        catch
        {
            return null; // never let crash-reporting itself throw
        }
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();

    /// <summary>
    /// Bring the already-running Director's window to the foreground when a second copy is
    /// launched (issue #242). Locates the other process by exact image path (a different PID
    /// running the same exe) and raises its main window via Win32 so a re-launch focuses the
    /// existing window instead of silently exiting. Returns true when a window was raised.
    /// Best-effort; never throws.
    /// </summary>
    /// <summary>
    /// Show a pre-UI startup notice (update warning, single-instance, fatal startup error). On Windows
    /// this is a native <c>MessageBoxW</c> so the message is visible even though no Avalonia window exists
    /// yet. On macOS/Linux there is no user32; the process has no window at this point, so the notice goes
    /// to the crash log and stderr. Calling MessageBoxW off Windows would throw <c>DllNotFoundException</c>
    /// and mask the real startup error (the crash file written by the caller already holds the details).
    /// </summary>
    private static void ShowStartupNotice(string text, string caption, uint icon)
    {
        if (OperatingSystem.IsWindows())
        {
            MessageBoxW(IntPtr.Zero, text, caption, MB_OK | icon | MB_TOPMOST);
            return;
        }
        FileLog.Write($"[Program] {caption}: {text.Replace('\n', ' ')}");
        Console.Error.WriteLine($"{caption}: {text}");
    }

    private static bool TryRaiseExistingWindow()
    {
        // The window-raising path is Win32-only (MainWindowHandle + user32 SetForegroundWindow/ShowWindow).
        // On macOS/Linux there is no window handle to raise, so report "not raised" and let the caller fall
        // back to its explanatory notice rather than touch user32.
        if (!OperatingSystem.IsWindows())
            return false;
        try
        {
            var self = Environment.ProcessPath;
            if (string.IsNullOrEmpty(self))
                return false;

            var myPid = Environment.ProcessId;
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            foreach (var p in System.Diagnostics.Process.GetProcessesByName(current.ProcessName))
            {
                using (p)
                {
                    if (p.Id == myPid)
                        continue;

                    string? otherPath;
                    try { otherPath = p.MainModule?.FileName; }
                    catch { continue; } // access denied / exited between enumeration and read

                    if (otherPath is null
                        || !string.Equals(Path.GetFullPath(otherPath), Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase))
                        continue;

                    var hwnd = p.MainWindowHandle;
                    if (hwnd == IntPtr.Zero)
                        continue; // window not realized yet (e.g. still on the splash)

                    if (IsIconic(hwnd))
                        ShowWindow(hwnd, SW_RESTORE);
                    SetForegroundWindow(hwnd);
                    FileLog.Write($"[Program] TryRaiseExistingWindow: raised window of PID {p.Id} ({otherPath})");
                    return true;
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[Program] TryRaiseExistingWindow FAILED: {ex.Message}");
        }
        return false;
    }

    private const uint MB_OK = 0x00000000;
    private const uint MB_ICONWARNING = 0x00000030;
    private const uint MB_ICONERROR = 0x00000010;
    private const uint MB_TOPMOST = 0x00040000;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hWnd);
}
