using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The legend's words, held to the fold that decides the colours - CLAUSE BY CLAUSE.
///
/// A legend that only renders its own strings proves nothing about whether they are TRUE: the first draft of this
/// legend passed tests of that kind with two false sentences in it ("or a schedule is driving it", and "with them off,
/// every stopped session is red"). One example per colour is not enough either - it binds the sentence's FIRST claim
/// and leaves the rest as prose. "You snoozed it, its agent exited, or its state could not be read" makes three
/// claims, so it carries three examples.
///
/// So every entry names its clauses, each clause carries a session built from exactly the facts that clause
/// describes, and each one is run through the real <see cref="SessionOrdering"/>: it must wear the colour the entry
/// promises, and it must sit on the side of the needs-you line the entry promises. A colour added to the legend
/// without clauses fails, because <see cref="ClausesFor"/> throws for a colour it does not know.
/// </summary>
public sealed class SessionColourLegendTests
{
    // The fold's vocabulary, spelled out literally rather than read from the palette under test.
    private static readonly string[] FoldColours =
        { "red", "yellow", "orange", "green", "cyan", "blue", "purple", "supporting", "error", "grey", "unknown" };

    /// <summary>One claim a sentence makes: the words, a session with exactly those facts, and the fold colour that
    /// session must wear (the entry's own, unless the clause is about a name that shares the entry - "unknown").</summary>
    private sealed record Clause(string Claim, SessionDto Session, string? FoldsTo = null);

    /// <summary>Every claim in an entry's sentence, each with the session that tests it.</summary>
    private static Clause[] ClausesFor(string colour) => colour switch
    {
        "red" =>
        [
            new("at a prompt", Stopped("red-prompt", "WaitingForInput")),
            new("on a permission", Stopped("red-permission", "WaitingForPerm")),
            new("with a question", Stopped("red-idle", "Idle")),
        ],
        "blue" =>
        [
            new("running a turn right now", Stopped("blue-working", "Working")),
            new("starting is running too", Stopped("blue-starting", "Starting")),
            // "always blue": snoozed, supervised and mid-dictation at once, and still working.
            new("always blue, whatever else is true", new SessionDto
            {
                SessionId = "blue-outranks-everything", ActivityState = "Working",
                OnHold = true, HasLiveSupervisor = true, Transcribing = true, IsBrandNew = true,
            }),
        ],
        "cyan" =>
        [
            new("the work is done", Judged("cyan-done", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindDone)),
            new("it is only reporting something", Judged("cyan-report", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindReport)),
        ],
        "purple" =>
        [
            new("it will continue on its own", Judged("purple-continues", SessionOrdering.VerdictContinuesAlone, finishedKind: null)),
        ],
        "yellow" =>
        [
            new("the Director is reading the stop", new SessionDto
            {
                SessionId = "yellow-briefing", ActivityState = "WaitingForInput", BriefingState = "Briefing",
            }),
            new("the Wingman is reading the stop", new SessionDto
            {
                SessionId = "yellow-verdict-reading", ActivityState = "WaitingForInput", VerdictState = VerdictStates.Reading,
            }),
            new("its voice summary is not ready yet", new SessionDto
            {
                SessionId = "yellow-voice", ActivityState = "WaitingForInput", VoiceMode = true, VoiceAudioReady = false,
            }),
        ],
        "green" =>
        [
            new("a brand-new session at its first prompt", new SessionDto
            {
                SessionId = "green-new", ActivityState = "WaitingForInput", IsBrandNew = true,
            }),
        ],
        "orange" =>
        [
            new("still uploading", new SessionDto
            {
                SessionId = "orange-uploading", ActivityState = "WaitingForInput", DictationStatus = "Uploading from phone",
            }),
            new("being turned into text", new SessionDto
            {
                SessionId = "orange-transcribing", ActivityState = "WaitingForInput", Transcribing = true,
            }),
            new("the desktop's own dictation counts too", new SessionDto
            {
                SessionId = "orange-desktop", ActivityState = "WaitingForInput", IsTranscribing = true,
            }),
        ],
        "supporting" =>
        [
            new("another live session is driving it", new SessionDto
            {
                SessionId = "supporting-supervised", ActivityState = "WaitingForInput", HasLiveSupervisor = true,
            }),
        ],
        "grey" =>
        [
            new("you snoozed it", new SessionDto { SessionId = "grey-snoozed", ActivityState = "WaitingForInput", OnHold = true }),
            new("its agent exited", new SessionDto { SessionId = "grey-exited", ActivityState = "Exited" }),
            // The fold's own word for a state it cannot read. A different NAME, the same pixel - which is why the
            // legend lets this entry speak for it (SessionColourLegend.SharesAnEntry).
            new("its state could not be read", new SessionDto { SessionId = "grey-unreadable", ActivityState = "SomethingNew" }, FoldsTo: "unknown"),
        ],
        "error" =>
        [
            new("the agent process died", new SessionDto { SessionId = "error-crashed", ActivityState = "Exited", Crashed = true }),
        ],
        _ => throw new InvalidOperationException(
            $"the legend has an entry for '{colour}' but this test names no claims for it - write one clause per thing " +
            "its sentence says, each with a session built from those facts, so the sentence is checked against the " +
            "fold rather than trusted"),
    };

    private static SessionDto Stopped(string id, string activity) => new() { SessionId = id, ActivityState = activity };

    private static SessionDto Judged(string id, string verdict, string? finishedKind) => new()
    {
        SessionId = id,
        ActivityState = "WaitingForInput",
        VerdictState = VerdictStates.Judged,
        VerdictLabel = "Pushed the branch",
        TurnVerdict = new TurnVerdictDto
        {
            VerdictId = id,
            Verdict = verdict,
            Confidence = SessionOrdering.ConfidenceHigh,
            FinishedKind = finishedKind,
            Label = "Pushed the branch",
        },
    };

    [Fact]
    public void EveryClause_OfEverySentence_FoldsToTheColourItsEntryPromises()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
            {
                var expected = clause.FoldsTo ?? entry.Colour;
                var actual = SessionOrdering.EffectiveColor(clause.Session);
                Assert.True(expected == actual,
                    $"'{entry.Title}' says \"{clause.Claim}\", but a session with those facts folds to '{actual}', not '{expected}'");
                // A clause that folds to a SHARED name must at least paint the same pixel, or the entry is speaking
                // for a colour the person would see as a different dot.
                Assert.Equal(SessionColorPalette.HexFor(entry.Colour), SessionColorPalette.HexFor(actual));
            }
    }

    [Fact]
    public void EveryClause_SitsOnTheSideOfTheNeedsYouLine_ItsEntryPromises()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            foreach (var clause in ClausesFor(entry.Colour))
            {
                var needsYou = SessionOrdering.Classify(clause.Session) == SessionOrdering.TriageBucket.NeedsYou;
                Assert.True(needsYou == (entry.AsksForYou == SessionColourLegend.AsksYes),
                    $"'{entry.Title}' says it asks for you: {entry.AsksForYou}, but \"{clause.Claim}\" lands in " +
                    $"{SessionOrdering.Classify(clause.Session)}");
            }
    }

    [Fact]
    public void EveryEntry_NamesAtLeastOneClause_AndEveryClauseSaysWhatItIsTesting()
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
        // "darker than the red that means needs you" - the part that can be executed is that they are not the same
        // pixel, which is the whole point of issue #959.
        Assert.NotEqual(SessionColorPalette.HexFor("red"), SessionColorPalette.HexFor("error"));
    }

    [Fact]
    public void TheVerdictNote_WithTheSwitchOff_TheCalmSessionsFoldRed_AndTheOtherYellowsDoNot()
    {
        // The note says two things. Execute both. With the switch off the Gateway stamps VerdictState "none" on every
        // row - so the same finished and carrying-on sessions, unstamped, must fold red...
        foreach (var colour in new[] { "cyan", "purple" })
            foreach (var clause in ClausesFor(colour))
            {
                clause.Session.VerdictState = VerdictStates.None;
                clause.Session.TurnVerdict = null;
                Assert.Equal("red", SessionOrdering.EffectiveColor(clause.Session));
            }

        // ...while the two yellows the note deliberately does not name - the Director's briefing and a voice summary -
        // are untouched by the switch.
        foreach (var claim in new[] { "the Director is reading the stop", "its voice summary is not ready yet" })
        {
            var clause = ClausesFor("yellow").Single(c => c.Claim == claim);
            clause.Session.VerdictState = VerdictStates.None;
            Assert.Equal("yellow", SessionOrdering.EffectiveColor(clause.Session));
        }
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
    public void TheBrokenNote_IsTheSentinel_AndNoRealColour()
    {
        var legend = SessionColourLegend.Build();

        Assert.Equal(SessionColorPalette.Broken, legend.Broken.Hex);
        Assert.DoesNotContain(legend.Entries, e => string.Equals(e.Hex, SessionColorPalette.Broken, StringComparison.OrdinalIgnoreCase));
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
