using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The owned sessions of a session, read over a roster whose roles arrive unresolved (owner ruling, 2026-09-15).
/// The counts must be the row's own crew line counts, at every level, the last activity must be the latest
/// across them, and a stop on an owner must tell the judge those counts.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictOwnedSessionsTests
{
    private static readonly DateTime T0 = new(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

    private static SessionDto Session(string sid, string state, string? owner = null, DateTime? lastActivity = null) => new()
    {
        SessionId = sid,
        Name = sid,
        Agent = "ClaudeCode",
        ActivityState = state,
        IsControlled = owner is not null,
        ControllerSessionId = owner,
        LastActivityAt = lastActivity,
        CreatedAt = T0.AddHours(-1),
    };

    private static List<(string DirectorId, SessionDto Session)> Roster() => new()
    {
        ("dir-1", Session("architect", "WaitingForInput")),
        ("dir-1", Session("worker-working", "Working", owner: "architect", lastActivity: T0.AddMinutes(1))),
        ("dir-1", Session("worker-stopped", "WaitingForInput", owner: "architect", lastActivity: T0.AddMinutes(2))),
        ("dir-2", Session("grandchild", "WaitingForInput", owner: "worker-working", lastActivity: T0.AddMinutes(7))),
        ("dir-1", Session("solo", "WaitingForInput", lastActivity: T0)),
    };

    [Fact]
    public void For_AnOwner_CountsEveryLevelAsTheCrewLineDoes_AndTakesTheLatestActivity()
    {
        var roster = Roster();

        var facts = TurnVerdictOwnedSessions.For(roster, "architect");

        Assert.NotNull(facts);
        Assert.Equal(1, facts!.Working);
        Assert.Equal(2, facts.Stopped);
        Assert.Equal(0, facts.NeedYou);
        Assert.Equal(T0.AddMinutes(7), facts.LastActivityAtUtc);

        // The same numbers the row's crew line prints, from the same fold.
        var sessions = roster.Select(r => r.Session).ToList();
        var tree = SessionTree.Build(sessions);
        var root = sessions.Single(s => s.SessionId == "architect");
        var crew = SessionTree.SummarizeCrew(root, SessionTree.DescendantsOf(tree, root).Select(d => d.Session));
        Assert.Equal((crew.Working, crew.Stopped, crew.NeedsYou), (facts.Working, facts.Stopped, facts.NeedYou));
    }

    [Fact]
    public void For_ASessionThatOwnsNothing_OrIsNotInTheRoster_IsNull()
    {
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "solo"));
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "worker-stopped"));
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "not-there"));
    }

    [Fact]
    public async Task AStopOnAnOwner_DoesNotTellTheJudgeTheSessionsItOwns()
    {
        const string reply = "I have handed the migration to the workers and am waiting on them.";
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => TurnVerdictTestDoubles.Screen("architect", reply, "> "),
            Conversation = _ => TurnVerdictTestDoubles.Reply("run the migration", reply),
            Owned = sid => sid == "architect" ? new OwnedSessionsFacts(Working: 2, Live: 3, Stopped: 1, NeedYou: 0, LastActivityAtUtc: T0) : null,
        };
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(new TurnEndSignal("architect", "dir-1", TenantId.Local, T0, IsNewTurn: true));

        // CONTRACT v4 (design v2): the owned-sessions line is NOT fed to Call A. The phase 1 data says owning working
        // sessions points the other way - a manager waiting on the owner while its workers run was labelled needs-you
        // 56 times of 72 - so it is not given to the model until it is measured.
        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        var prompt = Assert.Single(env.Prompts);
        Assert.DoesNotContain("Sessions this session owns", prompt);
        Assert.DoesNotContain("2 working", prompt);
    }
}
