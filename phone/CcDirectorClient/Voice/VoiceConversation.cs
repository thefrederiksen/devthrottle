namespace CcDirectorClient.Voice;

/// <summary>
/// Runs one full voice turn against a single session: take the recorded audio,
/// transcribe it, send it to the session, follow the agent's turn to completion,
/// and speak the reply with the Director's OpenAI TTS voice (fetched from /tts
/// and played on the device, identical to the web). Built on the injected clients and
/// device interfaces so the page that hosts it stays thin and so the round-trip
/// can be exercised with fakes.
///
/// Polling cadence: after the initial send returns "timeout" (the turn is still
/// running), the conversation polls every <see cref="PollSeconds"/> and asks for
/// a spoken progress note roughly every <see cref="ProgressEverySeconds"/> so a
/// driver hears that work is still happening without paying a Haiku call on each
/// cheap poll.
/// </summary>
public sealed class VoiceConversation
{
    private const int PollSeconds = 3;
    private const int ProgressEverySeconds = 120;
    private const int InitialTurnTimeoutMs = 45_000;
    private const int ReadyPollSeconds = 4;

    // FIFO delivery: we deliberately do NOT wait for the agent's turn - the point of FIFO
    // is to deposit the answer and move on. The /chat call sends the text to the PTY before
    // it begins polling, so a short timeout returns right after delivery (status "working"),
    // confirming the send landed without blocking the user behind the agent's whole turn.
    private const int FifoDeliverTimeoutMs = 2_500;

    private readonly DirectorVoiceClient _client;
    private readonly IReplySpeaker _tts;

    public VoiceConversation(DirectorVoiceClient client, IReplySpeaker tts)
    {
        _client = client;
        _tts = tts;
    }

    /// <summary>Status callback so the UI can show what is happening at each step.</summary>
    public sealed record TurnUpdate(string Stage, string Text);

    /// <summary>What a FIFO turn resolved to, so the page knows whether to auto-advance.</summary>
    public enum FifoOutcomeKind
    {
        /// <summary>The answer was delivered to the session; the page should advance to the next.</summary>
        Delivered,
        /// <summary>The user asked the wingman a question; it was answered aloud. Stay on this session.</summary>
        WingmanAnswered,
        /// <summary>The user told the wingman to skip this session; the page should advance.</summary>
        Skip,
        /// <summary>The user told the wingman to hold this session; the page should park it and advance.</summary>
        Hold,
    }

    /// <summary>Result of a FIFO turn: the resolved <see cref="FifoOutcomeKind"/> and the transcript that drove it.</summary>
    public sealed record FifoOutcome(FifoOutcomeKind Kind, string Transcript);

    /// <summary>
    /// Send a recorded utterance to <paramref name="session"/> and speak the reply.
    /// <paramref name="onUpdate"/> fires on the main concerns (transcript, thinking,
    /// progress, reply). Returns the final reply text. Throws on transcription or
    /// send failure so the caller can surface the real error.
    /// </summary>
    public async Task<string> SpeakTurnAsync(
        SessionInfo session, UtteranceAudio audio,
        Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default, bool forceWingman = false)
    {
        ClientLog.Write($"[VoiceConversation] SpeakTurn: session={session.DisplayName}, forceWingman={forceWingman}");
        onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));

        var t = await _client.TranscribeUtteranceAsync(
            session.TailnetEndpoint, session.SessionId, audio.Bytes, audio.Mime, ct);
        if (string.IsNullOrWhiteSpace(t.Text))
            throw new InvalidOperationException("nothing was transcribed from the recording");
        var transcript = t.Text;
        onUpdate?.Invoke(new TurnUpdate("transcript", transcript));

        // Route to the wingman when the user said "Hey wingman ..." (server-decided
        // target) or tapped the explicit Ask-Wingman button (forceWingman). The wingman
        // is read-only and answers immediately from the session - no waiting for the
        // agent's turn, no /chat - and reads content verbatim instead of summarizing.
        if (forceWingman || string.Equals(t.Target, "wingman", StringComparison.OrdinalIgnoreCase))
        {
            ClientLog.Write($"[VoiceConversation] SpeakTurn: routing to wingman for session={session.DisplayName}");
            onUpdate?.Invoke(new TurnUpdate("wingman", "Asking the wingman..."));
            var answer = await _client.AskWingmanAsync(session.TailnetEndpoint, session.SessionId, transcript, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "The wingman had nothing to report.";
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            await SpeakAsync(session.TailnetEndpoint,answer, ct);
            return answer;
        }

        // Only deliver the question to a session that has FINISHED its current
        // turn. If it is still working, the prompt would interleave with the
        // in-progress turn and the reply we read back would be that turn's
        // output, not an answer to the question. So wait for a stopping point
        // first - the same discipline as single-session voice.
        await WaitUntilReadyAsync(session, onUpdate, ct);

        onUpdate?.Invoke(new TurnUpdate("thinking", "Thinking..."));
        var result = await _client.SendChatAsync(
            session.TailnetEndpoint, session.SessionId, transcript, InitialTurnTimeoutMs, ct);

        result = await FollowTurnAsync(session, result, onUpdate, ct);

        if (!string.Equals(result.Status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"agent turn ended with status '{result.Status}': {result.Error}");

        var spoken = result.SpokenText();
        onUpdate?.Invoke(new TurnUpdate("reply", spoken));
        await SpeakAsync(session.TailnetEndpoint,spoken, ct);
        return spoken;
    }

    /// <summary>
    /// One FIFO turn: take the recorded utterance and resolve it WITHOUT waiting for the
    /// agent's reply - the whole point of FIFO is to deposit an answer and move on.
    ///
    ///   - If the user addressed the wingman ("Hey wingman ...", or <paramref name="forceWingman"/>),
    ///     the words are classified as a queue command: "skip" / "hold" return that outcome
    ///     for the page to act on; "answer" routes to the read-only wingman, which answers
    ///     aloud verbatim, and the page stays on this session.
    ///   - Otherwise the transcript is delivered to the session (sent, not followed) and a
    ///     one-line spoken receipt confirms it landed, then the page advances.
    ///
    /// Returns the <see cref="FifoOutcome"/> so the page decides whether to auto-advance.
    /// Throws on transcription failure so the caller can surface the real error.
    /// </summary>
    public async Task<FifoOutcome> DeliverToSessionAsync(
        SessionInfo session, UtteranceAudio audio,
        Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default, bool forceWingman = false)
    {
        ClientLog.Write($"[VoiceConversation] DeliverToSession: session={session.DisplayName}, forceWingman={forceWingman}");
        onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));

        var t = await _client.TranscribeUtteranceAsync(
            session.TailnetEndpoint, session.SessionId, audio.Bytes, audio.Mime, ct);
        if (string.IsNullOrWhiteSpace(t.Text))
            throw new InvalidOperationException("nothing was transcribed from the recording");
        var transcript = t.Text;
        onUpdate?.Invoke(new TurnUpdate("transcript", transcript));

        // Wingman channel: classify the spoken request as a queue command first.
        if (forceWingman || string.Equals(t.Target, "wingman", StringComparison.OrdinalIgnoreCase))
        {
            onUpdate?.Invoke(new TurnUpdate("wingman", "Asking the wingman..."));
            var cmd = await _client.InterpretCommandAsync(session.TailnetEndpoint, session.SessionId, transcript, ct);
            if (string.Equals(cmd.Action, "skip", StringComparison.OrdinalIgnoreCase))
                return new FifoOutcome(FifoOutcomeKind.Skip, transcript);
            if (string.Equals(cmd.Action, "hold", StringComparison.OrdinalIgnoreCase))
                return new FifoOutcome(FifoOutcomeKind.Hold, transcript);

            // Not a queue command - a question. Answer it aloud (read-only, verbatim) and stay.
            var answer = await _client.AskWingmanAsync(session.TailnetEndpoint, session.SessionId, transcript, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "The wingman had nothing to report.";
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            await SpeakAsync(session.TailnetEndpoint,answer, ct);
            return new FifoOutcome(FifoOutcomeKind.WingmanAnswered, transcript);
        }

        // Agent channel: deposit the answer and move on. Send (do NOT follow the turn) so
        // the user is freed immediately; a short spoken receipt confirms it landed.
        onUpdate?.Invoke(new TurnUpdate("delivering", "Sending your answer..."));
        var result = await _client.SendChatAsync(
            session.TailnetEndpoint, session.SessionId, transcript, FifoDeliverTimeoutMs, ct);
        if (result.IsGone)
            throw new InvalidOperationException("that session has exited");
        if (string.Equals(result.Status, "send_failed", StringComparison.OrdinalIgnoreCase)
            || string.Equals(result.Status, "no_session_configured", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"could not deliver your answer (status '{result.Status}'): {result.Error}");

        var receipt = $"Sent to {session.DisplayName}.";
        onUpdate?.Invoke(new TurnUpdate("delivered", receipt));
        await SpeakAsync(session.TailnetEndpoint,receipt, ct);
        return new FifoOutcome(FifoOutcomeKind.Delivered, transcript);
    }

    /// <summary>
    /// Brief the user on a session the FIFO queue just landed on: fetch the wingman's
    /// "what's happening / what it wants" explanation (the SAME mode=explain path the
    /// Voice tab uses) and read it aloud. Returns the spoken briefing text (empty when
    /// the wingman had nothing). Throws on an explain/synthesis failure so the page can
    /// surface it. The page then waits for the user to answer, skip, or hold.
    /// </summary>
    public async Task<string> SpeakExplainAsync(
        SessionInfo session, Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default)
    {
        ClientLog.Write($"[VoiceConversation] SpeakExplain: session={session.DisplayName}");
        onUpdate?.Invoke(new TurnUpdate("explaining", "Reading what's happening..."));
        var briefing = await _client.ExplainAsync(session.TailnetEndpoint, session.SessionId, ct);
        if (string.IsNullOrWhiteSpace(briefing))
            briefing = "Nothing to report on this one yet.";
        onUpdate?.Invoke(new TurnUpdate("briefing", briefing));
        await SpeakAsync(session.TailnetEndpoint, briefing, ct);
        return briefing;
    }

    /// <summary>
    /// Speak the conductor's intro for a session that needs the user: its name,
    /// then the recap, then the answer/question. Each part is spoken in turn so
    /// the user hears context before the ask. Returns the spoken answer text.
    /// </summary>
    public async Task<string> SpeakConductorItemAsync(
        SessionInfo session, Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default)
    {
        ClientLog.Write($"[VoiceConversation] SpeakConductorItem: session={session.DisplayName}");

        // 1. Name.
        onUpdate?.Invoke(new TurnUpdate("name", session.DisplayName));
        await SpeakAsync(session.TailnetEndpoint,session.DisplayName, ct);

        // 2. Recap (best-effort context; skip cleanly if none is available).
        var recap = await _client.GetOrCreateRecapAsync(session.TailnetEndpoint, session.SessionId, ct);
        if (!string.IsNullOrWhiteSpace(recap))
        {
            onUpdate?.Invoke(new TurnUpdate("recap", recap));
            await SpeakAsync(session.TailnetEndpoint,recap, ct);
        }

        // 3. The answer / question, read from the session's latest reply.
        var poll = await _client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false, ct);
        var answer = poll.SpokenText();
        if (!string.IsNullOrWhiteSpace(answer))
        {
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            await SpeakAsync(session.TailnetEndpoint,answer, ct);
        }
        return answer;
    }

    /// <summary>
    /// Block until the session has finished its current turn (is at a stopping
    /// point: Idle / WaitingForInput / WaitingForPerm, which the server reports as
    /// poll status "ok"). If it is still working, announce it once and poll until
    /// it finishes. Throws if the session has exited. Honors cancellation so the
    /// user can leave instead of waiting on a long turn.
    /// </summary>
    private async Task WaitUntilReadyAsync(SessionInfo session, Action<TurnUpdate>? onUpdate, CancellationToken ct)
    {
        var poll = await _client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false, ct);
        if (poll.IsGone)
            throw new InvalidOperationException("that session has exited");
        if (!poll.IsWorking)
            return; // already finished its turn - safe to ask now

        ClientLog.Write($"[VoiceConversation] WaitUntilReady: session={session.DisplayName} is working; holding the question");
        onUpdate?.Invoke(new TurnUpdate("waiting", "That session is still working. I will ask when it finishes."));
        await SpeakAsync(session.TailnetEndpoint,"That session is still working. I'll ask when it finishes.", ct);

        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(ReadyPollSeconds), ct);
            poll = await _client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false, ct);
            if (poll.IsGone)
                throw new InvalidOperationException("that session has exited");
            if (!poll.IsWorking)
            {
                ClientLog.Write($"[VoiceConversation] WaitUntilReady: session={session.DisplayName} now ready");
                return;
            }
        }
        ct.ThrowIfCancellationRequested();
    }

    private async Task<ChatTurnResult> FollowTurnAsync(
        SessionInfo session, ChatTurnResult result, Action<TurnUpdate>? onUpdate, CancellationToken ct)
    {
        var elapsed = 0;
        var sinceProgress = 0;
        while (result.ShouldKeepPolling && !ct.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(PollSeconds), ct);
            elapsed += PollSeconds;
            sinceProgress += PollSeconds;

            var wantProgress = sinceProgress >= ProgressEverySeconds;
            if (wantProgress) sinceProgress = 0;

            result = await _client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress, ct);

            if (wantProgress && !string.IsNullOrWhiteSpace(result.ProgressNote))
            {
                onUpdate?.Invoke(new TurnUpdate("progress", result.ProgressNote));
                await SpeakAsync(session.TailnetEndpoint,result.ProgressNote, ct);
            }
        }
        ClientLog.Write($"[VoiceConversation] FollowTurn done: status={result.Status}, elapsed~{elapsed}s");
        return result;
    }

    /// <summary>
    /// Speak a one-off line (e.g. "All caught up") with the Director's voice, using any
    /// reachable Director base URL. Public so a page can give the user an audible cue
    /// outside a session turn. No-op for empty text; throws on a synthesis failure.
    /// </summary>
    public Task SpeakLineAsync(string directorBase, string text, CancellationToken ct = default)
        => SpeakAsync(directorBase, text, ct);

    /// <summary>
    /// Speak <paramref name="text"/> with the Director's OpenAI voice: fetch the
    /// audio from /tts (the same endpoint and voice the web voice page uses) and
    /// play it on the device, completing when playback finishes. No-op for empty
    /// text. Throws on a synthesis failure so the caller surfaces it rather than
    /// going silently quiet - there is no on-device fallback voice by design.
    /// </summary>
    private async Task SpeakAsync(string directorBase, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var audio = await _client.SynthesizeSpeechAsync(directorBase, text, ct);
        await _tts.PlayAsync(audio, ct);
    }
}
