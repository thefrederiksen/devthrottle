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
