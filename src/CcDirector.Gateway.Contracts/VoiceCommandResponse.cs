namespace CcDirector.Gateway.Contracts;

/// <summary>
/// POST /voice/command response.
///
/// Voice mode is one-shot: the browser uploads an audio blob, the Director
/// transcribes via Whisper, classifies an intent, executes it against the
/// existing SessionManager-backed services, and returns this object.
/// The browser then displays the conversation and reads <see cref="ReplyText"/>
/// out via the browser's SpeechSynthesis API.
/// </summary>
public sealed class VoiceCommandResponse
{
    /// <summary>The transcript Whisper returned for the audio blob.</summary>
    public string Transcript { get; set; } = "";

    /// <summary>
    /// The Wingman-cleaned version of <see cref="Transcript"/>: filler words
    /// removed, obvious mis-transcriptions fixed, intent preserved.  Set by
    /// Phase 1 of the SessionWingman goal.  When cleanup fails or is skipped,
    /// this equals the raw transcript and <see cref="CleanupReason"/> explains why.
    /// The voice mode UI uses this (not the raw) when forwarding to /chat.
    /// </summary>
    public string? CleanedTranscript { get; set; }

    /// <summary>One-sentence explanation from the Wingman of what it changed
    /// (or "no changes needed" / a failure reason).</summary>
    public string? CleanupReason { get; set; }

    /// <summary>
    /// The spoken-style reply text the Manager wants the user to hear.
    /// Always present even when the command is unknown or failed (in which
    /// case it is the explanation).
    /// </summary>
    public string ReplyText { get; set; } = "";

    /// <summary>
    /// Intent classification: ListSessions / ListWaiting / DescribeSession /
    /// OpenSession / SendToSession / InterruptSession / Unknown.
    /// </summary>
    public string Intent { get; set; } = "";

    /// <summary>
    /// Session this command resolved to, when applicable (e.g. DescribeSession,
    /// SendToSession). Null for fleet-level intents.
    /// </summary>
    public string? TargetSessionId { get; set; }

    /// <summary>
    /// Friendly name of the resolved target session (CustomName or repo
    /// folder), so the UI can label suggestion buttons without re-fetching.
    /// </summary>
    public string? TargetSessionName { get; set; }

    /// <summary>
    /// Buttons the UI should show beneath the reply for one-click follow-up.
    /// Each suggestion is a labeled action the user can confirm by tapping
    /// or by saying its label aloud.
    /// </summary>
    public List<VoiceSuggestion> Suggestions { get; set; } = new();

    /// <summary>"ok" | "no_key" | "transcribe_failed" | "execute_failed" | "unknown_command".</summary>
    public string Status { get; set; } = "ok";

    /// <summary>Free-text error detail when Status != "ok".</summary>
    public string? Error { get; set; }
}

/// <summary>
/// One actionable suggestion the UI renders as a button under the reply.
/// </summary>
public sealed class VoiceSuggestion
{
    /// <summary>Button label shown to the user.</summary>
    public string Label { get; set; } = "";

    /// <summary>"open" | "send" | "interrupt".  The browser uses this to
    /// decide what to do when the button is clicked.</summary>
    public string Kind { get; set; } = "";

    /// <summary>Director session GUID the action targets (when applicable).</summary>
    public string? SessionId { get; set; }

    /// <summary>For Kind=="send", the text that will be sent as the prompt.</summary>
    public string? PromptText { get; set; }
}
