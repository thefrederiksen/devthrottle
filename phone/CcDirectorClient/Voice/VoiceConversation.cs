namespace CcDirectorClient.Voice;

/// <summary>
/// Runs one full voice turn against a single session. The agent turn goes through
/// the GATEWAY's async voice-turn pipeline (issue #378): the audio is submitted once
/// to the Gateway, which drives the owning Director server-side (transcribe, wait for
/// the session, run the turn, summarize, TTS) and caches the result, and the phone
/// polls every <see cref="VoiceTurnPollMs"/> ms until the reply (with its synthesized
/// audio) is ready. Built on the injected clients and device interfaces so the page
/// that hosts it stays thin and so the round-trip can be exercised with fakes.
/// </summary>
public sealed class VoiceConversation
{
    // FIFO delivery: we deliberately do NOT wait for the agent's turn - the point of FIFO
    // is to deposit the answer and move on. The /chat call sends the text to the PTY before
    // it begins polling, so a short timeout returns right after delivery (status "working"),
    // confirming the send landed without blocking the user behind the agent's whole turn.
    private const int FifoDeliverTimeoutMs = 2_500;

    private readonly DirectorVoiceClient _client;
    private readonly IReplySpeaker _tts;
    private readonly string _gatewayBaseUrl;
    private readonly VoiceTurnRetryPolicy _retryPolicy;

    /// <summary>
    /// <paramref name="gatewayBaseUrl"/> is the Gateway's base URL (the same address
    /// the roster comes from) - required by <see cref="SpeakTurnAsync"/>'s agent path,
    /// which submits/polls the voice turn on the Gateway (issue #378). Hosts that only
    /// use the Director-direct members (FIFO deliver, briefing, one-off lines) may omit it.
    /// <paramref name="retryPolicy"/> tunes the agent path's submit-retry + poll-backoff
    /// loop (issue #405); production omits it to take the resilient defaults (~1.5s cadence,
    /// backoff to ~5s, ~10-minute deadline aligned with the Gateway job TTL).
    /// </summary>
    public VoiceConversation(
        DirectorVoiceClient client, IReplySpeaker tts, string gatewayBaseUrl = "",
        VoiceTurnRetryPolicy? retryPolicy = null)
    {
        _client = client;
        _tts = tts;
        _gatewayBaseUrl = (gatewayBaseUrl ?? "").TrimEnd('/');
        _retryPolicy = retryPolicy ?? VoiceTurnRetryPolicy.Default;
    }

    /// <summary>Status callback so the UI can show what is happening at each step.</summary>
    public sealed record TurnUpdate(string Stage, string Text);

    /// <summary>What a FIFO turn resolved to, so the page knows whether to auto-advance.
    /// Skip and Hold are NOT here: they are explicit button actions on the queue UI, not
    /// inferences from speech. The dialogs hand back audio; the buttons hand back actions.</summary>
    public enum FifoOutcomeKind
    {
        /// <summary>The answer was delivered to the session; the page should advance to the next.</summary>
        Delivered,
        /// <summary>The user asked the wingman a question; it was answered aloud. Stay on this session.</summary>
        WingmanAnswered,
    }

    /// <summary>Result of a FIFO turn: the resolved <see cref="FifoOutcomeKind"/> and the transcript that drove it.</summary>
    public sealed record FifoOutcome(FifoOutcomeKind Kind, string Transcript);

    /// <summary>
    /// Send a recorded utterance to <paramref name="session"/> and speak the reply.
    /// The agent path runs on the GATEWAY's async voice-turn pipeline (issue #378):
    /// submit the audio once to the Gateway, which transcribes, waits for the session,
    /// runs the agent turn, summarizes, and synthesizes the reply audio entirely
    /// server-side; the phone polls ~every 1.5s and plays the returned audio directly,
    /// so a brief signal drop just means the next poll arrives a little late.
    /// The wingman path stays Director-direct (read-only, answers immediately).
    /// <paramref name="onUpdate"/> fires as processing advances (transcribing,
    /// transcript, waiting, thinking, summarizing, reply, speaking). Returns the final
    /// spoken summary text. Throws on submit/poll failure or a terminal error stage so
    /// the caller can surface the real error.
    /// <paramref name="onTurnSubmitted"/> fires with the turn id the instant submit returns,
    /// so the caller can persist the in-flight turn and resume polling it after an app
    /// restart/background/crash (issue #406). <paramref name="onTurnTerminal"/> fires once the
    /// turn reaches ANY terminal outcome (reply, error, expired, or session gone) so the caller
    /// can clear that persisted turn - it must no longer be resumed.
    /// </summary>
    public async Task<string> SpeakTurnAsync(
        SessionInfo session, UtteranceAudio audio,
        Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default, bool forceWingman = false,
        Action<string>? onTurnSubmitted = null, Action? onTurnTerminal = null)
    {
        ClientLog.Write($"[VoiceConversation] SpeakTurn: session={session.DisplayName}, forceWingman={forceWingman}");

        // Route to the wingman when the user tapped Ask Wingman. The wingman is
        // read-only and answers immediately from the session - no agent turn - so it
        // keeps the Director-direct transcribe-then-ask path.
        if (forceWingman)
        {
            onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));
            var t = await _client.TranscribeUtteranceAsync(
                session.TailnetEndpoint, session.SessionId, audio.Bytes, audio.Mime, ct);
            if (string.IsNullOrWhiteSpace(t.Text))
                throw new InvalidOperationException("nothing was transcribed from the recording");
            onUpdate?.Invoke(new TurnUpdate("transcript", t.Text));

            ClientLog.Write($"[VoiceConversation] SpeakTurn: routing to wingman for session={session.DisplayName}");
            onUpdate?.Invoke(new TurnUpdate("wingman", "Asking the wingman..."));
            var answer = await _client.AskWingmanAsync(session.TailnetEndpoint, session.SessionId, t.Text, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "The wingman had nothing to report.";
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            await SpeakAsync(session.TailnetEndpoint, answer, ct);
            return answer;
        }

        if (string.IsNullOrWhiteSpace(_gatewayBaseUrl))
            throw new InvalidOperationException(
                "gateway URL is not configured - set the Gateway address in settings to run a voice turn");

        // Submit once, with bounded retry: the Gateway queues the turn and answers 202 with a
        // turn id, so the phone is free while the Director does the work. A drop mid-upload is
        // retried with backoff (issue #405) rather than losing the recorded clip on the first
        // failure.
        onUpdate?.Invoke(new TurnUpdate("transcribing", "Sending to the gateway..."));
        var runner = new VoiceTurnRunner(_client, _retryPolicy);
        var submit = await runner.SubmitAsync(
            _gatewayBaseUrl, session.SessionId, audio.Bytes, audio.Mime, ct);
        ClientLog.Write($"[VoiceConversation] SpeakTurn: submitted turnId={submit.TurnId}");

        // Persist the in-flight turn the instant submit returns (issue #406): if the app is
        // killed/backgrounded/crashes during the poll below, the caller can reload this turn id
        // and resume polling the reply the Gateway already cached, instead of abandoning it.
        onTurnSubmitted?.Invoke(submit.TurnId);

        return await PollAndSpeakAsync(runner, session.SessionId, submit.TurnId, onUpdate, onTurnTerminal, ct);
    }

    /// <summary>
    /// Resume polling a turn that was submitted on a PREVIOUS app run and persisted (issue #406):
    /// the Gateway still has the cached result (within its ~10-minute job TTL), so this reuses the
    /// SAME hardened poll loop as a fresh turn (<see cref="VoiceTurnRunner.PollToCompletionAsync"/>)
    /// to deliver the reply - it does not reinvent the loop. <paramref name="onTurnTerminal"/> fires
    /// on any terminal outcome (reply, error, expired, gone) so the caller clears the persisted turn.
    /// Throws the same terminal exceptions as a fresh turn (e.g. <see cref="VoiceTurnExpiredException"/>
    /// when the cached turn has since expired) so the caller can surface a clear message.
    /// </summary>
    public Task<string> ResumeTurnAsync(
        string sessionId, string turnId,
        Action<TurnUpdate>? onUpdate = null, Action? onTurnTerminal = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            throw new ArgumentException("session id is required", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(turnId))
            throw new ArgumentException("turn id is required", nameof(turnId));
        if (string.IsNullOrWhiteSpace(_gatewayBaseUrl))
            throw new InvalidOperationException(
                "gateway URL is not configured - set the Gateway address in settings to resume a voice turn");

        ClientLog.Write($"[VoiceConversation] ResumeTurn: sid={sessionId}, turnId={turnId}");
        var runner = new VoiceTurnRunner(_client, _retryPolicy);
        return PollAndSpeakAsync(runner, sessionId, turnId, onUpdate, onTurnTerminal, ct);
    }

    /// <summary>
    /// Poll the Gateway's job cache until the turn lands in a terminal stage, surface progress
    /// via <paramref name="onUpdate"/>, then speak the reply audio. Shared by the fresh-turn and
    /// resume paths so both run the identical hardened loop (issue #405) and identical playback.
    /// <paramref name="onTurnTerminal"/> fires on every terminal outcome - the normal reply AND
    /// the terminal failures the runner throws (error stage, 404 expired, 410 gone, deadline) -
    /// so the caller always clears the persisted in-flight turn (issue #406).
    /// </summary>
    private async Task<string> PollAndSpeakAsync(
        VoiceTurnRunner runner, string sessionId, string turnId,
        Action<TurnUpdate>? onUpdate, Action? onTurnTerminal, CancellationToken ct)
    {
        // Poll the Gateway's job cache until the turn lands in a terminal stage. The runner
        // tolerates transient drops (network error, 5xx, timeout) by re-polling with backoff
        // and gives up only at the server-TTL deadline or on a terminal condition (issue #405).
        // Stage vocabulary: submitted, transcribing, transcript, waiting, thinking,
        // summarizing, reply (terminal), error (terminal).
        VoiceTurnPollResult poll;
        try
        {
            poll = await runner.PollToCompletionAsync(
                _gatewayBaseUrl, sessionId, turnId,
                onStage: p =>
                {
                    switch (p.Stage)
                    {
                        case "transcribing":
                            onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));
                            break;
                        case "transcript":
                            if (!string.IsNullOrWhiteSpace(p.Transcript))
                                onUpdate?.Invoke(new TurnUpdate("transcript", p.Transcript));
                            break;
                        case "waiting":
                            onUpdate?.Invoke(new TurnUpdate("waiting", "Waiting for the session to be ready..."));
                            break;
                        case "thinking":
                            onUpdate?.Invoke(new TurnUpdate("thinking", "Thinking..."));
                            break;
                        case "summarizing":
                            onUpdate?.Invoke(new TurnUpdate("summarizing", "Summarizing..."));
                            break;
                    }
                },
                ct: ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A terminal failure (error stage, expired 404, session gone 410, deadline) ends this
            // turn for good - clear the persisted in-flight turn so it is never resumed again.
            // A cancellation is NOT terminal (the user navigated away mid-turn): leave the persisted
            // turn so the next launch can resume it within the TTL.
            onTurnTerminal?.Invoke();
            throw;
        }

        // Reply is the normal terminal stage: clear the persisted in-flight turn.
        onTurnTerminal?.Invoke();

        var summary = poll.Summary ?? "";
        onUpdate?.Invoke(new TurnUpdate("reply", summary));

        // "speaking" fires before playback begins so the status label reads
        // "Speaking..." only while audio is actually playing.
        onUpdate?.Invoke(new TurnUpdate("speaking", "Speaking..."));

        // Issue #407: the reply audio is fetched from a DEDICATED, resumable endpoint, not from
        // the (now slim) poll. The slim poll advertises audioReady/audioLength; the runner
        // downloads the audio with the same resilience as the poll loop - a mid-download drop
        // resumes via HTTP Range instead of restarting. The audio is absent (audioReady=false)
        // when the Director has no TTS key, in which case the on-screen summary is the whole reply.
        //
        // Back-compat: an older Gateway that still inlines the bytes sets poll.AudioBase64; play
        // those directly rather than making a second round-trip to an endpoint it does not have.
        byte[] mp3 = Array.Empty<byte>();
        if (!string.IsNullOrEmpty(poll.AudioBase64))
        {
            mp3 = Convert.FromBase64String(poll.AudioBase64);
        }
        else if (poll.AudioReady)
        {
            mp3 = await runner.FetchAudioToCompletionAsync(_gatewayBaseUrl, sessionId, turnId, ct);
        }

        // Play the reply and capture the measurable outcome (issue #394) so the done line
        // records audio bytes AND the duration actually played versus the estimate - a
        // played time far short of the estimate is the cutout this issue makes visible.
        PlaybackOutcome playback = PlaybackOutcome.None;
        if (mp3.Length > 0)
            playback = await _tts.PlayAsync(mp3, ct);

        ClientLog.Write(
            $"[VoiceConversation] PollAndSpeak done: turnId={turnId}, summaryChars={summary.Length}, "
            + $"audioBytes={mp3.Length}, playback={playback.Result}, "
            + $"playedSeconds={playback.PlayedDuration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}, "
            + $"estimatedSeconds={playback.EstimatedDuration.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)}");
        return summary;
    }

    /// <summary>
    /// One FIFO turn: take the recorded utterance and resolve it WITHOUT waiting for the
    /// agent's reply - the whole point of FIFO is to deposit an answer and move on.
    ///
    ///   - <paramref name="forceWingman"/>=true (the user tapped Ask Wingman) routes the
    ///     transcript to the read-only wingman, which answers aloud verbatim, and the page
    ///     stays on this session.
    ///   - <paramref name="forceWingman"/>=false (Ask Agent) delivers the transcript to the
    ///     session (sent, not followed) and a one-line spoken receipt confirms it landed.
    ///     The page then advances.
    ///
    /// Returns the <see cref="FifoOutcome"/> so the page decides whether to auto-advance.
    /// Throws on transcription failure so the caller can surface the real error.
    /// </summary>
    public async Task<FifoOutcome> DeliverToSessionAsync(
        SessionInfo session, UtteranceAudio audio,
        Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default, bool forceWingman = false,
        Action<byte[]>? onClip = null)
    {
        ClientLog.Write($"[VoiceConversation] DeliverToSession: session={session.DisplayName}, forceWingman={forceWingman}");
        onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));

        var t = await _client.TranscribeUtteranceAsync(
            session.TailnetEndpoint, session.SessionId, audio.Bytes, audio.Mime, ct);
        if (string.IsNullOrWhiteSpace(t.Text))
            throw new InvalidOperationException("nothing was transcribed from the recording");
        var transcript = t.Text;
        onUpdate?.Invoke(new TurnUpdate("transcript", transcript));

        // Wingman channel: the user explicitly tapped Ask Wingman, so this utterance
        // is a question. Answer it aloud (read-only, verbatim) and stay on the session.
        // No queue-command classifier and no wake-phrase routing - skip and hold are
        // the queue's own buttons; the dictation only carries text; the BUTTON the user
        // pressed is the sole source of truth for routing.
        if (forceWingman)
        {
            onUpdate?.Invoke(new TurnUpdate("wingman", "Asking the wingman..."));
            var answer = await _client.AskWingmanAsync(session.TailnetEndpoint, session.SessionId, transcript, ct);
            if (string.IsNullOrWhiteSpace(answer))
                answer = "The wingman had nothing to report.";
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            // Cache this reply (issue #148): it becomes the clip the Replay button re-plays,
            // replacing the session's briefing.
            await SpeakAndCacheAsync(session.TailnetEndpoint, answer, onClip, ct);
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

        // The point of FIFO Answer is to deposit the text and immediately move on. We do
        // NOT speak a receipt here: awaiting it would delay the advance, and it would then
        // talk over the next session's briefing. The UI still shows "Sent to X"; the audible
        // cue that we moved on is the next session's briefing (or the "all caught up" idle line).
        onUpdate?.Invoke(new TurnUpdate("delivered", $"Sent to {session.DisplayName}."));
        return new FifoOutcome(FifoOutcomeKind.Delivered, transcript);
    }

    /// <summary>A briefing prepared but not yet spoken: the on-screen text and the ready-to-play audio.</summary>
    public sealed record PreparedBriefing(string DisplayText, byte[] Audio);

    /// <summary>
    /// Fetch the wingman's "what's happening" briefing AND synthesize its audio WITHOUT
    /// playing it. Lets the FIFO page generate the spoken briefing BEFORE it navigates to the
    /// session, so the user lands on a page whose audio is already in hand instead of waiting
    /// for it (issue #148). Returns the on-screen briefing text plus the MP3 bytes for the
    /// page to cache and play. Throws on an explain/synthesis failure so the page can surface it.
    /// </summary>
    public async Task<PreparedBriefing> PrepareExplainAsync(
        SessionInfo session, CancellationToken ct = default)
    {
        ClientLog.Write($"[VoiceConversation] PrepareExplain: session={session.DisplayName}");
        var structured = await _client.ExplainStructuredAsync(session.TailnetEndpoint, session.SessionId, ct);

        // What goes on the screen vs. what gets spoken are deliberately different shapes now:
        // the screen text may include a markdown table and file paths; the spoken-version field
        // is smooth prose tuned for the ear with no markdown. When the model omits `say`
        // (older Directors, partial JSON) we fall back to TTSing the on-screen text - the live
        // /tts engine is forgiving and the user still hears something useful.
        var onScreen = string.IsNullOrWhiteSpace(structured.OnScreenText)
            ? "Nothing to report on this one yet."
            : structured.OnScreenText;
        var spoken = string.IsNullOrWhiteSpace(structured.SpokenText)
            ? onScreen
            : structured.SpokenText;

        // Lead the spoken clip with which session this is - the name AND the repo - so the user
        // knows where they are before hearing what happened. The on-screen text stays just the
        // briefing (the name and repo are already shown in the session card above it).
        var spokenWithIntro = BuildSpokenIntro(session) + " " + spoken;
        var bytes = await _client.SynthesizeSpeechAsync(session.TailnetEndpoint, spokenWithIntro, ct);
        ClientLog.Write($"[VoiceConversation] PrepareExplain OK: screenChars={onScreen.Length}, sayChars={spoken.Length}, audioBytes={bytes.Length}");
        return new PreparedBriefing(onScreen, bytes);
    }

    /// <summary>
    /// Synthesize <paramref name="text"/>, hand the raw bytes to <paramref name="onClip"/> (so the
    /// page can cache them for Replay, issue #148), then play it. No-op for empty text.
    /// </summary>
    private async Task SpeakAndCacheAsync(string directorBase, string text, Action<byte[]>? onClip, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var audio = await _client.SynthesizeSpeechAsync(directorBase, text, ct);
        onClip?.Invoke(audio);
        await _tts.PlayAsync(audio, ct);
    }

    /// <summary>
    /// A one-line spoken intro that names the session and the repo it lives in, e.g.
    /// "auth-refactor, in the cc-director repo." When the session has no custom name (so its
    /// display name IS the repo folder), the repo is not repeated.
    /// </summary>
    private static string BuildSpokenIntro(SessionInfo session)
    {
        var name = session.DisplayName;
        var repo = session.RepoName;
        if (!string.IsNullOrWhiteSpace(repo) && !string.Equals(repo, name, StringComparison.OrdinalIgnoreCase))
            return $"{name}, in the {repo} repo.";
        return $"{name}.";
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
