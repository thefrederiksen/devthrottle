using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The legend's words, held to the fold that decides the colours - EVERY CLAIM, THE RELATION BETWEEN THEM, AND THE
/// NOTE.
///
/// Five rounds of review shaped this file, each finding the same species of defect in a narrower place - prose a
/// person reads that no test executes:
///   1. Tests that only rendered the legend's own strings proved nothing; two sentences were false the day they were
///      written ("or a schedule is driving it"; "with them off, every stopped session is red").
///   2. One example per colour executed a sentence's FIRST claim and left the rest as prose.
///   3. A clause table keyed by colour was not tied to the served words at all.
///   4. Claims checked by substring let a false statement be grown around true ones.
///   5. The joining words still carried meaning (", " where the fold means ", or ") and the verdict note was free
///      prose ("Blue also needs that switch" would have shipped).
///
///   6. The "all of these" relation was never validated (declaring it would render "and" over exclusive states), and
///      the magenta note was still free prose ("Magenta means the session is working." would have shipped).
///
/// So: a sentence is structure, not prose. This file demands a session for EVERY claim, proves a sentence's
/// alternatives really are DIFFERENT SITUATIONS, checks the rendered words are exactly what the structure produces,
/// demands an executable check for every claim in BOTH notes, and holds the one piece of advice - which cannot be
/// executed - to saying nothing about what a session is doing.
/// </summary>
public sealed class SessionColourLegendTests
{
    /// <summary>The carrying-on sentence, named once because two tests reach for it.</summary>
    private const string PurpleClaim =
        "The session stopped, but the Wingman judged it will continue on its own, and it turns red if it does not";

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

        // Snoozed, supervised, mid-dictation and brand-new at once - and still blue, which is what "always" claims.
        ("blue", "The agent is running a turn right now, and a working session is always blue") => new(new SessionDto
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

        // The row is purple NOW; that it turns red is the carrying-on clock's promise, executed against the real
        // watchdog in TheCarryingOnPromise_IsKeptByTheRealClock below.
        ("purple", PurpleClaim) => new(CarryingOn("purple-continues"), "Pushed the branch"),

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

        ("green", "A brand-new session at its first prompt, which has not done anything yet") => new(BrandNew("green-new"), "Ready"),

        ("orange", "Your dictation is on its way") => new(Transcribing("orange-on-its-way"), "Transcribing"),
        ("orange", "it is still uploading from your phone") => new(new SessionDto
        {
            SessionId = "orange-uploading", ActivityState = "WaitingForInput", DictationStatus = "Uploading from phone",
        }, "Uploading from phone"),
        ("orange", "it is being turned into text") => new(Transcribing("orange-transcribing"), "Transcribing"),

        ("supporting", "Stopped, but another live session is driving it, so it waits on that session instead of you") =>
            new(Supervised("supporting-driven"), "Snoozed"),

        ("grey", "This session is resting") =>
            new(new SessionDto { SessionId = "grey-resting", ActivityState = "WaitingForInput", OnHold = true }, "Snoozed"),
        ("grey", "you snoozed it") =>
            new(new SessionDto { SessionId = "grey-snoozed", ActivityState = "WaitingForInput", OnHold = true }, "Snoozed"),
        ("grey", "its agent exited") => new(new SessionDto { SessionId = "grey-exited", ActivityState = "Exited" }, "Exited"),
        // A different NAME for the same pixel, and - as round 3 found - its label is "Idle", not a word about being
        // unreadable. The sentence says so now, and the trailing claim checks that word.
        ("grey", "its state could not be read") => new(Unreadable("grey-unreadable"), "Idle", FoldsTo: "unknown"),

        ("error", "The agent process died") => new(Crashed("error-died"), "Crashed"),

        _ => throw new InvalidOperationException(
            $"the legend's '{colour}' sentence claims \"{claim}\" and no session here tests it. Write one built from " +
            "the facts those words describe - a claim nobody executes is prose, and prose is how two false sentences " +
            "reached this legend already."),
    };

    /// <summary>What a claim of the verdict note promises, executed. Throws for a claim with no check - which is what
    /// stopped the note being free prose (round 5: "Blue also needs that switch" would have shipped).</summary>
    private static void CheckNoteClaim(string claim)
    {
        switch (claim)
        {
            case "Done and Carrying on appear only when the Wingman's verdicts are switched on for your account":
                // "only": the two it names lose their colour without a verdict, and NOTHING ELSE does.
                foreach (var colour in new[] { "cyan", "purple" })
                    foreach (var text in SessionColourLegend.ClaimsFor(colour))
                        Assert.Equal("red", SessionOrdering.EffectiveColor(Unstamped(PromiseFor(colour, text).Session)));

                foreach (var entry in SessionColourLegend.Build().Entries.Where(e => e.Colour is not ("cyan" or "purple")))
                    foreach (var text in SessionColourLegend.ClaimsFor(entry.Colour))
                    {
                        var promise = PromiseFor(entry.Colour, text);
                        var expected = promise.FoldsTo ?? entry.Colour;
                        Assert.True(expected == SessionOrdering.EffectiveColor(Unstamped(promise.Session)),
                            $"the note says only Done and Carrying on need the switch, but '{entry.Title}' / \"{text}\" " +
                            $"changes without a verdict");
                    }
                return;

            case "With them off, those sessions show red instead":
                foreach (var colour in new[] { "cyan", "purple" })
                    foreach (var text in SessionColourLegend.ClaimsFor(colour))
                    {
                        var row = Unstamped(PromiseFor(colour, text).Session);
                        Assert.Equal("red", SessionOrdering.EffectiveColor(row));
                        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou, SessionOrdering.Classify(row));
                    }
                return;

            default:
                throw new InvalidOperationException(
                    $"the verdict note claims \"{claim}\" and nothing here executes it. The note is read verbatim by " +
                    "every client, so a sentence added to it without a check is prose in front of the owner.");
        }
    }

    /// <summary>What a claim of the magenta note promises, executed. Throws for a claim with no check.</summary>
    private static void CheckBrokenClaim(string claim)
    {
        switch (claim)
        {
            case "Not a state":
                // No colour the fold can emit paints this pixel, so it can never be mistaken for one.
                foreach (var colour in FoldColours)
                    Assert.NotEqual(SessionColorPalette.Broken, SessionColorPalette.HexFor(colour));
                return;

            case "The screen received a colour this app does not understand, so reload and report it if it stays":
                // A name outside the vocabulary is what produces it - that is the whole mechanism.
                Assert.False(SessionColorPalette.Knows("teal-ish"));
                Assert.Equal(SessionColorPalette.Broken, SessionColorPalette.HexFor("teal-ish"));
                return;

            default:
                throw new InvalidOperationException(
                    $"the magenta note claims \"{claim}\" and nothing here executes it. It is read verbatim by every " +
                    "client, so a sentence added to it without a check is prose in front of the owner.");
        }
    }

    private static SessionDto Unstamped(SessionDto session)
    {
        session.VerdictState = VerdictStates.None;
        session.TurnVerdict = null;
        session.VerdictLabel = null;
        return session;
    }

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

    /// <summary>The facts a session is built from, as a fingerprint - two alternatives that produce the same one are
    /// not alternatives at all.</summary>
    private static string Facts(SessionDto s) =>
        $"{s.ActivityState}|{s.OnHold}|{s.Crashed}|{s.IsBrandNew}|{s.HasLiveSupervisor}|{s.Transcribing}|" +
        $"{s.IsTranscribing}|{s.DictationStatus}|{s.BriefingState}|{s.VoiceMode}|{s.VoiceAudioReady}|" +
        $"{s.VerdictState}|{s.TurnVerdict?.Verdict}|{s.TurnVerdict?.FinishedKind}";

    // ================================================================= the sentence is its claims

    [Fact]
    public void EverySentence_IsExactlyWhatItsStructureRenders()
    {
        // Nobody types the joining words: they come from the structure. So the served text cannot carry a statement
        // that is not a claim, and cannot say "and" where the fold means "or".
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            var sentence = SessionColourLegend.SentenceFor(entry.Colour);
            Assert.NotNull(sentence);
            Assert.Equal(sentence!.Render(), entry.Means);
            foreach (var claim in sentence.Claims) Assert.False(string.IsNullOrWhiteSpace(claim));
        }
    }

    [Fact]
    public void TheAlternativesOfAOneOf_AreDifferentSituations()
    {
        // Round 5's finding: the joining words carried meaning. ", or " says these are cases of one another, so the
        // sessions behind them must differ in the facts the fold reads. Two "alternatives" built from identical facts
        // are one case written twice, and the "or" would be a lie.
        foreach (var entry in SessionColourLegend.Build().Entries)
        {
            var sentence = SessionColourLegend.SentenceFor(entry.Colour)!;
            if (sentence.Alternatives.Count < 2) continue;

            var fingerprints = sentence.Alternatives
                .Select(a => Facts(PromiseFor(entry.Colour, a).Session))
                .ToList();

            Assert.True(fingerprints.Distinct(StringComparer.Ordinal).Count() == fingerprints.Count,
                $"'{entry.Title}' offers its cases as alternatives, but two of them are the same session: " +
                string.Join(" / ", sentence.Alternatives));
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

    // ================================================================= promises about time

    [Fact]
    public void TheCarryingOnPromise_IsKeptByTheRealClock_NotByAVerdictThisTestWrote()
    {
        // Round 4's finding: this used to hand the fold an already-expired verdict of its own making, so deleting the
        // watchdog would have left "It turns red if it does not" false with the test still green.
        var row = PromiseFor("purple", PurpleClaim).Session;
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
    public void TheVerdictNote_IsItsClaims_AndEveryClaimIsExecuted()
    {
        var legend = SessionColourLegend.Build();

        // The note a person reads is exactly the claims, so it cannot grow a sentence nobody checks.
        Assert.Equal(string.Join(" ", SessionColourLegend.NoteClaims.Select(c => c + ".")), legend.VerdictNote);
        Assert.NotEmpty(SessionColourLegend.NoteClaims);

        foreach (var claim in SessionColourLegend.NoteClaims) CheckNoteClaim(claim);
    }

    [Fact]
    public void TheBrokenNote_IsItsClaimsAndItsAdvice_AndEveryClaimIsExecuted()
    {
        var legend = SessionColourLegend.Build();

        Assert.Equal(SessionColorPalette.Broken, legend.Broken.Hex);
        // The words a person reads are exactly the claims and the advice, so the note cannot grow a sentence nobody
        // checks - round 5 found "Magenta means the session is working." would have shipped.
        Assert.Equal(
            string.Join(" ", SessionColourLegend.BrokenClaims.Select(c => c + ".")),
            legend.Broken.Means);
        Assert.NotEmpty(SessionColourLegend.BrokenClaims);

        foreach (var claim in SessionColourLegend.BrokenClaims) CheckBrokenClaim(claim);
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
        // Round 1's finding: the first draft said "or a schedule is driving it". A schedule is not supervision -
        // IsSupervised reads only a live supervisor - so a stopped session without one is red, whoever started it.
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
