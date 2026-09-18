using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Wingman;

/// <summary>
/// The Wingman debug view's answer: for each stop, what was fed in, the exact prompt, and the raw answer - FOR
/// BOTH MODEL CALLS.
///
/// WHAT THESE PROVE. The view exists to answer one question - what was the model given, and what did it actually
/// say - so what is tested is that the stored bytes reach the reader UNCHANGED, and that a call which was not
/// made is said in words rather than shown as an empty box. An empty box and "this call was never made" are
/// different facts, and the second is usually the answer somebody opened this view to find.
/// </summary>
public sealed class WingmanDebugFoldTests
{
    private static readonly DateTime ObservedAt = new(2026, 9, 18, 12, 44, 41, DateTimeKind.Utc);

    private static TurnVerdictTrace Trace(
        string outcome = TurnVerdictTraceOutcomes.Judged,
        TurnVerdictDto? verdict = null,
        TurnVerdictPackage? package = null,
        string? prompt = "You are the WINGMAN...",
        string? rawReply = "{\"state\":\"finished-report\"}",
        string? narrationPrompt = "Retell this for the ear...",
        string? narrationRawReply = "The retention sweep is done.",
        string? narrationFailureDetail = null,
        double? narrationSeconds = 4.2)
        => new()
        {
            TraceId = "trace-1",
            SessionId = "session-1",
            DirectorId = "director-1",
            RecordedAtUtc = ObservedAt.AddSeconds(9),
            TurnEndObservedAtUtc = ObservedAt,
            Trigger = "turn-end",
            Outcome = outcome,
            VerdictId = verdict?.VerdictId,
            ReplySeconds = 3.1,
            Package = package,
            Prompt = prompt,
            RawReply = rawReply,
            NarrationPrompt = narrationPrompt,
            NarrationRawReply = narrationRawReply,
            NarrationFailureDetail = narrationFailureDetail,
            NarrationSeconds = narrationSeconds,
            Verdict = verdict,
            RowColour = "cyan",
            RowLabel = "Done",
        };

    private static TurnVerdictPackage Package() => new()
    {
        Kind = TurnVerdictPackageKind.AgentReply,
        ConversationAvailable = true,
        SessionTitle = "devthrottle - the retention timer",
        LatestReply = "The sweep is done.",
        ScreenRows = new[] { "The sweep is done.", "> " },
        ScreenHash = "HASH",
    };

    private static TurnVerdictDto Accepted() => new()
    {
        VerdictId = "verdict-1",
        Model = "devthrottle/wingman-fast",
        ContractVersion = "v3",
        Verdict = TurnVerdictVocabulary.Finished,
        FinishedKind = "report",
        Label = "Retention sweep done",
        AgentRecommends = "Turn the daily schedule off.",
        Narration = "The retention sweep is done and the test covers it.",
    };

    [Fact]
    public void BothCallsAreHandedOverWhole_ExactlyAsStored()
    {
        var answer = WingmanDebugFold.Fold("session-1", new[] { Trace(verdict: Accepted(), package: Package()) });

        var stop = Assert.Single(answer.Stops);
        Assert.Equal("You are the WINGMAN...", stop.JudgePrompt);
        Assert.Equal("{\"state\":\"finished-report\"}", stop.JudgeRawReply);
        Assert.Equal("Retell this for the ear...", stop.NarrationPrompt);
        Assert.Equal("The retention sweep is done.", stop.NarrationRawReply);
        Assert.Equal(3.1, stop.JudgeSeconds);
        Assert.Equal(4.2, stop.NarrationSeconds);
        // Nothing says a call was missing, because neither was.
        Assert.Null(stop.JudgeNotAskedText);
        Assert.Null(stop.NarrationNotMadeText);
        // And the two headings are the Gateway's own words, not the client's.
        Assert.Equal(WingmanDebugFold.JudgeCallTitle, answer.JudgeCallTitle);
        Assert.Equal(WingmanDebugFold.NarrationCallTitle, answer.NarrationCallTitle);
    }

    [Fact]
    public void WhatBothCallsWereFed_IsTheOnePackage_SerializedWhole()
    {
        var answer = WingmanDebugFold.Fold("session-1", new[] { Trace(verdict: Accepted(), package: Package()) });

        var stop = Assert.Single(answer.Stops);
        Assert.NotNull(stop.Fed);
        Assert.Contains("devthrottle - the retention timer", stop.Fed);
        Assert.Contains("The sweep is done.", stop.Fed);
        Assert.Null(stop.FedAbsentText);
    }

    [Fact]
    public void APackageThatWasTooLargeToKeep_SaysSoRatherThanShowingNothing()
    {
        var trace = Trace(verdict: Accepted(), package: null) with { PackageOmitted = true };

        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { trace }).Stops);

        Assert.Null(stop.Fed);
        Assert.Equal(WingmanDebugFold.PackageOmitted, stop.FedAbsentText);
    }

    [Fact]
    public void APackageThatWasNeverBuilt_IsADifferentSentenceFromOneThatWasTooLarge()
    {
        // "we did not keep it" and "there was never one" are different facts about the same empty box.
        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { Trace(package: null) }).Stops);

        Assert.Equal(WingmanDebugFold.PackageAbsent, stop.FedAbsentText);
        Assert.NotEqual(WingmanDebugFold.PackageOmitted, stop.FedAbsentText);
    }

    [Fact]
    public void ANarrationCallThatWasNeverMade_SaysSo()
    {
        var trace = Trace(verdict: Accepted(), package: Package(),
            narrationPrompt: null, narrationRawReply: null, narrationSeconds: null);

        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { trace }).Stops);

        Assert.Null(stop.NarrationPrompt);
        Assert.Equal(WingmanDebugFold.NarrationNotMade, stop.NarrationNotMadeText);
    }

    [Fact]
    public void ANarrationCallThatWasMadeAndFAILED_ShowsItsReason_AndIsNotCalledUnmade()
    {
        // The one case this whole view was built for: the call happened, and it produced no words. Reporting that
        // as "not made" would hide the failure behind the ordinary sentence for a stop that was never owed one.
        var trace = Trace(verdict: Accepted(), package: Package(),
            narrationRawReply: null, narrationFailureDetail: "the narration call answered with no words");

        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { trace }).Stops);

        Assert.Equal("the narration call answered with no words", stop.NarrationFailureDetail);
        Assert.Null(stop.NarrationNotMadeText);
    }

    [Fact]
    public void ARefusedJudgement_CarriesItsReason_AndIsMarkedFailed()
    {
        var refused = new TurnVerdictDto
        {
            VerdictId = "verdict-2",
            Model = "devthrottle/wingman-fast",
            ContractVersion = "v3",
            Failed = true,
            FailureReason = "unknown state word 'finished'; the seven allowed words are ...",
        };
        var trace = Trace(outcome: TurnVerdictTraceOutcomes.Refused, verdict: refused, package: Package(),
            narrationPrompt: null, narrationRawReply: null, narrationSeconds: null);

        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { trace }).Stops);

        Assert.True(stop.Failed);
        Assert.Contains("unknown state word", stop.FailureReason);
        // The raw answer that failed is exactly what this record exists for, so it is still handed over.
        Assert.Equal("{\"state\":\"finished-report\"}", stop.JudgeRawReply);
        Assert.Equal("", stop.State ?? "");
    }

    [Fact]
    public void TheFiveFieldsOfTheReadingAreShownBesideTheRawAnswer()
    {
        var stop = Assert.Single(
            WingmanDebugFold.Fold("session-1", new[] { Trace(verdict: Accepted(), package: Package()) }).Stops);

        Assert.Equal(TurnVerdictStates.FinishedReport, stop.State);
        Assert.Equal("Retention sweep done", stop.Label);
        Assert.Equal("Turn the daily schedule off.", stop.AgentRecommends);
        Assert.Equal("The retention sweep is done and the test covers it.", stop.Narration);
    }

    [Fact]
    public void ACutPromptOrAnswer_SaysItWasCut()
    {
        var trace = Trace(verdict: Accepted(), package: Package()) with
        {
            PromptTruncated = true,
            RawReplyTruncated = true,
            NarrationPromptTruncated = true,
            NarrationRawReplyTruncated = true,
        };

        var stop = Assert.Single(WingmanDebugFold.Fold("session-1", new[] { trace }).Stops);

        Assert.NotNull(stop.JudgePromptCutText);
        Assert.NotNull(stop.JudgeRawReplyCutText);
        Assert.NotNull(stop.NarrationPromptCutText);
        Assert.NotNull(stop.NarrationRawReplyCutText);
    }

    [Fact]
    public void NoTraces_IsAnEmptyList_AndNotAFabricatedRow()
    {
        var answer = WingmanDebugFold.Fold("session-1", Array.Empty<TurnVerdictTrace>());

        Assert.Equal("session-1", answer.SessionId);
        Assert.Empty(answer.Stops);
    }
}
