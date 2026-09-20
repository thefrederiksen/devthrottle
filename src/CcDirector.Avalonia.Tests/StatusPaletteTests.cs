using Avalonia.Headless.XUnit;
using Avalonia.Media;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Pins the ONE desktop palette (defect 18, mission "Session State Truth", phase 4).
///
/// There used to be five private palettes for the same colour names, and NOTHING tested any of
/// them - so nothing noticed that "red" meant #EF4444 on the rail, #E5484D in the turn review,
/// #F44747 in the removed FIFO window and the (dead) Director view, and #F14C4C on the phone. This file is
/// the C# half of the pin; the web/mobile client carries the same table in
/// packages/client-core/src/sessions/ordering.ts, and docs/new_architecture/sessions.html is
/// the single written source both sides cite. PaletteAgreementTests now READS that shipping TypeScript
/// table and asserts it equals the canonical map and this one, so the two sides are machine-checked to
/// agree instead of relying on a human to change both in the same pull request.
/// </summary>
public sealed class StatusPaletteTests
{
    // WHY [AvaloniaFact]/[AvaloniaTheory] AND NOT [Fact]/[Theory]: reading a brush's Colour is an Avalonia
    // property read, and it verifies it is on the dispatcher's thread. A plain [Fact] gets whatever thread
    // xUnit hands it, which is the dispatcher's only until another class in this assembly starts a headless
    // session - so the answer depends on who ran first. These run ON the dispatcher thread instead. Same
    // reason as SessionRailStateTests, where that ordering accident actually fired on Windows.
    // The canonical table, spelled out literally rather than referencing the constants - a test that
    // reads the value it is checking proves nothing.
    [AvaloniaTheory]
    [InlineData("red", "#EF4444")]          // red-500
    [InlineData("blue", "#3B82F6")]         // blue-500
    [InlineData("green", "#22C55E")]        // green-500
    [InlineData("cyan", "#06B6D4")]         // cyan-500 - finished, never the brand-new green (issue #2892)
    [InlineData("yellow", "#EAB308")]       // yellow-500
    [InlineData("orange", "#F97316")]       // orange-500
    [InlineData("purple", "#A855F7")]       // purple-500
    [InlineData("supporting", "#64748B")]   // slate-500
    [InlineData("error", "#B91C1C")]        // red-700 - crashed, NOT finished (issue #959)
    [InlineData("grey", "#6B7280")]         // gray-500
    public void BrushFor_FoldColour_IsTheCanonicalHex(string foldColor, string expectedHex)
    {
        Assert.Equal(Color.Parse(expectedHex), ((ISolidColorBrush)StatusPalette.BrushFor(foldColor)).Color);
        Assert.Equal(expectedHex, StatusPalette.HexFor(foldColor));
    }

    [AvaloniaFact]
    public void BrushFor_TheStrayHexesThatDied_AreInThePaletteNowhere()
    {
        // Every hex that a private palette once used for a name the canonical table also names.
        // If one of these ever comes back it means somebody re-hand-rolled a palette.
        // #9CA3AF (gray-400) is on this list twice over now: it was a hand-rolled snoozed grey, and it is
        // also the grey somebody reaching for "a lighter grey" would pick for the neutral - at 1.90:1 from
        // the palette grey it would be the very lie the neutral exists to avoid.
        var strays = new[] { "#E5484D", "#F44747", "#F14C4C", "#9CA3AF", "#6A6A6A", "#888888", "#5FD08A", "#2B6CB0", "#DCDCAA", "#F59E0B" };
        var live = new[] { "red", "blue", "green", "cyan", "yellow", "orange", "purple", "supporting", "error", "grey", "unknown" }
            .Select(StatusPalette.HexFor)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var stray in strays)
            Assert.DoesNotContain(stray, live);
    }

    [AvaloniaFact]
    public void BrushFor_Unknown_IsARealFoldColour_AndRendersTheOneGrey()
    {
        // "unknown" is EMITTED by the fold for an activity state it does not recognise, so this is a
        // mapping, not a fallback: an indeterminate session reads as one that is not asking for
        // anything, which is the honest answer when we do not know.
        Assert.Same(StatusPalette.BrushFor("grey"), StatusPalette.BrushFor("unknown"));
        Assert.True(StatusPalette.Knows("unknown"));
    }

    [AvaloniaFact]
    public void BrushFor_Unstamped_IsTheMagentaSentinel_NeverGrey()
    {
        // "unstamped" is the desktop's OWN sentinel (SessionViewModel.UnstampedSentinel): connected and
        // settled but the Gateway stamped nothing (issue #1966). It must render the magenta BROKEN pixel, not
        // grey - grey would read as "parked". It is deliberately NOT a Known fold colour; it routes to the
        // dedicated ReportMissingStamp log rather than the generic unknown-colour path.
        Assert.Equal(Color.Parse("#FF00FF"), ((ISolidColorBrush)StatusPalette.BrushFor("unstamped")).Color);
        Assert.NotEqual(Color.Parse(StatusPalette.Grey), ((ISolidColorBrush)StatusPalette.BrushFor("unstamped")).Color);
        Assert.False(StatusPalette.Knows("unstamped"));
        Assert.Equal(StatusPalette.Broken, StatusPalette.HexFor("unstamped"));
    }

    [AvaloniaFact]
    public void BrushFor_ANameTheFoldNeverEmits_IsTheNeutral_NeverGreyAndNeverTheMagentaAlarm()
    {
        // A name this build does not know means one thing: this Director is OLDER than its Gateway. The
        // fold gained cyan in v2.4.0 and every Director below it painted magenta for a session the Wingman
        // had judged finished - an old build borrowing an alarm that belongs to a broken push seam.
        //
        // So: NEVER grey (grey MEANS snoozed or exited - an affirmative lie that the session is parked),
        // and NEVER the magenta sentinel (which now means only "the Gateway stamped nothing"). The neutral,
        // which is not a state and does not claim to be one.
        foreach (var nonsense in new[] { "something-nobody-folds", "chartreuse", "", null })
        {
            Assert.False(StatusPalette.Knows(nonsense));

            var painted = ((ISolidColorBrush)StatusPalette.BrushFor(nonsense)).Color;
            Assert.Equal(Color.Parse("#E5E7EB"), painted);
            Assert.NotEqual(Color.Parse(StatusPalette.Grey), painted);
            Assert.NotEqual(Color.Parse(StatusPalette.Broken), painted);
            Assert.Equal("#E5E7EB", StatusPalette.HexFor(nonsense));
        }
    }

    /// <summary>
    /// THE TWO FAULTS MUST STAY TELLABLE APART - the standing constraint on the Session Cards mission's
    /// first item. Before it, both painted magenta, so the rail could not distinguish "this Director is
    /// older than its Gateway" (ordinary, fix by updating) from "the display-state push is not delivering"
    /// (a real alarm). A test that only asserted each arm on its own would pass if some later edit pointed
    /// them back at one pixel, so this one asserts the DIFFERENCE.
    /// </summary>
    [AvaloniaFact]
    public void AnUnknownName_AndAMissingStamp_AreTwoDifferentPixels()
    {
        var unknownName = ((ISolidColorBrush)StatusPalette.BrushFor("a-colour-this-build-never-heard-of")).Color;
        var missingStamp = ((ISolidColorBrush)StatusPalette.BrushFor(SessionViewModel.UnstampedSentinel)).Color;

        Assert.NotEqual(unknownName, missingStamp);
        Assert.Equal(Color.Parse(StatusPalette.Neutral), unknownName);
        Assert.Equal(Color.Parse(StatusPalette.Broken), missingStamp);
        Assert.NotEqual(StatusPalette.HexFor("a-colour-this-build-never-heard-of"),
                        StatusPalette.HexFor(SessionViewModel.UnstampedSentinel));
    }

    /// <summary>
    /// The neutral is a MEASURED choice, not a taste: it has to be obviously a different grey from the
    /// palette grey, which on this rail MEANS snoozed or exited. "Obviously" is 3:1, the bar for telling
    /// one user-interface element from another, and gray-400 #9CA3AF - the grey a reader reaching for
    /// "lighter" would pick first - fails it at 1.90:1. Pinned here so a later tidy-up cannot quietly
    /// darken the neutral back into the state it must not be mistaken for.
    /// </summary>
    [AvaloniaFact]
    public void Neutral_IsObviouslyADifferentGreyFromTheOneThatMeansSnoozedOrExited()
    {
        Assert.True(ContrastRatio(StatusPalette.Neutral, StatusPalette.Grey) >= 3.0,
            $"the neutral {StatusPalette.Neutral} is only " +
            $"{ContrastRatio(StatusPalette.Neutral, StatusPalette.Grey):F2}:1 from the palette grey " +
            $"{StatusPalette.Grey}, which MEANS snoozed or exited. A neutral that close is an affirmative " +
            "claim that the session is parked.");

        // And it must be plainly visible on the two surfaces the rail is drawn on (docs/VisualStyle.md:
        // PanelBackground and SidebarBackground). The owner's words were "so you can still see the dot".
        Assert.True(ContrastRatio(StatusPalette.Neutral, "#1E1E1E") >= 3.0);
        Assert.True(ContrastRatio(StatusPalette.Neutral, "#252526") >= 3.0);

        // The grey that does NOT clear the bar, so the assertion above is known to be capable of failing.
        Assert.True(ContrastRatio("#9CA3AF", StatusPalette.Grey) < 3.0);
    }

    /// <summary>The WCAG relative-contrast ratio between two hexes. Written out rather than referenced so
    /// the test does not measure with the same arithmetic the value was chosen by.</summary>
    private static double ContrastRatio(string a, string b)
    {
        static double Channel(int v)
        {
            var c = v / 255.0;
            return c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        }

        static double Luminance(string hex)
        {
            var h = hex.TrimStart('#');
            var r = Convert.ToInt32(h.Substring(0, 2), 16);
            var g = Convert.ToInt32(h.Substring(2, 2), 16);
            var b = Convert.ToInt32(h.Substring(4, 2), 16);
            return 0.2126 * Channel(r) + 0.7152 * Channel(g) + 0.0722 * Channel(b);
        }

        var (la, lb) = (Luminance(a), Luminance(b));
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    /// <summary>
    /// THE UNREACHABILITY PROOF, and the reason the sentinel is a tripwire rather than a guess.
    ///
    /// Drives the REAL fold (<see cref="SessionOrdering.EffectiveColor"/> - the same function the
    /// rail, the Cockpit and the phone call) across every activity state crossed with every overlay
    /// it folds, and asserts the palette knows every colour it can produce. Deliberately NOT a
    /// hand-copied list of nine names: a list would rot the moment the fold learned a tenth, which is
    /// how the desktop came to have five palettes in the first place. If anyone teaches the fold a new
    /// colour without teaching this palette, this goes red and names the colour.
    /// </summary>
    [AvaloniaFact]
    public void EveryColourTheRealFoldCanEmit_IsKnownToThePalette()
    {
        var emitted = EveryColourTheRealFoldCanEmit();

        Assert.NotEmpty(emitted);
        foreach (var color in emitted)
            Assert.True(StatusPalette.Knows(color),
                $"The fold can emit '{color}' and the desktop palette does not know it, so the rail would " +
                $"render the BROKEN magenta sentinel. Add it to StatusPalette AND to the palette table in " +
                $"docs/new_architecture/sessions.html AND to the client's ordering.ts, in one pull request.");
    }

    /// <summary>
    /// The CANONICAL half of the exhaustiveness proof. The desktop palette test above
    /// asserts StatusPalette.Knows, but the Gateway stamps EffectiveColorHex from the CANONICAL map
    /// (SessionColorPalette), and an unknown name there stamps the magenta sentinel. So a future fold colour
    /// could escape if the desktop table were taught it while the canonical map was not. Drive the SAME real
    /// fold and assert SessionColorPalette knows every colour it can emit, so the canonical map is provably
    /// exhaustive over the fold - not just the desktop table.
    /// </summary>
    [AvaloniaFact]
    public void EveryColourTheRealFoldCanEmit_IsKnownToTheCanonicalMap()
    {
        var emitted = EveryColourTheRealFoldCanEmit();

        Assert.NotEmpty(emitted);
        foreach (var color in emitted)
            Assert.True(SessionColorPalette.Knows(color),
                $"The fold can emit '{color}' and the canonical SessionColorPalette does not know it, so the " +
                "Gateway would stamp the magenta BROKEN sentinel for it. Add it to SessionColorPalette AND to " +
                "docs/new_architecture/sessions.html AND to the client's ordering.ts, in one pull request.");
    }

    /// <summary>Every colour the REAL fold (<see cref="SessionOrdering.EffectiveColor"/>) can emit across
    /// every activity state crossed with every overlay it folds. Deliberately NOT a hand-copied list of
    /// names: a list would rot the moment the fold learned a tenth colour, which is how the desktop came to
    /// have five palettes in the first place.</summary>
    private static HashSet<string> EveryColourTheRealFoldCanEmit()
    {
        var states = new[] { "Starting", "Working", "WaitingForInput", "WaitingForPerm", "Idle", "Exited", "NonsenseState" };
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var state in states)
            foreach (var onHold in new[] { false, true })
                foreach (var crashed in new[] { false, true })
                    foreach (var brandNew in new[] { false, true })
                        foreach (var transcribing in new[] { false, true })
                            foreach (var background in new[] { false, true })
                                foreach (var autoExplain in new[] { false, true })
                                    foreach (var role in new[] { SessionRoles.Standalone, SessionRoles.Worker, SessionRoles.Manager })
                                        // "None" is the DTO's own default; null is not a legal value for it.
                                        foreach (var briefing in new[] { "None", "Briefing", "Briefed", "Explaining" })
                                        {
                                            var dto = new SessionDto
                                            {
                                                SessionId = "x",
                                                ActivityState = state,
                                                OnHold = onHold,
                                                Crashed = crashed,
                                                IsBrandNew = brandNew,
                                                IsTranscribing = transcribing,
                                                IsBackgroundRunning = background,
                                                IsAutoExplaining = autoExplain,
                                                WingmanEnabled = true,
                                                SessionRole = role,
                                                BriefingState = briefing,
                                            };
                                            emitted.Add(SessionOrdering.EffectiveColor(dto));
                                        }

        return emitted;
    }

    [AvaloniaFact]
    public void BrushFor_IsCaseInsensitive()
    {
        // The names cross the wire. SessionOrdering.RawActivityColor being a case-SENSITIVE switch
        // while its neighbours are not is defect 16; this side must not add to that.
        Assert.Same(StatusPalette.BrushFor("red"), StatusPalette.BrushFor("RED"));
        Assert.Same(StatusPalette.BrushFor("supporting"), StatusPalette.BrushFor("Supporting"));
    }

    [AvaloniaFact]
    public void Error_IsNotNeedsYouRed()
    {
        // Issue #959. A session that DIED must never read as one that is merely waiting on you.
        Assert.NotEqual(StatusPalette.Error, StatusPalette.Red);
        Assert.NotSame(StatusPalette.BrushFor("error"), StatusPalette.BrushFor("red"));
    }
}
