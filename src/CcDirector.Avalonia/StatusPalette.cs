using Avalonia.Media;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// The desktop's Avalonia-brush adapter over the ONE canonical palette. This is the whole of defect 18.
///
/// The colour NAMES are the shared fold's (SessionOrdering.EffectiveColor). The hexes are NO LONGER
/// hand-written here: every value below references <see cref="SessionColorPalette"/> in
/// CcDirector.Gateway.Contracts - the single source the Gateway also stamps from onto
/// <see cref="SessionDto.EffectiveColorHex"/>. The desktop is the same C# solution as the Gateway, so
/// referencing that canonical map compile-time IS sharing the Gateway's own source of truth, not a
/// second copy that can drift. (A change to the canonical hex therefore needs a desktop rebuild; the
/// two ship together as one solution, so a colour never lands on one and not the other.) This class
/// stays because the rail and the turn review still need Avalonia
/// brushes and the magenta sentinel; only its VALUES moved to the canonical map.
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
    /// The BROKEN sentinel - magenta. Not a state. It means "the Gateway sent this desktop a colour
    /// name it does not know", and it exists to be impossible to misread.
    ///
    /// This arm used to be <c>_ =&gt; GreyBrush</c>, which was a fallback of the exact class this
    /// mission exists to kill: GREY IS A REAL STATE HERE - it means snoozed or exited - so an
    /// unrecognised colour rendering grey was not a neutral "we do not know", it was an affirmative
    /// lie that the session is parked.
    ///
    /// Why a sentinel and not a throw. A throw was the obvious answer and it is WRONG, by
    /// observation rather than argument: StatusColorBrush is read through a XAML binding, and when a
    /// bound getter throws, Avalonia swallows it and leaves the property unset - the probe rendered
    /// <c>Background = null</c>. That is an INVISIBLE dot: silent AND broken, strictly quieter than
    /// the grey it would have replaced. A log line alone is not loud either - the owner is looking at
    /// the rail, not the log. So: magenta on the dot (loud, and unmistakable for any real state) AND
    /// a logged error (so it is diagnosable), AND - the real fix - a test that drives the REAL fold
    /// across every state it can emit and proves this branch cannot fire. The sentinel is a tripwire
    /// a test guarantees is unreachable, not a guess that fires silently in production.
    /// </summary>
    public const string Broken     = SessionColorPalette.Broken;  // magenta - NOT a state; "this desktop does not know that colour"

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

    /// <summary>
    /// The brush for a fold colour name. Case-insensitive, because the names cross the wire.
    ///
    /// "unknown" is a REAL fold colour - SessionOrdering emits it for an activity state it does not
    /// recognise - and it maps to grey legitimately: an indeterminate session is not asking for
    /// anything. That is a mapping, not a fallback. Anything OUTSIDE the fold's vocabulary is a bug,
    /// and gets <see cref="Broken"/> plus a logged error rather than a colour that means something
    /// else. See the <see cref="Broken"/> docs for why this is a sentinel and not a throw.
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
        // grey, which would read as "parked". Explicit so it is intentional, not the catch-all fallback below.
        "unstamped"  => BrokenBrush,
        _            => BrokenBrush,
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
        _            => Broken,
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
    /// live session call this once when they notice, so the magenta on the dot has a matching line
    /// in the log that says which name it was.
    /// </summary>
    public static void ReportUnknownColor(string? foldColor, string sessionId)
        => FileLog.Write($"[StatusPalette] UNKNOWN FOLD COLOUR '{foldColor}' for session {sessionId} - " +
                         "not in the desktop palette, rendering the BROKEN magenta sentinel. The Gateway is " +
                         "emitting a colour name this build does not know; see docs/new_architecture/sessions.html.");

    /// <summary>
    /// Report that the Director is CONNECTED and settled but the Gateway has stamped NO display state for a
    /// session - the display-state push is not delivering, so the rail shows the magenta sentinel instead of a
    /// grey that would read as "parked" (issue #1966). Distinct from <see cref="ReportUnknownColor"/>: there
    /// the Gateway sent a colour NAME this build does not know; here it sent nothing at all. Edge-triggered by
    /// the caller, once per change, so a per-frame binding getter does not flood the log.
    /// </summary>
    public static void ReportMissingStamp(string sessionId)
        => FileLog.Write($"[StatusPalette] NO GATEWAY DISPLAY-STATE STAMP for session {sessionId} while the " +
                         "tunnel is connected and settled - the set-display-state push is not delivering the " +
                         "Gateway's verdict. Rendering the BROKEN magenta sentinel (never grey). Likely a " +
                         "Gateway/Director version or tenancy mismatch; redeploy the Gateway and Director " +
                         "together. See docs/new_architecture/sessions.html.");
}
