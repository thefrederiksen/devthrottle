namespace CcDirectorClient.Voice;

/// <summary>
/// Runs one full voice turn against a single session: take the recorded audio,
/// transcribe it, send it to the session, follow the agent's turn to completion,
/// and speak the reply with native TTS. Built on the injected clients and
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

    private readonly DirectorVoiceClient _client;
    private readonly IReplySpeaker _tts;

    public VoiceConversation(DirectorVoiceClient client, IReplySpeaker tts)
    {
        _client = client;
        _tts = tts;
    }

    /// <summary>Status callback so the UI can show what is happening at each step.</summary>
    public sealed record TurnUpdate(string Stage, string Text);

    /// <summary>
    /// Send a recorded utterance to <paramref name="session"/> and speak the reply.
    /// <paramref name="onUpdate"/> fires on the main concerns (transcript, thinking,
    /// progress, reply). Returns the final reply text. Throws on transcription or
    /// send failure so the caller can surface the real error.
    /// </summary>
    public async Task<string> SpeakTurnAsync(
        SessionInfo session, UtteranceAudio audio,
        Action<TurnUpdate>? onUpdate = null, CancellationToken ct = default)
    {
        ClientLog.Write($"[VoiceConversation] SpeakTurn: session={session.DisplayName}");
        onUpdate?.Invoke(new TurnUpdate("transcribing", "Transcribing..."));

        var transcript = await _client.TranscribeUtteranceAsync(
            session.TailnetEndpoint, session.SessionId, audio.Bytes, audio.Mime, ct);
        if (string.IsNullOrWhiteSpace(transcript))
            throw new InvalidOperationException("nothing was transcribed from the recording");
        onUpdate?.Invoke(new TurnUpdate("transcript", transcript));

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
        await _tts.SpeakAsync(spoken, ct);
        return spoken;
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
        await _tts.SpeakAsync(session.DisplayName, ct);

        // 2. Recap (best-effort context; skip cleanly if none is available).
        var recap = await _client.GetOrCreateRecapAsync(session.TailnetEndpoint, session.SessionId, ct);
        if (!string.IsNullOrWhiteSpace(recap))
        {
            onUpdate?.Invoke(new TurnUpdate("recap", recap));
            await _tts.SpeakAsync(recap, ct);
        }

        // 3. The answer / question, read from the session's latest reply.
        var poll = await _client.PollChatAsync(session.TailnetEndpoint, session.SessionId, wantProgress: false, ct);
        var answer = poll.SpokenText();
        if (!string.IsNullOrWhiteSpace(answer))
        {
            onUpdate?.Invoke(new TurnUpdate("answer", answer));
            await _tts.SpeakAsync(answer, ct);
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
        await _tts.SpeakAsync("That session is still working. I'll ask when it finishes.", ct);

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
                await _tts.SpeakAsync(result.ProgressNote, ct);
            }
        }
        ClientLog.Write($"[VoiceConversation] FollowTurn done: status={result.Status}, elapsed~{elapsed}s");
        return result;
    }
}
