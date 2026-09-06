using System.Globalization;
using System.Runtime.Versioning;
using CcDirector.Core.Instances;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Lifecycle;

/// <summary>
/// A one-shot "do this now" request sent between two processes on the SAME MACHINE with no network of
/// any kind - no socket, no port, no Gateway.
///
/// WHY THIS EXISTS AND WHY IT MUST NOT ROUTE THROUGH THE GATEWAY. Everything else an agent does goes
/// through the Gateway, deliberately, so there is one door. Process lifecycle is the exception, and it
/// is not a stylistic exception: the launcher supervising the Director, and the updater making the
/// Director exit so its executable can be replaced, have to work EXACTLY WHEN THE GATEWAY DOES NOT.
/// A Director that cannot be stopped because the internet is down cannot be updated, and a launcher
/// that cannot be quit because a cloud service is unreachable cannot be uninstalled. Routing any of
/// this through the Gateway would make the recovery path depend on the thing being recovered from.
///
/// WHAT IT IS. A named signal. The listener holds it open for its whole life; a sender raises it by
/// name. There is no payload and no reply - every one of these requests is a verb with no arguments
/// ("shut down", "restart the Director", "check for updates"), and a request with no payload cannot be
/// injected with one.
///
/// TWO MECHANISMS, ONE PER PLATFORM - THIS IS NOT A FALLBACK CHAIN. Windows gets a named
/// <see cref="EventWaitHandle"/>, which is the operating system's own answer: instantaneous, owned by
/// the kernel, and destroyed automatically when the listening process dies, so a signal can never be
/// left lying around to fire at the wrong process later. Unix has no named event in .NET (only
/// <see cref="Mutex"/> is named cross-platform), so it gets a request file that the listener polls. The
/// platform is chosen once, by <see cref="OperatingSystem.IsWindows"/>; neither arm is ever tried after
/// the other fails. A stale Unix request is defended against by an age stamp rather than by a retry.
///
/// THE CALLER CONTRACT, WHICH IS THE SAME ON BOTH PLATFORMS. <see cref="Raise"/> answering true means
/// the request was HANDED OVER, never that it was carried out. Every caller must verify the effect it
/// wanted - the process exited, the new version answered - and act on that. This is deliberate: the
/// Windows arm can tell you nobody was listening and the Unix arm cannot, and a contract that only one
/// platform can honour would quietly become a Windows-only guarantee that Unix code was written
/// against.
/// </summary>
public static class LifecycleSignal
{
    /// <summary>
    /// The Windows object-namespace prefix. <c>Local\</c> - the caller's own logon session - and not
    /// <c>Global\</c>, because creating a Global object needs a privilege a standard user does not
    /// have, and every process involved here (the launcher, the Director, the installer) runs as the
    /// same interactive user.
    /// </summary>
    private const string WindowsPrefix = @"Local\";

    /// <summary>
    /// How old a Unix request file may be and still be acted on. Longer than any plausible gap between
    /// a sender writing and a listener polling, and far shorter than the time between one run of the
    /// product and the next - so a request whose sender died mid-write is discarded rather than
    /// delivered to a process that starts an hour later.
    /// </summary>
    public static readonly TimeSpan UnixRequestFreshness = TimeSpan.FromMinutes(2);

    /// <summary>How often the Unix listener looks for a request file.</summary>
    public static readonly TimeSpan UnixPollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Start listening for <paramref name="name"/>. The returned handle must be kept for as long as the
    /// signal should be answered, and disposed to stop listening.
    ///
    /// <paramref name="onSignal"/> runs on a background thread, never on the caller's. It is invoked
    /// once per raise, and an exception from it is logged and swallowed - a listener that dies on a bad
    /// signal is a listener that cannot be shut down.
    /// </summary>
    public static ILifecycleSignalListener Listen(string name, Action onSignal)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A signal name is required", nameof(name));
        ArgumentNullException.ThrowIfNull(onSignal);

        FileLog.Write($"[LifecycleSignal] Listen: name={name}");
        return OperatingSystem.IsWindows()
            ? new WindowsListener(name, onSignal)
            : new UnixListener(name, onSignal);
    }

    /// <summary>
    /// Ask whoever is listening for <paramref name="name"/> to act.
    ///
    /// Returns true when the request was handed over - NOT that it was carried out. See the class
    /// comment: the caller verifies the effect. False means the request could not even be delivered,
    /// which on Windows is the strong statement that nothing is listening.
    /// </summary>
    public static bool Raise(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A signal name is required", nameof(name));

        try
        {
            var delivered = OperatingSystem.IsWindows() ? RaiseWindows(name) : RaiseUnix(name);
            FileLog.Write($"[LifecycleSignal] Raise: name={name}, delivered={delivered}");
            return delivered;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LifecycleSignal] Raise FAILED: name={name}: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Is anything listening for <paramref name="name"/> right now - asked WITHOUT asking it to do
    /// anything?
    ///
    /// This exists because the only other way to find out was <see cref="Raise"/>, and raising a
    /// signal to see whether somebody is there restarts a Director or quits a launcher as a side
    /// effect of the question. A capability check may not have consequences.
    ///
    /// THE ANSWER IS A TRI-STATE AND THAT IS NOT A HEDGE. On Windows a named event either exists in
    /// this logon session or it does not, so true and false are both facts. On Unix the mechanism is a
    /// request FILE that the listener polls (see the class comment): there is no listener registry to
    /// consult, and the absence of a request file says nothing at all about whether anybody is
    /// watching for one. Returning false there would be an invented no, so the answer is null - NOT
    /// OBSERVABLE - and a caller has to decide what to do about a question this platform cannot
    /// answer, rather than being handed a confident wrong one.
    /// </summary>
    /// <returns>True/false on Windows; null on Unix, where a listener cannot be observed at all.</returns>
    public static bool? HasListener(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ArgumentException("A signal name is required", nameof(name));
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            if (!EventWaitHandle.TryOpenExisting(WindowsPrefix + name, out var handle) || handle is null)
                return false;
            // Opened, never set: this asks who is there, it does not ask them to act.
            handle.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[LifecycleSignal] HasListener FAILED: name={name}: {ex.Message}");
            return null;
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool RaiseWindows(string name)
    {
        if (!EventWaitHandle.TryOpenExisting(WindowsPrefix + name, out var handle) || handle is null)
        {
            FileLog.Write($"[LifecycleSignal] Raise: nothing is listening for {name}");
            return false;
        }

        using (handle)
        {
            handle.Set();
            return true;
        }
    }

    private static bool RaiseUnix(string name)
    {
        var path = UnixRequestPath(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, DateTimeOffset.UtcNow.ToString("o", CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>
    /// Where a Unix request file lives: <c>config/lifecycle-signals</c> under the machine-wide SHARED
    /// storage root. Scoped per root, so two roots on one machine - a test rig and the real install -
    /// never signal each other.
    ///
    /// THE ROOT IS THE SHARED ONE, NEVER THIS PROCESS'S OWN, and this is the load-bearing line of the
    /// whole Unix arm. A Director redirects its data tree to its instance home at startup, so a path
    /// resolved through CcStorage differs between a Director (its instance home) and the launcher (the
    /// shared root) - the two ends of every signal here. Resolved that way, the Director polled a
    /// directory the launcher never wrote and vice versa: every launcher-initiated stop became a
    /// 20-second stall and a force-kill, and "install it now" reported success while the request sat in
    /// a directory nobody watched. <see cref="LifecycleSignalNames.RootKey"/> already derives the NAME
    /// from <see cref="InstanceContext.SharedRoot"/> for exactly this reason; the path must agree with
    /// the name.
    ///
    /// WINDOWS CANNOT SEE A BUG HERE, WHICH IS WHY IT SHIPPED. The Windows arm addresses a kernel
    /// object by NAME alone - the two processes never have to agree on a file path at all. The Unix arm
    /// is the only place where agreeing on a path is load-bearing, so a platform-shared test suite ran
    /// entirely green while one platform's delivery was completely inert. The cross-process tests in
    /// <c>LifecycleSignalCrossProcessTests</c> exist to make two real processes agree on where a signal
    /// lives; they are the detector this defect proved was missing.
    /// </summary>
    internal static string UnixRequestPath(string name)
        => Path.Combine(InstanceContext.SharedRoot, "config", "lifecycle-signals", name + ".request");

    [SupportedOSPlatform("windows")]
    private sealed class WindowsListener : ILifecycleSignalListener
    {
        private readonly EventWaitHandle _signal;
        private readonly EventWaitHandle _stop = new(false, EventResetMode.ManualReset);
        private readonly Thread _thread;
        private bool _disposed;

        public WindowsListener(string name, Action onSignal)
        {
            Name = name;
            // AutoReset: one raise wakes the wait exactly once and re-arms itself, so a signal cannot
            // be left set and re-delivered on the next loop.
            _signal = new EventWaitHandle(false, EventResetMode.AutoReset, WindowsPrefix + name, out var createdNew);
            if (!createdNew)
            {
                // Another process in this logon session already listens for this name. That is a real
                // anomaly worth saying out loud - the two would take alternate signals - and it is not
                // fatal, because refusing to listen would leave the caller with no way to be stopped
                // at all.
                FileLog.Write($"[LifecycleSignal] {name} was ALREADY being listened for by another process in this "
                              + "logon session. Two listeners will take alternate signals; expect a shutdown or a "
                              + "restart request to reach the wrong one.");
            }

            _thread = new Thread(() => Pump(onSignal))
            {
                IsBackground = true,
                Name = $"lifecycle-signal-{name}",
            };
            _thread.Start();
        }

        public string Name { get; }

        private void Pump(Action onSignal)
        {
            var handles = new WaitHandle[] { _signal, _stop };
            while (true)
            {
                var which = WaitHandle.WaitAny(handles);
                if (which == 1) return;

                FileLog.Write($"[LifecycleSignal] {Name}: signalled");
                try { onSignal(); }
                catch (Exception ex) { FileLog.Write($"[LifecycleSignal] {Name} handler FAILED: {ex}"); }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stop.Set(); } catch { }
            // A shutdown handler disposes the very listener that invoked it, so this can be the pump
            // thread joining itself - which throws. Skipping the join there is correct rather than
            // merely safe: the pump returns as soon as the handler does, and the stop is already set.
            // The handles are only released once the pump is known to have stopped waiting on them;
            // on the self-disposing path the process is exiting and the kernel releases them.
            if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))
            {
                _signal.Dispose();
                _stop.Dispose();
            }
            FileLog.Write($"[LifecycleSignal] stopped listening for {Name}");
        }
    }

    private sealed class UnixListener : ILifecycleSignalListener
    {
        private readonly string _path;
        private readonly CancellationTokenSource _stop = new();
        private readonly Thread _thread;
        private bool _disposed;

        public UnixListener(string name, Action onSignal)
        {
            Name = name;
            _path = UnixRequestPath(name);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

            // A leftover request from a previous run is deleted rather than answered: this process has
            // only just started, so a request that predates it was meant for its predecessor.
            TryDeleteRequest();

            _thread = new Thread(() => Pump(onSignal))
            {
                IsBackground = true,
                Name = $"lifecycle-signal-{name}",
            };
            _thread.Start();
        }

        public string Name { get; }

        private void Pump(Action onSignal)
        {
            // POLLED, not watched. A FileSystemWatcher silently drops notifications - this repository
            // measured roughly one lost event in five, with the file present and complete and no Error
            // raised - so a watcher may be a latency win but must never be the delivery guarantee. A
            // shutdown that is missed one time in five is worse than one that arrives half a second
            // late.
            while (!_stop.IsCancellationRequested)
            {
                try
                {
                    if (File.Exists(_path) && TakeRequest(out var stamp))
                    {
                        var age = DateTimeOffset.UtcNow - stamp;
                        if (age > UnixRequestFreshness)
                        {
                            FileLog.Write($"[LifecycleSignal] {Name}: discarding a request written {age.TotalSeconds:F0}s "
                                          + "ago - too old to have been meant for this process.");
                        }
                        else
                        {
                            FileLog.Write($"[LifecycleSignal] {Name}: signalled");
                            try { onSignal(); }
                            catch (Exception ex) { FileLog.Write($"[LifecycleSignal] {Name} handler FAILED: {ex}"); }
                        }
                    }
                }
                catch (Exception ex)
                {
                    FileLog.Write($"[LifecycleSignal] {Name}: poll failed: {ex.Message}");
                }

                if (_stop.Token.WaitHandle.WaitOne(UnixPollInterval)) return;
            }
        }

        /// <summary>Read and remove the request in one pass, so it is answered exactly once.</summary>
        private bool TakeRequest(out DateTimeOffset stamp)
        {
            stamp = default;
            string text;
            try { text = File.ReadAllText(_path); }
            catch (FileNotFoundException) { return false; }
            TryDeleteRequest();
            return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out stamp);
        }

        private void TryDeleteRequest()
        {
            try { if (File.Exists(_path)) File.Delete(_path); }
            catch (Exception ex) { FileLog.Write($"[LifecycleSignal] {Name}: cannot remove {_path}: {ex.Message}"); }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            // See the Windows listener: a handler that disposes its own listener runs ON this thread.
            if (Thread.CurrentThread != _thread && _thread.Join(TimeSpan.FromSeconds(2)))
                _stop.Dispose();
            FileLog.Write($"[LifecycleSignal] stopped listening for {Name}");
        }
    }
}

/// <summary>A live subscription to a named lifecycle signal. Dispose to stop answering it.</summary>
public interface ILifecycleSignalListener : IDisposable
{
    /// <summary>The signal name being answered.</summary>
    string Name { get; }
}
