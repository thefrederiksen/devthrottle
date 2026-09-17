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

        var facts = TurnVerdictOwnedSessions.For(roster, "architect", _ => null);

        Assert.NotNull(facts);
        Assert.Equal(1, facts!.Working);
        Assert.Equal(2, facts.Stopped);
        Assert.Equal(0, facts.NeedYou);
        Assert.Equal(1, facts.InTurn);
        Assert.Equal(T0.AddMinutes(7), facts.LastStoppedAtUtc);

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
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "solo", _ => null));
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "worker-stopped", _ => null));
        Assert.Null(TurnVerdictOwnedSessions.For(Roster(), "not-there", _ => null));
    }

    [Fact]
    public async Task AStopOnAnOwner_TellsTheJudgeTheSessionsItOwns()
    {
        const string reply = "I have handed the migration to the workers and am waiting on them.";
        var env = new FakeTurnVerdictEnvironment
        {
            Screen = () => TurnVerdictTestDoubles.Screen("architect", reply, "> "),
            Conversation = _ => TurnVerdictTestDoubles.Reply("run the migration", reply),
            Owned = sid => sid == "architect" ? new OwnedSessionsFacts(Working: 2, Stopped: 1, NeedYou: 0, InTurn: 2, LastStoppedAtUtc: T0) : null,
        };
        var service = new TurnVerdictService(env);

        var outcome = await service.StartTurnEnd(new TurnEndSignal("architect", "dir-1", TenantId.Local, T0, IsNewTurn: true));

        Assert.Equal(TurnVerdictOutcomeKind.Judged, outcome.Kind);
        Assert.Contains("Sessions this session owns: 2 working, 1 stopped, 0 need a person", Assert.Single(env.Prompts));
    }
}
