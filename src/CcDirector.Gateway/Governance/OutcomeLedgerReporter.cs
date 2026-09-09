using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Governance;

/// <summary>
/// The weekly Outcome Ledger reporter (issue #1771, spine item 4) - the first report that pays rent. It is a
/// READ-ONLY assembly over the governance spine already on the data layer: the workflow-run rows (#1779), the
/// event ledger (#1782), the session/account spend (#1789), and the audit trail (#1794). It writes nothing
/// and needs no table of its own.
///
/// It answers, for a window, the hero question - verified yield (accepted runs over runs that reached a
/// terminal outcome and were not waived) - and lays three buckets side by side, each row carrying its token
/// cost and its attention-burden: DELIVERED (accepted), AGING WIP (succeeded but still unaccepted), and
/// HIGH-EFFORT / NO-OUTCOME (failed, abandoned, or rejected). It aggregates by RUN, never by person, and
/// discloses its own coverage (token-capture gaps, account-level dollars kept separate) so a low number is
/// never mistaken for a good one.
/// </summary>
public sealed class OutcomeLedgerReporter
{
    private readonly GatewayDatabase _db;

    /// <summary>The per-bucket row cap, so one call cannot materialize an unbounded history.</summary>
    public const int MaxRowsPerBucket = 500;

    public OutcomeLedgerReporter(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>Assemble the Outcome Ledger for the window [<paramref name="sinceUtc"/>, <paramref name="untilUtc"/>).</summary>
    public OutcomeLedgerReportDto Build(DateTime sinceUtc, DateTime untilUtc)
    {
        var since = DateTime.SpecifyKind(sinceUtc, DateTimeKind.Utc);
        var until = DateTime.SpecifyKind(untilUtc, DateTimeKind.Utc);
        if (until <= since)
            throw new GovernanceValidationException("The report window's end must be after its start.");

        using var ctx = _db.CreateContext();

        // The window population: runs that reached a terminal outcome in the window. This is the denominator's
        // universe and the source of Delivered + High-effort/no-outcome.
        var completedInWindow = ctx.WorkflowRuns.AsNoTracking()
            .Where(r => r.CompletedUtc != null && r.CompletedUtc >= since && r.CompletedUtc < until)
            .ToList();

        // Aging WIP is NOT window-bounded on completion: it is the acceptance backlog - succeeded runs still
        // pending acceptance as of the window end, oldest first.
        var agingWip = ctx.WorkflowRuns.AsNoTracking()
            .Where(r => r.Status == WorkflowRunStatus.Succeeded &&
                        r.AcceptanceStatus == WorkflowRunAcceptance.Pending &&
                        r.CompletedUtc != null && r.CompletedUtc < until)
            .OrderBy(r => r.CompletedUtc)
            .Take(MaxRowsPerBucket)
            .ToList();

        // Every session that did work for any run in scope - the join key set for the cost/attention lookups.
        var runsInScope = completedInWindow.Concat(agingWip).ToList();
        var sessionIds = runsInScope
            .SelectMany(r => r.Participants.Select(p => p.SessionId))
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Load the cost and attention facts for those sessions in ONE query each (an IN list), then join in
        // memory - a weekly window is bounded, so this stays cheap and avoids an N+1 per run.
        var spendBySession = sessionIds.Count == 0
            ? new Dictionary<string, SessionSpendEntity>(StringComparer.Ordinal)
            : ctx.SessionSpend.AsNoTracking()
                .Where(s => sessionIds.Contains(s.SessionId))
                .ToList()
                .ToDictionary(s => s.SessionId, StringComparer.Ordinal);

        var interventionsBySession = sessionIds.Count == 0
            ? new Dictionary<string, int>(StringComparer.Ordinal)
            : ctx.GovernanceAuditEvents.AsNoTracking()
                // A STOP IS NOT COUNTED HERE, AND THE ROW STILL EXISTS. Stopping a session is recorded in
                // the intervention category - the audit trail the owner's ruling rests on has to hold it,
                // and reusing an existing "a human did X" type would have been a lie the moment one agent
                // stopped another. But this DERIVED number means "how often did this session need a
                // person", and the mission that added the stop makes agent-stops-agent the ORDINARY case,
                // so most stops involve no human at all. Counting them would quietly change what this
                // number means underneath the people reading it, which is worse than not having it.
                // "Stops per session" is a different number, and it should be computed on purpose.
                .Where(e => sessionIds.Contains(e.SessionId) &&
                            e.Category == GovernanceAuditCategory.Intervention &&
                            e.EventType != GovernanceAuditEventType.Stopped &&
                            e.OccurredUtc >= since && e.OccurredUtc < until)
                .ToList()
                .GroupBy(e => e.SessionId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);

        var waitingEvents = sessionIds.Count == 0
            ? new List<GovernanceEventEntity>()
            : ctx.GovernanceEvents.AsNoTracking()
                .Where(e => sessionIds.Contains(e.SessionId!) &&
                            e.SubjectKind == GovernanceEventSubject.Session &&
                            e.OccurredUtc >= since && e.OccurredUtc < until)
                .OrderBy(e => e.OccurredUtc)
                .ToList();
        var waitingSecondsBySession = WaitingOnHumanSecondsBySession(waitingEvents, until);

        var report = new OutcomeLedgerReportDto
        {
            SinceUtc = since,
            UntilUtc = until,
            VerifiedYield = Yield(completedInWindow),
            Delivered = completedInWindow
                .Where(r => r.AcceptanceStatus == WorkflowRunAcceptance.Accepted)
                .OrderByDescending(r => r.CompletedUtc)
                .Take(MaxRowsPerBucket)
                .Select(r => Row(r, spendBySession, interventionsBySession, waitingSecondsBySession))
                .ToList(),
            HighEffortNoOutcome = completedInWindow
                .Where(IsNoOutcome)
                .OrderByDescending(r => r.CompletedUtc)
                .Take(MaxRowsPerBucket)
                .Select(r => Row(r, spendBySession, interventionsBySession, waitingSecondsBySession))
                .ToList(),
            AgingWip = agingWip
                .Select(r => Row(r, spendBySession, interventionsBySession, waitingSecondsBySession))
                .ToList(),
            HostedAiServices = HostedAiSummary(ctx, since, until),
            SpendCoverage = Coverage(ctx, since, until),
        };

        FileLog.Write($"[OutcomeLedgerReporter] Build: window={since:o}..{until:o}, " +
                      $"yield={report.VerifiedYield.AcceptedRuns}/{report.VerifiedYield.EffortRuns}, " +
                      $"delivered={report.Delivered.Count}, agingWip={report.AgingWip.Count}, " +
                      $"noOutcome={report.HighEffortNoOutcome.Count}");
        return report;
    }

    /// <summary>A run ended without an accepted outcome: it failed or was abandoned, or its outcome was
    /// rejected. A succeeded-but-pending run is NOT here - that is aging WIP, not a value leak.</summary>
    private static bool IsNoOutcome(WorkflowRunEntity r) =>
        r.Status == WorkflowRunStatus.Failed ||
        r.Status == WorkflowRunStatus.Abandoned ||
        r.AcceptanceStatus == WorkflowRunAcceptance.Rejected;

    private static VerifiedYieldDto Yield(List<WorkflowRunEntity> completedInWindow) => new()
    {
        AcceptedRuns = completedInWindow.Count(r => r.AcceptanceStatus == WorkflowRunAcceptance.Accepted),
        // Denominator excludes waived (excused runs), so a waiver neither helps nor hurts the yield.
        EffortRuns = completedInWindow.Count(r => r.AcceptanceStatus != WorkflowRunAcceptance.Waived),
        WaivedRuns = completedInWindow.Count(r => r.AcceptanceStatus == WorkflowRunAcceptance.Waived),
        RejectedRuns = completedInWindow.Count(r => r.AcceptanceStatus == WorkflowRunAcceptance.Rejected),
    };

    private static OutcomeLedgerRowDto Row(
        WorkflowRunEntity run,
        IReadOnlyDictionary<string, SessionSpendEntity> spendBySession,
        IReadOnlyDictionary<string, int> interventionsBySession,
        IReadOnlyDictionary<string, long> waitingSecondsBySession)
    {
        var sessions = run.Participants
            .Select(p => p.SessionId)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        long output = 0, input = 0, interventions = 0, waiting = 0;
        // Coverage is complete only when EVERY participant session has a spend row that captured tokens; a
        // participant with no spend row, or a context-gauge-only driver, makes the token cost a floor.
        var coverageComplete = sessions.Count > 0;
        foreach (var sessionId in sessions)
        {
            if (spendBySession.TryGetValue(sessionId, out var spend) && spend.TokensCaptured)
            {
                output += spend.OutputTokens;
                input += spend.InputTokens;
            }
            else
            {
                coverageComplete = false;
            }
            if (interventionsBySession.TryGetValue(sessionId, out var count))
                interventions += count;
            if (waitingSecondsBySession.TryGetValue(sessionId, out var secs))
                waiting += secs;
        }

        return new OutcomeLedgerRowDto
        {
            RunId = run.Id,
            RunName = run.Name,
            WorkflowId = run.WorkflowId,
            RepoPath = run.RepoPath,
            Status = run.Status,
            AcceptanceStatus = run.AcceptanceStatus,
            CompletedUtc = run.CompletedUtc,
            ParticipantSessions = sessions.Count,
            OutputTokens = output,
            InputTokens = input,
            TokenCoverageComplete = coverageComplete,
            InterventionCount = (int)interventions,
            WaitingOnHumanSeconds = waiting,
        };
    }

    /// <summary>
    /// Sum the seconds each session spent in "waiting-on-human" over the window. For a session's ordered
    /// events, a waiting-on-human state runs until the next event (or the window end if it is the last), so
    /// the ledger's transitions give a real duration - the attention-burden a manager actually feels.
    /// </summary>
    private static Dictionary<string, long> WaitingOnHumanSecondsBySession(
        List<GovernanceEventEntity> orderedEvents, DateTime until)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var group in orderedEvents.Where(e => e.SessionId is not null)
                     .GroupBy(e => e.SessionId!, StringComparer.Ordinal))
        {
            var events = group.OrderBy(e => e.OccurredUtc).ToList();
            long seconds = 0;
            for (var i = 0; i < events.Count; i++)
            {
                if (events[i].State != GovernanceEventState.WaitingOnHuman)
                    continue;
                var end = i + 1 < events.Count ? events[i + 1].OccurredUtc : until;
                var span = end - events[i].OccurredUtc;
                if (span > TimeSpan.Zero)
                    seconds += (long)span.TotalSeconds;
            }
            if (seconds > 0)
                result[group.Key] = seconds;
        }
        return result;
    }

    private static AccountHostedAiSpendSummaryDto HostedAiSummary(
        GatewayDbContext ctx, DateTime since, DateTime until)
    {
        var rows = ctx.AccountHostedAiSpend.AsNoTracking()
            .Where(e => e.TransactionCreatedUtc >= since && e.TransactionCreatedUtc < until)
            .ToList();
        return new AccountHostedAiSpendSummaryDto
        {
            TotalMicros = rows.Sum(e => e.AmountMicros),
            DebitCount = rows.Count,
            SinceUtc = since,
            UntilUtc = until,
        };
    }

    private static SpendCoverageDto Coverage(GatewayDbContext ctx, DateTime since, DateTime until)
    {
        var rows = ctx.SessionSpend.AsNoTracking()
            .Where(s => s.LastObservedUtc >= since && s.LastObservedUtc < until)
            .ToList();
        return new SpendCoverageDto
        {
            Sessions = rows.Count,
            SessionsWithTokens = rows.Count(s => s.TokensCaptured),
            SessionsWithMeteredDollars = rows.Count(s => s.MeteredCostMicros.HasValue),
            SessionsSubscriptionIncluded =
                rows.Count(s => s.BillingMode == SessionBillingMode.SubscriptionIncluded),
            SessionsWithoutTokenCapture = rows.Count(s => !s.TokensCaptured),
        };
    }
}
