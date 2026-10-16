namespace CcDirector.Gateway.Contracts;

/// <summary>
/// Gateway Cleanup mission, Phase 0: the up-stream primitive. This is the ONE frame type that carries every
/// Director-to-Gateway byte stream over the tunnel - both the open-ended live terminal output AND a finite
/// file/screenshot byte read - keyed by <see cref="StreamId"/>.
///
/// The tunnel is the existing two-way SignalR channel (the Director dials the Gateway's DirectorHub and binds
/// its id with <see cref="DirectorStreamHello"/>). The Director is the SignalR CLIENT, so an up-stream is
/// native client-to-server streaming: the Gateway sends a unary "open" command carrying a fresh
/// <see cref="StreamId"/>, the Director starts a producer and streams these frames UP under that id via the
/// hub's StreamUp method until the Gateway sends a "close-stream" command (the load-bearing stop signal - see
/// the Architect ruling in docs/architecture/gateway-cleanup-phase0-tunnel-protocol.md) or the producer
/// reaches its end.
///
/// The browser-facing contract is UNCHANGED: the browser still opens the same WebSocket at
/// <c>/sessions/{sid}/stream</c> and the same GET for a file; the Gateway translates each frame back into the
/// browser wire form it always sent (a Size frame -> the {"type":"size"} json, a Binary frame -> a binary
/// WebSocket message or an HTTP body chunk, a Closed frame -> {"type":"closed"} then close).
/// </summary>
public sealed class DirectorStreamFrame
{
    /// <summary>Correlates this frame to the browser request the Gateway is serving. A fresh Guid per open, never reused.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>What this frame carries. See <see cref="DirectorStreamFrameType"/>.</summary>
    public DirectorStreamFrameType Kind { get; set; }

    /// <summary>
    /// The binary payload for a <see cref="DirectorStreamFrameType.Binary"/> frame: raw PTY bytes for the
    /// terminal, or a file chunk for a finite read. Null for Size/Closed frames. BOUNDED: a producer never
    /// emits more than <see cref="DirectorStreamLimits.MaxBinaryFrameBytes"/> in one frame, so no single frame
    /// can monopolize the one shared tunnel connection (Architect ruling 2).
    /// </summary>
    public byte[]? Data { get; set; }

    /// <summary>Terminal grid columns; meaningful only on a <see cref="DirectorStreamFrameType.Size"/> frame.</summary>
    public int Cols { get; set; }

    /// <summary>Terminal grid rows; meaningful only on a <see cref="DirectorStreamFrameType.Size"/> frame.</summary>
    public int Rows { get; set; }

    /// <summary>
    /// Why the stream ended; meaningful only on a <see cref="DirectorStreamFrameType.Closed"/> frame
    /// (for example "session exited", "session not found", or "eof" for the natural end of a finite read).
    /// </summary>
    public string? Reason { get; set; }
}

/// <summary>The three frame kinds that serve both the terminal stream and a finite byte read.</summary>
public enum DirectorStreamFrameType
{
    /// <summary>A terminal grid size (Cols x Rows). Sent first on a terminal stream and again on every PTY resize.</summary>
    Size = 0,

    /// <summary>A chunk of bytes (<see cref="DirectorStreamFrame.Data"/>): live PTY output, or a file chunk.</summary>
    Binary = 1,

    /// <summary>The stream has ended (<see cref="DirectorStreamFrame.Reason"/> says why). The last frame of any stream.</summary>
    Closed = 2,
}

/// <summary>
/// Shared numeric bounds for the up-stream (Architect ruling 2). The frame cap and the hub's
/// MaximumReceiveMessageSize are set from the SAME constant so they can never drift apart: a producer that
/// chunks at the cap can never emit a frame the hub would reject, and a bounded frame keeps the one shared
/// tunnel connection interleaving terminal output, file chunks, and unary commands instead of stalling on a
/// single large message.
/// </summary>
public static class DirectorStreamLimits
{
    /// <summary>
    /// The maximum bytes in a single <see cref="DirectorStreamFrame.Data"/> payload (48 KB - inside the
    /// 32-to-64 KB band the Architect fixed). The file producer chunks at this bound; the terminal producer's
    /// tail frames are already smaller. The hub's MaximumReceiveMessageSize is set to this plus a small
    /// envelope allowance so the framed message (with its StreamId and metadata) still fits.
    /// </summary>
    public const int MaxBinaryFrameBytes = 48 * 1024;

    /// <summary>
    /// SignalR StreamBufferCapacity for the up-stream (Architect ruling 1): small, single-digit, so a slow
    /// browser sink fills this channel quickly and pushes back onto the Director producer's <c>yield return</c>,
    /// giving end-to-end backpressure with bounded memory. NOT an optimization - the backpressure invariant.
    /// </summary>
    public const int StreamBufferCapacity = 4;

    /// <summary>
    /// The extra envelope room (over <see cref="MaxBinaryFrameBytes"/>) allowed for a framed message's own
    /// fields (StreamId, kind, size ints, reason) plus the SignalR/MessagePack framing, used to set the hub's
    /// MaximumReceiveMessageSize = MaxBinaryFrameBytes + this.
    /// </summary>
    public const int FrameEnvelopeAllowanceBytes = 4 * 1024;

    /// <summary>
    /// Gateway Cleanup mission, Phase 2 (upload-image): the RAW byte size of one DOWN-stream image chunk. An
    /// image uploaded to the Gateway is split into pieces this large and sent to the Director across unary
    /// commands (begin / chunk / complete), so a whole photo never rides as one large unary message that would
    /// monopolize the shared tunnel (Architect ruling 2). Deliberately small: each chunk is base64-encoded in
    /// the command payload (+33%), so 20 KB raw is ~27 KB on the wire - comfortably under the SignalR default
    /// receive limit even with the command envelope, whatever a client's limit is. Images are small and
    /// infrequent, so the extra chunks are free; the invariant that matters is bounded, no-monopoly framing.
    /// </summary>
    public const int UploadChunkRawBytes = 20 * 1024;

    /// <summary>
    /// How often each side sends a keep-alive ping down an otherwise idle tunnel.
    ///
    /// DELIBERATELY SHORTER THAN THE SIGNALR DEFAULT of 15 seconds, and the reason is an outage rather than a
    /// preference. A peer hangs up when it has heard NOTHING for <see cref="SilenceTolerance"/>, so on the
    /// default pair (15s ping, 30s tolerance) exactly TWO late pings end the tunnel - and the keep-alive is a
    /// timer callback on the same thread pool the hub's own handlers run on, so a busy Gateway delays the
    /// pings FIRST. That is not hypothetical: on 2026-07-30 one Director hung up 35 times in a day on
    /// "Server timeout (30000.00ms) elapsed without receiving a message from the server" while the Gateway was
    /// alive and answering other work in the same second (devthrottle_internal issue #1153).
    ///
    /// At 5 seconds the SAME tolerance absorbs six missed pings instead of two, so a momentarily busy Gateway
    /// is no longer mistaken for a dead one. The robustness is bought with cheap empty frames, NOT by waiting
    /// longer to notice a genuinely dead peer - see <see cref="SilenceTolerance"/>, which does not move.
    /// </summary>
    public static readonly TimeSpan KeepAlivePing = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How long each side tolerates COMPLETE SILENCE from the other before declaring the connection dead.
    ///
    /// Held at the SignalR default of 30 seconds ON PURPOSE, and it is worth saying why it is not being tuned
    /// in either direction. Shortening it makes a Director hang up on a merely busy Gateway sooner, which is
    /// the exact failure this pair exists to prevent. Lengthening it delays noticing a genuinely dead peer,
    /// which costs the fleet real recovery time. Neither is the fix; pinging more often is.
    ///
    /// INVARIANT: this must stay at least twice <see cref="KeepAlivePing"/> - the framework's own rule - or a
    /// single late ping ends the connection. At 5s against 30s there is six times the room.
    /// </summary>
    public static readonly TimeSpan SilenceTolerance = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The maximum bytes in a single unary command REPLY (a <see cref="DirectorCommandResult"/> carried back
    /// over the tunnel as a SignalR client result via InvokeAsync). Unlike the up-stream - which is chunked at
    /// <see cref="MaxBinaryFrameBytes"/> - a command reply is ONE message: some read verbs (turns, full
    /// buffer/html) legitimately return the whole structure at once, and for a long-running session that is far
    /// larger than a single stream frame. The hub's MaximumReceiveMessageSize must therefore admit the LARGER of
    /// a framed up-stream message and this value; otherwise a big reply tears down the shared tunnel connection
    /// ("The maximum message size ... was exceeded"), which both 500s the read (voice/explain, History/turns,
    /// full buffer) AND drops the Director's roster registration - the mobile "blinking". Raising the receive cap
    /// does NOT weaken streaming backpressure: the up-stream producer still chunks at <see cref="MaxBinaryFrameBytes"/>
    /// and <see cref="StreamBufferCapacity"/> is unchanged. 32 MB is a generous ceiling for a realistic
    /// full-history turns or whole-buffer reply while still bounding a single message's memory.
    /// </summary>
    public const int MaxCommandReplyBytes = 32 * 1024 * 1024;

    /// <summary>
    /// The hub's MaximumReceiveMessageSize: the LARGER of a framed up-stream message
    /// (<see cref="MaxBinaryFrameBytes"/> + <see cref="FrameEnvelopeAllowanceBytes"/>) and a unary command reply
    /// (<see cref="MaxCommandReplyBytes"/>). One knob bounds every inbound message on the shared tunnel. Kept as
    /// a single derived constant so the hub option and any future producer share one source of truth.
    /// </summary>
    public const int MaxInboundMessageBytes =
        MaxBinaryFrameBytes + FrameEnvelopeAllowanceBytes > MaxCommandReplyBytes
            ? MaxBinaryFrameBytes + FrameEnvelopeAllowanceBytes
            : MaxCommandReplyBytes;
}

/// <summary>
/// The payload of the unary "open" command that starts an up-stream (verbs open-terminal-stream / read-file /
/// screenshot-file). Rides in <see cref="DirectorCommand.PayloadJson"/>; the target session is
/// <see cref="DirectorCommand.SessionId"/> on the command itself.
/// </summary>
public sealed class OpenStreamRequest
{
    /// <summary>The fresh Guid the Gateway minted for this stream. The Director tags every up-frame with it.</summary>
    public string StreamId { get; set; } = "";

    /// <summary>For a file read (read-file): the file path to serve. Null/absent for a terminal stream.</summary>
    public string? Path { get; set; }

    /// <summary>For a screenshot read (screenshot-file): the screenshot id/name to serve. Null/absent otherwise.</summary>
    public string? ScreenshotId { get; set; }
}

/// <summary>
/// The success body of a finite read's "open" command (Architect ruling 4). When the Director can cheaply
/// stat the resource up front, it returns the total byte length and content type in the open command's
/// <see cref="DirectorCommandResult.BodyJson"/>, so the Gateway can set <c>Content-Length</c> on the browser
/// response instead of falling back to chunked transfer. A terminal open returns no body (open-ended).
/// </summary>
public sealed class OpenReadResponse
{
    /// <summary>Total bytes the finite read will deliver, when known up front; null when not cheaply knowable.</summary>
    public long? TotalBytes { get; set; }

    /// <summary>The resource's content type (for the browser response), when known.</summary>
    public string? ContentType { get; set; }
}

/// <summary>The payload of the unary "close-stream" command (Architect ruling 3): the id of the stream to stop.
/// Idempotent on the Director - closing a stream that already ended or never started is a safe no-op.</summary>
public sealed class CloseStreamRequest
{
    /// <summary>The stream to stop. Cancels the Director producer's CancellationToken for this id.</summary>
    public string StreamId { get; set; } = "";
}

/// <summary>
/// API mode (mission #4070, phase 2): ONE SESSION'S API-MODE EVENTS, CARRIED UP FROM THE DIRECTOR TO THE GATEWAY. The
/// Director invokes the hub's <c>PushApiModeEvents</c> with one of these; the Gateway stores the receipts the events
/// carry, keeps the recent events per session, and hands them to the Gateway code that folds and relays them. The
/// events themselves are the session event model (<see cref="ApiMode.SessionEvent"/>,
/// <c>docs/missions/api-mode-pi-2026-10-16/event-model.md</c>, section 2).
///
/// <para><b>Each event travels as its own JSON text</b>, written with <see cref="ApiMode.SessionEventJson.Options"/>
/// (one object per event, its <c>type</c> naming the event). The tunnel negotiates MessagePack or JSON, and the event
/// records are polymorphic: carried as text, an event reaches the Gateway in exactly the form the event model writes,
/// whichever protocol the Director's connection chose, and no second encoding of the model exists to drift.</para>
///
/// <para><b>Ordering: generation, then sequence.</b> <see cref="Generation"/> is assigned by the Director each time it
/// starts holding the session's event stream (a Director restart is a new generation; taking the wheel and handing
/// back inside one Director process is not), with <see cref="GenerationStartedUtc"/>. Inside one generation each
/// event's <c>sequence</c> rises, and the FIRST event of every generation has sequence <see cref="FirstSequence"/> (1);
/// a new generation starts its sequence again. The Gateway relies on that start: when it first holds a generation
/// from a later sequence (it restarted while the Director did not), it knows the earlier events were never held and
/// tells its readers so. The Gateway's rule, the same one the turn push uses for its generations:</para>
/// <list type="bullet">
/// <item>Same generation: an event at or below the highest sequence the Gateway holds is a duplicate or a late
/// arrival and stores nothing.</item>
/// <item>A generation that started LATER (ties broken by the generation id, ordinally) replaces the session's
/// current one, and its sequence counts afresh.</item>
/// <item>A generation that started EARLIER is refused whole: a Director that has been superseded cannot move the
/// session backwards.</item>
/// </list>
///
/// <para><b>Rules for a batch</b> (a batch that breaks one is refused whole and nothing is stored): every event names
/// <see cref="SessionId"/>; sequences start at <see cref="FirstSequence"/> and rise strictly inside the batch; at most
/// <see cref="MaxEvents"/> events and <see cref="MaxCharacters"/> characters of event JSON in all; a <c>TurnEnded</c>
/// that is not <c>done</c> carries a reason. The session must be the sending Director's: in a team, the same ownership
/// rule as the turn push. A Director sizes its batches by characters as well as by count: a tool's output can be
/// large, and a message past the tunnel's receive limit closes the connection instead of being answered.</para>
///
/// <para><b>The answer</b> is one of three, and a Director must tell them apart:</para>
/// <list type="bullet">
/// <item>An <see cref="ApiModeEventWatermark"/> with a generation: STORED. The generation the session is on and the
/// highest sequence held in it, so the Director knows what has landed.</item>
/// <item>An <see cref="ApiModeEventWatermark"/> whose <see cref="ApiModeEventWatermark.Generation"/> is EMPTY (and
/// sequence -1): REFUSED FOR NOW, nothing was stored. In a team, this Director is not yet the only one whose roster
/// holds the session. It is not a watermark to adopt: send the same batch again at the next trigger.</item>
/// <item><c>null</c>: REFUSED FOR GOOD - malformed, an older generation, or not this Director's session - and
/// re-sending the same batch cannot succeed; the Gateway logs the reason.</item>
/// </list>
/// <para>A hub error (the database unreachable) is an exception: nothing was kept, and the same batch may be sent
/// again safely, because storing is idempotent.</para>
/// </summary>
public sealed class ApiModeEventBatch
{
    /// <summary>The most events one batch may carry.</summary>
    public const int MaxEvents = 500;

    /// <summary>The longest generation id accepted.</summary>
    public const int MaxGenerationLength = 64;

    /// <summary>The most characters of event JSON one batch may carry, all events together.</summary>
    public const int MaxCharacters = 4_000_000;

    /// <summary>The sequence of the first event of every generation.</summary>
    public const long FirstSequence = 1;

    /// <summary>The DevThrottle session every event in the batch belongs to.</summary>
    public string SessionId { get; set; } = "";

    /// <summary>The Director's generation of this session's event stream (a fresh id each time the Director starts
    /// holding it; a Guid is ideal).</summary>
    public string Generation { get; set; } = "";

    /// <summary>When the Director started this generation, UTC. Compared at whole milliseconds.</summary>
    public DateTime GenerationStartedUtc { get; set; }

    /// <summary>The events, oldest first, each one <see cref="ApiMode.SessionEvent"/> serialised with
    /// <see cref="ApiMode.SessionEventJson.Options"/>.</summary>
    public List<string> Events { get; set; } = new();
}

/// <summary>The Gateway's answer to an <see cref="ApiModeEventBatch"/>: what it now holds for the session.</summary>
public sealed class ApiModeEventWatermark
{
    public string SessionId { get; set; } = "";

    /// <summary>The generation the session is on at the Gateway.</summary>
    public string Generation { get; set; } = "";

    /// <summary>The highest sequence of <see cref="Generation"/> the Gateway holds; -1 when it holds none.</summary>
    /// <remarks>An EMPTY <see cref="Generation"/> is not a watermark: it is the answer "refused for now, nothing stored,
    /// send it again" (see <see cref="ApiModeEventBatch"/>).</remarks>
    public long Sequence { get; set; }
}
