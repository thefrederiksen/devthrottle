namespace CcDirector.Gateway.Contracts;

/// <summary>
/// THE MISSION BUTTON'S VERDICT for one session (the Missions First-Class mission), folded ONCE on the Gateway and
/// rendered verbatim by every client (repository rule 7: the client is dumb). A client never decides which of the
/// three states a session is in, never composes the label or the tooltip, and never builds the address: it shows
/// <see cref="Label"/> in the colour <see cref="ColorKey"/> names, with the icon <see cref="IconKey"/> names before it,
/// shows <see cref="Tooltip"/> on hover, and opens <see cref="DocumentAddress"/> on a click when there is one.
///
/// Folded by <c>CcDirector.Gateway.Missions.MissionButtonFold</c>. Null on a session when the fold was given no
/// missions to read, which claims nothing.
/// </summary>
public sealed class MissionButtonDisplay
{
    /// <summary>The session is attached to a Mission that has a mission document.</summary>
    public const string VerdictMissionWithDocument = "mission-with-document";

    /// <summary>The session is attached to a Mission that has no mission document yet.</summary>
    public const string VerdictMissionWithoutDocument = "mission-without-document";

    /// <summary>The session is attached to no Mission. A warning, never a refusal: nothing stops the session.</summary>
    public const string VerdictNoMission = "no-mission";

    /// <summary>Colour key of a Mission with a document: indigo, #4338CA, filled.</summary>
    public const string ColorMission = "mission";

    /// <summary>Colour key of a Mission with no document yet: the same indigo, as an outline only.</summary>
    public const string ColorMissionOutline = "mission-outline";

    /// <summary>Colour key of a session with no Mission: the warning yellow.</summary>
    public const string ColorWarning = "warning";

    /// <summary>Icon key of a Mission with a document: a small document drawn before the word.</summary>
    public const string IconDocument = "document";

    /// <summary>Icon key of a Mission with no document yet: no icon, the word alone.</summary>
    public const string IconNone = "none";

    /// <summary>Icon key of a session with no Mission: a small yellow warning triangle drawn before the word.</summary>
    public const string IconWarning = "warning";

    /// <summary>One of the three <c>Verdict*</c> values.</summary>
    public string Verdict { get; set; } = "";

    /// <summary>The words on the button. Always "Mission" since 7 November 2026: the Mission's name is in the tooltip,
    /// so the button stays small in every header.</summary>
    public string Label { get; set; } = "";

    /// <summary>One of the three <c>Color*</c> keys. The client maps the key to its own drawing of it.</summary>
    public string ColorKey { get; set; } = "";

    /// <summary>One of the three <c>Icon*</c> keys. The client maps the key to its own drawing of it; a key it does not
    /// know is a broken contract and is shown as a problem, never guessed.</summary>
    public string IconKey { get; set; } = "";

    /// <summary>The words shown on hover, finished. May hold line breaks.</summary>
    public string Tooltip { get; set; } = "";

    /// <summary>
    /// The Gateway-relative address of the Mission's document, <c>/r/&lt;report id&gt;</c>, which sends a phone to the
    /// phone's report screen and anything else to the Cockpit. Null when there is no document to open.
    /// </summary>
    public string? DocumentAddress { get; set; }
}
