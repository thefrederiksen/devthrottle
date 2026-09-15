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
/// EVERY SENTENCE IS HELD TO THE FOLD. <c>SessionColourLegendTests</c> builds an example session for each entry and
/// runs it through the real fold: it must wear the entry's colour, and "asks for you: Yes" must be exactly the
/// needs-you bucket. The verdict note is executed the same way - a calm session with the switch off must fold red.
/// </summary>
public static class SessionColourLegend
{
    /// <summary>The route that serves <see cref="Build"/>.</summary>
    public const string Route = "/gateway/session-colours";

    /// <summary>The legend, most urgent colour first. A fresh object each call - the DTOs are mutable.</summary>
    public static SessionColourLegendDto Build() => new()
    {
        Entries =
        {
            Entry("red", "Needs you",
                "The session has stopped and is waiting for you - at a prompt, on a permission, or with a question.",
                AsksYes),
            Entry("blue", "Working",
                "The agent is running a turn right now. A working session is always blue.",
                AsksNo),
            Entry("cyan", "Done",
                "The session stopped and the Wingman judged it finished: the work is done, or it is only reporting " +
                "something and asks you nothing.",
                AsksNo),
            Entry("purple", "Carrying on",
                "The session stopped, but the Wingman judged it will continue on its own. It turns red if it does not.",
                AsksNo),
            Entry("yellow", "Being read",
                "The session stopped and is being looked at before it is shown to you: the Wingman or the Director is " +
                "reading the stop, or its voice summary is not ready yet.",
                AsksNotYet),
            Entry("green", "Ready",
                "A brand-new session at its first prompt. It has not done anything yet.",
                AsksNo),
            Entry("orange", "Transcribing",
                "Your dictation is still uploading or being turned into text. Wait before typing into it.",
                AsksNo),
            Entry("supporting", "Supervised",
                "Stopped, but another live session is driving it, so it waits on that session instead of you.",
                AsksNo),
            Entry("grey", "Snoozed or exited",
                "You snoozed it, its agent exited, or its state could not be read. The words beside the dot say which.",
                AsksNo),
            Entry("error", "Crashed",
                "The agent process died. It is darker than the red that means needs you, so a crash never reads as a " +
                "finish.",
                AsksLookAtIt),
        },
        Broken = new SessionColourLegendNoteDto
        {
            Hex = SessionColorPalette.Broken,
            Title = "Magenta",
            Means = "Not a state. The screen received a colour it does not understand - usually the Gateway and this " +
                    "app on different versions. Reload, and report it if it stays.",
        },
        VerdictNote = "Done and Carrying on, and the yellow for the Wingman reading a stop, appear only when the " +
                      "Wingman's verdicts are switched on for your account. With them off, those sessions show red instead.",
    };

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

    private static SessionColourLegendEntryDto Entry(string colour, string title, string means, string asksForYou) => new()
    {
        Colour = colour,
        // The canonical pixel, resolved here exactly as the roster's EffectiveColorHex is, so the legend's dot and
        // the session's dot are the same hex by construction.
        Hex = SessionColorPalette.HexFor(colour),
        Title = title,
        Means = means,
        AsksForYou = asksForYou,
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

    /// <summary>What the session is doing, in one or two plain sentences.</summary>
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
