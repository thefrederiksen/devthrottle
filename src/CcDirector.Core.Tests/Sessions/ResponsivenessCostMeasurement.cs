using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Backends;
using CcDirector.Core.Configuration;
using CcDirector.Core.Git;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Sessions;

/// <summary>
/// The MEASUREMENT behind issue #1111 - "the Director gets slower with every session".
///
/// These are not pass/fail guards; they print numbers. The guards live next door in
/// <see cref="DictationRosterRefreshCostTests"/> (the loop shape) and in
/// <c>SessionGitStatusMonitorTests</c> (one probe per repository), both of which are deterministic.
/// This file exists because "feels faster" is not evidence and a timing assertion is a flaky guard:
/// the honest split is to MEASURE here and PIN the shape there.
///
/// Read the numbers with <c>dotnet test --filter ResponsivenessCostMeasurement -l "console;verbosity=detailed"</c>.
///
/// The POSITIVE CONTROL is the point of the first measurement. Before believing any fix, the old shape
/// has to be seen getting worse as sessions accumulate - if the cost does not climb with the session
/// count on this machine, then this machine cannot observe the defect and nothing measured here about
/// its removal would mean anything.
/// </summary>
[Collection("ProcessWideState")]
public sealed class ResponsivenessCostMeasurement
{
    private readonly ITestOutputHelper _out;

    public ResponsivenessCostMeasurement(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// One tick of the roster's one-second "receiving a dictation" refresh, timed both ways against a
    /// marker store shaped like the real one on SOREN_NORTH: 28 marker directories, every one of them in
    /// a terminal state (the machine had not a single Pending marker; the oldest was three weeks old).
    ///
    /// That detail is what makes the old cost so galling - the whole per-second bill was being paid to
    /// re-confirm that there was nothing to report.
    /// </summary>
    [Fact]
    public void Dictation_tick_cost_old_shape_versus_new_shape()
    {
        var root = Path.Combine(Path.GetTempPath(), "cc-dictation-cost-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const int markers = 28;
            for (var i = 0; i < markers; i++)
            {
                var dir = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(dir);
                File.WriteAllText(Path.Combine(dir, "record.json"), JsonSerializer.Serialize(new
                {
                    state = "Delivered",
                    sessionId = Guid.NewGuid().ToString(),
                    uploadId = Guid.NewGuid().ToString(),
                    receivedUtc = DateTime.UtcNow,
                }));
            }

            var sessionIds = Enumerable.Range(0, 32).Select(_ => Guid.NewGuid().ToString()).ToArray();

            // Warm the OS file cache so this measures our work, not a cold disk. The real Director is warm.
            for (var i = 0; i < 3; i++) _ = DictationLockReader.LockedSessionIds(root);

            _out.WriteLine($"dictation-uploads root holds {markers} markers, all terminal (the SOREN_NORTH shape)");
            _out.WriteLine("");
            _out.WriteLine(" sessions |   old: ask per session |   new: read once per tick |  ratio");
            _out.WriteLine("----------+------------------------+---------------------------+-------");

            foreach (var n in new[] { 1, 10, 19, 27 })
            {
                var live = sessionIds.Take(n).ToArray();

                var oldMs = TimeTick(() =>
                {
                    // What the roster used to do: every session asks the store for itself, and each ask
                    // re-enumerates the directory and re-reads all 28 markers.
                    foreach (var id in live) _ = DictationLockReader.IsSessionLocked(root, id);
                });

                var newMs = TimeTick(() =>
                {
                    // What it does now: one pass over the store, then each session asks the set for free.
                    var locked = DictationLockReader.LockedSessionIds(root);
                    foreach (var id in live) _ = locked.Contains(id);
                });

                var ratio = newMs > 0 ? (oldMs / newMs).ToString("0.0") + "x" : "n/a";
                _out.WriteLine($"{n,9} | {oldMs,19:0.00} ms | {newMs,22:0.00} ms | {ratio,6}");
            }

            _out.WriteLine("");
            _out.WriteLine("Every millisecond in the 'old' column is spent on the dispatcher thread, once a second,");
            _out.WriteLine("forever - which is the thread that paints the user interface.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }
        }
    }

    /// <summary>
    /// The git side of the same complaint, counted rather than timed: how many probes one refresh cycle
    /// asks for, against a session distribution of the shape observed on SOREN_NORTH - here 23 sessions
    /// spread over 6 real repositories, whose <c>RepoPath</c> values were stored in 8 different spellings.
    /// (The live session count moved between 19 and 27 while the issue was being written, so this pins a
    /// representative distribution of that shape rather than claiming to be a snapshot of one instant.)
    ///
    /// A count is the better measurement here. Timing a stubbed probe would measure the stub; the number
    /// that matters is how many times the monitor asks a question whose answer it already has.
    /// </summary>
    [Fact]
    public async Task Git_probe_count_per_cycle_against_the_real_session_distribution()
    {
        // The real spread, verbatim: same six directories, nine spellings, separator and case as stored.
        var observed = new (string RepoPath, int Sessions)[]
        {
            ("D:/ReposFred/devthrottle", 8),
            (@"D:\ReposFred\devthrottle", 4),
            ("D:/ReposMindzie/mindzieWeb", 3),
            (@"D:\ReposMindzie\mindzieWeb", 3),
            ("D:/ReposFred/devthrottle_internal", 2),
            ("D:/ReposFred/dti-qa-clean", 1),
            ("D:/ReposMindzie/mindzieWeb.wt-8241", 1),
            ("D:/ReposFred/dt-pure-roster", 1),
        };

        using var manager = new SessionManager(new AgentOptions());
        var sessions = new List<Session>();
        foreach (var (repoPath, count) in observed)
        {
            for (var i = 0; i < count; i++)
            {
                var s = new Session(Guid.NewGuid(), repoPath, repoPath, null, new NullBackend(), SessionBackendType.ConPty);
                sessions.Add(s);
                manager.AdoptSession(s);
            }
        }

        try
        {
            var asked = new List<string>();
            var monitor = new SessionGitStatusMonitor(
                manager,
                interval: TimeSpan.FromHours(1),
                probe: (path, _) =>
                {
                    lock (asked) asked.Add(path);
                    return Task.FromResult(new GitCountResult(Success: true, Count: 0));
                },
                directoryExists: _ => true);

            await monitor.RefreshOnceAsync();

            var distinctRaw = asked.Distinct(StringComparer.OrdinalIgnoreCase).Count();

            _out.WriteLine($"live sessions in the cycle ....... {sessions.Count}");
            _out.WriteLine($"real repositories behind them .... 6");
            _out.WriteLine($"distinct RepoPath spellings ...... {observed.Length}");
            _out.WriteLine($"PROBES THE MONITOR ASKED FOR ..... {asked.Count}");
            _out.WriteLine($"distinct paths it asked about .... {distinctRaw}");
            _out.WriteLine("");
            _out.WriteLine("Each probe is a `git status` against a working tree that live agents are writing to.");
            _out.WriteLine("The provider does hold a ten-second path-keyed cache, but it cannot save these:");
            _out.WriteLine("it is keyed on the RAW path so the two spellings of one directory never share an");
            _out.WriteLine("entry, it is populated on completion so concurrent probes all miss together, and");
            _out.WriteLine("its ten seconds are shorter than the fifteen-second poll, so it is cold every cycle.");
        }
        finally
        {
            foreach (var s in sessions) s.Dispose();
        }
    }

    /// <summary>Median of several ticks - one tick is too short and too noisy to quote.</summary>
    private static double TimeTick(Action tick)
    {
        const int runs = 9;
        var samples = new double[runs];
        for (var i = 0; i < runs; i++)
        {
            var sw = Stopwatch.StartNew();
            tick();
            sw.Stop();
            samples[i] = sw.Elapsed.TotalMilliseconds;
        }
        Array.Sort(samples);
        return samples[runs / 2];
    }

    private sealed class NullBackend : ISessionBackend
    {
        public CircularTerminalBuffer? Buffer => null;
        public int ProcessId => 1;
        public string Status => "Null";
        public bool IsRunning => true;
        public bool HasExited => false;

#pragma warning disable CS0067
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Kill() { }
        public void Dispose() { }
    }
}
