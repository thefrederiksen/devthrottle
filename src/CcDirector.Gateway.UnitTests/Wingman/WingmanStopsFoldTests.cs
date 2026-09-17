using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// <see cref="WingmanStopsFold"/>, the one fold behind the Wingman tab (the Wingman inspector, phase 2): every outcome
/// word, the filter groups and their counts, the colour as recorded, the refusal reason verbatim, and the carrying-on
/// clock paired with the expiry that ended it.
///
/// THE ROWS HERE ARE BUILT BY HAND, and that is right for a pure fold and nothing else: what these prove is what the
/// fold says about a row. That a row really carries the colour its stop produced is proven through the real write
/// path in <c>WingmanStopsColourAtTheTimeTests</c>, not here.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class WingmanStopsFoldTests
{
    private const string Sid = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTime T0 = new(2026, 9, 16, 15, 0, 0, DateTimeKind.Utc);

    private static TurnVerdictDto Verdict(string id, string word, string label = "A label", bool failed = false,
        string? reason = null, DateTime? judgedAt = null) => new()
    {
        VerdictId = id,
        JudgedAtUtc = judgedAt ?? T0,
        TurnEndObservedAtUtc = T0,
        Failed = failed,
        FailureReason = reason,
        Verdict = failed ? "" : word,
        Confidence = failed ? "" : "high",
        Label = failed ? "" : label,
        Summary = failed ? "" : "A summary.",
    };

    private static TurnVerdictTrace Trace(string outcome, TurnVerdictDto? verdict = null, int minute = 0, string? cause = null,
        string? rowColour = null, string? rowLabel = null) => new()
    {
        TraceId = "trace-" + outcome + "-" + minute,
        SessionId = Sid,
        RecordedAtUtc = T0.AddMinutes(minute),
        TurnEndObservedAtUtc = T0.AddMinutes(minute),
        Trigger = "turn-end",
        Outcome = outcome,
        Cause = cause,
        VerdictId = verdict?.VerdictId,
        Verdict = verdict,
        RowColour = rowColour,
        RowLabel = rowLabel,
    };

    private static WingmanStopDto One(TurnVerdictTrace trace)
        => Assert.Single(WingmanStopsFold.Fold(Sid, new[] { trace }).Stops);

    // ================================================================= every outcome word

    [Theory]
    [InlineData(TurnVerdictTraceOutcomes.Judged, "needed-you", WingmanStopsFold.GroupNeedsYou)]
    [InlineData(TurnVerdictTraceOutcomes.Judged, "stuck-needs-person", WingmanStopsFold.GroupNeedsYou)]
    [InlineData(TurnVerdictTraceOutcomes.Judged, "cannot-tell", WingmanStopsFold.GroupNeedsYou)]
    [InlineData(TurnVerdictTraceOutcomes.Judged, "finished", WingmanStopsFold.GroupCalm)]
    [InlineData(TurnVerdictTraceOutcomes.Judged, "continues-alone", WingmanStopsFold.GroupCalm)]
    [InlineData(TurnVerdictTraceOutcomes.Expired, "needed-you", WingmanStopsFold.GroupNeedsYou)]
    [InlineData(TurnVerdictTraceOutcomes.Reused, "finished", WingmanStopsFold.GroupNotAsked)]
    public void A_stop_with_a_verdict_lands_in_its_group_and_shows_its_word(string outcome, string word, string group)
    {
        var stop = One(Trace(outcome, Verdict("v1", word, "The label")));

        Assert.Equal(group, stop.Group);
        Assert.Equal(word, stop.VerdictWord);
        Assert.Equal("high", stop.Confidence);
        Assert.Equal($"{word}, high confidence", stop.VerdictText);
        Assert.Equal("The label", stop.Strip.Label);
        Assert.Equal("The label", stop.Did.VerdictLabel);
        Assert.Null(stop.Did.Reason);
    }

    [Theory]
    [InlineData(TurnVerdictTraceOutcomes.Refused, "Refused")]
    [InlineData(TurnVerdictTraceOutcomes.DidNotAnswer, "No verdict formed")]
    [InlineData(TurnVerdictTraceOutcomes.RateLimited, "No verdict formed")]
    [InlineData(TurnVerdictTraceOutcomes.Unavailable, "No verdict formed")]
    public void A_failed_stop_is_in_Failed_names_no_verdict_and_carries_its_reason_verbatim(string outcome, string decision)
    {
        const string reason = "evidence: the sentence \"I deployed the Gateway\" is not on the screen or in the reply";
        var stop = One(Trace(outcome, Verdict("v1", "", failed: true, reason: reason)));

        Assert.Equal(WingmanStopsFold.GroupFailed, stop.Group);
        Assert.Null(stop.VerdictWord);
        Assert.Null(stop.Confidence);
        Assert.Equal("No verdict", stop.VerdictText);
        Assert.Equal(decision, stop.Did.Decision);
        Assert.False(stop.Did.Accepted);
        // VERBATIM: the contract's own words, not a code and not "1 of 4 checks".
        Assert.Equal(reason, stop.Did.Reason);
        Assert.Null(stop.Did.VerdictLabel);
        Assert.Equal(WingmanStopsFold.OutcomeText(outcome), stop.Strip.Label);
    }

    [Theory]
    [InlineData(TurnVerdictTraceOutcomes.Skipped, ActivityCauses.Held, "A live session that owns this one holds it")]
    [InlineData(TurnVerdictTraceOutcomes.Skipped, ActivityCauses.InFlightCap, "This account already had as many judgements running as it allows")]
    [InlineData(TurnVerdictTraceOutcomes.Cancelled, ActivityCauses.WorkingObservation, "The session was working")]
    [InlineData(TurnVerdictTraceOutcomes.Cancelled, ActivityCauses.Shutdown, "The Gateway was shutting down")]
    [InlineData(TurnVerdictTraceOutcomes.Joined, ActivityCauses.AlreadyJudging, "A judgement for this session was already running")]
    public void A_stop_that_asked_nothing_is_in_Not_asked_and_says_why(string outcome, string cause, string causeText)
    {
        var stop = One(Trace(outcome, cause: cause));

        Assert.Equal(WingmanStopsFold.GroupNotAsked, stop.Group);
        Assert.Equal("Not asked", stop.Did.Decision);
        Assert.Equal(causeText, stop.Did.CauseText);
        Assert.Equal("No verdict", stop.VerdictText);
        Assert.Equal("Not asked", stop.Strip.ReplyText);
        Assert.NotNull(stop.Asked.NotAskedText);
        Assert.False(stop.Saw.Kept);
    }

    [Fact]
    public void Only_a_judged_stop_is_Accepted_a_reused_or_clock_written_verdict_says_what_it_was()
    {
        Assert.Equal("Accepted", One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "finished"))).Did.Decision);
        Assert.True(One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "finished"))).Did.Accepted);

        var reused = One(Trace(TurnVerdictTraceOutcomes.Reused, Verdict("v1", "finished")));
        Assert.False(reused.Did.Accepted);
        Assert.StartsWith("Used again", reused.Did.Decision);

        var expired = One(Trace(TurnVerdictTraceOutcomes.Expired, Verdict("v2", "needed-you")));
        Assert.False(expired.Did.Accepted);
        Assert.Equal("Written by the carrying-on clock", expired.Did.Decision);
    }

    [Fact]
    public void An_unavailable_stop_names_the_exception_it_met()
    {
        var stop = One(Trace(TurnVerdictTraceOutcomes.Unavailable, Verdict("v1", "", failed: true, reason: "the verdict could not be formed: System.IO.IOException"), cause: "IOException"));

        Assert.Equal("It met an exception: IOException", stop.Did.CauseText);
    }

    [Fact]
    public void An_outcome_word_this_Gateway_does_not_know_is_counted_under_All_alone_and_says_so()
    {
        var answer = WingmanStopsFold.Fold(Sid, new[] { Trace("pondered") });

        var stop = Assert.Single(answer.Stops);
        Assert.Null(stop.Group);
        Assert.Equal("Recorded by a newer Gateway as \"pondered\"", stop.OutcomeText);
        Assert.Equal(1, answer.Groups.Single(g => g.Key == WingmanStopsFold.GroupAll).Count);
        Assert.All(answer.Groups.Where(g => g.Key != WingmanStopsFold.GroupAll), g => Assert.Equal(0, g.Count));
    }

    // ================================================================= groups and counts

    [Fact]
    public void The_chips_are_in_order_and_count_what_the_answer_holds()
    {
        var answer = WingmanStopsFold.Fold(Sid, new[]
        {
            Trace(TurnVerdictTraceOutcomes.Judged, Verdict("a", "needed-you"), minute: 6),
            Trace(TurnVerdictTraceOutcomes.Expired, Verdict("b", "needed-you"), minute: 5),
            Trace(TurnVerdictTraceOutcomes.Judged, Verdict("c", "finished"), minute: 4),
            Trace(TurnVerdictTraceOutcomes.Refused, Verdict("d", "", failed: true, reason: "r"), minute: 3),
            Trace(TurnVerdictTraceOutcomes.Skipped, cause: ActivityCauses.Held, minute: 2),
            Trace(TurnVerdictTraceOutcomes.Joined, cause: ActivityCauses.AlreadyJudging, minute: 1),
        });

        Assert.Equal(new[] { "all", "needs-you", "calm", "failed", "not-asked" }, answer.Groups.Select(g => g.Key));
        Assert.Equal(new[] { "All", "Needs you", "Calm", "Failed", "Not asked" }, answer.Groups.Select(g => g.Label));
        Assert.Equal(new[] { 6, 2, 1, 1, 2 }, answer.Groups.Select(g => g.Count));
        // The order the store handed in is the order served: newest first.
        Assert.Equal(new[] { 6, 5, 4, 3, 2, 1 }, answer.Stops.Select(s => s.RecordedAtUtc.Minute));
    }

    // ================================================================= the colour, as recorded

    [Fact]
    public void A_recorded_colour_is_served_as_recorded_with_its_pixel_and_label()
    {
        var stop = One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "finished"), rowColour: "red", rowLabel: "Needs you"));

        // RED, although the verdict is "finished": the fold serves what the row wore, and never works a colour out of
        // the verdict. A fold that recomputed would say cyan here.
        Assert.True(stop.RowRecorded);
        Assert.Equal("red", stop.RowColour);
        Assert.Equal(SessionColorPalette.Red, stop.RowColourHex);
        Assert.Equal("Needs you", stop.RowLabel);
    }

    [Fact]
    public void A_row_whose_colour_was_not_recorded_says_so_and_is_never_given_one()
    {
        var stop = One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "needed-you")));

        Assert.False(stop.RowRecorded);
        Assert.Null(stop.RowColour);
        Assert.Null(stop.RowColourHex);
        Assert.Equal("Not recorded", stop.RowLabel);
    }

    // ================================================================= the carrying-on clock

    [Fact]
    public void A_carrying_on_stop_and_the_expiry_that_ended_it_point_at_each_other()
    {
        var carryingOn = Verdict("v-carry", TurnVerdictVocabulary.ContinuesAlone, "Watching the test run");
        var deadline = T0.AddMinutes(10);
        var expiredAt = T0.AddMinutes(11);
        var expiredVerdict = Verdict("v-expired", TurnVerdictVocabulary.NeededYou, TurnVerdictWatchdog.ExpiredLabel, judgedAt: expiredAt);
        var judged = Trace(TurnVerdictTraceOutcomes.Judged, carryingOn, minute: 0) with { ClockDeadlineUtc = deadline };
        var expiry = Trace(TurnVerdictTraceOutcomes.Expired, expiredVerdict, minute: 11) with
        {
            Trigger = "clock",
            ReplacedVerdictId = "v-carry",
        };

        var stops = WingmanStopsFold.Fold(Sid, new[] { expiry, judged }).Stops;

        var expiryStop = stops[0];
        var carryStop = stops[1];
        Assert.Equal(deadline, carryStop.Did.Clock!.SetToRunOutAtUtc);
        Assert.Equal(expiredAt, carryStop.Did.Clock.RanOutAtUtc);
        Assert.Equal(expiry.TraceId, carryStop.Did.Clock.RanOutTraceId);
        Assert.Contains("It ran out", carryStop.Did.Clock.Text);
        Assert.Contains("moves later while the sessions it waits on keep working", carryStop.Did.Clock.Text);

        Assert.Equal(judged.TraceId, expiryStop.Did.ReplacedTraceId);
        Assert.NotNull(expiryStop.Did.ReplacedText);
        Assert.Equal(deadline, expiryStop.Did.Clock!.SetToRunOutAtUtc);
        Assert.Equal(expiredAt, expiryStop.Did.Clock.RanOutAtUtc);
        Assert.Equal("The carrying-on clock ran out", expiryStop.TriggerText);
        Assert.Equal(WingmanStopsFold.GroupNeedsYou, expiryStop.Group);
    }

    [Fact]
    public void A_carrying_on_stop_that_has_not_run_out_says_so_and_one_with_no_deadline_says_why()
    {
        var carryingOn = Verdict("v-carry", TurnVerdictVocabulary.ContinuesAlone);

        var running = One(Trace(TurnVerdictTraceOutcomes.Judged, carryingOn) with { ClockDeadlineUtc = T0.AddMinutes(10) });
        Assert.Null(running.Did.Clock!.RanOutAtUtc);
        Assert.Null(running.Did.Clock.RanOutTraceId);
        Assert.Contains("did not run out", running.Did.Clock.Text);

        var noDeadline = One(Trace(TurnVerdictTraceOutcomes.Judged, carryingOn));
        Assert.Null(noDeadline.Did.Clock!.SetToRunOutAtUtc);
        Assert.StartsWith("No deadline was recorded", noDeadline.Did.Clock.Text);

        // Any other verdict has no clock at all.
        Assert.Null(One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v", "finished"))).Did.Clock);
    }

    // ================================================================= the four blocks

    [Fact]
    public void A_judged_stop_shows_what_it_saw_what_it_was_asked_and_what_it_answered()
    {
        var package = new TurnVerdictPackage
        {
            ScreenRows = new[] { "row one", "row two" },
            LatestReply = "I pushed it.",
            RecentTurns = "user: push it",
            SessionTitle = "devthrottle - release",
            ScreenHash = "hash-1",
            OwnedSessions = new OwnedSessionCounts(1, 2, 0),
            ConversationAvailable = true,
        };
        var stop = One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "finished")) with
        {
            Package = package,
            Prompt = "the prompt",
            RawReply = "{\"verdict\":\"finished\"}",
            ReplySeconds = 3.84,
        });

        Assert.True(stop.Saw.Kept);
        Assert.Equal(new[] { "row one", "row two" }, stop.Saw.ScreenRows);
        Assert.Equal("Latest reply", stop.Saw.SourceHeading);
        Assert.Equal("I pushed it.", stop.Saw.SourceText);
        Assert.Equal("user: push it", stop.Saw.RecentTurns);
        Assert.Contains(stop.Saw.Facts, f => f.Name == "Session title" && f.Value == "devthrottle - release");
        Assert.Contains(stop.Saw.Facts, f => f.Name == "Sessions it owns" && f.Value == "1 working, 2 stopped, 0 need you");
        Assert.Equal("the prompt", stop.Asked.Prompt);
        Assert.Null(stop.Asked.NotAskedText);
        Assert.Equal("{\"verdict\":\"finished\"}", stop.Answered.RawReply);
        Assert.Equal(3.84, stop.Answered.ReplySeconds);
        Assert.Equal("Answered in 3.8 seconds", stop.Strip.ReplyText);
    }

    [Fact]
    public void A_package_over_the_ceiling_says_it_was_not_kept_and_cut_text_says_what_was_cut()
    {
        var stop = One(Trace(TurnVerdictTraceOutcomes.Judged, Verdict("v1", "finished")) with
        {
            PackageOmitted = true,
            Prompt = "cut prompt",
            PromptTruncated = true,
            RawReply = "cut reply",
            RawReplyTruncated = true,
        });

        Assert.False(stop.Saw.Kept);
        Assert.Equal("Not kept - over the size ceiling", stop.Saw.NotKeptText);
        Assert.True(stop.Asked.Cut);
        Assert.StartsWith("Cut at 128,000 characters", stop.Asked.CutText);
        Assert.True(stop.Answered.Cut);
        Assert.StartsWith("Cut at 64,000 characters", stop.Answered.CutText);
    }

    [Fact]
    public void A_stop_that_was_asked_and_got_no_answer_says_so_rather_than_not_asked()
    {
        var stop = One(Trace(TurnVerdictTraceOutcomes.DidNotAnswer, Verdict("v1", "", failed: true, reason: "no answer inside the timeout")) with
        {
            Prompt = "the prompt",
        });

        Assert.Null(stop.Answered.RawReply);
        Assert.Equal("No answer arrived before the timeout", stop.Answered.NoAnswerText);
        Assert.Equal("No answer arrived", stop.Strip.ReplyText);
    }
}
