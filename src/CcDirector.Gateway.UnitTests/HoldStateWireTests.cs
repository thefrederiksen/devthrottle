using System.Text.Json;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Defect 12: the hold state crosses the wire as THREE states, not one boolean, and the boolean
/// <see cref="SessionDto.OnHold"/> is DERIVED from it so the two can never disagree.
///
/// This is the root cause of defect 20 and it is not cosmetic. When the wire carried only the boolean,
/// <c>None</c> and <c>DeferredHold</c> were byte-identical - both false - so nothing downstream could tell
/// "not held" from "about to be held". The Gateway's expiry sweep read that boolean, concluded a deferred
/// snooze was over, and deleted its timer 15 seconds after it was asked for.
/// See docs/new_architecture/sessions.html.
/// </summary>
public sealed class HoldStateWireTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(HoldStates.None, false)]
    [InlineData(HoldStates.Held, true)]
    [InlineData(HoldStates.DeferredHold, false)]
    public void OnHold_isDerivedFromHoldState_andIsTrueOnlyForHeld(string holdState, bool expectedOnHold)
    {
        // A DeferredHold reads FALSE here, correctly - it is not parked yet. That is exactly why nothing
        // that must distinguish it from None may read this boolean.
        Assert.Equal(expectedOnHold, new SessionDto { HoldState = holdState }.OnHold);
    }

    [Fact]
    public void ADefaultSessionIsNotHeld_AndDoesNotClaimToKnowWhy()
    {
        var s = new SessionDto();

        // NOT HELD is the claim this test's name makes, and it still holds: IsHeld(null) is false, so a
        // default DTO renders unheld exactly as before. What changed on 15 July 2026 is the STRENGTH of
        // the claim underneath it. This asserted HoldState == None, i.e. "affirmatively not held" - and
        // that default is what let an old Director's silence be read as a positive statement that a
        // deferred snooze was over, so the sweep deleted it. A freshly constructed DTO has been told
        // nothing by anyone; null says so. Anything that must ACT on a hold reads HoldState and treats
        // null as "ask again", never as None.
        Assert.Null(s.HoldState);
        Assert.False(s.OnHold);
        Assert.False(HoldStates.IsHeld(s.HoldState));
    }

    [Theory]
    [InlineData(HoldStates.None)]
    [InlineData(HoldStates.Held)]
    [InlineData(HoldStates.DeferredHold)]
    public void AllThreeStatesSurviveAJsonRoundTrip(string holdState)
    {
        // THE REGRESSION: DeferredHold used to arrive at the Gateway as plain false, indistinguishable
        // from None. It must now survive the trip intact.
        var json = JsonSerializer.Serialize(new SessionDto { SessionId = "s", HoldState = holdState }, Web);
        var back = JsonSerializer.Deserialize<SessionDto>(json, Web)!;

        Assert.Equal(holdState, back.HoldState);
    }

    [Fact]
    public void TheDerivedBooleanIsStillOnTheWireForAnOlderClient()
    {
        var json = JsonSerializer.Serialize(new SessionDto { SessionId = "s", HoldState = HoldStates.Held }, Web);

        Assert.Contains("\"onHold\":true", json);
    }

    [Theory]
    [InlineData(true, HoldStates.Held)]
    [InlineData(false, null)]
    public void AnOlderDirectorSendingOnlyTheBooleanStillLands(bool onHold, string? expected)
    {
        // Backward compatibility: a payload that predates HoldState carries only the boolean, and the
        // setter maps it onto the tri-state rather than leaving the DTO reading "None" for a held session.
        //
        // onHold=false MUST land on null, NOT None - and this row asserted None until 15 July 2026, which
        // made it a test defending defect 12. An old Director reports onHold=false for BOTH "not held" and
        // "deferred", so false is not evidence of None; it is the absence of evidence. Answering None here
        // told the sweep the session was genuinely unheld and it deleted a live deferred snooze. Null is
        // the honest answer - "this Director did not say" - and the sweep changes nothing on it.
        //
        // Read the two rows as the asymmetry they are: true is CONCLUSIVE (only a landed hold reports it),
        // false is AMBIGUOUS. The boolean can prove a hold and can never disprove one.
        var json = $$"""{"sessionId":"s","onHold":{{(onHold ? "true" : "false")}}}""";

        var dto = JsonSerializer.Deserialize<SessionDto>(json, Web)!;

        Assert.Equal(expected, dto.HoldState);
        Assert.Equal(onHold, dto.OnHold);
    }

    [Fact]
    public void AnOldDirectorsSilenceIsNotAClaimThatTheSnoozeIsOver()
    {
        // The exact mixed-version case, at the seam that matters: a Gateway newer than the Director it
        // serves. The old Director has a DEFERRED hold and says only onHold=false, because that is all its
        // wire has. The Gateway must read "I do not know", never "not held" - the sweep clears on None,
        // and clearing here destroys the user's snooze fifteen seconds after they asked for it.
        var json = """{"sessionId":"s","onHold":false}""";

        var dto = JsonSerializer.Deserialize<SessionDto>(json, Web)!;

        Assert.Null(dto.HoldState);
        Assert.Null(HoldStates.Normalize(dto.HoldState));
        Assert.False(HoldStates.IsHeld(dto.HoldState));
        Assert.False(HoldStates.IsDeferred(dto.HoldState));
    }

    [Theory]
    // Both fields present, in BOTH orders: whichever the deserializer assigns first, the DTO must land on
    // the state the tri-state names. The OnHold setter is idempotent precisely so this cannot go wrong.
    [InlineData("""{"holdState":"DeferredHold","onHold":false}""", HoldStates.DeferredHold)]
    [InlineData("""{"onHold":false,"holdState":"DeferredHold"}""", HoldStates.DeferredHold)]
    [InlineData("""{"holdState":"Held","onHold":true}""", HoldStates.Held)]
    [InlineData("""{"onHold":true,"holdState":"Held"}""", HoldStates.Held)]
    public void WhenBothFieldsArePresentTheTriStateIsWhatSurvives(string json, string expected)
    {
        Assert.Equal(expected, JsonSerializer.Deserialize<SessionDto>(json, Web)!.HoldState);
    }

    [Fact]
    public void SettingOnHoldFalseDoesNotDestroyADeferredHold()
    {
        // The snooze expiry overlay writes OnHold=false onto the aggregated copy. A DeferredHold already
        // reads false, so that write must be a no-op against it - not a silent downgrade to None by a
        // writer that only knows about the boolean.
        var s = new SessionDto { HoldState = HoldStates.DeferredHold };

        s.OnHold = false;

        Assert.Equal(HoldStates.DeferredHold, s.HoldState);
    }

    [Fact]
    public void SettingOnHoldFalseReleasesALandedHold()
    {
        // ...and it still does the job it exists for: the expiry overlay flips a genuinely-parked session
        // back to un-held, and the tri-state follows it rather than contradicting it.
        var s = new SessionDto { HoldState = HoldStates.Held };

        s.OnHold = false;

        Assert.Equal(HoldStates.None, s.HoldState);
        Assert.False(s.OnHold);
    }

    [Fact]
    public void CloneCarriesTheHoldState()
    {
        // The Gateway serves deep copies of the pushed cache. A copy that dropped the tri-state would
        // reintroduce the exact loss this defect is about - silently, and only in production.
        var clone = new SessionDto { SessionId = "s", HoldState = HoldStates.DeferredHold }.Clone();

        Assert.Equal(HoldStates.DeferredHold, clone.HoldState);
    }

    [Fact]
    public void AWorkingSessionWithADeferredHoldIsStillBlueAndStillReadsWorking()
    {
        // THE LAW: if a session is working, it is BLUE. Always. "Snooze me when this finishes" does not
        // park anything yet, so it cannot touch the colour, the label, or the bucket.
        var s = new SessionDto { SessionId = "s", ActivityState = "Working", HoldState = HoldStates.DeferredHold };

        Assert.Equal("blue", SessionOrdering.EffectiveColor(s));
        Assert.Equal("Working", SessionOrdering.StateLabel(s));
        Assert.Equal(SessionOrdering.TriageBucket.Active, SessionOrdering.Classify(s));
    }
}
