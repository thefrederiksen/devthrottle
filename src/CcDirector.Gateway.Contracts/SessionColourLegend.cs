namespace CcDirector.Gateway.Contracts;

/// <summary>
/// WHAT EVERY SESSION COLOUR MEANS - the words behind each dot, served to the Cockpit and the phone by
/// <c>GET /gateway/session-colours</c> and rendered there verbatim by the "What do the colours mean?" legend.
///
/// IT LIVES BESIDE THE FOLD ON PURPOSE. The colour a session wears is <see cref="SessionOrdering.EffectiveColor"/>,
/// its pixel is <see cref="SessionColorPalette"/>, and what the colour MEANS is a ruling of the same kind, so it is
/// written here once rather than in each client (the repository's law 7: the client is dumb, the Gateway owns all
/// ruling). A client-side copy was the first draft of this legend, and two of its sentences were already false
/// against the fold on the day it was written - which is the whole argument for this file.
///
/// EVERY SENTENCE IS BUILT FROM CLAIMS, AND EVERY CLAIM IS EXECUTED. A sentence is not a free string here: it is a
/// list of <see cref="SessionColourLegendSegment"/>, each either a CLAIM about what a session is doing or one of the
/// few joining strings in <see cref="AllowedGlue"/>. <c>SessionColourLegendTests</c> builds a session for every claim
/// and runs it through the real fold - for its colour, for the words beside the dot, and for which side of the
/// needs-you line it lands on - and it FAILS when a claim has no session, when a segment is glue the list does not
/// allow, or when a claim's session does not behave as the claim says.
///
/// That shape is the answer to four rounds of review. Prose used to be checked by substring, so a false statement
/// could be grown around a true claim ("The session is working and does not need you - at a prompt..."). Now no words
/// reach a screen unless they are either a claim with a session behind them or one of the joining strings.
/// </summary>
public static class SessionColourLegend
{
    /// <summary>The route that serves <see cref="Build"/>.</summary>
    public const string Route = "/gateway/session-colours";

    /// <summary>
    /// The only strings a sentence may use to join its claims. Everything else a person reads is a claim, and every
    /// claim is executed against the fold - so a sentence cannot grow a statement nobody checked.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedGlue =
        new[] { ", ", ", or ", ". ", ": ", " - ", " or ", "." };

    /// <summary>The legend, most urgent colour first. A fresh object each call - the DTOs are mutable.</summary>
    public static SessionColourLegendDto Build() => new()
    {
        Entries = Order.Select(colour => Entry(colour)).ToList(),
        Broken = new SessionColourLegendNoteDto
        {
            Hex = SessionColorPalette.Broken,
            Title = "Magenta",
            Means = "Not a state. The screen received a colour it does not understand - usually the Gateway and this " +
                    "app on different versions. Reload, and report it if it stays.",
        },
        // NAMES ONLY THE COLOURS THE SWITCH DECIDES. It used to name the Wingman's yellow as well, and that was false:
        // a Director's briefing paints the same yellow with the same words and does not depend on this switch at all.
        VerdictNote = "Done and Carrying on appear only when the Wingman's verdicts are switched on for your account. " +
                      "With them off, those sessions show red instead.",
    };

    /// <summary>The claims and joining strings that make one colour's sentence, in order.</summary>
    /// <param name="colour">A fold colour name.</param>
    /// <returns>The segments; empty when the legend has no entry for that colour.</returns>
    public static IReadOnlyList<SessionColourLegendSegment> SegmentsFor(string colour) =>
        Sentences.TryGetValue(colour, out var segments) ? segments : Array.Empty<SessionColourLegendSegment>();

    /// <summary>Just the claims of one colour's sentence, in order.</summary>
    /// <param name="colour">A fold colour name.</param>
    /// <returns>Each claim's words.</returns>
    public static IReadOnlyList<string> ClaimsFor(string colour) =>
        SegmentsFor(colour).Where(s => s.IsClaim).Select(s => s.Text).ToList();

    /// <summary>The fold colour names that share another entry's explanation. "unknown" is the fold's word for a
    /// state it could not read, and it paints the one grey on purpose, so "Snoozed or exited" covers it.</summary>
    public static readonly IReadOnlyDictionary<string, string> SharesAnEntry =
        new Dictionary<string, string>(StringComparer.Ordinal) { ["unknown"] = "grey" };

    /// <summary>The four answers to "does this colour ask for you?".</summary>
    public const string AsksYes = "Yes";

    /// <inheritdoc cref="AsksYes"/>
    public const string AsksNo = "No";

    /// <inheritdoc cref="AsksYes"/>
    public const string AsksNotYet = "Not yet";

    /// <inheritdoc cref="AsksYes"/>
    public const string AsksLookAtIt = "Look at it";

    /// <summary>The order a person meets the colours in: what needs them, then what does not.</summary>
    private static readonly string[] Order =
        { "red", "blue", "cyan", "purple", "yellow", "green", "orange", "supporting", "grey", "error" };

    private static SessionColourLegendSegment Claim(string text) => new(true, text);

    private static SessionColourLegendSegment Glue(string text) => new(false, text);

    private static readonly Dictionary<string, string> Titles = new(StringComparer.Ordinal)
    {
        ["red"] = "Needs you",
        ["blue"] = "Working",
        ["cyan"] = "Done",
        ["purple"] = "Carrying on",
        ["yellow"] = "Being read",
        ["green"] = "Ready",
        ["orange"] = "Transcribing",
        ["supporting"] = "Supervised",
        ["grey"] = "Snoozed or exited",
        ["error"] = "Crashed",
    };

    private static readonly Dictionary<string, string> Asks = new(StringComparer.Ordinal)
    {
        ["red"] = AsksYes,
        ["blue"] = AsksNo,
        ["cyan"] = AsksNo,
        ["purple"] = AsksNo,
        ["yellow"] = AsksNotYet,
        ["green"] = AsksNo,
        ["orange"] = AsksNo,
        ["supporting"] = AsksNo,
        ["grey"] = AsksNo,
        ["error"] = AsksLookAtIt,
    };

    // The sentences. Every claim below has a session in SessionColourLegendTests; adding one here without adding that
    // session fails the suite, and so does joining claims with anything outside AllowedGlue.
    private static readonly Dictionary<string, SessionColourLegendSegment[]> Sentences = new(StringComparer.Ordinal)
    {
        ["red"] =
        [
            Claim("The session has stopped and is waiting for you"), Glue(" - "),
            Claim("at a prompt"), Glue(", "),
            Claim("on a permission"), Glue(", or "),
            Claim("gone quiet"), Glue("."),
        ],
        ["blue"] =
        [
            Claim("The agent is running a turn right now"), Glue(". "),
            Claim("A working session is always blue"), Glue("."),
        ],
        ["cyan"] =
        [
            Claim("The session stopped and the Wingman judged it finished"), Glue(": "),
            Claim("the work is done"), Glue(", or "),
            Claim("it is only reporting something and asks you nothing"), Glue("."),
        ],
        ["purple"] =
        [
            Claim("The session stopped, but the Wingman judged it will continue on its own"), Glue(". "),
            Claim("It turns red if it does not"), Glue("."),
        ],
        ["yellow"] =
        [
            Claim("The session stopped and is being looked at before it is shown to you"), Glue(": "),
            Claim("the Wingman or the Director is reading the stop"), Glue(", or "),
            Claim("its voice summary is not ready yet"), Glue("."),
        ],
        ["green"] =
        [
            Claim("A brand-new session at its first prompt"), Glue(". "),
            Claim("It has not done anything yet"), Glue("."),
        ],
        ["orange"] =
        [
            Claim("Your dictation is still uploading"), Glue(" or "),
            Claim("being turned into text"), Glue(". "),
            Claim("Wait before typing into it"), Glue("."),
        ],
        ["supporting"] =
        [
            Claim("Stopped, but another live session is driving it"), Glue(", "),
            Claim("so it waits on that session instead of you"), Glue("."),
        ],
        ["grey"] =
        [
            Claim("You snoozed it"), Glue(", "),
            Claim("its agent exited"), Glue(", or "),
            Claim("its state could not be read"), Glue(". "),
            Claim("The label reads Snoozed, Exited or Idle"), Glue("."),
        ],
        ["error"] =
        [
            Claim("The agent process died"), Glue(". "),
            Claim("It is darker than the red that means needs you, so a crash never reads as a finish"), Glue("."),
        ],
    };

    private static SessionColourLegendEntryDto Entry(string colour) => new()
    {
        Colour = colour,
        // The canonical pixel, resolved here exactly as the roster's EffectiveColorHex is, so the legend's dot and
        // the session's dot are the same hex by construction.
        Hex = SessionColorPalette.HexFor(colour),
        Title = Titles[colour],
        Means = string.Concat(Sentences[colour].Select(s => s.Text)),
        AsksForYou = Asks[colour],
    };
}

/// <summary>One piece of a colour's sentence: a claim about what a session is doing, or a joining string.</summary>
/// <param name="IsClaim">True when these words assert something a session does - and so must be executed.</param>
/// <param name="Text">The words, exactly as the person reads them.</param>
public readonly record struct SessionColourLegendSegment(bool IsClaim, string Text);

/// <summary>The legend as served by <c>GET /gateway/session-colours</c>.</summary>
public sealed class SessionColourLegendDto
{
    /// <summary>One entry per colour a session dot can wear, most urgent first.</summary>
    public List<SessionColourLegendEntryDto> Entries { get; set; } = new();

    /// <summary>The magenta protocol-error sentinel. Not a state, so not an entry.</summary>
    public SessionColourLegendNoteDto Broken { get; set; } = new();

    /// <summary>Which colours depend on the account's verdict switch, and what shows when it is off.</summary>
    public string VerdictNote { get; set; } = "";
}

/// <summary>One colour, in words.</summary>
public sealed class SessionColourLegendEntryDto
{
    /// <summary>The fold colour name (<see cref="SessionOrdering.EffectiveColor"/>).</summary>
    public string Colour { get; set; } = "";

    /// <summary>Its canonical pixel (<see cref="SessionColorPalette.HexFor"/>).</summary>
    public string Hex { get; set; } = "";

    /// <summary>The short name a person calls this state.</summary>
    public string Title { get; set; } = "";

    /// <summary>What the session is doing: the claims of <see cref="SessionColourLegend.SegmentsFor"/>, joined.</summary>
    public string Means { get; set; } = "";

    /// <summary>Whether this colour is asking for the person: Yes, No, Not yet, or Look at it.</summary>
    public string AsksForYou { get; set; } = "";
}

/// <summary>A note with a swatch but no fold colour behind it.</summary>
public sealed class SessionColourLegendNoteDto
{
    /// <summary>The pixel.</summary>
    public string Hex { get; set; } = "";

    /// <summary>What to call it.</summary>
    public string Title { get; set; } = "";

    /// <summary>What it means.</summary>
    public string Means { get; set; } = "";
}
