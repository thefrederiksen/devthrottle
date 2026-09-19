using CcDirector.Gateway.Contracts;

namespace CcDirector.Avalonia;

/// <summary>
/// WHAT THE RAIL'S COLOUR DOT SAYS WHEN YOU HOVER IT - the legend's name for the colour the Gateway
/// painted, then the Gateway's own label for this session when that adds anything: "Carrying on: Monitor
/// fix round 2 progress".
///
/// EVERY WORD COMES FROM THE GATEWAY. The name comes from the legend the Gateway serves
/// (<c>GET /gateway/session-colours</c>, read by <c>SessionColourLegendCache</c>); the label is the fold's
/// own <c>SessionDto.StateLabel</c>, stamped down onto the session. This class chooses between them and
/// punctuates them, and writes no word of its own.
///
/// WHAT IT REPLACED, AND WHY. The hover used to be the DIRECTOR's <c>LastStatusReason</c> - a sentence
/// this machine wrote itself, before the Wingman had judged the stop. The Director only knows running or
/// stopped, so a purple "Carrying on" dot hovered "needs you", and a calm row could hover the words the
/// owner scans for. That is the ruling behind this file, in his words: <em>"Nobody should give any local
/// reason for anything. It should always be the gateway ... if you hover over the color, it should show
/// you what that color means."</em> Both halves of the hover now come from the one fold, so the hover
/// cannot disagree with the dot it is attached to.
///
/// It is the same rule and the same shape as the web client's <c>dotTitle</c>
/// (<c>packages/client-core/src/sessions/sessionColours.ts</c>), so the three surfaces answer "what is
/// this dot?" the same way.
///
/// Pure, so it is tested without an Avalonia application and without a Gateway.
/// </summary>
public static class SessionDotHover
{
    /// <summary>
    /// The hover text for one dot.
    /// </summary>
    /// <param name="foldColour">The colour name the rail is painting (<c>SessionViewModel.EffectiveColor</c>).</param>
    /// <param name="gatewayLabel">The Gateway's stamped label for this session (<c>SessionDto.StateLabel</c>).</param>
    /// <param name="legend">The legend this desktop last read from its Gateway, or null when it has not
    /// read one yet. Null is not a failure to paper over: the hover then shows the stamped label alone,
    /// which is still the Gateway's word, rather than a name this build guessed.</param>
    /// <returns>
    /// The words, or an empty string when the Gateway has given this desktop neither a name nor a label -
    /// which is the honest answer, and is exactly what an unstamped session (the magenta sentinel) is.
    /// </returns>
    public static string For(string? foldColour, string? gatewayLabel, SessionColourLegendDto? legend)
    {
        var label = (gatewayLabel ?? "").Trim();
        var title = TitleFor(foldColour, legend);

        if (title.Length == 0) return label;
        if (label.Length == 0 || string.Equals(label, title, StringComparison.OrdinalIgnoreCase)) return title;
        return $"{title}: {label}";
    }

    /// <summary>
    /// The legend's short name for a colour - "Needs you", "Carrying on" - or an empty string when this
    /// desktop has no legend yet, or when the legend has no entry for that name.
    ///
    /// A MISSING ENTRY IS THE ORDINARY CASE, NOT A FAULT. It is what a Director older than its Gateway
    /// sees when the fold has learned a colour this build never knew; the dot goes
    /// <see cref="StatusPalette.Neutral"/> and the hover falls back to the Gateway's label, which this
    /// build can still render because it is words rather than a name to resolve. It is also what the
    /// rail's own "unstamped" sentinel gets - there is no legend entry for it because it is not a colour
    /// the Gateway decides.
    /// </summary>
    private static string TitleFor(string? foldColour, SessionColourLegendDto? legend)
    {
        if (legend is null || string.IsNullOrWhiteSpace(foldColour)) return "";

        // The fold's "unknown" and its "grey" paint the one grey and the Gateway explains them under one
        // entry. That aliasing is the Gateway's ruling, read from the Gateway's own table rather than
        // written down again here.
        var key = SessionColourLegend.SharesAnEntry.TryGetValue(foldColour.ToLowerInvariant(), out var shared)
            ? shared
            : foldColour;

        var entry = legend.Entries.FirstOrDefault(e => string.Equals(e.Colour, key, StringComparison.OrdinalIgnoreCase));
        return entry?.Title.Trim() ?? "";
    }
}
