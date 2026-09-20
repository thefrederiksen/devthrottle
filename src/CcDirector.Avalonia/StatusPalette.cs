using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The desktop's Avalonia-brush adapter over the ONE canonical palette. This is the whole of defect 18.
///
/// The colour NAMES are the shared fold's (SessionOrdering.EffectiveColor). Every COLOUR's hex
/// references <see cref="SessionColorPalette"/> in CcDirector.Gateway.Contracts - the single source the
/// Gateway also stamps from onto <see cref="SessionDto.EffectiveColorHex"/>. The desktop is the same C#
/// solution as the Gateway, so referencing that canonical map compile-time IS sharing the Gateway's own
/// source of truth, not a second copy that can drift. (A change to the canonical hex therefore needs a
/// desktop rebuild; the two ship together as one solution, so a colour never lands on one and not the
/// other.) This class stays because the rail and the turn review still need Avalonia brushes and the two
/// sentinels; only its COLOUR values moved to the canonical map.
///
/// EXACTLY ONE hex is written here and not read from the canonical map: <see cref="Neutral"/>, the pixel
/// this client paints when it is handed a colour NAME it has never heard of. It answers to no fold name,
/// so it has no row in a name-to-hex map; its own docs carry the argument and the measurements.
///
/// The web/mobile client (packages/client-core/src/sessions/ordering.ts) cannot reference C#, so it
/// carries its own COLORS table for legend swatches AND renders the Gateway-stamped hex for a real
/// session dot. The StateAgreementCheck asserts canonical == this table == that COLORS table every run,
/// so the three can never drift - which is the guard that ends this defect for good.
///
/// There used to be FIVE private palettes for the same colour names: the rail said red was #EF4444,
/// the turn review said #E5484D, the (dead) Director view said #F44747 - and there it meant EXITED,
/// not needs-you - the removed FIFO window said #F44747, and the web client said #F14C4C. Every one of them
/// was a hand-rolled switch beside the code that used it. Nothing tested any of them, so nothing
/// noticed that "the same colour" meant five different pixels.
///
/// TWO deliberate departures from the 500 ramp, both load-bearing:
///   - Error is red-700, not red-500. A session that DIED must never read as one that finished
///     (issue #959).
///   - Grey is ONE grey. Snoozed, exited, and indeterminate all render it, because the fold folds
///     them to one "grey" string on purpose, so clients render them identically. Whether snoozed
///     deserves its own dot colour is an open product question; if the answer is ever yes, it must
///     arrive as a distinct NAME from the fold, never as a client re-reading a raw flag.
/// </summary>
public static class StatusPalette
{
    public const string Red        = SessionColorPalette.Red;         // red-500      - needs you
    public const string Blue       = SessionColorPalette.Blue;        // blue-500     - working
    public const string Green      = SessionColorPalette.Green;       // green-500    - ready (brand new)
    public const string Cyan       = SessionColorPalette.Cyan;        // cyan-500     - the Wingman judged the stop finished (issue #2892)
    public const string Yellow     = SessionColorPalette.Yellow;      // yellow-500   - wingman reading / preparing voice
    public const string Orange     = SessionColorPalette.Orange;      // orange-500   - dictation in flight / deep dive
    public const string Purple     = SessionColorPalette.Purple;      // purple-500   - the Wingman judged it carrying on by itself
    public const string Supporting = SessionColorPalette.Supporting;  // slate-500    - a live Worker's suppressed red
    public const string Error      = SessionColorPalette.Error;       // red-700      - crashed, NOT finished (issue #959)
    public const string Grey       = SessionColorPalette.Grey;        // gray-500     - snoozed, exited, or indeterminate

    /// <summary>
    /// The BROKEN sentinel - magenta. Not a state, and since the Session Cards mission it means ONE
    /// thing and not two: <b>the Director is connected to its Gateway and settled, and the Gateway has
    /// stamped NO display state at all</b> (<c>SessionViewModel.UnstampedSentinel</c>, issue #1966).
    /// The push seam is not delivering. It exists to be impossible to misread.
    ///
    /// IT NO LONGER MEANS "A COLOUR NAME THIS BUILD DOES NOT KNOW". That was its second job and it was
    /// the wrong sentinel for it, because the two faults are not the same fault: an unrecognised name is
    /// a Director OLDER than its Gateway, which is an ordinary and expected state of a fleet that
    /// updates one machine at a time - the fold gained cyan in v2.4.0 and every Director below that
    /// version started painting a magenta alarm for a session the Wingman had judged finished. An old
    /// build borrowing an alarm that belongs to a broken seam is a false alarm, and a false alarm
    /// teaches the owner to stop believing a real one. An unrecognised name now paints
    /// <see cref="Neutral"/>; see its docs. The two faults stay distinguishable in the log as well as
    /// on the dot - <see cref="ReportMissingStamp"/> against <see cref="ReportUnknownColor"/> - and
    /// <c>StatusPaletteTests</c> asserts the distinction rather than leaving it to be read off the code.
    ///
    /// Why a sentinel and not a throw. A throw was the obvious answer and it is WRONG, by
    /// observation rather than argument: StatusColorBrush is read through a XAML binding, and when a
    /// bound getter throws, Avalonia swallows it and leaves the property unset - the probe rendered
    /// <c>Background = null</c>. That is an INVISIBLE dot: silent AND broken, strictly quieter than
    /// the grey it would have replaced. A log line alone is not loud either - the owner is looking at
    /// the rail, not the log. So: magenta on the dot (loud, and unmistakable for any real state) AND
    /// a logged error (so it is diagnosable).
    /// </summary>
    public const string Broken     = SessionColorPalette.Broken;  // magenta - NOT a state; "the Gateway stamped nothing"

    /// <summary>
    /// The NEUTRAL - the pixel this desktop paints for a fold colour name it has never heard of. Not a
    /// state either, and deliberately not an alarm.
    ///
    /// <b>The owner's ruling (Session Cards mission):</b> <em>"I don't like painting the hex. That's just
    /// stupid. If we don't know the color, we could leave the color blank ... just make it very, very
    /// light gray so you can still see the dot, with the square, but don't print text."</em> The rejected
    /// alternative was pushing the Gateway's resolved hex down the seam so an old build could paint the
    /// right pixel; he was right to reject it, because a build painting a colour it has never heard of
    /// has no legend entry for it, no words and nothing to hover - it would look correct and mean
    /// nothing.
    ///
    /// <b>WHY NOT <see cref="Grey"/>, AND WHY NOT A GREY NEAR IT.</b> <c>#6B7280</c> is a REAL STATE on
    /// this rail: it means snoozed or exited. A neutral that reads as that grey is not "we do not know",
    /// it is an affirmative claim that the session is parked - which is precisely why this arm was
    /// magenta in the first place. So the neutral has to be obviously a DIFFERENT grey, and "obviously"
    /// is measured, not asserted:
    ///
    /// <list type="bullet">
    ///   <item>gray-200 <c>#E5E7EB</c> against the palette grey <c>#6B7280</c> is <b>3.90:1</b> - above the
    ///     3:1 that separates one user-interface element from another, with margin.</item>
    ///   <item>Against the two surfaces the rail is actually drawn on - <c>PanelBackground #1E1E1E</c> and
    ///     <c>SidebarBackground #252526</c> in docs/VisualStyle.md - it is <b>13.5:1</b> and <b>12.4:1</b>,
    ///     so the dot is plainly there, which is the owner's "so you can still see the dot".</item>
    ///   <item>It is also clear of <c>supporting</c> slate-500 <c>#64748B</c> at <b>3.84:1</b>.</item>
    /// </list>
    ///
    /// The candidates that were measured and rejected: gray-400 <c>#9CA3AF</c> reaches only <b>1.90:1</b>
    /// against the palette grey - indistinguishable at the size of a 14-pixel dot - and is one of the
    /// hand-rolled strays <c>StatusPaletteTests</c> already forbids; gray-300 <c>#D1D5DB</c> passes at
    /// <b>3.28:1</b> but buys nothing over gray-200; gray-100 <c>#F3F4F6</c> is <b>1.10:1</b> from white,
    /// and a white dot on this rail reads as a highlight, which is a signal of its own.
    ///
    /// <b>ONE THEME, SAID OUT LOUD.</b> The desktop Director is dark-only - <c>App.axaml</c> pins
    /// <c>RequestedThemeVariant="Dark"</c> and nothing anywhere changes it - so those are the only two
    /// backgrounds this pixel is ever drawn on. The browser shells have light and dark and do NOT render
    /// this neutral: they paint the hex the Gateway stamped, and the Gateway is never older than itself.
    ///
    /// <b>WHY THE HEX IS WRITTEN HERE AND NOT IN <see cref="SessionColorPalette"/>.</b> That map is
    /// fold-NAME to pixel, and the neutral answers to no name - it is what this client paints when it has
    /// no name it recognises. Putting it in the canonical map would invite the next reader to give it a
    /// name and teach the fold to emit it, which is the one thing this mission must not do. It is also
    /// the only hex in this file that is not a reference to the canonical map, and that is the honest
    /// signal that it is a rendering decision of this client's, not a colour of the product's.
    /// </summary>
    public const string Neutral    = "#E5E7EB";  // gray-200 - NOT a state; "this build does not know that colour name"

    private static readonly ISolidColorBrush RedBrush        = new SolidColorBrush(Color.Parse(Red));
    private static readonly ISolidColorBrush BlueBrush       = new SolidColorBrush(Color.Parse(Blue));
    private static readonly ISolidColorBrush GreenBrush      = new SolidColorBrush(Color.Parse(Green));
    private static readonly ISolidColorBrush CyanBrush       = new SolidColorBrush(Color.Parse(Cyan));
    private static readonly ISolidColorBrush YellowBrush     = new SolidColorBrush(Color.Parse(Yellow));
    private static readonly ISolidColorBrush OrangeBrush     = new SolidColorBrush(Color.Parse(Orange));
    private static readonly ISolidColorBrush PurpleBrush     = new SolidColorBrush(Color.Parse(Purple));
    private static readonly ISolidColorBrush SupportingBrush = new SolidColorBrush(Color.Parse(Supporting));
    private static readonly ISolidColorBrush ErrorBrush      = new SolidColorBrush(Color.Parse(Error));
    private static readonly ISolidColorBrush GreyBrush       = new SolidColorBrush(Color.Parse(Grey));
    private static readonly ISolidColorBrush BrokenBrush     = new SolidColorBrush(Color.Parse(Broken));
    private static readonly ISolidColorBrush NeutralBrush    = new SolidColorBrush(Color.Parse(Neutral));

    /// <summary>
    /// The brush for a fold colour name. Case-insensitive, because the names cross the wire.
    ///
    /// "unknown" is a REAL fold colour - SessionOrdering emits it for an activity state it does not
    /// recognise - and it maps to grey legitimately: an indeterminate session is not asking for
    /// anything. That is a mapping, not a fallback.
    ///
    /// THE TWO NON-STATES BELOW ARE DIFFERENT FAULTS AND PAINT DIFFERENT PIXELS, which is the whole of
    /// the Session Cards mission's first item:
    ///   - "unstamped" is this desktop's own sentinel: connected, settled, and the Gateway has stamped
    ///     nothing. The push seam is broken. <see cref="Broken"/> magenta, loud, plus
    ///     <see cref="ReportMissingStamp"/>.
    ///   - Anything ELSE outside the fold's vocabulary is a Director older than its Gateway - a name
    ///     this build was never taught. <see cref="Neutral"/>, quiet, plus
    ///     <see cref="ReportUnknownColor"/>. Never grey, which MEANS snoozed or exited, and no longer
    ///     magenta, which now means only the broken seam.
    /// See the <see cref="Broken"/> and <see cref="Neutral"/> docs for why these are sentinels and not
    /// a throw.
    /// </summary>
    public static ISolidColorBrush BrushFor(string? foldColor) => foldColor?.ToLowerInvariant() switch
    {
        "red"        => RedBrush,
        "blue"       => BlueBrush,
        "green"      => GreenBrush,
        "cyan"       => CyanBrush,
        "yellow"     => YellowBrush,
        "orange"     => OrangeBrush,
        "purple"     => PurpleBrush,
        "supporting" => SupportingBrush,
        "error"      => ErrorBrush,
        "grey"       => GreyBrush,
        "unknown"    => GreyBrush,
        // The Director is connected and settled but the Gateway has stamped nothing (SessionViewModel
        // .UnstampedSentinel): a broken display-state push, rendered the magenta sentinel on purpose - never
        // grey, which would read as "parked". Explicit so it is intentional, not the catch-all below.
        "unstamped"  => BrokenBrush,
        // A name this build has never heard of - an older Director against a newer Gateway. The neutral,
        // never the magenta that now belongs to the broken seam alone, and never grey.
        _            => NeutralBrush,
    };

    /// <summary>The hex for a fold colour name, for the surfaces that style with strings rather
    /// than brushes. Same table, same rules as <see cref="BrushFor"/>.</summary>
    public static string HexFor(string? foldColor) => foldColor?.ToLowerInvariant() switch
    {
        "red"        => Red,
        "blue"       => Blue,
        "green"      => Green,
        "cyan"       => Cyan,
        "yellow"     => Yellow,
        "orange"     => Orange,
        "purple"     => Purple,
        "supporting" => Supporting,
        "error"      => Error,
        "grey"       => Grey,
        "unknown"    => Grey,
        "unstamped"  => Broken,
        _            => Neutral,
    };

    /// <summary>
    /// True when <paramref name="foldColor"/> is a name this palette knows. The unreachability test
    /// drives the REAL fold over every state it can emit and asserts this holds for all of them, so
    /// the sentinel arm above is a branch a test guarantees cannot fire rather than a silent guess.
    /// </summary>
    public static bool Knows(string? foldColor) => foldColor?.ToLowerInvariant() is
        "red" or "blue" or "green" or "cyan" or "yellow" or "orange" or "purple" or "supporting" or "error" or "grey" or "unknown";

    /// <summary>
    /// Report a colour name the Gateway emitted and this desktop does not know. Separate from
    /// <see cref="BrushFor"/> (which is called from a binding on every repaint and must stay a pure,
    /// allocation-free read - logging in there would write a line per frame). Callers that fold a
    /// live session call this once when they notice, so the neutral on the dot has a matching line
    /// in the log that says which name it was.
    ///
    /// It says NEUTRAL and says why, because the line a person reads while diagnosing must not describe
    /// the pixel the code stopped painting.
    /// </summary>
    public static void ReportUnknownColor(string? foldColor, string sessionId)
        => FileLog.Write($"[StatusPalette] UNKNOWN FOLD COLOUR '{foldColor}' for session {sessionId} - " +
                         $"not in the desktop palette, rendering the NEUTRAL {Neutral}. The Gateway is emitting a " +
                         "colour name this build does not know, which usually means this Director is older than " +
                         "its Gateway - update the Director. This is NOT the magenta broken-seam sentinel; see " +
                         "docs/new_architecture/sessions.html.");

    /// <summary>
    /// Report that the Director is CONNECTED and settled but the Gateway has stamped NO display state for a
    /// session - the display-state push is not delivering, so the rail shows the magenta sentinel instead of a
    /// grey that would read as "parked" (issue #1966). Distinct from <see cref="ReportUnknownColor"/>: there
    /// the Gateway sent a colour NAME this build does not know and the dot goes NEUTRAL; here it sent nothing
    /// at all and the dot goes MAGENTA. Two faults, two pixels, two log lines. Edge-triggered by
    /// the caller, once per change, so a per-frame binding getter does not flood the log.
    /// </summary>
    public static void ReportMissingStamp(string sessionId)
        => FileLog.Write($"[StatusPalette] NO GATEWAY DISPLAY-STATE STAMP for session {sessionId} while the " +
                         "tunnel is connected and settled - the set-display-state push is not delivering the " +
                         "Gateway's verdict. Rendering the BROKEN magenta sentinel (never grey). Likely a " +
                         "Gateway/Director version or tenancy mismatch; redeploy the Gateway and Director " +
                         "together. See docs/new_architecture/sessions.html.");
}
