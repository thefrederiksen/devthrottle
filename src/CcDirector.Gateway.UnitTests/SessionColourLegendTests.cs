using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The legend's words, held to the fold that decides the colours.
///
/// A legend that only renders its own strings proves nothing about whether they are TRUE - the first draft of this
/// legend passed every test of that kind with two false sentences in it ("or a schedule is driving it", and "with
/// them off, every stopped session is red"). So every entry here is EXECUTED: <see cref="ExampleFor"/> builds a
/// session with the facts the entry's sentence describes, and the real <see cref="SessionOrdering"/> must give it that
/// colour and the matching needs-you answer. An entry added without an example fails, because the example switch
/// throws for a colour it does not know.
/// </summary>
public sealed class SessionColourLegendTests
{
    // The fold's vocabulary, spelled out literally rather than read from the palette under test.
    private static readonly string[] FoldColours =
        { "red", "yellow", "orange", "green", "cyan", "blue", "purple", "supporting", "error", "grey", "unknown" };

    /// <summary>A session with exactly the facts the entry's sentence names, and nothing else.</summary>
    private static SessionDto ExampleFor(string colour) => colour switch
    {
        // "stopped and is waiting for you"
        "red" => new SessionDto { SessionId = "example-red", ActivityState = "WaitingForInput" },
        // "running a turn right now"
        "blue" => new SessionDto { SessionId = "example-blue", ActivityState = "Working" },
        // "stopped and the Wingman judged it finished ... only reporting"
        "cyan" => Judged("example-cyan", SessionOrdering.VerdictFinished, SessionOrdering.FinishedKindReport),
        // "stopped, but the Wingman judged it will continue on its own"
        "purple" => Judged("example-purple", SessionOrdering.VerdictContinuesAlone, finishedKind: null),
        // "the Director is reading the stop" - the yellow that does NOT depend on the verdict switch
        "yellow" => new SessionDto { SessionId = "example-yellow", ActivityState = "WaitingForInput", BriefingState = "Briefing" },
        // "a brand-new session at its first prompt"
        "green" => new SessionDto { SessionId = "example-green", ActivityState = "WaitingForInput", IsBrandNew = true },
        // "your dictation is still ... being turned into text"
        "orange" => new SessionDto { SessionId = "example-orange", ActivityState = "WaitingForInput", Transcribing = true },
        // "stopped, but another live session is driving it" - a live supervisor and nothing else
        "supporting" => new SessionDto { SessionId = "example-supporting", ActivityState = "WaitingForInput", HasLiveSupervisor = true },
        // "you snoozed it"
        "grey" => new SessionDto { SessionId = "example-grey", ActivityState = "WaitingForInput", OnHold = true },
        // "the agent process died"
        "error" => new SessionDto { SessionId = "example-error", ActivityState = "Exited", Crashed = true },
        _ => throw new InvalidOperationException(
            $"the legend has an entry for '{colour}' but this test has no example session for it - write the facts " +
            "its sentence describes, so the sentence is checked against the fold rather than trusted"),
    };

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
    public void EveryEntry_ItsExampleSession_FoldsToTheEntrysColour()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
            Assert.True(entry.Colour == SessionOrdering.EffectiveColor(ExampleFor(entry.Colour)),
                $"the legend says '{entry.Title}' is {entry.Colour}, but a session with those facts folds to " +
                $"'{SessionOrdering.EffectiveColor(ExampleFor(entry.Colour))}'");
    }

    [Fact]
    public void EveryEntry_AsksForYouYes_IsExactlyTheNeedsYouBucket()
    {
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            var needsYou = SessionOrdering.Classify(ExampleFor(entry.Colour)) == SessionOrdering.TriageBucket.NeedsYou;
            Assert.True(needsYou == (entry.AsksForYou == SessionColourLegend.AsksYes),
                $"'{entry.Title}' says it asks for you: {entry.AsksForYou}, but the fold puts its example in " +
                $"{SessionOrdering.Classify(ExampleFor(entry.Colour))}");
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
    public void ASharedName_PointsAtAnEntry_ThatPaintsTheSamePixel()
    {
        var unreadable = new SessionDto { SessionId = "example-unknown", ActivityState = "SomethingNew" };
        Assert.Equal("unknown", SessionOrdering.EffectiveColor(unreadable));

        foreach (var (name, entryColour) in SessionColourLegend.SharesAnEntry)
        {
            Assert.Contains(SessionColourLegend.Build().Entries, e => e.Colour == entryColour);
            Assert.Equal(SessionColorPalette.HexFor(entryColour), SessionColorPalette.HexFor(name));
        }
    }

    [Fact]
    public void TheVerdictNote_WithTheSwitchOff_TheCalmSessionsFoldRed_AndTheDirectorsYellowDoesNot()
    {
        // The note says two things. Execute both. With the switch off the Gateway stamps VerdictState "none" on every
        // row - so the same finished and carrying-on sessions, unstamped, must fold red...
        foreach (var calm in new[] { ExampleFor("cyan"), ExampleFor("purple") })
        {
            calm.VerdictState = VerdictStates.None;
            Assert.Equal("red", SessionOrdering.EffectiveColor(calm));
        }

        // ...while the Director's briefing yellow, which the note deliberately does not name, stays yellow.
        var briefing = ExampleFor("yellow");
        briefing.VerdictState = VerdictStates.None;
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(briefing));
    }

    [Fact]
    public void TheSupervisedEntry_IsALiveSupervisor_NotASchedule()
    {
        // The first draft said "or a schedule is driving it". A schedule is not supervision: IsSupervised reads only a
        // live supervisor. A stopped session with no live supervisor is red, whoever started it.
        var noSupervisor = new SessionDto { SessionId = "example-scheduled", ActivityState = "WaitingForInput", HasLiveSupervisor = false };

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
