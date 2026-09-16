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
/// NOBODY WRITES A SENTENCE HERE. A sentence is a <see cref="Sentence"/>: an opening CLAIM, an optional set of
/// ALTERNATIVES, and trailing CLAIMS. Every claim is a statement about what a session is doing, and
/// <c>SessionColourLegendTests</c> refuses any claim without a session behind it, running each one through the real
/// fold for its colour, its label and its bucket. The joining words - the commas, the "or", the full stops - are
/// RENDERED from the structure, not typed, so a sentence cannot say "and" where the fold means "or".
///
/// THE SAME IS TRUE OF <see cref="NoteClaims"/>. The verdict note is a list of claims, each with an executable check
/// in the test; a note that grows a sentence nobody checks fails.
///
/// That shape is the answer to five rounds of review. Each round found the same species of defect - prose a person
/// reads that no test executes - in a narrower place: a client-side copy, then one example per colour, then claims
/// checked by substring, then free joining words, then the note. There is nowhere left for a sentence to hide.
/// </summary>
public static class SessionColourLegend
{
    /// <summary>The route that serves <see cref="Build"/>.</summary>
    public const string Route = "/gateway/session-colours";

    /// <summary>How a sentence's alternatives relate to each other.</summary>
    public enum Relation
    {
        /// <summary>Exactly one of them is true of any session - rendered "a, b, or c".</summary>
        OneOf,

        /// <summary>All of them are true at once - rendered "a, b, and c".</summary>
        AllOf,
    }

    /// <summary>
    /// One colour's sentence, as structure rather than prose.
    /// </summary>
    /// <param name="Opening">The claim the sentence opens with.</param>
    /// <param name="Alternatives">The cases, each a claim. Empty when the sentence has none.</param>
    /// <param name="How">Whether the alternatives are cases of one another (OneOf) or all true together (AllOf).</param>
    /// <param name="Trailing">Further claims, each its own sentence after the first.</param>
    public sealed record Sentence(
        string Opening,
        IReadOnlyList<string> Alternatives,
        Relation How = Relation.OneOf,
        IReadOnlyList<string>? Trailing = null)
    {
        /// <summary>Every claim in the sentence, in the order a person reads them.</summary>
        public IEnumerable<string> Claims =>
            new[] { Opening }.Concat(Alternatives).Concat(Trailing ?? Array.Empty<string>());

        /// <summary>The words, rendered. The joining strings come from the structure and nowhere else.</summary>
        public string Render()
        {
            var text = Opening;
            if (Alternatives.Count > 0)
            {
                var last = How == Relation.OneOf ? ", or " : ", and ";
                var cases = Alternatives.Count == 1
                    ? Alternatives[0]
                    : string.Join(", ", Alternatives.Take(Alternatives.Count - 1)) + last + Alternatives[^1];
                text += ": " + cases;
            }
            foreach (var trailing in Trailing ?? Array.Empty<string>()) text += ". " + trailing;
            return text + ".";
        }
    }

    /// <summary>The legend, most urgent colour first. A fresh object each call - the DTOs are mutable.</summary>
    public static SessionColourLegendDto Build() => new()
    {
        Entries = Order.Select(Entry).ToList(),
        Broken = new SessionColourLegendNoteDto
        {
            Hex = SessionColorPalette.Broken,
            Title = "Magenta",
            Means = "Not a state. The screen received a colour it does not understand - usually the Gateway and this " +
                    "app on different versions. Reload, and report it if it stays.",
        },
        VerdictNote = string.Join(" ", NoteClaims.Select(c => c + ".")),
    };

    /// <summary>
    /// The claims the verdict note makes, each with its own check in the test. It names ONLY the colours the switch
    /// decides: it used to name the Wingman's yellow as well, and that was false - a Director's briefing paints the
    /// same yellow with the same words whatever the switch says.
    /// </summary>
    public static readonly IReadOnlyList<string> NoteClaims =
    [
        "Done and Carrying on appear only when the Wingman's verdicts are switched on for your account",
        "With them off, those sessions show red instead",
    ];

    /// <summary>One colour's sentence, as structure.</summary>
    /// <param name="colour">A fold colour name.</param>
    /// <returns>The sentence, or null when the legend has no entry for that colour.</returns>
    public static Sentence? SentenceFor(string colour) => Sentences.GetValueOrDefault(colour);

    /// <summary>Every claim of one colour's sentence, in reading order.</summary>
    /// <param name="colour">A fold colour name.</param>
    /// <returns>The claims; empty when there is no entry.</returns>
    public static IReadOnlyList<string> ClaimsFor(string colour) =>
        SentenceFor(colour)?.Claims.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();

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

    // The sentences. Every claim has a session in SessionColourLegendTests, and the alternatives of a OneOf are
    // proven to be different situations - adding a claim without a session, or calling two overlapping states
    // alternatives, fails the suite.
    private static readonly Dictionary<string, Sentence> Sentences = new(StringComparer.Ordinal)
    {
        ["red"] = new("The session has stopped and is waiting for you",
            ["at a prompt", "on a permission", "gone quiet"]),

        ["blue"] = new("The agent is running a turn right now", [],
            Trailing: ["A working session is always blue"]),

        ["cyan"] = new("The session stopped and the Wingman judged it finished",
            ["the work is done", "it is only reporting something and asks you nothing"]),

        ["purple"] = new("The session stopped, but the Wingman judged it will continue on its own", [],
            Trailing: ["It turns red if it does not"]),

        ["yellow"] = new("The session stopped and is being looked at before it is shown to you",
            ["the Wingman or the Director is reading the stop", "its voice summary is not ready yet"]),

        ["green"] = new("A brand-new session at its first prompt", [],
            Trailing: ["It has not done anything yet"]),

        ["orange"] = new("Your dictation is on its way",
            ["it is still uploading from your phone", "it is being turned into text"],
            Trailing: ["Wait before typing into it"]),

        ["supporting"] = new("Stopped, but another live session is driving it", [],
            Trailing: ["So it waits on that session instead of you"]),

        ["grey"] = new("This session is resting",
            ["you snoozed it", "its agent exited", "its state could not be read"],
            Trailing: ["The label reads Snoozed, Exited or Idle"]),

        ["error"] = new("The agent process died", [],
            Trailing: ["It is darker than the red that means needs you, so a crash never reads as a finish"]),
    };

    private static SessionColourLegendEntryDto Entry(string colour) => new()
    {
        Colour = colour,
        // The canonical pixel, resolved here exactly as the roster's EffectiveColorHex is, so the legend's dot and
        // the session's dot are the same hex by construction.
        Hex = SessionColorPalette.HexFor(colour),
        Title = Titles[colour],
        Means = Sentences[colour].Render(),
        AsksForYou = Asks[colour],
    };
}

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

    /// <summary>What the session is doing: <see cref="SessionColourLegend.Sentence.Render"/> of its claims.</summary>
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
