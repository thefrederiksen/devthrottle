using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// The door a prompt came through. One token per entry point, so a ledger row says WHERE a turn entered
/// without anything downstream inferring it from the surface or the send source.
/// </summary>
public static class SubmissionRoutes
{
    /// <summary>Keystrokes typed straight into the desktop terminal control (raw bytes, Enter submits).</summary>
    public const string DesktopTerminal = "desktop-terminal";

    /// <summary>The desktop compose box's Send button or Ctrl+Enter.</summary>
    public const string DesktopComposer = "desktop-composer";

    /// <summary>The desktop Speak dialog's fire-and-forget Send (transcribed, then submitted directly).</summary>
    public const string DesktopDictation = "desktop-dictation";

    /// <summary>A queued prompt drained into the session by the queue.</summary>
    public const string QueueDrain = "queue-drain";

    /// <summary>The Gateway's <c>POST /sessions/{sid}/prompt</c> - the Cockpit, the phone's typed composer,
    /// an operator's command line, and any client with a device key or the machine token.</summary>
    public const string GatewayPrompt = "gateway-prompt";

    /// <summary>The Gateway's durable dictation completion, <c>POST /dictation/{uploadId}/complete</c>.</summary>
    public const string GatewayDictation = "gateway-dictation";

    /// <summary>Raw terminal bytes relayed by the Gateway from a browser terminal.</summary>
    public const string GatewayTerminal = "gateway-terminal";

    /// <summary>A session prompting another session through the Gateway - a fleet message, ask or broadcast.</summary>
    public const string FleetMessage = "fleet-message";

    /// <summary>Text the product itself authored and sent - a handover, a pre-prompt, a chat relay, the wingman.</summary>
    public const string Framework = "framework";

    /// <summary>The owner's notes and answers on a dev report, delivered by the Gateway after the session's turn
    /// ends (issue #2958). The owner's turn, not the product's: the words are the owner's.</summary>
    public const string GatewayDevReport = "gateway-dev-report";
}

/// <summary>The kind of credential that stood behind the submission when it entered.</summary>
public static class SubmissionIdentityKinds
{
    /// <summary>The person at the desktop, at the keyboard of the machine running the Director.</summary>
    public const string LocalUser = "local-user";

    /// <summary>A per-device key the Gateway verified - a phone, a browser.</summary>
    public const string Device = "device";

    /// <summary>The shared machine token - an operator's command line, a script.</summary>
    public const string MachineToken = "machine-token";

    /// <summary>A session credential - another session in the fleet.</summary>
    public const string Session = "session";

    /// <summary>The product itself.</summary>
    public const string Framework = "framework";

    /// <summary>The door recorded nothing it could vouch for - a Gateway older than this field, or a
    /// request the gate let through with no credential at all. Recorded as such, never guessed.</summary>
    public const string Unknown = "unknown";
}

/// <summary>
/// WHAT A PROMPT ENTRY POINT KNOWS AT THE MOMENT OF ENTRY (owner's ruling, 2026-09-05: source logging). Every
/// door that puts text into a session says which door it is, what credential stood behind the caller, which
/// transcript any spoken characters came from, and which character ranges of the text were spoken. The
/// Session's one choke point adds the content digest and length where the text is in hand, and the activity
/// producer writes all of it onto the turn-submitted ledger row. Nothing downstream ever has to infer any of it.
///
/// It is a REQUIRED argument of the two Session entry points, so a new door cannot be added without saying
/// what it is: the compiler enumerates the entry points.
/// </summary>
/// <param name="Route">One of <see cref="SubmissionRoutes"/>.</param>
/// <param name="IdentityKind">One of <see cref="SubmissionIdentityKinds"/>.</param>
/// <param name="TranscriptId">The transcript's identifier when the door has one (a Gateway upload id), else null.</param>
/// <param name="SpokenSpans">The character ranges of the SENT text that came from a transcript, in text order;
/// empty when none did.</param>
public sealed record SubmissionProvenance(
    string Route,
    string IdentityKind,
    string? TranscriptId,
    IReadOnlyList<SpokenTurnRule.SpokenSpan> SpokenSpans)
{
    public static readonly IReadOnlyList<SpokenTurnRule.SpokenSpan> NoSpans = Array.Empty<SpokenTurnRule.SpokenSpan>();

    /// <summary>A door with no transcript behind it.</summary>
    public static SubmissionProvenance Typed(string route, string identityKind) => new(route, identityKind, null, NoSpans);

    /// <summary>Text the product authored, through the named door.</summary>
    public static SubmissionProvenance FrameworkText(string route = SubmissionRoutes.Framework)
        => new(route, SubmissionIdentityKinds.Framework, null, NoSpans);

    /// <summary>What a relayed prompt carries on the wire, or - when the relay carried nothing - the honest
    /// unknown for the door it came through.</summary>
    public static SubmissionProvenance FromWire(SubmissionProvenanceDto? dto, string routeWhenAbsent)
    {
        if (dto is null) return new(routeWhenAbsent, SubmissionIdentityKinds.Unknown, null, NoSpans);
        var spans = (dto.SpokenSpans ?? new List<SpokenSpanDto>())
            .Select(s => new SpokenTurnRule.SpokenSpan(s.Start, s.Length)).ToArray();
        return new(
            string.IsNullOrWhiteSpace(dto.Route) ? routeWhenAbsent : dto.Route,
            string.IsNullOrWhiteSpace(dto.IdentityKind) ? SubmissionIdentityKinds.Unknown : dto.IdentityKind,
            string.IsNullOrWhiteSpace(dto.TranscriptId) ? null : dto.TranscriptId,
            spans);
    }

    public SubmissionProvenanceDto ToDto() => new()
    {
        Route = Route,
        IdentityKind = IdentityKind,
        TranscriptId = TranscriptId,
        SpokenSpans = SpokenSpans.Select(s => new SpokenSpanDto { Start = s.Start, Length = s.Length }).ToList(),
    };

    /// <summary>The spans as the ledger column stores them: "start+length" pairs, comma-separated, in text
    /// order; null when there are none.</summary>
    public static string? SpansToText(IReadOnlyList<SpokenTurnRule.SpokenSpan> spans)
        => spans.Count == 0 ? null : string.Join(",", spans.Select(s => s.Start.ToString() + "+" + s.Length.ToString()));

    /// <summary>Every span must lie inside the text it describes; a span that does not is a door's defect and
    /// is refused at the choke point rather than written as a lie.</summary>
    public void RequireSpansWithin(int textLength)
    {
        foreach (var span in SpokenSpans)
            if (span.Start < 0 || span.Length <= 0 || span.End > textLength)
                throw new ArgumentException(
                    $"The {Route} door recorded a spoken span {span.Start}+{span.Length} outside a text of {textLength} characters.");
    }
}

/// <summary>
/// What the Session's choke point hands its observers for one submission: the door's provenance, plus the
/// content digest and length the choke point computed. On the text path both are of the text sent; on the
/// raw-byte terminal path the text is never in hand (backspace, arrows and the agent's own line editor mutate
/// the line invisibly), so the digest is null and the length is the printable keystrokes since the last submit.
/// </summary>
public sealed record SubmissionEvidence(SubmissionProvenance Provenance, string? ContentSha256, long ContentLength)
{
    public static string Sha256Of(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();

    public static SubmissionEvidence OfText(SubmissionProvenance provenance, string text)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        ArgumentNullException.ThrowIfNull(text);
        provenance.RequireSpansWithin(text.Length);
        return new(provenance, Sha256Of(text), text.Length);
    }

    public static SubmissionEvidence OfKeystrokes(SubmissionProvenance provenance, int printableCharacters)
    {
        ArgumentNullException.ThrowIfNull(provenance);
        return new(provenance, null, printableCharacters);
    }
}
