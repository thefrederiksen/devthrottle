using CcDirector.Gateway.Throttle;
using Xunit;
using static CcDirector.Gateway.Throttle.ThrottleDefinition;

namespace CcDirector.Gateway.UnitTests.Throttle;

/// <summary>
/// The Systems split of Your Throttle (owner's ask, 2026-10-15): every counted turn lands on exactly one operating
/// system and one machine, a session whose system is not known is its own Unknown row rather than dropped or
/// guessed, agent-driven turns are attributed beside the human ones and never added to them, and every share and
/// card is finished here. These run the pure fold.
/// </summary>
public sealed class ThrottleSystemsTests
{
    private static readonly DateTime From = new(2026, 10, 8, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime To = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private static LedgerSubmission Turn(string session, string origin = "voice/desktop")
        => new(From.AddHours(1), session, "ClaudeCode", origin, null);

    private static LedgerSubmission Driven(string session)
        => new(From.AddHours(1), session, "ClaudeCode", null, AgentSendSource);

    private static SessionFacts On(string machine, string? os)
        => new(null, null, "human", machine, os);

    [Fact]
    public void Fold_TurnsOnThreeSystemsAndAnUnknown_RowsAddUpToTheHeadlineWithMachinesUnderTheirSystem()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["w1"] = On("SOREN_NORTH", "windows"),
            ["w2"] = On("SORENLAPTOP", "windows"),
            ["l1"] = On("devlinux", "linux"),
            ["m1"] = On("mac-mini", "macos"),
            ["u1"] = On("old-box", null),
        };
        var rows = new List<LedgerSubmission>
        {
            Turn("w1"), Turn("w1"), Turn("w1", "typed/cockpit"), Turn("w2"),
            Turn("l1"), Turn("l1", "typed/desktop"),
            Turn("m1", "typed/desktop"),
            Turn("u1"),
        };

        var figure = Fold(rows, From, To, sessions);

        Assert.Equal(8, figure.Turns);
        Assert.Equal(figure.Turns, figure.Systems.Sum(s => s.Turns));
        // Mac and Unknown tie at one turn each; a known system ranks before Unknown on a tie.
        Assert.Equal(new[] { "windows", "linux", "macos", "" }, figure.Systems.Select(s => s.Os));
        Assert.Equal(new[] { "Windows", "Linux", "Mac", "Unknown" }, figure.Systems.Select(s => s.OsName));

        var windows = figure.Systems[0];
        Assert.Equal(4, windows.Turns);
        Assert.Equal(3, windows.VoiceTurns);
        Assert.Equal(2, windows.Sessions);
        Assert.Equal(50, windows.TurnPercent);
        Assert.Equal(75, windows.VoicePercent);
        Assert.Equal(new[] { "SOREN_NORTH", "SORENLAPTOP" }, windows.Machines.Select(m => m.MachineName));
        Assert.Equal(3, windows.Machines[0].Turns);
        // A machine's share is of the WHOLE figure, the same scale as its system row: 3 of 8 rounds to 38.
        Assert.Equal(38, windows.Machines[0].TurnPercent);
    }

    [Fact]
    public void Fold_SessionHistoryHoldsNoRow_LandsOnUnknownWithMachineNotRecorded()
    {
        var figure = ThrottleDefinition.Fold(new[] { Turn("ghost") }, From, To,
            new Dictionary<string, SessionFacts> { ["ghost"] = new(null, null, "human") });

        var unknown = Assert.Single(figure.Systems);
        Assert.Equal("", unknown.Os);
        Assert.Equal("Unknown", unknown.OsName);
        var machine = Assert.Single(unknown.Machines);
        Assert.Equal("", machine.MachineName);
        Assert.Equal("(machine not recorded)", machine.DisplayName);
        Assert.Equal(1, figure.SystemsSummary.UnknownTurns);
        Assert.Equal(0, figure.SystemsSummary.SystemCount);
        Assert.Null(figure.SystemsSummary.TopSystemName);
    }

    [Fact]
    public void Fold_AReportedSystemThatIsNotOnTheList_IsUnknownNotStoredAsWritten()
    {
        var figure = Fold(new[] { Turn("s") }, From, To, new Dictionary<string, SessionFacts> { ["s"] = On("box", "TempleOS") });

        Assert.Equal("", Assert.Single(figure.Systems).Os);
    }

    [Fact]
    public void Fold_AgentDrivenTurns_AreAttributedToTheirSystemAndMachine_AndNeverAddedToTurns()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["w1"] = On("SOREN_NORTH", "windows"),
            ["l1"] = On("devlinux", "linux"),
        };
        var rows = new[] { Turn("l1"), Driven("w1"), Driven("w1"), Driven("w1"), Driven("l1") };

        var figure = Fold(rows, From, To, sessions);

        var windows = figure.Systems.Single(s => s.Os == "windows");
        Assert.Equal(0, windows.Turns);
        Assert.Equal(3, windows.AgentDrivenTurns);
        Assert.Equal(75, windows.AgentDrivenPercent);
        Assert.Equal(3, Assert.Single(windows.Machines).AgentDrivenTurns);
        Assert.Equal(1, figure.Systems.Sum(s => s.Turns));
        Assert.Equal("Windows", figure.SystemsSummary.TopAgentDrivenSystemName);
        Assert.Equal(75, figure.SystemsSummary.TopAgentDrivenPercent);
        Assert.Equal(4, figure.SystemsSummary.AgentDrivenTurns);
    }

    [Fact]
    public void Fold_SessionAnotherSessionStarted_DrivesNoHumanTurnIntoTheSplit()
    {
        var figure = ThrottleDefinition.Fold(new[] { Turn("worker") }, From, To,
            new Dictionary<string, SessionFacts> { ["worker"] = new(null, null, "agent", "devlinux", "linux") });

        Assert.Empty(figure.Systems);
        Assert.False(figure.SystemsSummary.HasData);
    }

    [Fact]
    public void SystemsSummary_FinishesTheCards_TopSystemBusiestMachineAndCounts()
    {
        var sessions = new Dictionary<string, SessionFacts>
        {
            ["w1"] = On("SOREN_NORTH", "windows"),
            ["w2"] = On("SORENLAPTOP", "windows"),
            ["l1"] = On("devlinux", "linux"),
        };
        var rows = new[] { Turn("w1"), Turn("w1"), Turn("w2"), Turn("w2"), Turn("l1"), Turn("l1"), Turn("l1") };

        var summary = Fold(rows, From, To, sessions).SystemsSummary;

        Assert.True(summary.HasData);
        Assert.Equal(2, summary.SystemCount);
        Assert.Equal(3, summary.MachineCount);
        Assert.Equal(7, summary.TotalTurns);
        Assert.Equal(3, summary.TotalSessions);
        // Windows leads as a system (4 of 7, two machines) while the busiest single machine is the Linux one (3).
        Assert.Equal("Windows", summary.TopSystemName);
        Assert.Equal(57, summary.TopPercent);
        Assert.Equal("devlinux", summary.TopMachineName);
        Assert.Equal(3, summary.TopMachineTurns);
        Assert.Equal("Linux", summary.TopMachineOsName);
        Assert.Equal(0, summary.UnknownTurns);
    }

    [Fact]
    public void Fold_NothingCounted_SystemsSummaryHasNoData()
    {
        var summary = Fold(Array.Empty<LedgerSubmission>(), From, To, new Dictionary<string, SessionFacts>()).SystemsSummary;

        Assert.False(summary.HasData);
        Assert.Null(summary.TopPercent);
        Assert.Null(summary.TopMachineName);
    }
}
