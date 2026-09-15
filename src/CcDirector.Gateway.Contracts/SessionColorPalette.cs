namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE ONE canonical map from a fold colour NAME (<see cref="SessionOrdering.EffectiveColor"/>) to its
/// pixel HEX. This is the single source of truth for the dot colour every surface paints.
///
/// The mission "Dumb Clients" exists because the NAME-&gt;hex step was hand-maintained in three separate
/// places - the web client table, the desktop palette, and the state-agreement check's own copy - and
/// they DRIFTED: red and yellow rendered as different shades on different screens even though all three
/// folded to the same name. The fix is this one map. The Gateway stamps the resolved hex onto
/// <see cref="SessionDto.EffectiveColorHex"/> from HERE, so a browser paints exactly the pixel the
/// Gateway chose; the desktop palette (<c>StatusPalette</c>) sources its values from these same
/// constants so a C# client cannot drift from the wire either.
///
/// The values are the Tailwind-500 ramp, written down in docs/new_architecture/sessions.html - the
/// single spec both this map and the web client (packages/client-core/src/sessions/ordering.ts) cite.
/// TWO deliberate departures from the 500 ramp, both load-bearing:
///   - <see cref="Error"/> is red-700, not red-500. A session that DIED must never read as one that
///     merely finished (issue #959).
///   - <see cref="Grey"/> is ONE grey. Snoozed, exited, and indeterminate all fold to the "grey" name on
///     purpose, so every client renders them identically.
/// </summary>
public static class SessionColorPalette
{
    public const string Red        = "#EF4444";  // red-500      - needs you
    public const string Yellow     = "#EAB308";  // yellow-500   - wingman reading / preparing voice
    public const string Orange     = "#F97316";  // orange-500   - dictation in flight / deep dive
    public const string Green      = "#22C55E";  // green-500    - ready (brand new)
    public const string Cyan       = "#06B6D4";  // cyan-500     - the Wingman judged the stop finished (issue #2892)
    public const string Blue       = "#3B82F6";  // blue-500     - working
    public const string Purple     = "#A855F7";  // purple-500   - parked on its own background task
    public const string Supporting = "#64748B";  // slate-500    - a live Worker's suppressed red
    public const string Error      = "#B91C1C";  // red-700      - crashed, NOT finished (issue #959)
    public const string Grey       = "#6B7280";  // gray-500     - snoozed, exited, or indeterminate

    /// <summary>
    /// The PROTOCOL-ERROR sentinel - magenta. Not a state. It is what a client paints when the Gateway
    /// sends it a colour name (or a stamped hex) it does not understand - a mixed-version deploy, or a
    /// fold that learned a name nobody taught the palette. It exists to be impossible to misread: it is
    /// not grey (which MEANS snoozed/exited), so it can never be mistaken for a real state, and it comes
    /// with a logged error so it is diagnosable rather than a silent guess.
    /// </summary>
    public const string Broken     = "#FF00FF";  // magenta - NOT a state; "this colour name is not in the palette"

    /// <summary>
    /// The canonical hex for a fold colour name. Case-insensitive, because the names cross the wire.
    ///
    /// "unknown" is a REAL fold colour - <see cref="SessionOrdering.EffectiveColor"/> emits it for an
    /// activity state it does not recognise - and it maps to <see cref="Grey"/> legitimately: an
    /// indeterminate session is not asking for anything. That is a mapping, not a fallback. Anything
    /// OUTSIDE the fold's vocabulary is a bug and gets the <see cref="Broken"/> sentinel, never a colour
    /// that means something else.
    /// </summary>
    public static string HexFor(string? foldColor) => foldColor?.ToLowerInvariant() switch
    {
        "red"        => Red,
        "yellow"     => Yellow,
        "orange"     => Orange,
        "green"      => Green,
        "cyan"       => Cyan,
        "blue"       => Blue,
        "purple"     => Purple,
        "supporting" => Supporting,
        "error"      => Error,
        "grey"       => Grey,
        "unknown"    => Grey,
        _            => Broken,
    };

    /// <summary>True when <paramref name="foldColor"/> is a name this palette knows. A name it does not
    /// know is a bug (it renders the <see cref="Broken"/> sentinel), never a state.</summary>
    public static bool Knows(string? foldColor) => foldColor?.ToLowerInvariant() is
        "red" or "yellow" or "orange" or "green" or "cyan" or "blue" or "purple" or "supporting" or "error" or "grey" or "unknown";
}
