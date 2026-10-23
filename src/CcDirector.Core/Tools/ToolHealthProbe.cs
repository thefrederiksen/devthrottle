using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>What the health run concluded about one tool.</summary>
public enum ToolVerdict
{
    /// <summary>The health run has not judged this tool yet.</summary>
    Unchecked,

    /// <summary>The tool is not installed here, so no checks were run against it.</summary>
    NotInstalled,

    /// <summary>Every declared check passed.</summary>
    Working,

    /// <summary>The tool is installed but a declared check failed. <see cref="ToolCheckOutcome.Detail"/> says which.</summary>
    NotWorking,
}

/// <summary>One tool's verdict from a health run, with the human-readable reason behind it.</summary>
public sealed record ToolCheckOutcome(string Name, ToolVerdict Verdict, string Detail);

/// <summary>
/// The result of one full health run: the roll-up every surface shows, plus the per-tool verdicts, plus
/// when it was taken.
/// </summary>
public sealed record ToolHealthSnapshot(
    ToolHealthSummary Summary, IReadOnlyList<ToolCheckOutcome> Tools, DateTime TakenUtc);

/// <summary>
/// Runs every shipped tool's declared checks and rolls them up - the ONE place that answers "do the
/// tools work?", and the one snapshot every surface reads.
///
/// It exists because the answer used to be computed in one surface and guessed at in another (issue
/// #1045). The Director's board ran these checks and reported cc-pdf failing; the first-run wizard read
/// only the catalog - a presence check - and rendered it as "Ready" under the heading "All 9 tools are
/// installed and up to date". Both were describing the same nine tools at the same moment, in opposite
/// terms, because they were asking different questions and labelling both answers the same way. A
/// surface that has no verdict must say so ("checking"), not supply a cheerful one of its own.
/// </summary>
public static class ToolHealthProbe
{
    private static readonly object Gate = new();
    private static ToolHealthSnapshot? _last;
    private static Task<ToolHealthSnapshot>? _inFlight;
    // Bumped by Invalidate. A run that started before the tools on disk changed is answering a question
    // about a machine that no longer exists, so it must neither be published nor joined by a later caller.
    private static int _generation;

    /// <summary>
    /// The most recent completed health run, or null when none has finished yet. Null means "not judged
    /// yet" and must be rendered as such - never as a pass.
    /// </summary>
    public static ToolHealthSnapshot? Last
    {
        get { lock (Gate) return _last; }
    }

    /// <summary>
    /// Discard the cached verdict so the next run re-judges from scratch. Call this whenever the tools on
    /// disk change (a repair, a reconcile) - a verdict taken before that change is about a different
    /// machine. It also detaches any run already in flight, so a caller that arrives immediately after a
    /// repair starts a FRESH run rather than joining the one that was mid-way through measuring the
    /// pre-repair state and would have handed back its answer as though it were the post-repair one.
    /// </summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _last = null;
            _inFlight = null;
            _generation++;
        }
        FileLog.Write("[ToolHealthProbe] snapshot invalidated");
    }

    /// <summary>
    /// Run every tool's declared checks and publish the snapshot. Bounded concurrency: tools are process
    /// launches, and a first run on a fresh install is the slowest they ever are. A tool that is not
    /// available runs no checks and is reported as not installed.
    ///
    /// Concurrent callers share ONE run rather than each starting their own. On first launch the board and
    /// the setup wizard both want this answer at the same moment; two runs would be eighteen cold process
    /// launches racing each other on the slowest machine state there is, and could hand the two surfaces
    /// two different answers - the disagreement this whole seam exists to prevent.
    /// </summary>
    public static Task<ToolHealthSnapshot> RunAsync(CancellationToken ct = default)
    {
        lock (Gate)
        {
            if (_inFlight is { } running)
            {
                FileLog.Write("[ToolHealthProbe] RunAsync: joining the health run already in flight");
                return running;
            }
            // Task.Run so the catalog read (synchronous file I/O) never runs while this lock is held, and
            // never on a caller's UI thread.
            var generation = _generation;
            var started = Task.Run(() => RunCoreAsync(generation, ct), ct);
            _inFlight = started;
            return started;
        }
    }

    private static async Task<ToolHealthSnapshot> RunCoreAsync(int generation, CancellationToken ct)
    {
        try
        {
            var snapshot = await RunUncoalescedAsync(ct);
            lock (Gate)
            {
                if (generation == _generation)
                {
                    _last = snapshot;
                }
                else
                {
                    // The tools changed under this run. Its answer describes the machine as it was before
                    // that change, so publishing it would put a stale verdict on every surface.
                    FileLog.Write("[ToolHealthProbe] discarding a health run that the tools changed underneath");
                }
            }
            return snapshot;
        }
        finally
        {
            lock (Gate)
            {
                if (generation == _generation) _inFlight = null;
            }
        }
    }

    private static async Task<ToolHealthSnapshot> RunUncoalescedAsync(CancellationToken ct)
    {
        FileLog.Write("[ToolHealthProbe] RunAsync: starting a full tool health run");
        var catalog = new ToolCatalogService().GetCatalog();
        var runner = new ToolTestRunner();
        using var gate = new SemaphoreSlim(Math.Max(1, Environment.ProcessorCount - 1));

        var outcomes = await Task.WhenAll(catalog.Select(async descriptor =>
        {
            // Availability (PATH or bundled bin), not bin-only IsBuilt, decides whether the tool can run
            // its checks (issue #448). A PATH-only tool's BinaryPath is its PATH-resolved exe.
            if (!descriptor.IsAvailable)
            {
                return (input: new ToolHealthInput(descriptor.Name, false, descriptor.IsExpected, false),
                        outcome: new ToolCheckOutcome(descriptor.Name, ToolVerdict.NotInstalled, "not installed here"));
            }

            await gate.WaitAsync(ct);
            try
            {
                var results = await runner.RunAllForToolAsync(descriptor, ct);
                var firstFailure = results.FirstOrDefault(r => !r.Passed);
                if (firstFailure is null)
                {
                    return (input: new ToolHealthInput(descriptor.Name, true, descriptor.IsExpected, true),
                            outcome: new ToolCheckOutcome(descriptor.Name, ToolVerdict.Working, "all checks passed"));
                }

                var reason = DescribeFailure(firstFailure);
                // Log the reason HERE, where it is known. Reporting "1 fail" and dropping the cause is
                // what made the clean-install cc-pdf failure undiagnosable from the log it left behind.
                FileLog.Write($"[ToolHealthProbe] {descriptor.Name} FAILED: {reason}" +
                              DescribeOutput(firstFailure));
                return (input: new ToolHealthInput(descriptor.Name, true, descriptor.IsExpected, false, reason),
                        outcome: new ToolCheckOutcome(descriptor.Name, ToolVerdict.NotWorking, reason));
            }
            finally { gate.Release(); }
        }));

        var summary = ToolHealthSummary.From(outcomes.Select(o => o.input));
        var snapshot = new ToolHealthSnapshot(summary, outcomes.Select(o => o.outcome).ToList(), DateTime.UtcNow);

        FileLog.Write($"[ToolHealthProbe] RunAsync done: pass={summary.Pass}, fail={summary.Fail}, " +
                      $"notBuilt={summary.NotBuilt}, broken={summary.Broken}" +
                      (summary.Failures.Count == 0 ? "" : $", failing: {string.Join("; ", summary.Failures)}"));
        return snapshot;
    }

    /// <summary>
    /// One line naming WHICH check failed and what it reported, e.g. "smoke check: timed out after 90s"
    /// or "version check: exit 1". This is the string the board row, the wizard row, and the log all show.
    /// </summary>
    private static string DescribeFailure(ToolTestResult result)
    {
        var check = result.Kind switch
        {
            ToolTestKind.OnPath => "binary missing",
            ToolTestKind.Version => "version check",
            ToolTestKind.Smoke => "smoke check",
            _ => result.Kind.ToString(),
        };
        return string.IsNullOrWhiteSpace(result.Message) ? check : $"{check}: {result.Message}";
    }

    /// <summary>The tool's own output on a failure, trimmed to one readable line for the log.</summary>
    private static string DescribeOutput(ToolTestResult result)
    {
        var text = !string.IsNullOrWhiteSpace(result.Stderr) ? result.Stderr : result.Stdout;
        if (string.IsNullOrWhiteSpace(text)) return "";
        var oneLine = text.Replace("\r", " ").Replace("\n", " ").Trim();
        if (oneLine.Length > 300) oneLine = oneLine[..300] + "...";
        return $" | output: {oneLine}";
    }
}
