using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// THE DECISION DOES NOT TRAVEL; THE ANSWER DOES (the Wingman-on-every-turn mission, slice F).
///
/// <see cref="SessionDto.SnoozeEndedNothingNew"/> is a fact of the FOLD. The colour and the label it produces
/// carry the whole of its meaning to every client, and THE CLIENT IS DUMB: a client handed the decision itself
/// would eventually branch on it, and that is how a second colour authority is born - the exact failure that put
/// a red "Voice unavailable" badge next to a button that could never work. The inspector raised it as an
/// unnecessary new wire surface on pull request 2899 and the Architect promoted it.
///
/// A COMMENT SAYING "internal" WOULD NOT HOLD, so this is a test. It serializes the row the way the roster does
/// and looks for the name.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class SnoozeEndedNothingNewStaysOffTheWireTests
{
    /// <summary>A row as the fold leaves it after a snooze came back with nothing new: the decision set, and the
    /// colour and words it produced already stamped.</summary>
    private static SessionDto FoldedRow() => new()
    {
        SessionId = "s1",
        DirectorId = "dir-1",
        Agent = "TestAgent",
        RepoPath = "repo",
        ActivityState = "WaitingForInput",
        Status = "Running",
        SnoozeExpired = true,
        SnoozeEndedNothingNew = true,
        EffectiveColor = SessionOrdering.SnoozeEndedNothingNewColor,
        StateLabel = SessionOrdering.SnoozeEndedNothingNewLabel,
        TriageBucket = "active",
    };

    /// <summary>Both shapes a response can be written in: the minimal-api web defaults the roster serializes
    /// with, and the plain defaults, so the exclusion cannot be an accident of a naming policy.</summary>
    public static TheoryData<string> BothWireShapes() => new()
    {
        "web",
        "plain",
    };

    private static string Serialize(SessionDto row, string shape) => JsonSerializer.Serialize(
        row,
        shape == "web" ? new JsonSerializerOptions(JsonSerializerDefaults.Web) : new JsonSerializerOptions());

    [Theory]
    [MemberData(nameof(BothWireShapes))]
    public void TheRosterResponse_DoesNotCarryTheDecision(string shape)
    {
        var json = Serialize(FoldedRow(), shape);

        Assert.DoesNotContain("snoozeEndedNothingNew", json, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [MemberData(nameof(BothWireShapes))]
    public void TheRosterResponse_DoesCarryTheAnswerTheDecisionProduced(string shape)
    {
        // The other half, and it is what makes the exclusion safe rather than merely quiet: the clients lose
        // nothing, because everything they render is already on the row.
        var json = Serialize(FoldedRow(), shape);

        Assert.Contains(SessionOrdering.SnoozeEndedNothingNewColor, json, StringComparison.Ordinal);
        Assert.Contains(SessionOrdering.SnoozeEndedNothingNewLabel, json, StringComparison.Ordinal);
        Assert.Contains("\"active\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowComingBackOffTheWire_CarriesNoDecisionOfItsOwn()
    {
        // The fold assigns this field on EVERY row in both directions, so a round trip must not be able to hand a
        // later fold somebody else's answer to start from.
        var json = Serialize(FoldedRow(), "web");

        var back = JsonSerializer.Deserialize<SessionDto>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(back);
        Assert.False(back!.SnoozeEndedNothingNew);
        Assert.Equal(SessionOrdering.SnoozeEndedNothingNewColor, back.EffectiveColor);
    }
}
