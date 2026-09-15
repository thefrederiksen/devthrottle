using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The legend's words, held to the fold that decides the colours - EVERY CLAIM, AND NOTHING BUT CLAIMS.
///
/// Four rounds of review shaped this file, each closing a way a FALSE SENTENCE could still pass:
///   1. Tests that only rendered the legend's own strings proved nothing; two sentences were false the day they were
///      written ("or a schedule is driving it"; "with them off, every stopped session is red").
///   2. One example per colour executed a sentence's FIRST claim and left the rest as prose.
///   3. A clause table keyed by colour was not tied to the served words at all - rewording a sentence changed nothing.
///   4. Requiring each claim to appear SOMEWHERE in the sentence was still not fail-closed: a false statement could be
///      grown around the true claims ("The session is working and does not need you - at a prompt, ...").
///
/// So the sentence is no longer a string the legend writes: it is claims joined by the nine strings in
/// <see cref="SessionColourLegend.AllowedGlue"/>. This file demands a session for EVERY claim, refuses any glue
/// outside that list, and runs each claim's session through the real <see cref="SessionOrdering"/> for its colour, the
/// words beside its dot, and which side of the needs-you line it lands on. New prose cannot reach a screen without an
/// executed claim behind it.
/// </summary>
public sealed class SessionColourLegendTests
{
    // The fold's vocabulary, spelled out literally rather than read from the palette under test.
    private static readonly string[] FoldColours =
        { "red", "yellow", "orange", "green", "cyan", "blue", "purple", "supporting", "error", "grey", "unknown" };

    /// <summary>What one claim promises: a session with exactly those facts, and what the fold must do with it.</summary>
    /// <param name="Session">A session built from the facts the claim describes.</param>
    /// <param name="Label">The words the fold puts beside its dot.</param>
    /// <param name="FoldsTo">The colour it must wear, when that is not the entry's own (a name that shares the entry).</param>
    /// <param name="WhenTheHoldClears">The same session once the thing it waits for has arrived. Required for an entry
    /// that answers "Not yet": it must then need the person.</param>
    private sealed record Promise(
        SessionDto Session,
        string Label,
        string? FoldsTo = null,
        SessionDto? WhenTheHoldClears = null);

    /// <summary>The session behind one claim. Throws for a claim nobody has written one for - which is what makes new
    /// prose impossible to ship unexecuted.</summary>
    private static Promise PromiseFor(string colour, string claim) => (colour, claim) switch
    {
        ("red", "The session has stopped and is waiting for you") => new(Stopped("red-stopped", "WaitingForInput"), "Needs you"),
        ("red", "at a prompt") => new(Stopped("red-prompt", "WaitingForInput"), "Needs you"),
        ("red", "on a permission") => new(Stopped("red-permission", "WaitingForPerm"), "Needs you"),
        ("red", "gone quiet") => new(Stopped("red-idle", "Idle"), "Needs you"),

        ("blue", "The agent is running a turn right now") => new(Stopped("blue-working", "Working"), "Working"),
        // Snoozed, supervised, mid-dictation and brand-new at once - and still blue, which is what "always" claims.
        ("blue", "A working session is always blue") => new(new SessionDto
        {
            SessionId = "blue-outranks-everything", ActivityState = "Working",
            OnHold = true, HasLiveSupervisor = true, Transcribing = true, IsBrandNew = true,
        }, "Working"),

        ("cyan", "The session stopped and the Wingman judged it finished") =>
            new(Judged("cyan-finished", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindDone), "Done - Pushed the branch"),
        ("cyan", "the work is done") =>
            new(Judged("cyan-done", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindDone), "Done - Pushed the branch"),
        ("cyan", "it is only reporting something and asks you nothing") =>
            new(Judged("cyan-report", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindReport), "Report - Pushed the branch"),

        ("purple", "The session stopped, but the Wingman judged it will continue on its own") =>
            new(CarryingOn("purple-continues"), "Pushed the branch"),
        // The row is purple NOW; that it turns red is the carrying-on clock's promise, executed against the real
        // watchdog in TheCarryingOnPromise_IsKeptByTheRealClock below.
        ("purple", "It turns red if it does not") => new(CarryingOn("purple-waiting"), "Pushed the branch"),

        ("yellow", "The session stopped and is being looked at before it is shown to you") => new(
            Briefing("yellow-being-read"), "Wingman reading",
            WhenTheHoldClears: new SessionDto { SessionId = "yellow-read", ActivityState = "WaitingForInput", BriefingState = "None" }),
        ("yellow", "the Wingman or the Director is reading the stop") => new(
            Briefing("yellow-briefing"), "Wingman reading",
            WhenTheHoldClears: new SessionDto { SessionId = "yellow-briefed", ActivityState = "WaitingForInput", BriefingState = "None" }),
        ("yellow", "its voice summary is not ready yet") => new(
            new SessionDto { SessionId = "yellow-voice", ActivityState = "WaitingForInput", VoiceMode = true, VoiceAudioReady = false },
            "Preparing voice",
            WhenTheHoldClears: new SessionDto { SessionId = "yellow-voice-ready", ActivityState = "WaitingForInput", VoiceMode = true, VoiceAudioReady = true }),

        ("green", "A brand-new session at its first prompt") => new(BrandNew("green-new"), "Ready"),
        ("green", "It has not done anything yet") => new(BrandNew("green-untouched"), "Ready"),

        ("orange", "Your dictation is still uploading") => new(new SessionDto
        {
            SessionId = "orange-uploading", ActivityState = "WaitingForInput", DictationStatus = "Uploading from phone",
        }, "Uploading from phone"),
        ("orange", "being turned into text") => new(Transcribing("orange-transcribing"), "Transcribing"),
        ("orange", "Wait before typing into it") => new(Transcribing("orange-busy"), "Transcribing"),

        ("supporting", "Stopped, but another live session is driving it") => new(Supervised("supporting-driven"), "Snoozed"),
        ("supporting", "so it waits on that session instead of you") => new(Supervised("supporting-waits"), "Snoozed"),

        ("grey", "You snoozed it") =>
            new(new SessionDto { SessionId = "grey-snoozed", ActivityState = "WaitingForInput", OnHold = true }, "Snoozed"),
        ("grey", "its agent exited") => new(new SessionDto { SessionId = "grey-exited", ActivityState = "Exited" }, "Exited"),
        // A different NAME for the same pixel, and - as round 3 found - its label is "Idle", not a word about being
        // unreadable. The sentence says so now, and the next claim checks that word.
        ("grey", "its state could not be read") =>
            new(Unreadable("grey-unreadable"), "Idle", FoldsTo: "unknown"),
        ("grey", "The label reads Snoozed, Exited or Idle") => new(Unreadable("grey-label"), "Idle", FoldsTo: "unknown"),

        ("error", "The agent process died") => new(Crashed("error-died"), "Crashed"),
        ("error", "It is darker than the red that means needs you, so a crash never reads as a finish") =>
            new(Crashed("error-darker"), "Crashed"),

        _ => throw new InvalidOperationException(
            $"the legend's '{colour}' sentence claims \"{claim}\" and no session here tests it. Write one built from " +
            "the facts those words describe - a claim nobody executes is prose, and prose is how two false sentences " +
            "reached this legend already."),
    };

    private static SessionDto Stopped(string id, string activity) => new() { SessionId = id, ActivityState = activity };
    private static SessionDto Briefing(string id) => new() { SessionId = id, ActivityState = "WaitingForInput", BriefingState = "Briefing" };
    private static SessionDto BrandNew(string id) => new() { SessionId = id, ActivityState = "WaitingForInput", IsBrandNew = true };
    private static SessionDto Transcribing(string id) => new() { SessionId = id, ActivityState = "WaitingForInput", Transcribing = true };
    private static SessionDto Supervised(string id) => new() { SessionId = id, ActivityState = "WaitingForInput", HasLiveSupervisor = true };
    private static SessionDto Unreadable(string id) => new() { SessionId = id, ActivityState = "SomethingNew" };
    private static SessionDto Crashed(string id) => new() { SessionId = id, ActivityState = "Exited", Crashed = true };

    private static SessionDto CarryingOn(string id) => Judged(id, SessionOrdering.VerdictContinuesAlone, finishedKind: null);

    private static SessionDto Judged(string id, string verdict, string? finishedKind, string label = "Pushed the branch") => new()
    {
        SessionId = id,
        ActivityState = "WaitingForInput",
        VerdictState = VerdictStates.Judged,
        VerdictLabel = label,
        TurnVerdict = new TurnVerdictDto
        {
            VerdictId = id,
            Verdict = verdict,
            Confidence = SessionOrdering.ConfidenceHigh,
            FinishedKind = finishedKind,
            Label = label,
            JudgedAtUtc = new DateTime(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc),
        },
    };

    // ================================================================= nothing but claims and allowed glue

    [Fact]
    public void EverySentence_IsClaimsAndNothingElse()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            var segments = SessionColourLegend.SegmentsFor(entry.Colour);
            Assert.NotEmpty(segments);

            foreach (var segment in segments)
            {
                if (segment.IsClaim)
                {
                    Assert.False(string.IsNullOrWhiteSpace(segment.Text));
                    continue;
                }
                // A statement smuggled in as glue is exactly how a sentence grows something nobody executes.
                Assert.True(SessionColourLegend.AllowedGlue.Contains(segment.Text),
                    $"'{entry.Title}' joins its claims with \"{segment.Text}\", which is not one of the allowed joining " +
                    "strings - if those words say something about a session, they must be a claim");
            }

            // The served sentence is exactly its segments: no words reach a person from anywhere else.
            Assert.Equal(string.Concat(segments.Select(s => s.Text)), entry.Means);
        }
    }

    [Fact]
    public void EveryClaim_HasASessionBehindIt_ThatFoldsToTheColourItsEntryPromises()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var claim in SessionColourLegend.ClaimsFor(entry.Colour))
            {
                var promise = PromiseFor(entry.Colour, claim);
                var expected = promise.FoldsTo ?? entry.Colour;
                var actual = SessionOrdering.EffectiveColor(promise.Session);

                Assert.True(expected == actual,
                    $"'{entry.Title}' claims \"{claim}\", but a session with those facts folds to '{actual}', not '{expected}'");
                Assert.Equal(SessionColorPalette.HexFor(entry.Colour), SessionColorPalette.HexFor(actual));
            }
    }

    [Fact]
    public void EveryClaim_ReadsTheWordsTheLegendImplies()
    {
        // Round 3's finding: the grey entry promised the label said which of three things had happened, and for an
        // unreadable state the label is "Idle". A claim about the words is checked against the words.
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var claim in SessionColourLegend.ClaimsFor(entry.Colour))
            {
                var promise = PromiseFor(entry.Colour, claim);
                Assert.True(promise.Label == SessionOrdering.StateLabel(promise.Session),
                    $"'{entry.Title}' / \"{claim}\": the row reads '{SessionOrdering.StateLabel(promise.Session)}', not '{promise.Label}'");
            }
    }

    // ================================================================= does it ask for you?

    [Fact]
    public void AsksForYou_Yes_IsExactlyTheNeedsYouBucket()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var claim in SessionColourLegend.ClaimsFor(entry.Colour))
            {
                var promise = PromiseFor(entry.Colour, claim);
                var needsYou = SessionOrdering.Classify(promise.Session) == SessionOrdering.TriageBucket.NeedsYou;

                Assert.True(needsYou == (entry.AsksForYou == SessionColourLegend.AsksYes),
                    $"'{entry.Title}' says it asks for you: {entry.AsksForYou}, but \"{claim}\" lands in " +
                    $"{SessionOrdering.Classify(promise.Session)}");
            }
    }

    [Fact]
    public void AsksForYou_NotYet_MeansItWillAskOnceTheThingItWaitsForArrives()
    {
        // "Not yet" is a promise about the FUTURE, and it used to be indistinguishable from "No".
        foreach (var entry in SessionColourLegend.Build().Entries.Where(e => e.AsksForYou == SessionColourLegend.AsksNotYet))
            foreach (var claim in SessionColourLegend.ClaimsFor(entry.Colour))
            {
                var promise = PromiseFor(entry.Colour, claim);
                Assert.True(promise.WhenTheHoldClears is not null,
                    $"'{entry.Title}' says \"Not yet\", so \"{claim}\" must show the same session once the hold clears");
                Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(promise.WhenTheHoldClears!));
                Assert.Equal("red", SessionOrdering.EffectiveColor(promise.WhenTheHoldClears!));
            }
    }

    [Fact]
    public void AsksForYou_LookAtIt_IsASessionThatDied_NotOneThatIsMerelyQuiet()
    {
        foreach (var entry in SessionColourLegend.Build().Entries.Where(e => e.AsksForYou == SessionColourLegend.AsksLookAtIt))
            foreach (var claim in SessionColourLegend.ClaimsFor(entry.Colour))
            {
                var promise = PromiseFor(entry.Colour, claim);
                Assert.True(promise.Session.Crashed, $"'{entry.Title}' says \"Look at it\", so \"{claim}\" must be a crashed session");
                Assert.Equal("Crashed", SessionOrdering.StateLabel(promise.Session));
                Assert.NotEqual(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(promise.Session));
            }
    }

    // ================================================================= the promises that are about time

    [Fact]
    public void TheCarryingOnPromise_IsKeptByTheRealClock_NotByAVerdictThisTestWrote()
    {
        // Round 4's finding: this used to hand the fold an already-expired verdict of its own making, so deleting the
        // watchdog would have left "It turns red if it does not" false with the test still green. It now drives the
        // real clock: TurnVerdictWatchdog decides WHEN, and produces the verdict the row then folds from.
        var row = PromiseFor("purple", "It turns red if it does not").Session;
        var verdict = row.TurnVerdict!;
        var judgedAt = verdict.JudgedAtUtc;

        Assert.Equal("purple", SessionOrdering.EffectiveColor(row));
        Assert.False(TurnVerdictWatchdog.IsExpired(verdict, judgedAt.AddMinutes(9)));
        Assert.True(TurnVerdictWatchdog.IsExpired(verdict, judgedAt.AddMinutes(10)));

        row.TurnVerdict = TurnVerdictWatchdog.Expire(verdict, judgedAt.AddMinutes(10));
        row.VerdictLabel = row.TurnVerdict.Label;

        Assert.Equal("red", SessionOrdering.EffectiveColor(row));
        Assert.Equal(TurnVerdictWatchdog.ExpiredLabel, SessionOrdering.StateLabel(row));
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(row));
    }

    // ================================================================= the notes

    [Fact]
    public void TheVerdictNote_NamesOnlyTheColoursTheSwitchDecides_AndThatIsExecuted()
    {
        var legend = SessionColourLegend.Build();
        var note = legend.VerdictNote;

        Assert.Contains("Done", note, StringComparison.Ordinal);
        Assert.Contains("Carrying on", note, StringComparison.Ordinal);

        // Round 4's finding: it used to name the Wingman's yellow too, and a Director's briefing paints that same
        // yellow with the same words whatever the switch says. The note must not speak for it.
        foreach (var word in new[] { "yellow", "Wingman reading", "Being read" })
            Assert.DoesNotContain(word, note, StringComparison.Ordinal);
        foreach (var title in new[] { "Needs you", "Working", "Ready", "Transcribing", "Supervised", "Snoozed or exited", "Crashed" })
            Assert.DoesNotContain(title, note, StringComparison.Ordinal);

        // With the switch off the Gateway stamps no verdict on any row, so the two it names fold red...
        foreach (var colour in new[] { "cyan", "purple" })
            foreach (var claim in SessionColourLegend.ClaimsFor(colour))
            {
                var row = PromiseFor(colour, claim).Session;
                row.VerdictState = VerdictStates.None;
                row.TurnVerdict = null;
                Assert.Equal("red", SessionOrdering.EffectiveColor(row));
            }

        // ...and the briefing yellow it does NOT name is untouched by the switch, which is why it may not be named.
        var briefing = PromiseFor("yellow", "the Wingman or the Director is reading the stop").Session;
        briefing.VerdictState = VerdictStates.None;
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(briefing));
        Assert.Equal("Wingman reading", SessionOrdering.StateLabel(briefing));
    }

    [Fact]
    public void TheBrokenNote_IsTheSentinel_AndNoRealColour()
    {
        var legend = SessionColourLegend.Build();

        Assert.Equal(SessionColorPalette.Broken, legend.Broken.Hex);
        Assert.DoesNotContain(legend.Entries, e => string.Equals(e.Hex, SessionColorPalette.Broken, StringComparison.OrdinalIgnoreCase));
    }

    // ================================================================= coverage and shape

    [Fact]
    public void EveryFoldColour_HasAnEntry_OrSharesOne()
    {
        var explained = SessionColourLegend.Build().Entries.Select(e => e.Colour).ToHashSet(StringComparer.Ordinal);

        foreach (var colour in FoldColours)
            Assert.True(explained.Contains(colour) || SessionColourLegend.SharesAnEntry.ContainsKey(colour),
                $"the fold can paint '{colour}' and the legend does not say what it means");
    }

    [Fact]
    public void EveryEntry_IsAColourThePaletteKnows_AndCarriesItsCanonicalHex()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            Assert.True(SessionColorPalette.Knows(entry.Colour), $"'{entry.Colour}' is not a palette colour");
            Assert.Equal(SessionColorPalette.HexFor(entry.Colour), entry.Hex);
        }
    }

    [Fact]
    public void NoTwoEntries_ShareAColourOrAPixel()
    {
        var entries = SessionColourLegend.Build().Entries;

        Assert.Equal(entries.Count, entries.Select(e => e.Colour).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(entries.Count, entries.Select(e => e.Hex.ToUpperInvariant()).Distinct().Count());
    }

    [Fact]
    public void TheCrashedEntry_IsADifferentRed_FromTheOneThatMeansNeedsYou()
    {
        Assert.NotEqual(SessionColorPalette.HexFor("red"), SessionColorPalette.HexFor("error"));
    }

    [Fact]
    public void TheSupervisedEntry_IsALiveSupervisor_NotASchedule()
    {
        // The first draft said "or a schedule is driving it". A schedule is not supervision: IsSupervised reads only a
        // live supervisor. A stopped session without one is red, whoever started it.
        var noSupervisor = new SessionDto
        {
            SessionId = "scheduled-run", ActivityState = "WaitingForInput", HasLiveSupervisor = false,
            OriginKind = "schedule",
        };

        Assert.Equal("red", SessionOrdering.EffectiveColor(noSupervisor));
        Assert.DoesNotContain("schedule", SessionColourLegend.Build().Entries.Single(e => e.Colour == "supporting").Means,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EveryEntry_HasWordsInEveryField()
    {
        var legend = SessionColourLegend.Build();

        foreach (var entry in legend.Entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(entry.Title));
            Assert.False(string.IsNullOrWhiteSpace(entry.Means));
            Assert.Contains(entry.AsksForYou, new[] { SessionColourLegend.AsksYes, SessionColourLegend.AsksNo, SessionColourLegend.AsksNotYet, SessionColourLegend.AsksLookAtIt });
        }
        Assert.False(string.IsNullOrWhiteSpace(legend.VerdictNote));
        Assert.False(string.IsNullOrWhiteSpace(legend.Broken.Means));
    }
}
