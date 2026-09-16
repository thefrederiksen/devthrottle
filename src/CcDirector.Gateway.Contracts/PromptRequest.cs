namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Body of POST /sessions/{sid}/prompt on both the Director Control API and the Gateway.
/// </summary>
public sealed class PromptRequest
{
    /// <summary>The text to send to the session.</summary>
    public string Text { get; set; } = "";

    /// <summary>If true (default), append Enter after the text so the session executes the prompt.</summary>
    public bool AppendEnter { get; set; } = true;

    /// <summary>
    /// If true, the Gateway holds the response open until the session returns to Idle (or timeout).
    /// Ignored by the Director's own Control API (which always returns immediately after queueing).
    /// </summary>
    public bool WaitForIdle { get; set; } = false;

    /// <summary>Wait timeout in milliseconds. Default 120000 (2 min). Only used when WaitForIdle=true.</summary>
    public int TimeoutMs { get; set; } = 120_000;

    /// <summary>
    /// True when this prompt is one AGENT prompting another across the fleet (issue #1636), rather than a
    /// person. Set by the fleet relay when a message is addressed to a session on ANOTHER Director: such a
    /// message arrives here as an ordinary prompt, and without this marker it is indistinguishable from a
    /// human one - so the same fleet message would count as agent-driven or not purely by the accident of
    /// whether the two sessions happened to share a Director.
    ///
    /// Same trick as <see cref="DeliveryUploadId"/>, which marks a dictation delivery over the tunnel: the
    /// DTO carries what an HTTP header used to.
    ///
    /// Never counted as a human turn, whatever Surface accompanies it.
    /// </summary>
    public bool AgentDriven { get; set; }

    /// <summary>
    /// DevThrottle Stats surface tag: which surface this remote prompt came from - "phone", "cockpit",
    /// or null when unknown. GATEWAY-AUTHORITATIVE: the Gateway resolves it from the verified per-device
    /// key and OVERWRITES any client-supplied value before forwarding, so it cannot be forged from a
    /// client. Null on a direct Director call (there is no device key to resolve). Rides both prompt
    /// delivery paths (the HTTP body and the SignalR command payload), so the Director's choke-point tally
    /// gets the surface without a separate header. The modality (typed vs voice) is NOT carried here - it
    /// comes from the existing X-Dictation-Delivery marker (a voice delivery) via <c>SendSource</c>.
    /// </summary>
    public string? Surface { get; set; }

    /// <summary>
    /// Gateway Cleanup mission, Phase 2: the dictation delivery marker (issue #1181). When set, this prompt
    /// is a dictation's OWN arrival, so the Director sends it as <c>SendSource.Delivery</c> (a voice turn),
    /// exempt from the dictation lock. Over the REST path this used to ride ONLY the <c>X-Dictation-Delivery</c>
    /// header; carrying it in the request DTO instead lets the tunnel prompt verb mark a Delivery without any
    /// HTTP header (the frozen tunnel envelope is unchanged - this is a request-DTO field). The header is still
    /// honored on the REST path for back-compat; either signal marks the send as a Delivery. Null for a normal
    /// operator prompt.
    /// </summary>
    public string? DeliveryUploadId { get; set; }

    /// <summary>
    /// Wingman menu guard (issue #2193). When true, the GATEWAY reads the session's live screen immediately
    /// before forwarding this prompt and REFUSES to send it if a menu owns that screen - answering
    /// <see cref="PromptResponse.BlockedByMenu"/> instead. Typing a spoken sentence into an on-screen picker
    /// achieves nothing, and because voice replies carry <see cref="AppendEnter"/> the trailing Enter would
    /// CONFIRM whichever option happened to be highlighted - a selection the person never made and is never
    /// told about.
    ///
    /// Opt-in and off by default, so the typed composer, the Chat send, and every fleet/automation caller are
    /// completely unchanged. Only the voice reply paths set it. It is a GATEWAY field: it is consumed there
    /// and never forwarded, so the Director's own prompt verb sees exactly what it always saw.
    /// </summary>
    public bool MenuGuard { get; set; }

    /// <summary>
    /// TYPE THIS ONLY IF THE SESSION IS WAITING FOR A PROMPT RIGHT NOW (the Fleet Manager mission, step 4). When
    /// true, the DIRECTOR - the one process that knows the session's state at the moment it types - refuses the
    /// prompt unless the session is <c>Idle</c> or <c>WaitingForInput</c> at that instant, and answers
    /// <see cref="PromptResponse.RefusedBusy"/> instead. Nothing is typed and no Enter is pressed. A session that is
    /// working, starting, waiting on a permission question, or exited is refused.
    ///
    /// It exists for text the product sends on its own - the Fleet Manager's events - which must never land in a
    /// turn the owner has just started. A Gateway reading the pushed state and then sending cannot promise that: the
    /// owner can submit between the read and the send. The Director checks and types in the same step.
    ///
    /// A Director older than this field ignores it and types; <see cref="PromptResponse.IdleChecked"/> is how the
    /// sender can tell afterwards. Off by default: every other caller is unchanged.
    /// </summary>
    public bool OnlyWhenWaitingForInput { get; set; }

    /// <summary>
    /// A client's CLAIM about which characters of <see cref="Text"/> came from which transcript (source logging,
    /// 2026-09-05). A browser composer tracks the ranges its dictation occupies as the person edits around them
    /// and sends them here, so a turn that mixes typing and speech still says WHICH characters were spoken.
    ///
    /// This is a claim, not a fact, and the Gateway treats it as one: each span is verified against the spoken
    /// claim registry - the characters it names must BE the transcript that id registered - and only verified
    /// spans reach <see cref="Provenance"/>. An unverified span is dropped and logged, never recorded. The route
    /// and the credential kind are never taken from a client at all; the Gateway owns both.
    /// </summary>
    public List<SpokenSpanClaimDto>? SpokenSpans { get; set; }

    /// <summary>What the Gateway's door knew at entry (source logging, 2026-09-05): the route, the credential
    /// kind, the transcript claim and where its characters stand in <see cref="Text"/>. Built by the Gateway
    /// route that accepted the request; the Director records it on the ledger row untouched. Null only from a
    /// Gateway older than this field, which the Director records as the unknown it is.
    ///
    /// A client cannot set this: whatever arrives here is overwritten by the route from what it verified.</summary>
    public SubmissionProvenanceDto? Provenance { get; set; }
}

/// <summary>One client claim: the text from <see cref="Start"/> for <see cref="Length"/> characters is the
/// transcript the Gateway registered under <see cref="TranscriptId"/>. Verified before it is believed.</summary>
public sealed class SpokenSpanClaimDto
{
    public int Start { get; set; }
    public int Length { get; set; }
    public string? TranscriptId { get; set; }
}

/// <summary>
/// Response from POST /sessions/{sid}/prompt.
/// </summary>
public sealed class PromptResponse
{
    /// <summary>True if the prompt was accepted and dispatched to the session.</summary>
    public bool Accepted { get; set; }

    /// <summary>UTC timestamp the prompt was accepted by the Director.</summary>
    public DateTime SentAt { get; set; }

    /// <summary>Buffer position (TotalBufferBytes) immediately before the prompt was sent. Use as ?since= cursor.</summary>
    public long BufferCursor { get; set; }

    /// <summary>Activity state after dispatch. Working if accepted; whatever the session was if rejected.</summary>
    public string ActivityState { get; set; } = "";

    /// <summary>Only set if WaitForIdle=true: cleaned output produced by the session for this prompt.</summary>
    public string? Output { get; set; }

    /// <summary>Only set if WaitForIdle=true: idle | timeout | failed.</summary>
    public string? WaitStatus { get; set; }

    /// <summary>Error message if Accepted == false.</summary>
    public string? Error { get; set; }

    /// <summary>
    /// Wingman menu guard (issue #2193): true when this prompt was NOT sent because a menu owns the session's
    /// live screen and the caller asked for <see cref="PromptRequest.MenuGuard"/>. Nothing was typed and no
    /// Enter was pressed. This is a REFUSAL, not a failure - it rides a 200 with <see cref="Accepted"/> false,
    /// because the caller asked for exactly this behaviour and there is nothing to retry.
    /// </summary>
    public bool BlockedByMenu { get; set; }

    /// <summary>The line to SPEAK when <see cref="BlockedByMenu"/> is true - the wingman is hands-free, so the
    /// refusal has to reach the ear, not just a screen. Empty otherwise.</summary>
    public string? BlockedSpoken { get; set; }

    /// <summary>
    /// The language <see cref="BlockedSpoken"/> is written in, as the short code (<c>en</c>/<c>fr</c>/<c>es</c>)
    /// - issue #1031.
    ///
    /// It travels WITH the words because this particular notice is spoken by the BROWSER's own speech synthesis
    /// rather than by the Gateway, and the browser cannot build an utterance without a language. Before it, the
    /// client was handed a bare string, set no language, and pronounced a correctly translated French refusal
    /// with the device's default English voice: the audio plays, nothing errors, and it is wrong. The client
    /// cannot work this out for itself - it does not know the account's language - so the Gateway states it.
    /// </summary>
    public string? BlockedSpokenLanguage { get; set; }

    /// <summary>
    /// True when this prompt was NOT typed because the caller asked for
    /// <see cref="PromptRequest.OnlyWhenWaitingForInput"/> and the session was not waiting for a prompt at the moment
    /// the Director would have typed it. <see cref="ActivityState"/> says what it was. A refusal, not a failure: it
    /// rides a success with <see cref="Accepted"/> false, and the caller tries again at the session's next idle moment.
    /// </summary>
    public bool RefusedBusy { get; set; }

    /// <summary>
    /// True when the Director checked <see cref="PromptRequest.OnlyWhenWaitingForInput"/> before typing. False on an
    /// accepted prompt means the Director did not check it (it is older than the field), so the text was typed
    /// whatever the session was doing.
    /// </summary>
    public bool IdleChecked { get; set; }
}

/// <summary>
/// Body of POST /sessions/{sid}/resize on the Director Control API. Sets the session's PTY
/// grid so a remote terminal (the Cockpit) can use the full window width.
/// </summary>
public sealed class ResizeRequest
{
    /// <summary>Column count (must be &gt; 0).</summary>
    public int Cols { get; set; }

    /// <summary>Row count (must be &gt; 0).</summary>
    public int Rows { get; set; }
}

/// <summary>
/// Response of the resize verb / POST /sessions/{sid}/resize: the grid the session settled on after the
/// resize (clamped to the PTY's limits). Gateway Cleanup mission, Phase 0: the resize verb returns this so
/// the REST route and the tunnel verb share one typed body.
/// </summary>
public sealed class ResizeResponse
{
    /// <summary>Always true on success (the resize was applied).</summary>
    public bool Accepted { get; set; }

    /// <summary>The session's resulting column count.</summary>
    public int Cols { get; set; }

    /// <summary>The session's resulting row count.</summary>
    public int Rows { get; set; }
}

/// <summary>
/// Payload of the terminal-input verb (Gateway Cleanup mission, Phase 0): a browser keystroke frame to
/// forward verbatim to the session's PTY. The bytes are base64-encoded so control bytes (arrows, Ctrl+C,
/// Esc) survive the JSON envelope intact. The target session is the command's SessionId. This is a plain
/// unary write, not a stream verb.
/// </summary>
public sealed class TerminalInputRequest
{
    /// <summary>The raw keystroke bytes, base64-encoded.</summary>
    public string Bytes { get; set; } = "";

    /// <summary>What the Gateway's terminal relay knew when the browser opened this socket (source logging,
    /// 2026-09-05): the route, and the kind of credential the gate verified for the person typing. Stamped at
    /// the socket, not per frame - one browser holds one identity for the life of its terminal. Null only from
    /// a Gateway older than this field, which the Director records as the unknown it is.</summary>
    public SubmissionProvenanceDto? Provenance { get; set; }
}

/// <summary>Body of the git stage/unstage/discard endpoints. Empty paths means "all" (stage/unstage only).</summary>
public sealed class GitPathsRequest
{
    public List<string> Paths { get; set; } = new();
}

/// <summary>Body of POST /sessions/{sid}/git/commit.</summary>
public sealed class GitCommitRequest
{
    public string Message { get; set; } = "";
}

/// <summary>Body of POST /sessions/{sid}/relink - re-point a Director session at a different Claude session id.</summary>
public sealed class RelinkRequest
{
    public string ClaudeSessionId { get; set; } = "";
}
