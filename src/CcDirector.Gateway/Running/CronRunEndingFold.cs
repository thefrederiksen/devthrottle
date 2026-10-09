using System.Globalization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;

namespace CcDirector.Gateway.Running;

/// <summary>
/// How each scheduled run's session ended, and whether a schedule's sessions close themselves (the owner,
/// 2026-10-09: too many scheduled sessions run and do not shut down on their own). The Gateway owns this ruling, so
/// the Schedule page shows the words it is given and never works out from a status what an ending means.
///
/// THE INPUTS. Who asked for a session to end is known only to the Gateway route that carried the request, so that
/// route writes it onto the run (<see cref="CronRunHistoryStore.StampEnding"/>). Everything else - still open, closed
/// on the machine, taken down with its Director, interrupted - comes from the session's work-history row, which the
/// Gateway already keeps for every session.
/// </summary>
public static class CronRunEndingFold
{
    // The two work-list outcomes that are not a failed fire (CronEngine.WorkListStatusSuffix).
    private const string WorkListStarted = "worklist-started";
    private const string WorkListEmpty = "worklist-empty";

    /// <summary>How many of a schedule's newest runs its run record covers.</summary>
    public const int SummaryRuns = 10;

    /// <summary>
    /// A session still open after this long is reported as LEFT open, not as running. Scheduled sessions that close
    /// themselves take about eight minutes (median of 151 runs, 6 to 9 October 2026), so an hour is far past an
    /// ordinary run without condemning a long one in its first minutes.
    /// </summary>
    public static readonly TimeSpan LeftOpenAfter = TimeSpan.FromHours(1);

    /// <summary>
    /// Stamp <see cref="CronRunRecord.Ending"/> and <see cref="CronRunRecord.EndingText"/> on each run, in place.
    /// <paramref name="endings"/> holds the work-history facts of the runs' sessions, as
    /// <see cref="SessionHistoryStore.EndingsOf"/> returns them.
    /// </summary>
    public static void Stamp(IEnumerable<CronRunRecord> runs, IReadOnlyDictionary<string, SessionEndingFact> endings, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(endings);
        foreach (var run in runs)
        {
            var (ending, endedUtc) = EndingOf(run, endings);
            run.Ending = ending;
            run.EndingText = TextOf(ending, run.FiredUtc, endedUtc, nowUtc);
        }
    }

    /// <summary>
    /// Which ending a stop or a deletion request records, from who made it. A session asking about itself closed
    /// itself - that is what <c>cc-devthrottle session done</c> is. Another session's key means another session.
    /// No session key means a person: the desktop, the Cockpit and the phone all call with the owner's own
    /// credentials, never a session's.
    /// </summary>
    public static string EndingForRequest(string? callingSessionId, string targetSessionId)
    {
        if (string.IsNullOrWhiteSpace(callingSessionId))
            return CronRunEndings.StoppedByYou;
        return string.Equals(callingSessionId.Trim(), targetSessionId.Trim(), StringComparison.OrdinalIgnoreCase)
            ? CronRunEndings.ClosedItself
            : CronRunEndings.StoppedBySession;
    }

    /// <summary>The ending of one run, and when it ended when that is known.</summary>
    public static (string Ending, DateTime? EndedUtc) EndingOf(CronRunRecord run, IReadOnlyDictionary<string, SessionEndingFact> endings)
    {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(endings);
        if (string.IsNullOrWhiteSpace(run.SessionId))
        {
            // A work-list fire records no session BY DESIGN (CronEngine: a drain starts many), so a drain that started -
            // or found its list empty, which starts nothing on purpose - is not a failed start. Every other work-list
            // outcome (no such list, no Director, the machine busy, already claimed) is a fire that did not run.
            return (run.InfraStatus is WorkListStarted or WorkListEmpty ? CronRunEndings.WorkList : CronRunEndings.NoSession, null);
        }

        endings.TryGetValue(run.SessionId, out var fact);
        // A session the history still holds open IS open, whatever was asked of it: a session flagged for deletion
        // stays listed until the reaper reaches it, and one whose deletion was cancelled carries on.
        if (fact is not null && fact.EndingKind is null)
            return (CronRunEndings.StillOpen, null);
        if (CronRunEndings.IsRecorded(run.TaskStatus))
            return (run.TaskStatus, fact?.EndedAtUtc);
        if (fact is null)
            return (CronRunEndings.Unknown, null);
        var ending = fact.EndingKind switch
        {
            // The agent exited, or the Director dismissed it as done: the session ended itself.
            SessionHistoryEndings.Finished => CronRunEndings.ClosedItself,
            SessionHistoryEndings.Closed => CronRunEndings.ClosedNotRecorded,
            SessionHistoryEndings.DirectorStopped => CronRunEndings.DirectorStopped,
            SessionHistoryEndings.Interrupted => CronRunEndings.Interrupted,
            _ => CronRunEndings.Unknown,
        };
        return (ending, fact.EndedAtUtc);
    }

    /// <summary>One run's ending in words, for the run table.</summary>
    public static string TextOf(string ending, DateTime firedUtc, DateTime? endedUtc, DateTime nowUtc)
    {
        var after = endedUtc is { } e && e >= firedUtc ? $" after {Duration(e - firedUtc)}" : "";
        return ending switch
        {
            CronRunEndings.NoSession => "did not start a session",
            CronRunEndings.WorkList => "a work-list drain - its sessions are not tracked here",
            CronRunEndings.StillOpen => nowUtc - firedUtc >= LeftOpenAfter
                ? $"left open - {Duration(nowUtc - firedUtc)} so far"
                : $"running - {Duration(nowUtc - firedUtc)} so far",
            CronRunEndings.ClosedItself => "closed itself" + after,
            CronRunEndings.StoppedByYou => "stopped by you" + after,
            CronRunEndings.StoppedBySession => "stopped by another session" + after,
            CronRunEndings.ClosedNotRecorded => "closed" + after + " - who closed it was not recorded",
            CronRunEndings.DirectorStopped => "ended when its Director shut down",
            CronRunEndings.Interrupted => "interrupted - its Director went silent",
            _ => "no record of how it ended",
        };
    }

    /// <summary>
    /// A schedule's run record from its newest runs (newest first), already stamped by <see cref="Stamp"/>. A run
    /// that closed itself counts for the schedule; one stopped by someone, left open past <see cref="LeftOpenAfter"/>,
    /// or that failed to start a session at all counts against it. Everything else - a run that is still young, one
    /// whose closer was not recorded, one that ended with its Director, a work-list drain - is left out, because it
    /// says nothing either way.
    /// </summary>
    public static CronRunRecordSummaryDto Summarize(IReadOnlyList<CronRunRecord> runs, IReadOnlyDictionary<string, SessionEndingFact> endings, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(endings);
        var considered = runs.Where(r => r.Ending is not null).Take(SummaryRuns).ToList();
        if (considered.Count == 0)
            return new CronRunRecordSummaryDto { Verdict = "none", Text = "no runs yet", Runs = 0 };

        var closedItself = considered.Where(r => r.Ending == CronRunEndings.ClosedItself).ToList();
        var byYou = considered.Count(r => r.Ending == CronRunEndings.StoppedByYou);
        var bySession = considered.Count(r => r.Ending == CronRunEndings.StoppedBySession);
        var leftOpen = considered.Count(r => r.Ending == CronRunEndings.StillOpen && nowUtc - r.FiredUtc >= LeftOpenAfter);
        var didNotStart = considered.Count(r => r.Ending == CronRunEndings.NoSession);
        var judged = closedItself.Count + byYou + bySession + leftOpen + didNotStart;

        if (judged == 0)
            return new CronRunRecordSummaryDto { Verdict = "none", Text = "nothing recorded yet", Runs = considered.Count };

        if (byYou + bySession + leftOpen + didNotStart == 0)
        {
            var minutes = closedItself
                .Select(r => endings.TryGetValue(r.SessionId!, out var f) && f.EndedAtUtc is { } e && e >= r.FiredUtc
                    ? (double?)(e - r.FiredUtc).TotalMinutes
                    : null)
                .Where(m => m is not null)
                .Select(m => m!.Value)
                .OrderBy(m => m)
                .ToList();
            var typical = minutes.Count == 0 ? "" : $", about {Duration(TimeSpan.FromMinutes(minutes[minutes.Count / 2]))}";
            return new CronRunRecordSummaryDto
            {
                Verdict = "ok",
                Text = $"closes itself - {closedItself.Count} of {judged}{typical}",
                Runs = considered.Count,
            };
        }

        var parts = new List<string>();
        if (byYou > 0) parts.Add($"{byYou} stopped by you");
        if (bySession > 0) parts.Add($"{bySession} stopped by another session");
        if (leftOpen > 0) parts.Add($"{leftOpen} left open");
        if (didNotStart > 0) parts.Add($"{didNotStart} did not start");
        return new CronRunRecordSummaryDto
        {
            Verdict = "bad",
            Text = $"{closedItself.Count} of {judged} closed itself - {string.Join(", ", parts)}",
            Runs = considered.Count,
        };
    }

    /// <summary>A span as "6 min", "2h 05m" or "3d 4h".</summary>
    public static string Duration(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 60)
            return $"{Math.Max(1, (int)Math.Round(span.TotalMinutes))} min";
        if (span.TotalHours < 24)
            return string.Format(CultureInfo.InvariantCulture, "{0}h {1:00}m", (int)span.TotalHours, span.Minutes);
        return string.Format(CultureInfo.InvariantCulture, "{0}d {1}h", (int)span.TotalDays, span.Hours);
    }
}
