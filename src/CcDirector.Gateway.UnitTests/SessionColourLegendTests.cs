using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The legend's words, held to the fold that decides the colours - CLAUSE BY CLAUSE, AND BOUND TO THE SERVED TEXT.
///
/// Three rounds of review shaped this file, each closing a way a FALSE SENTENCE could still pass:
///   1. Tests that only rendered the legend's own strings proved nothing. Two sentences were false on the day they
///      were written ("or a schedule is driving it"; "with them off, every stopped session is red").
///   2. One example per colour executed a sentence's FIRST claim and left the rest as prose.
///   3. A clause table keyed only by colour was still not tied to the SERVED WORDS: the claims were assertion
///      messages, so rewording a sentence into a lie changed nothing. And "Not yet" and "Look at it" were treated
///      as "not red", so they promised nothing.
///
/// So now: every clause's <see cref="Clause.Claim"/> MUST appear verbatim in the entry's served
/// <see cref="SessionColourLegendEntryDto.Means"/>; each clause carries a session built from exactly the facts it
/// describes and is run through the real <see cref="SessionOrdering"/> for its colour, its LABEL, and which side of
/// the needs-you line it lands on; "Not yet" must become needs-you once the thing it waits for arrives; "Look at it"
/// must be a session that died; and the verdict note is READ and its claims executed.
/// </summary>
public sealed class SessionColourLegendTests
{
    // The fold's vocabulary, spelled out literally rather than read from the palette under test.
    private static readonly string[] FoldColours =
        { "red", "yellow", "orange", "green", "cyan", "blue", "purple", "supporting", "error", "grey", "unknown" };

    /// <summary>
    /// One claim a sentence makes, and the session that tests it.
    /// </summary>
    /// <param name="Claim">Words that must appear VERBATIM in the entry's served sentence.</param>
    /// <param name="Session">A session with exactly the facts the claim describes.</param>
    /// <param name="Label">The words the fold puts beside the dot for it, when the claim is about the label.</param>
    /// <param name="FoldsTo">The colour it must wear, when that is not the entry's own.</param>
    /// <param name="SamePixelAsEntry">Whether it must also paint the entry's pixel (false when the claim is about
    /// what happens NEXT, rather than about this colour).</param>
    /// <param name="WhenTheHoldClears">The same session once the thing it waits for has arrived. Required for an
    /// entry that says "Not yet": it must then need the person.</param>
    private sealed record Clause(
        string Claim,
        SessionDto Session,
        string? Label = null,
        string? FoldsTo = null,
        bool SamePixelAsEntry = true,
        SessionDto? WhenTheHoldClears = null);

    private static Clause[] ClausesFor(string colour) => colour switch
    {
        "red" =>
        [
            new("at a prompt", Stopped("red-prompt", "WaitingForInput"), Label: "Needs you"),
            new("on a permission", Stopped("red-permission", "WaitingForPerm"), Label: "Needs you"),
            new("gone quiet", Stopped("red-idle", "Idle"), Label: "Needs you"),
        ],
        "blue" =>
        [
            new("running a turn right now", Stopped("blue-working", "Working"), Label: "Working"),
            new("running a turn right now", Stopped("blue-starting", "Starting"), Label: "Working"),
            // "always blue": snoozed, supervised, mid-dictation and brand-new at once, and still working.
            new("always blue", new SessionDto
            {
                SessionId = "blue-outranks-everything", ActivityState = "Working",
                OnHold = true, HasLiveSupervisor = true, Transcribing = true, IsBrandNew = true,
            }, Label: "Working"),
        ],
        "cyan" =>
        [
            new("the work is done", Judged("cyan-done", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindDone),
                Label: "Done - Pushed the branch"),
            new("only reporting something", Judged("cyan-report", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindReport),
                Label: "Report - Pushed the branch"),
        ],
        "purple" =>
        [
            new("will continue on its own", Judged("purple-continues", SessionOrdering.VerdictContinuesAlone, finishedKind: null),
                Label: "Pushed the branch"),
            // "It turns red if it does not" - the carrying-on clock re-judges the row, and the fold's half of that
            // promise is that the re-judged verdict is a red one. A different colour on purpose, so no pixel check.
            new("It turns red if it does not", Judged("purple-expired", "needed-you", finishedKind: null, label: "Said it would continue and did not"),
                Label: "Said it would continue and did not", FoldsTo: "red", SamePixelAsEntry: false),
        ],
        "yellow" =>
        [
            new("the Wingman or the Director is reading the stop", new SessionDto
            {
                SessionId = "yellow-briefing", ActivityState = "WaitingForInput", BriefingState = "Briefing",
            }, Label: "Wingman reading",
                WhenTheHoldClears: new SessionDto { SessionId = "yellow-briefing-done", ActivityState = "WaitingForInput", BriefingState = "None" }),
            new("the Wingman or the Director is reading the stop", new SessionDto
            {
                SessionId = "yellow-verdict-reading", ActivityState = "WaitingForInput", VerdictState = VerdictStates.Reading,
            }, Label: "Wingman reading",
                WhenTheHoldClears: new SessionDto { SessionId = "yellow-verdict-read", ActivityState = "WaitingForInput", VerdictState = VerdictStates.None }),
            new("its voice summary is not ready yet", new SessionDto
            {
                SessionId = "yellow-voice", ActivityState = "WaitingForInput", VoiceMode = true, VoiceAudioReady = false,
            }, Label: "Preparing voice",
                WhenTheHoldClears: new SessionDto { SessionId = "yellow-voice-ready", ActivityState = "WaitingForInput", VoiceMode = true, VoiceAudioReady = true }),
        ],
        "green" =>
        [
            new("A brand-new session at its first prompt", new SessionDto
            {
                SessionId = "green-new", ActivityState = "WaitingForInput", IsBrandNew = true,
            }, Label: "Ready"),
        ],
        "orange" =>
        [
            new("uploading", new SessionDto
            {
                SessionId = "orange-uploading", ActivityState = "WaitingForInput", DictationStatus = "Uploading from phone",
            }, Label: "Uploading from phone"),
            new("being turned into text", new SessionDto
            {
                SessionId = "orange-transcribing", ActivityState = "WaitingForInput", Transcribing = true,
            }, Label: "Transcribing"),
            new("being turned into text", new SessionDto
            {
                SessionId = "orange-desktop", ActivityState = "WaitingForInput", IsTranscribing = true,
            }, Label: "Transcribing"),
        ],
        "supporting" =>
        [
            new("another live session is driving it", new SessionDto
            {
                SessionId = "supporting-supervised", ActivityState = "WaitingForInput", HasLiveSupervisor = true,
            }, Label: "Snoozed"),
        ],
        "grey" =>
        [
            new("You snoozed it", new SessionDto { SessionId = "grey-snoozed", ActivityState = "WaitingForInput", OnHold = true },
                Label: "Snoozed"),
            new("its agent exited", new SessionDto { SessionId = "grey-exited", ActivityState = "Exited" }, Label: "Exited"),
            // The fold's own word for a state it cannot read: a different NAME, the same pixel, and - as the third
            // review found - the label is "Idle", not a word about being unreadable. The sentence now says so.
            new("Idle when it cannot be read", new SessionDto { SessionId = "grey-unreadable", ActivityState = "SomethingNew" },
                Label: "Idle", FoldsTo: "unknown"),
        ],
        "error" =>
        [
            new("The agent process died", new SessionDto { SessionId = "error-crashed", ActivityState = "Exited", Crashed = true },
                Label: "Crashed"),
        ],
        _ => throw new InvalidOperationException(
            $"the legend has an entry for '{colour}' but this test names no claims for it - write one clause per thing " +
            "its sentence says, each with a session built from those facts, so the sentence is checked against the " +
            "fold rather than trusted"),
    };

    private static SessionDto Stopped(string id, string activity) => new() { SessionId = id, ActivityState = activity };

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
        },
    };

    // ================================================================= the words, bound to the served text

    [Fact]
    public void EveryClaim_AppearsVerbatimInTheSentenceTheGatewayServes()
    {
        // THE BINDING. Without this the clauses are a private table: reword a sentence into a lie and every other
        // test here still passes, because they only ever read the clause list.
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
                Assert.True(entry.Means.Contains(clause.Claim, StringComparison.Ordinal),
                    $"'{entry.Title}' is tested for \"{clause.Claim}\", but its sentence does not say that: \"{entry.Means}\"");
    }

    [Fact]
    public void EveryClause_FoldsToTheColourItsEntryPromises()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
            {
                var expected = clause.FoldsTo ?? entry.Colour;
                var actual = SessionOrdering.EffectiveColor(clause.Session);
                Assert.True(expected == actual,
                    $"'{entry.Title}' says \"{clause.Claim}\", but a session with those facts folds to '{actual}', not '{expected}'");
                if (clause.SamePixelAsEntry)
                    Assert.Equal(SessionColorPalette.HexFor(entry.Colour), SessionColorPalette.HexFor(actual));
            }
    }

    [Fact]
    public void EveryClause_ReadsTheWordsTheLegendImplies()
    {
        // The third review's finding: the grey entry promised the label said which of three things had happened, and
        // for an unreadable state the label is "Idle". A claim about the words is checked against the words.
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
            {
                if (clause.Label is null) continue;
                Assert.True(clause.Label == SessionOrdering.StateLabel(clause.Session),
                    $"'{entry.Title}' / \"{clause.Claim}\": the row reads '{SessionOrdering.StateLabel(clause.Session)}', not '{clause.Label}'");
            }
    }

    // ================================================================= does it ask for you?

    [Fact]
    public void AsksForYou_Yes_IsExactlyTheNeedsYouBucket_AndEveryOtherAnswerIsNot()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
            {
                var needsYou = SessionOrdering.Classify(clause.Session) == SessionOrdering.TriageBucket.NeedsYou;

                // A clause about what happens NEXT - purple's "It turns red if it does not" - is about a different
                // colour, so the entry's own answer does not govern it. It is judged by the colour it becomes.
                if (!clause.SamePixelAsEntry)
                {
                    Assert.Equal("red", clause.FoldsTo);
                    Assert.True(needsYou, $"\"{clause.Claim}\" says the row turns red, so it must then need the person");
                    continue;
                }

                Assert.True(needsYou == (entry.AsksForYou == SessionColourLegend.AsksYes),
                    $"'{entry.Title}' says it asks for you: {entry.AsksForYou}, but \"{clause.Claim}\" lands in " +
                    $"{SessionOrdering.Classify(clause.Session)}");
            }
    }

    [Fact]
    public void AsksForYou_NotYet_MeansItWillAskOnceTheThingItWaitsForArrives()
    {
        // "Not yet" is a promise about the FUTURE, and it was previously indistinguishable from "No". Every clause of
        // a "Not yet" entry must carry the same session with its hold cleared, and that one must need the person.
        foreach (var entry in SessionColourLegend.Build().Entries.Where(e => e.AsksForYou == SessionColourLegend.AsksNotYet))
            foreach (var clause in ClausesFor(entry.Colour).Where(c => c.SamePixelAsEntry))
            {
                Assert.True(clause.WhenTheHoldClears is not null,
                    $"'{entry.Title}' says \"Not yet\", so \"{clause.Claim}\" must show the same session once the hold clears");
                Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(clause.WhenTheHoldClears!));
                Assert.Equal("red", SessionOrdering.EffectiveColor(clause.WhenTheHoldClears!));
            }
    }

    [Fact]
    public void AsksForYou_LookAtIt_IsASessionThatDied_NotOneThatIsMerelyQuiet()
    {
        // The other answer that used to mean nothing. "Look at it" is the crash: the process is gone, so nobody can
        // ask you anything - but it is not a session resting either.
        foreach (var entry in SessionColourLegend.Build().Entries.Where(e => e.AsksForYou == SessionColourLegend.AsksLookAtIt))
            foreach (var clause in ClausesFor(entry.Colour))
            {
                Assert.True(clause.Session.Crashed, $"'{entry.Title}' says \"Look at it\", so \"{clause.Claim}\" must be a crashed session");
                Assert.Equal("Crashed", SessionOrdering.StateLabel(clause.Session));
                Assert.NotEqual(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(clause.Session));
            }
    }

    // ================================================================= the notes

    [Fact]
    public void TheVerdictNote_NamesExactlyTheColoursThatDependOnTheSwitch_AndEachClaimIsExecuted()
    {
        var legend = SessionColourLegend.Build();
        var note = legend.VerdictNote;

        // READ THE NOTE. It names the calm colours by their entry titles, and the reading yellow by its words.
        Assert.Contains("Done", note, StringComparison.Ordinal);
        Assert.Contains("Carrying on", note, StringComparison.Ordinal);
        Assert.Contains("Wingman reading a stop", note, StringComparison.Ordinal);

        // It must NOT claim anything about the colours the switch cannot touch.
        foreach (var title in new[] { "Needs you", "Working", "Ready", "Transcribing", "Supervised", "Snoozed or exited", "Crashed" })
            Assert.DoesNotContain(title, note, StringComparison.Ordinal);

        // EXECUTE IT. With the switch off the Gateway stamps no verdict on any row, so the calm rows fold red...
        foreach (var colour in new[] { "cyan", "purple" })
            foreach (var clause in ClausesFor(colour).Where(c => c.FoldsTo is null))
            {
                clause.Session.VerdictState = VerdictStates.None;
                clause.Session.TurnVerdict = null;
                Assert.Equal("red", SessionOrdering.EffectiveColor(clause.Session));
            }

        // ...and so does the reading yellow, while the two yellows the note does NOT name are untouched by it.
        var reading = ClausesFor("yellow").First(c => c.Session.VerdictState == VerdictStates.Reading);
        reading.Session.VerdictState = VerdictStates.None;
        Assert.Equal("red", SessionOrdering.EffectiveColor(reading.Session));

        foreach (var claim in new[] { "yellow-briefing", "yellow-voice" })
        {
            var clause = ClausesFor("yellow").Single(c => c.Session.SessionId == claim);
            clause.Session.VerdictState = VerdictStates.None;
            Assert.Equal("yellow", SessionOrdering.EffectiveColor(clause.Session));
        }
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
    public void EveryEntry_NamesAtLeastOneClause_AndEveryClaimSaysWhatItIsTesting()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            var clauses = ClausesFor(entry.Colour);
            Assert.NotEmpty(clauses);
            foreach (var clause in clauses) Assert.False(string.IsNullOrWhiteSpace(clause.Claim));
        }
    }

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
        var crashed = SessionColourLegend.Build().Entries.Single(e => e.Colour == "error");

        Assert.Contains("darker than the red that means needs you", crashed.Means, StringComparison.Ordinal);
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
