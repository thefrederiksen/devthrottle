using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Governance;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workflows;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Tests for the weekly Outcome Ledger reporter (issue #1771, spine item 4). The claims that matter: verified
/// yield reads acceptance (accepted / terminal-not-waived), a waiver is excluded from the denominator, runs
/// bucket into delivered / aging-WIP / high-effort-no-outcome, and each row carries the token cost and
/// attention-burden (interventions + waiting-on-human seconds) joined from the spend, audit, and event tables
/// via the run's participants. Aggregated by run, never by person.
/// </summary>
public sealed class OutcomeLedgerReporterTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly WorkflowStore _workflows;
    private readonly WorkflowRunStore _runs;
    private readonly SessionSpendStore _spend;
    private readonly GovernanceEventLedger _events;
    private readonly GovernanceAuditLog _audit;
    private readonly AccountHostedAiSpendStore _hostedAi;
    private readonly OutcomeLedgerReporter _reporter;

    public OutcomeLedgerReporterTests()
    {
        _db = _h.Open();
        _workflows = new WorkflowStore(_db);
        _runs = new WorkflowRunStore(_db);
        _spend = new SessionSpendStore(_db);
        _events = new GovernanceEventLedger(_db);
        _audit = new GovernanceAuditLog(_db);
        _hostedAi = new AccountHostedAiSpendStore(_db);
        _reporter = new OutcomeLedgerReporter(_db);

        _workflows.CreateDraft(new WorkflowContentRequest
        {
            Id = "test-flow",
            Name = "Mission",
            Summary = "Run a mission.",
            Steps = new List<WorkflowStepDto> { new() { Name = "Do", Doer = "Worker", Done = "Merged." } },
            InstructionsMarkdown = "# Mission",
            OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>
            {
                new() { CriterionId = "merged", Description = "A pull request merged." },
            },
            AuthoredBy = "test",
        });
        _workflows.Publish("test-flow");
    }

    public void Dispose() => _h.Dispose();

    /// <summary>Create a run, attach a session, drive it to succeeded, and set its acceptance.</summary>
    private WorkflowRunDto SeedRun(string name, string sessionId, string acceptance, bool succeed = true)
    {
        var run = _runs.Create("test-flow", name);
        _runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            Status = WorkflowRunStatus.Active,
            AddParticipants = new List<WorkflowRunParticipantDto> { new() { SessionId = sessionId } },
        });
        _runs.Patch(run.Id, new PatchWorkflowRunRequest
        {
            Status = succeed ? WorkflowRunStatus.Succeeded : WorkflowRunStatus.Failed,
        });
        if (acceptance != WorkflowRunAcceptance.Pending)
            _runs.Patch(run.Id, new PatchWorkflowRunRequest
            {
                AcceptanceStatus = acceptance,
                AcceptedBy = "human:soren",
            });
        return run;
    }

    private void SeedSpend(string sessionId, long output, bool captured = true) =>
        _spend.Record(new RecordSessionSpendRequest
        {
            SessionId = sessionId,
            AgentKind = "claude",
            TokensCaptured = captured,
            OutputTokens = output,
            InputTokens = 10,
            BillingMode = SessionBillingMode.SubscriptionIncluded,
        });

    /// <summary>
    /// A STOP IS IN THE INTERVENTION CATEGORY AND IS NOT COUNTED HERE, and the two halves of that are
    /// tested together because separating them is how one of them would quietly be lost.
    ///
    /// The row must exist: the owner allowed any session to stop any other ON THE GROUND THAT IT IS
    /// AUDITED, so the trail has to hold it. The count must not move: this number means "how often did
    /// this session need a person", and the mission that added the stop makes one agent stopping another
    /// the ORDINARY case. Counting them would change what the number means underneath the people reading
    /// it, which is worse than not having it at all.
    /// </summary>
    [Fact]
    public void A_stop_is_recorded_in_the_trail_but_never_counted_as_an_intervention()
    {
        const string session = "sess-stopped";
        var run = SeedRun("Ship it", session, WorkflowRunAcceptance.Accepted);
        SeedSpend(session, output: 5000);

        // One real intervention - the agent needed a person.
        _audit.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = session, RunId = run.Id,
            Category = GovernanceAuditCategory.Intervention, EventType = GovernanceAuditEventType.Needed,
        });
        // And two stops, which are interventions in the plain sense and not in this number's sense.
        _audit.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = session, RunId = run.Id,
            Category = GovernanceAuditCategory.Intervention, EventType = GovernanceAuditEventType.Stopped,
            Actor = "session 9c41e7a2", Detail = "it was editing the wrong repository",
        });
        _audit.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = session, RunId = run.Id,
            Category = GovernanceAuditCategory.Intervention, EventType = GovernanceAuditEventType.Stopped,
            Actor = "session 9c41e7a2", Detail = "spawned into the wrong mode",
        });

        var report = _reporter.Build(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        // The count sees the one that means "a person was needed", and neither of the stops.
        var row = Assert.Single(report.Delivered);
        Assert.Equal(1, row.InterventionCount);

        // The trail still holds all three, stops included - this is the half that must not be lost.
        var trail = _audit.List(sessionId: session, category: GovernanceAuditCategory.Intervention);
        Assert.Equal(3, trail.Count);
        Assert.Equal(2, trail.Count(e => e.EventType == GovernanceAuditEventType.Stopped));
        Assert.Contains(trail, e => e.Detail == "it was editing the wrong repository");
    }

    [Fact]
    public void A_delivered_run_carries_its_yield_cost_and_attention()
    {
        const string session = "sess-deliver";
        var run = SeedRun("Ship it", session, WorkflowRunAcceptance.Accepted);
        SeedSpend(session, output: 5000);

        // One intervention and a 60-second wait on a human.
        _audit.Append(new AppendGovernanceAuditEventRequest
        {
            SessionId = session, RunId = run.Id,
            Category = GovernanceAuditCategory.Intervention, EventType = GovernanceAuditEventType.Needed,
        });
        var t = DateTime.UtcNow.AddMinutes(-30);
        _events.Append(new AppendGovernanceEventRequest
        {
            SubjectKind = GovernanceEventSubject.Session, SessionId = session,
            State = GovernanceEventState.WaitingOnHuman, OccurredUtc = t,
        });
        _events.Append(new AppendGovernanceEventRequest
        {
            SubjectKind = GovernanceEventSubject.Session, SessionId = session,
            State = GovernanceEventState.Active, OccurredUtc = t.AddSeconds(60),
        });
        _hostedAi.RecordObservedDebits(new List<ObservedAccountDebit>
        {
            new() { Kind = "debit", AmountMicros = 1234, TransactionCreatedUtc = DateTime.UtcNow.AddMinutes(-10) },
        });

        var report = _reporter.Build(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        Assert.Equal(1, report.VerifiedYield.AcceptedRuns);
        Assert.Equal(1, report.VerifiedYield.EffortRuns);
        var row = Assert.Single(report.Delivered);
        Assert.Equal(run.Id, row.RunId);
        Assert.Equal(5000, row.OutputTokens);
        Assert.True(row.TokenCoverageComplete);
        Assert.Equal(1, row.InterventionCount);
        Assert.Equal(60, row.WaitingOnHumanSeconds);
        Assert.Equal(1234, report.HostedAiServices.TotalMicros);
        Assert.Equal(1, report.SpendCoverage.SessionsWithTokens);
    }

    [Fact]
    public void Runs_bucket_by_outcome_and_a_waiver_leaves_the_denominator()
    {
        SeedRun("Delivered", "s-accepted", WorkflowRunAcceptance.Accepted);
        SeedRun("Rejected", "s-rejected", WorkflowRunAcceptance.Rejected);
        SeedRun("Blew up", "s-failed", WorkflowRunAcceptance.Pending, succeed: false);
        SeedRun("Excused", "s-waived", WorkflowRunAcceptance.Waived);
        SeedRun("Awaiting", "s-pending", WorkflowRunAcceptance.Pending); // succeeded, pending -> aging WIP

        var report = _reporter.Build(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        // Yield: accepted=1; denominator excludes the waiver -> 4 (accepted, rejected, failed, pending).
        Assert.Equal(1, report.VerifiedYield.AcceptedRuns);
        Assert.Equal(4, report.VerifiedYield.EffortRuns);
        Assert.Equal(1, report.VerifiedYield.WaivedRuns);
        Assert.Equal(1, report.VerifiedYield.RejectedRuns);

        Assert.Single(report.Delivered);
        Assert.Contains(report.Delivered, r => r.RunName == "Delivered");

        // High-effort/no-outcome: the rejected and the failed run.
        Assert.Equal(2, report.HighEffortNoOutcome.Count);
        Assert.Contains(report.HighEffortNoOutcome, r => r.RunName == "Rejected");
        Assert.Contains(report.HighEffortNoOutcome, r => r.RunName == "Blew up");

        // Aging WIP: the succeeded-but-pending run.
        Assert.Single(report.AgingWip);
        Assert.Equal("Awaiting", report.AgingWip[0].RunName);
    }

    [Fact]
    public void A_context_gauge_only_participant_marks_coverage_incomplete()
    {
        const string session = "sess-codex";
        SeedRun("Codex run", session, WorkflowRunAcceptance.Accepted);
        SeedSpend(session, output: 0, captured: false); // Codex: no additive token capture

        var report = _reporter.Build(DateTime.UtcNow.AddHours(-1), DateTime.UtcNow.AddHours(1));

        var row = Assert.Single(report.Delivered);
        Assert.False(row.TokenCoverageComplete);
        Assert.Equal(1, report.SpendCoverage.SessionsWithoutTokenCapture);
    }

    [Fact]
    public void An_inverted_window_is_rejected()
    {
        Assert.Throws<GovernanceValidationException>(
            () => _reporter.Build(DateTime.UtcNow, DateTime.UtcNow.AddHours(-1)));
    }
}
