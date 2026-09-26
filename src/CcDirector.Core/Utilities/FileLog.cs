using CcDirector.Core.Storage;

namespace CcDirector.Core.Utilities;

/// <summary>
/// Simple thread-safe file logger. Writes to cc-director logs/director/ by default; a hosted process may
/// select container-local storage before it starts.
///
/// The actual dequeue/rollover/flush work lives in <see cref="FileLogWriter"/> so the day-rollover
/// behavior can be unit-tested with an injectable clock (issue #171). This type is the thin static
/// facade the rest of the app calls; it wires the engine to wall-clock time and the real log
/// directory.
/// </summary>
public static class FileLog
{
    private static readonly string DefaultLogDir = CcStorage.ToolLogs("director");
    private static string _logDir = DefaultLogDir;

    // What distinguishes this process's log file from another process's. The desktop uses the process id,
    // which is unique there and is what the desktop tooling globs for. A container must call
    // UseUniqueInstanceId() before Start(), because inside a container the process id is always 1.
    private static string _instanceId = Environment.ProcessId.ToString();

    /// <summary>The file-name prefix of a Director's log: director-yyyy-MM-dd-{pid}.log.</summary>
    public const string DirectorFilePrefix = "director";

    // The first word of the file name. "director" unless the process chose otherwise before Start.
    private static string _filePrefix = DirectorFilePrefix;

    // The active writer. Reassigned in exactly three places, all of them deliberate: UseUniqueInstanceId
    // before the writer has started, Start when the previous writer is SPENT (a stop completed its queue,
    // which cannot be undone), and the test-only RedirectForTests seam (issue #862) which swaps in an
    // isolated writer for one test and restores the previous one afterwards.
    //
    // This comment used to say "Production never reassigns it", which was true and was the bug: a stopped
    // writer was restarted rather than replaced, so every later line threw. See Start.
    private static FileLogWriter _writer =
        new(_logDir, _instanceId, () => DateTime.Now);

    private static int _started;

    /// <summary>
    /// Also write every line to standard output. Off by default; a hosted/container deployment turns it on.
    ///
    /// This is a SECOND SINK, not a fallback: it is written on the caller's thread with no queue in front of
    /// it, so it survives the exact failure that erased the evidence of three hosted startup failures - the
    /// file share stalling, the bounded queue filling, and every line being dropped before it reached disk.
    /// In a container, standard output is what the platform captures per container, so it is also the only
    /// sink that is guaranteed unshared.
    /// </summary>
    public static bool MirrorToConsole { get; set; }

    /// <summary>
    /// Give this process its own log file, distinct from any other process writing the same directory.
    /// Must be called BEFORE <see cref="Start"/>; calling it afterwards throws rather than silently
    /// leaving the process on the shared file.
    ///
    /// A hosted Gateway needs this because the default discriminator - the process id - is always 1 inside
    /// a container, so every container computed the same path. Two of them then appended to one file on a
    /// Server Message Block share mounted <c>nobrl</c>, where the FileShare.Read request is not enforced
    /// across clients, and clobbered each other mid-record. The token is generated per process rather than
    /// read from the environment so its uniqueness does not depend on the platform supplying anything.
    ///
    /// A caller may also choose a different directory before the writer starts. The hosted Gateway uses
    /// that seam to keep its process log on the container's temporary disk instead of the durable workload
    /// share. Durable product state still uses <see cref="CcStorage"/>; a process log is not product state.
    /// </summary>
    public static void UseUniqueInstanceId() => UseUniqueInstanceId(DefaultLogDir);

    /// <summary>
    /// Give this process a unique log file in a caller-selected directory. Like the parameterless overload,
    /// this must be called before <see cref="Start"/>.
    /// </summary>
    public static void UseUniqueInstanceId(string logDirectory)
    {
        if (_started != 0)
            throw new InvalidOperationException(
                "FileLog.UseUniqueInstanceId() must be called before FileLog.Start(); the writer is already " +
                "running on the shared log file and moving it now would split this process's record in two.");

        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("A log directory cannot be blank.", nameof(logDirectory));

        _instanceId = Guid.NewGuid().ToString("N")[..12];
        _logDir = logDirectory;
        _writer = new FileLogWriter(_logDir, _instanceId, () => DateTime.Now, _filePrefix);
    }

    /// <summary>
    /// Write this process's log to <paramref name="logDirectory"/> as <c>{filePrefix}-yyyy-MM-dd-{pid}.log</c>.
    /// Must be called before <see cref="Start"/>, like <see cref="UseUniqueInstanceId()"/>.
    ///
    /// The launcher is why (issue #3311, B4). It used to share the Director's folder and file name, so its
    /// record sat in <c>logs/director/director-*.log</c> while every message the installer shows a person, and
    /// the launchd output beside it, pointed at <c>logs/launcher/</c> - the one folder that did not have it.
    /// </summary>
    public static void UseLogDirectory(string logDirectory, string filePrefix)
    {
        if (_started != 0)
            throw new InvalidOperationException(
                "FileLog.UseLogDirectory() must be called before FileLog.Start(); the writer is already running " +
                "and moving it now would split this process's record in two.");
        if (string.IsNullOrWhiteSpace(logDirectory))
            throw new ArgumentException("A log directory cannot be blank.", nameof(logDirectory));
        if (string.IsNullOrWhiteSpace(filePrefix))
            throw new ArgumentException("A log file prefix cannot be blank.", nameof(filePrefix));

        _logDir = logDirectory;
        _filePrefix = filePrefix;
        _writer = new FileLogWriter(_logDir, _instanceId, () => DateTime.Now, _filePrefix);
    }

    /// <summary>The folder this process writes its log to.</summary>
    public static string LogDirectory => _logDir;

    /// <summary>
    /// True when the file sink has failed repeatedly and the console mirror was turned on to report it
    /// rather than by configuration. Read it to tell "the mirror is on because we asked for it" apart from
    /// "the mirror is on because the file is dead".
    /// </summary>
    public static bool FileSinkFailed { get; private set; }

    /// <summary>
    /// Report a file sink that has stopped working, on a channel that is not the file (issue #2223).
    ///
    /// A permanently failing write repeats forever behind the writer's per-line catch, so before this the
    /// only symptom was a log that stopped growing - indistinguishable from a process with nothing to say.
    /// Turning the console mirror ON here is a REPORT, not a fallback: the point is that the failure
    /// becomes audible and names itself, and that the record continues somewhere a reader can find it.
    /// It is turned back off on recovery ONLY if this is what turned it on, so a hosted deployment that
    /// deliberately set the mirror never has it revoked underneath it.
    /// </summary>
    private static void OnSinkHealthChanged(int consecutiveFailures, Exception? ex)
    {
        if (ex is not null)
        {
            FileSinkFailed = true;
            if (!MirrorToConsole)
            {
                MirrorToConsole = true;
                _mirrorForcedBySinkFailure = true;
            }
            Console.Error.WriteLine(
                $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FileLog] FILE SINK DEAD: {consecutiveFailures} consecutive " +
                $"write failures ({ex.GetType().Name}: {ex.Message}). Path: {CurrentLogPath}. The log file is NOT " +
                "being written; this process's record continues on standard output until the file sink recovers.");
            return;
        }

        FileSinkFailed = false;
        Console.Error.WriteLine(
            $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [FileLog] file sink recovered; writing to {CurrentLogPath} again.");
        if (_mirrorForcedBySinkFailure)
        {
            _mirrorForcedBySinkFailure = false;
            MirrorToConsole = false;
        }
    }

    private static bool _mirrorForcedBySinkFailure;

    /// <summary>
    /// Start the background writer thread. Safe to call multiple times, INCLUDING after a
    /// <see cref="Stop"/> - which is the case that used to break everything.
    ///
    /// Stopping completes the writer's queue, and a completed queue can never accept another item. This
    /// method used to set <c>_started</c> back to 1 and start a thread on the SAME spent writer, so it
    /// restarted the FLAG and not the WRITER. Every later Write then passed the <c>_started</c> guard and
    /// threw from Enqueue into whichever caller happened to be logging. The guard was correct code and
    /// saved nothing, because <c>_started</c> had quietly stopped meaning "the writer can accept lines" -
    /// a guard is only as good as the invariant it reads (devthrottle_internal#1312).
    ///
    /// So a spent writer is REPLACED here rather than revived. A flag is not a lifetime.
    /// </summary>
    public static void Start()
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
            return;

        if (_writer.IsSpent)
            _writer = new FileLogWriter(_logDir, _instanceId, () => DateTime.Now, _filePrefix);

        _writer.OnSinkHealthChanged = OnSinkHealthChanged;
        _writer.Start();
    }

    /// <summary>
    /// Handed every logged message that is an error line (<see cref="ErrorReports.ErrorLine.IsError"/>), after
    /// it has been queued for the file. Set by <see cref="ErrorReports.ErrorReporter.Start"/> so the errors a
    /// Director or launcher logs also reach its Gateway (issue #3311). The observer must not block and must
    /// not throw; the reporter's own observer only adds to an in-memory table.
    /// </summary>
    public static Action<string>? ErrorObserver { get; set; }

    /// <summary>Log a message with a timestamp prefix.</summary>
    public static void Write(string message)
    {
        if (_started == 0) return;
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}";
        _writer.Enqueue(line);
        if (MirrorToConsole) Console.Out.WriteLine(line);
        System.Diagnostics.Debug.WriteLine(line);
        if (ErrorObserver is { } observer && ErrorReports.ErrorLine.IsError(message))
            observer(message);
    }

    /// <summary>
    /// TEST-ONLY. True when the installed writer's queue has been completed and can never accept another
    /// line - the state devthrottle_internal#1312 was about.
    ///
    /// This exists because the invariant is otherwise unobservable, and the obvious substitute is a trap.
    /// The first version of those tests asserted on <see cref="DroppedLines"/>, which is a PROCESS-WIDE
    /// counter every other test contributes to: the assertion passed alone and failed inside the full
    /// suite, where the ambient writer's bounded queue had filled from thousands of unrelated lines. An
    /// order-dependent test for an order-dependent bug is not a test, it is the same defect wearing a
    /// different hat.
    /// </summary>
    internal static bool InstalledWriterIsSpent => _writer.IsSpent;

    /// <summary>
    /// Lines this process could not fit into the writer's queue - a stalled writer means the file record is
    /// incomplete by exactly this many lines. Zero on a healthy process.
    /// </summary>
    public static long DroppedLines => _writer.DroppedLines;

    /// <summary>Flush remaining messages and stop the writer thread.</summary>
    public static void Stop()
    {
        if (Interlocked.CompareExchange(ref _started, 0, 1) != 1)
            return;
        _writer.Stop();
    }

    /// <summary>Returns the current log file path (useful for display).</summary>
    public static string CurrentLogPath =>
        Path.Combine(_logDir, $"{_filePrefix}-{DateTime.Now:yyyy-MM-dd}-{_instanceId}.log");

    /// <summary>
    /// TEST-ONLY seam (issue #862). Redirects FileLog to a private, throwaway directory for the life
    /// of the returned scope, then lets a test read exactly the lines it produced by draining the
    /// writer synchronously. This removes the two flakiness sources of asserting against the shared,
    /// process-wide writer: (1) <em>carryover</em> - a previous test's still-queued lines flushing
    /// into this test's file; and (2) <em>flush timing</em> - reading before the 1-second background
    /// flush landed the lines. Swapping the single static writer is safe because the test assemblies
    /// disable parallelization, so exactly one test owns FileLog at a time. Not for production use.
    /// </summary>
    internal static FileLogTestScope RedirectForTests() => new();

    /// <summary>The scope returned by <see cref="RedirectForTests"/>; restores the previous writer
    /// on dispose and deletes the throwaway directory. See that method for the rationale.</summary>
    internal sealed class FileLogTestScope : IDisposable
    {
        private readonly string _dir;
        private readonly FileLogWriter _previousWriter;
        private readonly int _previousStarted;
        private readonly FileLogWriter _testWriter;
        private List<string>? _lines;

        internal FileLogTestScope()
        {
            _dir = Path.Combine(Path.GetTempPath(), "cc-filelog-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            _previousWriter = _writer;
            _previousStarted = _started;
            _testWriter = new FileLogWriter(_dir, Environment.ProcessId.ToString(), () => DateTime.Now);
            _writer = _testWriter;
            _started = 1;
            _testWriter.Start();
        }

        /// <summary>
        /// Synchronously drain the writer to disk and return every line it wrote during this scope.
        /// Stop() completes the queue and joins the writer thread, so all lines are flushed before
        /// the read - no polling, no carryover. Idempotent: repeated calls return the same lines.
        /// </summary>
        internal IReadOnlyList<string> DrainAndReadLines()
        {
            if (_lines is not null) return _lines;
            _testWriter.Stop();
            _writer = _previousWriter;
            _started = _previousStarted;
            var lines = new List<string>();
            foreach (var file in Directory.EnumerateFiles(_dir, "*.log"))
                lines.AddRange(ReadAllLinesShared(file));
            _lines = lines;
            return lines;
        }

        public void Dispose()
        {
            // Ensure the writer thread is stopped (and its file handle released) before restoring,
            // even if the test never called DrainAndReadLines.
            if (_lines is null) _testWriter.Stop();
            _writer = _previousWriter;
            _started = _previousStarted;
            // Best-effort cleanup of the throwaway directory; a leftover temp dir is harmless.
            try { Directory.Delete(_dir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Read a log file with FileShare.ReadWrite so a still-open writer handle never
        /// blocks the read.</summary>
        private static List<string> ReadAllLinesShared(string path)
        {
            var lines = new List<string>();
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            string? line;
            while ((line = reader.ReadLine()) is not null)
                lines.Add(line);
            return lines;
        }
    }
}
