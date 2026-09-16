namespace CcDirector.Gateway.Contracts;

/// <summary>
/// The FOLDED voice-mode display verdict for one session - what the Voice screen must show and offer,
/// computed once on the Gateway and rendered VERBATIM by every client.
///
/// This exists because the voice screen was the last place a client still RULED for itself. Every other
/// display verdict in the product moved to the Gateway long ago (the session rail color is folded by the
/// Gateway and pushed as a dumb blue/red/yellow - see SessionOrdering / the set-display-state verb), but
/// the phone's Voice tab kept deriving its own "is the audio available" answer from nine local inputs
/// (the old client-core voiceAvailability.ts) and then branching on retrying / service-down / reason to
/// choose the badge, the message, and whether a Generate button appeared. When the Gateway handed it no
/// reason for "there is no narration", the client had to GUESS what to render - and it guessed a hopeful
/// "Generate narration now" button next to a red "Voice unavailable" badge, a button that re-ran the same
/// empty read and could never succeed. A dumb client cannot guess; it renders what it is told.
///
/// THE LAW (docs/new_architecture/sessions.html): the client is dumb. ALL ruling - state, colors,
/// labels, and which actions are offered - is computed on the Gateway and pushed; clients only render.
/// So this carries the finished strings and booleans, not the facts to re-derive them from. Add a new
/// voice state HERE and in <c>VoiceDisplayFold</c>, never as a fresh branch in a client view.
/// </summary>
public sealed class VoiceDisplay
{
    /// <summary>Machine-readable state key, for the client's tone/style lookup only (never re-ruling):
    /// <c>off</c>, <c>ready</c>, <c>preparing</c>, <c>retrying</c>, <c>serviceDown</c>, <c>blocked</c>
    /// (credits / cap / no key), <c>nothingToNarrate</c> (waiting on a prompt, no text reply to read),
    /// or <c>notReady</c> (no audio yet, generation may still produce some).</summary>
    public string Kind { get; set; } = "";

    /// <summary>Render hint for the color band ONLY - <c>green</c>, <c>yellow</c>, <c>red</c>, or
    /// <c>neutral</c>. A dumb tone-to-class lookup, not a decision: the Gateway already decided.</summary>
    public string Tone { get; set; } = "";

    /// <summary>The badge/headline, rendered verbatim (e.g. "Voice ready", "Voice on its way",
    /// "Nothing to read aloud"). Never assembled or overridden on the client.</summary>
    public string Label { get; set; } = "";

    /// <summary>The one-line body, rendered verbatim (e.g. "This session is waiting on a prompt, not a
    /// text answer, so there is nothing to read aloud yet."). Never assembled on the client.</summary>
    public string Message { get; set; } = "";

    /// <summary>True when there is fetchable, playable audio for this turn - the client may show a play
    /// affordance. Playback itself (buffering the bytes, play/pause) is the phone's own local concern;
    /// WHETHER there is anything to play is this Gateway verdict.</summary>
    public bool CanPlay { get; set; }

    /// <summary>True when offering "Generate narration now" is valid - i.e. there may be a text reply to
    /// narrate that simply has not been made yet. FALSE for every dead-end (nothing to narrate, service
    /// down, retrying, out of credits): a button that cannot succeed is worse than no button, because it
    /// invites the user to keep pressing and blame themselves. This one flag is the whole screenshot bug.</summary>
    public bool CanGenerate { get; set; }

    /// <summary>The optional shared call-to-action (add credit / add a key) when <see cref="Kind"/> is
    /// <c>blocked</c> - the same single-source <see cref="HostedAiMessageDto"/> the roster uses. Null
    /// otherwise.</summary>
    public HostedAiMessageDto? Reason { get; set; }

    /// <summary>
    /// How long this session has been waiting for its voice, already in words ("4m", "1h 12m"), or null
    /// when it is not waiting or has waited under a minute (issue #2576).
    ///
    /// Composed HERE rather than on each client, from the Gateway's own VoiceWaitingSince stamp, for the
    /// same reason every other string on this record is: a second place that turns the stamp into words is
    /// a second answer to a question this fold has already answered. A spinner alone reads identically at
    /// two seconds and at forty-eight minutes, which is exactly how a session sat stuck for forty-eight
    /// minutes with nobody able to say so.
    ///
    /// COARSE on purpose - whole minutes, refreshed at whatever rate the client polls. A precise duration
    /// computed at stamp time would be wrong by however long it sat in transit, and this number exists to
    /// answer "is this stuck?", which minutes answer and seconds do not.
    /// </summary>
    public string? WaitedLabel { get; set; }

    /// <summary>
    /// The optional GENERIC heads-up shown when this turn's ready clip was made by the BACKUP voice
    /// provider (the primary was temporarily overloaded and the cloud proxy quietly failed over - see the
    /// TTS-fallback mission). Null in the normal case. It is the finished, verbatim string every client
    /// shows AS-IS next to the voice card; the Gateway computes it once (VoiceDisplayFold) so no client
    /// re-derives it. It NAMES NO PROVIDER and states there is no extra charge, because the member is
    /// billed the normal rate on a fallback. A success-with-a-note, not a failure: it rides the green
    /// <c>ready</c> verdict, never a red/unavailable one.
    /// </summary>
    public string? VoiceFallbackNotice { get; set; }
}

/// <summary>
/// The <see cref="VoiceDisplay.Kind"/> values that other folds ask about by name, in ONE place so a rule
/// and the fold that produces it cannot spell the same state two ways.
///
/// Only the kinds a rule outside <c>VoiceDisplayFold</c> actually reads are named here. The rest stay as
/// that fold's own literals: a constant nobody asks for is a second name for one idea, which is the thing
/// this file exists to prevent.
/// </summary>
public static class VoiceDisplayKinds
{
    /// <summary>Nothing arrived inside the give-up window. Automatic attempts continue; the PROMISE is over.</summary>
    public const string GaveUp = "gaveUp";

    /// <summary>The narration was abandoned: the model leg did not answer and nothing further is scheduled.</summary>
    public const string NotNarrated = "notNarrated";
}
