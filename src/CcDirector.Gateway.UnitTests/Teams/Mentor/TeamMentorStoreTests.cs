using CcDirector.Gateway.Teams;
using CcDirector.Gateway.Teams.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Teams.Mentor;

/// <summary>The Mentor's storage (devthrottle_internal#2305) over a real migrated database.</summary>
public sealed class TeamMentorStoreTests : IDisposable
{
    private readonly MentorRig _rig = new();

    public void Dispose() => _rig.Dispose();

    private static MentorBlock Block(string person, params MentorQuote[] quotes) => new(
        MentorRig.Week.ToString(), person, "hard", "Worked on it.", null, "It went badly.", quotes, "Try this.",
        new DateTime(2026, 10, 5, 1, 0, 0, DateTimeKind.Utc), "fake-model");

    [Fact]
    public void SaveBlock_ThenBlocks_ReturnsItWordForWord_WithItsQuotes_AndRecordsWritten()
    {
        var quote = new MentorQuote("id-1", new DateTime(2026, 9, 29, 9, 14, 3, DateTimeKind.Utc), "fix the signup thing \"now\"\nplease");
        _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Rob, quote));

        var block = Assert.Single(_rig.Store.Blocks(_rig.Team, MentorRig.Week));

        Assert.Equal(Block(MentorRig.Rob, quote) with { Quotes = block.Quotes }, block);
        Assert.Equal(quote, Assert.Single(block.Quotes));
        Assert.Equal(DateTimeKind.Utc, block.WrittenAtUtc.Kind);
        Assert.Equal(MentorOutcomes.Written, _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Outcome);
    }

    [Fact]
    public void Blocks_AreOnlyThatWeeksAndThatTeams_InAFixedOrder()
    {
        _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Rob));
        _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Dana));
        _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Manager) with { Week = MentorRig.Week.Previous.ToString() });
        var other = _rig.Teams.CreateTeam(MentorRig.Owner, "Another team").Team!.Tenant;
        _rig.Store.SaveBlock(other, Block(MentorRig.Owner));

        Assert.Equal(new[] { MentorRig.Dana, MentorRig.Rob }, _rig.Store.Blocks(_rig.Team, MentorRig.Week).Select(b => b.PersonSubject));
        Assert.Equal(new[] { MentorRig.Owner }, _rig.Store.Blocks(other, MentorRig.Week).Select(b => b.PersonSubject));
    }

    [Fact]
    public void SaveBlock_TwiceForOnePersonAndWeek_Throws()
    {
        _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Rob));

        Assert.ThrowsAny<Exception>(() => _rig.Store.SaveBlock(_rig.Team, Block(MentorRig.Rob)));
    }

    [Fact]
    public void RecordOutcome_IsReadBack_WithItsReason()
    {
        _rig.Store.RecordOutcome(_rig.Team, MentorRig.Week, MentorRig.Rob, MentorOutcomes.Refused, "bad shape", _rig.Now);
        _rig.Store.RecordOutcome(_rig.Team, MentorRig.Week, MentorRig.Dana, MentorOutcomes.NoSessions, null, _rig.Now);

        Assert.Equal("bad shape", _rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Rob)!.Reason);
        Assert.Equal(2, _rig.Store.Outcomes(_rig.Team, MentorRig.Week).Count);
        Assert.Null(_rig.Store.OutcomeOf(_rig.Team, MentorRig.Week, MentorRig.Owner));
    }

    [Theory]
    [InlineData(MentorOutcomes.Written)]
    [InlineData("made-up")]
    public void RecordOutcome_NotAnOutcomeWithoutABlock_Throws(string outcome)
    {
        Assert.ThrowsAny<ArgumentException>(() => _rig.Store.RecordOutcome(_rig.Team, MentorRig.Week, MentorRig.Rob, outcome, null, _rig.Now));
    }

    [Fact]
    public void RecordRun_MarksTheWeek_OnlyForThatTeamAndWeek_AndASecondRunThrows()
    {
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week));

        _rig.Store.RecordRun(_rig.Team, MentorRig.Week, "UTC", 1, _rig.Now);

        Assert.True(_rig.Store.HasRun(_rig.Team, MentorRig.Week));
        Assert.False(_rig.Store.HasRun(_rig.Team, MentorRig.Week.Previous));
        Assert.ThrowsAny<Exception>(() => _rig.Store.RecordRun(_rig.Team, MentorRig.Week, "UTC", 1, _rig.Now));
    }
}
