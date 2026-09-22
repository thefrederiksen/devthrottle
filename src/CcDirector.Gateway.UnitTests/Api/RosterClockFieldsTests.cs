using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Api;

/// <summary>
/// Traffic optimization, phase 2: the roster without its clock. The opt-in form leaves out exactly the two fields
/// the Gateway recomputes from its clock on every read, so two reads of an unchanged roster are byte-identical -
/// and therefore a 304 - while every other field is carried unchanged.
///
/// Revert-proof: stop removing <c>idleSeconds</c> or <c>lastSeenAgeSeconds</c> and the two-reads-are-identical test
/// goes red; remove <c>idleSeconds</c> from a session without <c>lastActivityAt</c> and the keep test goes red.
/// </summary>
public sealed class RosterClockFieldsTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Pushed = new(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);

    private static SessionDto Session(string id, DateTime? lastActivity, double idle) => new()
    {
        SessionId = id,
        Agent = "claude",
        StatusColor = "blue",
        LastActivityAt = lastActivity,
        IdleSeconds = idle,
        CreatedAt = Pushed.AddHours(-1),
    };

    // The envelope as the Gateway builds it at `now`: the clock fields computed the way the Gateway computes them.
    private static object Envelope(DateTime now) => new
    {
        sessions = new List<SessionDto>
        {
            Session("a", Pushed, (now - Pushed).TotalSeconds),
            Session("b", Pushed.AddSeconds(-30), (now - Pushed.AddSeconds(-30)).TotalSeconds),
        },
        machineErrors = new List<MachineErrorDto>(),
        directors = new List<DirectorReachabilityDto>
        {
            new() { DirectorId = "d1", MachineName = "m1", State = "online", LastSeenUtc = Pushed, LastSeenAgeSeconds = (now - Pushed).TotalSeconds },
            new() { DirectorId = "d2", MachineName = "m2", State = "offline", LastSeenUtc = null, LastSeenAgeSeconds = null },
        },
        unreachableBanner = (string?)null,
        rosterComplete = true,
        rosterIncompleteReason = (string?)null,
        rosterStaleAnswerCaution = (string?)null,
    };

    private static byte[] Bytes(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, Web);

    [Fact]
    public void TwoReadsOfAnUnchangedRoster_SecondsApart_AreByteIdentical()
    {
        var first = RosterClockFields.Strip(Envelope(Pushed.AddSeconds(4.1)), Web);
        var second = RosterClockFields.Strip(Envelope(Pushed.AddSeconds(6.3)), Web);

        Assert.Equal(Bytes(first), Bytes(second));
        Assert.Equal(ConditionalJson.TagFor(Bytes(first)), ConditionalJson.TagFor(Bytes(second)));
    }

    [Fact]
    public void TheOldForm_TwoReadsSecondsApart_Differ()
    {
        // The baseline this change exists for: the same roster, two seconds apart, never matched.
        var first = JsonSerializer.SerializeToUtf8Bytes(Envelope(Pushed.AddSeconds(4.1)), Web);
        var second = JsonSerializer.SerializeToUtf8Bytes(Envelope(Pushed.AddSeconds(6.3)), Web);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void OnlyTheClockFieldsAreRemoved_EverythingElseIsCarriedUnchanged()
    {
        var now = Pushed.AddSeconds(4);
        var full = JsonSerializer.SerializeToNode(Envelope(now), Web)!.AsObject();

        var stripped = RosterClockFields.Strip(Envelope(now), Web).AsObject();

        // Put the two fields back and the answer is the old answer, field for field.
        foreach (var s in stripped["sessions"]!.AsArray().OfType<JsonObject>())
        {
            var original = full["sessions"]!.AsArray().OfType<JsonObject>().Single(o => (string)o["sessionId"]! == (string)s["sessionId"]!);
            Assert.False(s.ContainsKey("idleSeconds"));
            s["idleSeconds"] = original["idleSeconds"]!.DeepClone();
        }
        foreach (var d in stripped["directors"]!.AsArray().OfType<JsonObject>())
        {
            Assert.False(d.ContainsKey("lastSeenAgeSeconds"));
            var original = full["directors"]!.AsArray().OfType<JsonObject>().Single(o => (string)o["directorId"]! == (string)d["directorId"]!);
            d["lastSeenAgeSeconds"] = original["lastSeenAgeSeconds"]?.DeepClone();
        }
        Assert.True(JsonNode.DeepEquals(full, NormaliseOrder(stripped, full)));
    }

    [Fact]
    public void ASessionWithNoLastActivity_KeepsItsIdleSeconds_BecauseTheGatewayNeverRecomputedIt()
    {
        var roster = new List<SessionDto> { Session("a", null, 12.5), Session("b", Pushed, 3) };

        var stripped = RosterClockFields.Strip(roster, Web).AsArray();

        Assert.Equal(12.5, (double)stripped[0]!["idleSeconds"]!);
        Assert.False(stripped[1]!.AsObject().ContainsKey("idleSeconds"));
    }

    [Theory]
    [InlineData("absolute", true)]
    [InlineData("ABSOLUTE", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("relative", false)]
    public void OnlyTheOneValue_OptsIn(string? value, bool expected) =>
        Assert.Equal(expected, RosterClockFields.Requested(value));

    [Fact]
    public void TheTimeHeader_IsIso8601Utc_ToTheTick()
    {
        var now = new DateTime(2026, 9, 21, 12, 0, 4, DateTimeKind.Utc).AddTicks(1234567);
        Assert.Equal("2026-09-21T12:00:04.1234567Z", RosterClockFields.FormatTime(now));
    }

    // Re-adding a property appends it at the end of the object; the comparison is about content, so put the
    // properties back in the original order before comparing.
    private static JsonNode NormaliseOrder(JsonNode candidate, JsonNode template)
    {
        if (candidate is JsonObject co && template is JsonObject to)
        {
            var result = new JsonObject();
            foreach (var (key, value) in to)
                result[key] = co[key] is { } cv ? (value is null ? cv.DeepClone() : NormaliseOrder(cv, value)) : null;
            return result;
        }
        if (candidate is JsonArray ca && template is JsonArray ta)
        {
            var result = new JsonArray();
            for (var i = 0; i < ca.Count; i++)
                result.Add(i < ta.Count && ca[i] is not null && ta[i] is not null ? NormaliseOrder(ca[i]!, ta[i]!) : ca[i]?.DeepClone());
            return result;
        }
        return candidate.DeepClone();
    }
}
