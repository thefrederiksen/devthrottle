using System.Globalization;

namespace CcDirector.Reclaim.Background;

/// <summary>
/// Where the background scan of one folder stands.
///
/// These are four different things and a reader is told which one it is looking at. "There is no
/// report yet" that quietly means "the scan crashed" is the failure this whole product exists to
/// prevent, met one level up: an absence standing in for an answer.
/// </summary>
public enum BackgroundScanState
{
    /// <summary>No background scan of this folder has ever been started on this machine.</summary>
    NeverRun,

    /// <summary>A scan is running now, in a process that is still alive.</summary>
    Running,

    /// <summary>The last scan finished and its result is saved.</summary>
    Completed,

    /// <summary>
    /// The last scan did not produce a result: it threw, its measurement was a broken instrument, it
    /// was asked to stop, or the process running it went away before it finished. The reason says
    /// which.
    /// </summary>
    Failed
}

/// <summary>
/// The state of the background scan of one folder, with the finished sentences a screen prints.
///
/// The engine decides what the record on the disk MEANS - including that a record saying "running"
/// left behind by a process that no longer exists is a scan that failed - and writes the sentences.
/// Nothing that renders this works any of it out again: critical rule 7 in CLAUDE.md.
/// </summary>
public sealed record BackgroundScanStatus
{
    /// <summary>The folder the scan is of.</summary>
    public required string RootPath { get; init; }

    /// <summary>Where the scan stands.</summary>
    public required BackgroundScanState State { get; init; }

    /// <summary>When the latest scan started, or null when none ever has.</summary>
    public required DateTimeOffset? StartedUtc { get; init; }

    /// <summary>When the latest scan ended, or null when it has not, or when none ever started.</summary>
    public required DateTimeOffset? FinishedUtc { get; init; }

    /// <summary>Why the latest scan failed, in one finished sentence, or null when it did not.</summary>
    public required string? FailureReason { get; init; }

    /// <summary>
    /// When a scan of this folder last finished with a result, or null when none ever has. It
    /// survives a later scan that is running or that failed, because the result it names is still
    /// on the disk and is still the newest whole one there is.
    /// </summary>
    public required DateTimeOffset? LastCompletedUtc { get; init; }

    /// <summary>The saved scan that last completed scan wrote, or null when none ever has.</summary>
    public required string? IndexPath { get; init; }

    /// <summary>The saved recommendations that last completed scan wrote, or null when none ever has.</summary>
    public required string? RecommendationPath { get; init; }

    /// <summary>The status itself: finished sentences, printed as they stand.</summary>
    public required IReadOnlyList<string> Lines { get; init; }

    /// <summary>
    /// Write the finished sentences for a status whose every other field is set.
    /// </summary>
    /// <param name="status">The status to describe.</param>
    public static IReadOnlyList<string> Describe(BackgroundScanStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);

        var lines = new List<string>
        {
            $"root: {status.RootPath}",
            $"background-scan: {StateWord(status.State)}"
        };

        switch (status.State)
        {
            case BackgroundScanState.NeverRun:
                lines.Add(
                    "detail: no background scan of this folder has ever been started on this machine, " +
                    "so there is no result to show and none was lost");
                break;

            case BackgroundScanState.Running:
                lines.Add($"detail: a scan started at {Moment(status.StartedUtc)} and is still running");
                break;

            case BackgroundScanState.Completed:
                lines.Add($"detail: the scan that started at {Moment(status.StartedUtc)} finished at {Moment(status.FinishedUtc)}");
                break;

            case BackgroundScanState.Failed:
                lines.Add($"detail: the scan that started at {Moment(status.StartedUtc)} did not produce a result");
                lines.Add($"reason: {status.FailureReason}");
                break;

            default:
                throw new InvalidOperationException($"There is no background scan state {status.State}.");
        }

        if (status.State != BackgroundScanState.Completed && status.State != BackgroundScanState.NeverRun)
        {
            lines.Add(status.LastCompletedUtc is null
                ? "last-result: none, because no scan of this folder has ever finished"
                : $"last-result: the scan that finished at {Moment(status.LastCompletedUtc)} is still saved and is the newest whole one");
        }

        return lines;
    }

    /// <summary>The state as the one word a report prints and a machine matches on.</summary>
    /// <param name="state">The state.</param>
    public static string StateWord(BackgroundScanState state) => state switch
    {
        BackgroundScanState.NeverRun => "never-run",
        BackgroundScanState.Running => "running",
        BackgroundScanState.Completed => "completed",
        BackgroundScanState.Failed => "failed",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "There is no such background scan state.")
    };

    private static string Moment(DateTimeOffset? moment) =>
        moment is null
            ? "an unrecorded time"
            : moment.Value.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
}
