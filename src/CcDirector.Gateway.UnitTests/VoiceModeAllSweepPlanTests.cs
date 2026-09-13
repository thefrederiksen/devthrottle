using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The decision half of the voice-mode sweep. Voice mode is a STANDING intent - a tenant that turned it on
/// wants every one of its sessions narrating, including the ones that did not exist when the switch was
/// thrown. <see cref="VoiceModeAllSweep.Plan"/> answers which sessions still need switching on.
///
/// Both directions of the guard matter and both are proved here: a tenant that is NOT in voice mode must
/// yield nothing at all (the sweep must never put a fleet on voice that did not ask), and a tenant that IS in
/// voice mode must yield exactly the sessions still off (so a steady fleet produces no traffic at all).
/// </summary>
public sealed class VoiceModeAllSweepPlanTests
{
    private static (string, SessionDto) Row(string directorId, string sid) =>
        (directorId, new SessionDto { SessionId = sid, Name = sid, Status = "WaitingForInput" });

    private static Func<string, bool> On(params string[] sids)
    {
        var set = new HashSet<string>(sids, StringComparer.Ordinal);
        return set.Contains;
    }

    // A row whose supervision can actually be resolved: the session, plus its supervisor on the same roster.
    // Both are needed - "Worker" means CONTROLLED AND THE CONTROLLER IS ALIVE, and the resolver can only see
    // that if the controller is in the roster it is given.
    private static (string, SessionDto) SupervisedRow(string directorId, string sid, string supervisorId) =>
        (directorId, new SessionDto
        {
            SessionId = sid, Name = sid, Status = "WaitingForInput", ActivityState = "WaitingForInput",
            IsControlled = true, ControllerSessionId = supervisorId,
        });

    private static (string, SessionDto) LiveSupervisorRow(string directorId, string sid) =>
        (directorId, new SessionDto { SessionId = sid, Name = sid, Status = "Working", ActivityState = "Working" });

    [Fact]
    public void PlanOff_clearsASupervisedSessionThatWasAlreadyMarkedForVoice()
    {
        // THE CASE THE ON-DIRECTION CANNOT REACH. Voice marking is PERSISTED, so a worker enrolled before the
        // supervised rule existed stays enrolled across restarts and goes on being narrated at the owner for
        // ever. Without this pass the whole change would read as "no effect" on exactly the fleet that
        // prompted it - the sessions already running.
        var roster = new[]
        {
            LiveSupervisorRow("d1", "the-manager"),
            SupervisedRow("d1", "the-worker", "the-manager"),
        };

        var off = VoiceModeAllSweep.PlanOff(roster, On("the-manager", "the-worker"));

        Assert.Equal(new[] { ("d1", "the-worker") }, off);
    }

    [Fact]
    public void PlanOff_neverTouchesTheOwnersOwnSessions()
    {
        // The negative control that matters most. This pass turns voice OFF, so an over-broad match here
        // would silently stop narrating the sessions the owner actually listens to - a failure he would
        // experience as "voice mode is broken" with nothing on any screen to explain it.
        var roster = new[]
        {
            LiveSupervisorRow("d1", "a-manager"),
            Row("d1", "a-standalone"),
        };

        Assert.Empty(VoiceModeAllSweep.PlanOff(roster, On("a-manager", "a-standalone")));
    }

    [Fact]
    public void PlanOff_ignoresASupervisedSessionThatWasNeverOnVoice()
    {
        // A steady fleet must produce no traffic: there is nothing to switch off on a session that was never
        // switched on, and sending the command anyway would put a tunnel round-trip per worker per sweep.
        var roster = new[]
        {
            LiveSupervisorRow("d1", "the-manager"),
            SupervisedRow("d1", "the-worker", "the-manager"),
        };

        Assert.Empty(VoiceModeAllSweep.PlanOff(roster, On()));
    }

    [Fact]
    public void PlanOff_leavesAnOrphanedWorkerOnVoice_itIsTheOwnersAgain()
    {
        // Its supervisor is gone, so it is not supervised, so it is back to being the owner's - including
        // being read aloud. The same escape hatch the colour fold has, asserted on this path too.
        var roster = new[] { SupervisedRow("d1", "stranded", "a-supervisor-not-on-this-roster") };

        Assert.Empty(VoiceModeAllSweep.PlanOff(roster, On("stranded")));
    }

    [Fact]
    public void PlanOff_isNotGatedOnTheFleetVoiceSwitch()
    {
        // PlanOff takes no voiceModeOn argument AT ALL, and that is the point: the fleet switch answers "does
        // this tenant want voice?", while this answers "is this session the owner's to be read?". A tenant who
        // has since turned voice mode off would otherwise keep a set of marked workers that nothing ever
        // clears. This test exists to make the missing parameter deliberate rather than an omission somebody
        // later "fixes".
        var roster = new[]
        {
            LiveSupervisorRow("d1", "the-manager"),
            SupervisedRow("d1", "the-worker", "the-manager"),
        };

        Assert.Equal(new[] { ("d1", "the-worker") }, VoiceModeAllSweep.PlanOff(roster, On("the-worker")));
    }

    [Fact]
    public void Plan_neverSwitchesOnASupervisedSession_theOwnerIsNotItsAudience()
    {
        // Owner's ruling, 2026-09-02. Voice mode reads a finished turn ALOUD TO THE OWNER, and a worker's
        // turn is not his to hear. Before this, "voice mode for all" meant literally all: the roster receded
        // the row and the wingman narrated it anyway, because the suppression lived in the colour and
        // nowhere else.
        var roster = new[]
        {
            LiveSupervisorRow("d1", "the-manager"),
            SupervisedRow("d1", "the-worker", "the-manager"),
        };

        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());

        Assert.Equal(new[] { ("d1", "the-manager") }, plan);
    }

    [Fact]
    public void Plan_switchesOnAScheduledRun_TheReversalOf13September2026()
    {
        // REVERSED, AND THE OLD REASONING IS KEPT because it was the whole case for the silence: "nobody is
        // at a keyboard for a cron firing, so there is nobody for the wingman to read it to."
        //
        // That is backwards. Nobody at a keyboard means nobody but the OWNER, and being read aloud is one of
        // the three things human-facing means. A cron run that has finished and needs him is exactly the row
        // he is least likely to notice on a roster, so it is the one that most needs saying out loud.
        var cron = new SessionDto
        {
            SessionId = "cron", Name = "cron", Status = "WaitingForInput", ActivityState = "WaitingForInput",
            OriginKind = "schedule",
        };
        var roster = new[] { ("d1", cron), Row("d1", "ordinary") };

        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());

        Assert.Equal(new[] { ("d1", "cron"), ("d1", "ordinary") }, plan);
    }

    [Fact]
    public void Plan_switchesOnAnArchitect_becauseItIsTheSeatTheOwnerTalksTo()
    {
        // The owner's ruling, 2026-09-06: "the architect is always the session i talk to", "the architect
        // should push to me". Being read aloud is one of the three things being human-facing means, and it
        // is the one that surfaced the defect: an Architect asked to make itself narratable could not,
        // because its own role was what the wingman was silencing.
        //
        // The roster is deliberately MIXED so the assertion is not just "something came back": the worker
        // beside it, whose supervisor is alive on the same roster, is still skipped - which proves this is
        // the Architect changing side rather than the supervised check having stopped working altogether.
        //
        // That control used to be a scheduled run. It cannot be one any more: since 2026-09-13 a cron firing
        // has no supervisor and is therefore human-facing like anything else nobody is holding, so it would
        // come back in the plan and prove nothing. A negative control has to be something the rule still
        // genuinely silences.
        var architect = new SessionDto
        {
            SessionId = "arch", Name = "arch", Status = "WaitingForInput", ActivityState = "WaitingForInput",
            ExplicitRole = SessionRoles.Architect,
        };
        var roster = new[]
        {
            ("d1", architect),
            LiveSupervisorRow("d1", "the-manager"),
            SupervisedRow("d1", "the-worker", "the-manager"),
        };

        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());

        Assert.Equal(new[] { ("d1", "arch"), ("d1", "the-manager") }, plan);
    }

    [Fact]
    public void PlanOff_neverSwitchesAnArchitectBackOff_soTheEarlierRuleUnwindsItself()
    {
        // The off-direction is what un-enrolled the seats the 2026-09-02 rule silenced, and it must not now
        // undo the 2026-09-06 ruling by turning an Architect back off the moment the on-direction enrols it.
        // Without this the two sweeps would fight over the same session on every tick.
        var architect = new SessionDto
        {
            SessionId = "arch", Name = "arch", Status = "WaitingForInput", ActivityState = "WaitingForInput",
            ExplicitRole = SessionRoles.Architect,
        };

        Assert.Empty(VoiceModeAllSweep.PlanOff(new[] { ("d1", architect) }, On("arch")));
    }

    [Fact]
    public void Plan_resolvesTheRolesItself_soTheCheckIsNotBlind()
    {
        // THE POINT OF THIS TEST IS THE INPUT, NOT THE OUTPUT. Every SessionRole below is deliberately left
        // NULL, which is exactly how these sessions arrive in real life: PushedSessionStore nulls the role at
        // ingest on purpose, so only the Gateway can decide one. A supervision check that merely READ the
        // field would therefore see null for every session on the fleet, answer "not supervised" every time,
        // and narrate exactly as before - passing because it is blind. This proves Plan resolves first.
        var supervisor = new SessionDto
        {
            SessionId = "boss", Name = "boss", Status = "Working", ActivityState = "Working",
        };
        var worker = new SessionDto
        {
            SessionId = "hand", Name = "hand", Status = "WaitingForInput", ActivityState = "WaitingForInput",
            IsControlled = true, ControllerSessionId = "boss",
        };
        Assert.Null(supervisor.SessionRole);
        Assert.Null(worker.SessionRole);

        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, new[] { ("d1", supervisor), ("d1", worker) }, On());

        Assert.Equal(new[] { ("d1", "boss") }, plan);
        Assert.Equal(SessionRoles.Worker, worker.SessionRole);       // resolved, in place
        Assert.Equal(SessionRoles.Manager, supervisor.SessionRole);  // and it became a Manager by having one
    }

    [Fact]
    public void Plan_stillSwitchesOnAWorkerWhoseSupervisorIsGone_theEscapeHatch()
    {
        // The orphan is NOT supervised - there is nobody left to report to - so it reaches the owner by every
        // channel, this one included. The negative control for the three tests above: if this ever goes
        // quiet, the rule has stopped routing attention and started losing it.
        var roster = new[] { SupervisedRow("d1", "stranded", "a-supervisor-not-on-this-roster") };

        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());

        Assert.Equal(new[] { ("d1", "stranded") }, plan);
    }

    [Fact]
    public void Plan_whenVoiceModeIsOff_yieldsNothing_evenWithSessionsThatAreNotOnVoice()
    {
        var roster = new[] { Row("d1", "a"), Row("d1", "b") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: false, roster, On());
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_whenVoiceModeIsOn_namesOnlyTheSessionsNotYetOnVoice()
    {
        var roster = new[] { Row("d1", "already"), Row("d2", "needsIt") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On("already"));
        Assert.Equal(new[] { ("d2", "needsIt") }, plan);
    }

    [Fact]
    public void Plan_whenEverySessionIsAlreadyOnVoice_yieldsNothing_soASteadyFleetIsSilent()
    {
        var roster = new[] { Row("d1", "a"), Row("d1", "b") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On("a", "b"));
        Assert.Empty(plan);
    }

    [Fact]
    public void Plan_carriesTheOwningDirector_soTheSweepKnowsWhereToSendTheCommand()
    {
        var roster = new[] { Row("machine-a", "one"), Row("machine-b", "two") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Equal(new[] { ("machine-a", "one"), ("machine-b", "two") }, plan);
    }

    [Fact]
    public void Plan_switchesADuplicatedRosterEntryOnce_neverTwice()
    {
        var roster = new[] { Row("d1", "same"), Row("d1", "same") };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Single(plan);
    }

    [Fact]
    public void Plan_ignoresRowsWithNoSessionIdOrNoDirector_thereIsNothingToSwitchOn()
    {
        var roster = new[]
        {
            ("d1", new SessionDto { SessionId = "", Name = "blank" }),
            ("", new SessionDto { SessionId = "orphan", Name = "no director" }),
            Row("d1", "real"),
        };
        var plan = VoiceModeAllSweep.Plan(voiceModeOn: true, roster, On());
        Assert.Equal(new[] { ("d1", "real") }, plan);
    }
}
