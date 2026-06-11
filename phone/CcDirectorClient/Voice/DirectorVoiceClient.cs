using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// Result of transcribing one utterance: the cleaned <paramref name="Text"/> the
/// caller dispatches to whichever channel the user's button choice picked
/// (agent via /chat, or wingman via /wingman/ask). The dictate pipeline no
/// longer carries a routing field - buttons are the source of truth.
/// </summary>
public sealed record TranscribeResult(string Text);

/// <summary>
/// One incremental slice of the raw terminal buffer: the new <paramref name="Text"/>
/// since the previous cursor and the <paramref name="NewCursor"/> to pass on the next
/// poll of the Terminal mirror.
/// </summary>
public sealed record BufferSlice(string Text, long NewCursor);

/// <summary>
/// Drives the per-session voice round-trip directly against the owning Director's
/// Control API (the same endpoints the web voice page uses), using the Director's
/// Tailnet base URL taken from the roster. Three concerns, kept apart:
///
///   1. <see cref="TranscribeUtteranceAsync"/> - upload the recorded audio and
///      get the transcript back (POST /voice/utterance -> PUT chunk -> POST complete).
///   2. <see cref="SendChatAsync"/> / <see cref="PollChatAsync"/> - send the
///      transcript to the session and follow the turn to completion (POST /chat).
///   3. <see cref="GetOrCreateRecapAsync"/> - the conductor's spoken recap.
///
/// The reply is read aloud with the Director's OpenAI voice: the caller fetches
/// the audio from <see cref="SynthesizeSpeechAsync"/> (POST /tts, the same
/// endpoint and voice the web voice page uses) and plays the returned MP3, so
/// the phone sounds identical to the web instead of falling back to a robotic
/// on-device engine.
/// </summary>
public sealed class DirectorVoiceClient
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _token;

    public DirectorVoiceClient(string token = "")
    {
        _token = token ?? "";
    }

    private HttpClient NewClient()
    {
        // The whole voice round-trip (upload + transcribe + the agent turn) can be
        // slow on car LTE; the turn-following uses short per-call timeouts via
        // polling, but a single transcription can still take a while.
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return http;
    }

    // ===== 1. UPLOAD + TRANSCRIBE ==========================================

    /// <summary>
    /// Upload the recorded utterance as a single chunk and return the cleaned
    /// transcript. Throws on any HTTP or transcription failure so the caller can
    /// tell the user plainly rather than silently sending an empty prompt.
    /// Routing (agent vs wingman) is the caller's button choice; the server no
    /// longer infers it.
    /// </summary>
    public async Task<TranscribeResult> TranscribeUtteranceAsync(
        string directorBase, string sessionId, byte[] audio, string mime, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] TranscribeUtterance: base={b}, sid={sessionId}, bytes={audio.Length}, mime={mime}");
        using var http = NewClient();

        // Register with a client-minted GUID (server requires a GUID-shaped id).
        var utteranceId = Guid.NewGuid().ToString("N");
        var regResp = await http.PostAsync($"{b}/voice/utterance",
            JsonBody(new { UtteranceId = utteranceId }), ct);
        regResp.EnsureSuccessStatusCode();
        var regJson = await regResp.Content.ReadAsStringAsync(ct);
        var reg = JsonSerializer.Deserialize<RegisterResp>(regJson, Json);
        if (reg is null || string.IsNullOrWhiteSpace(reg.UtteranceId))
            throw new InvalidOperationException("voice/utterance register returned no id");
        var id = reg.UtteranceId!;

        // Upload the whole clip as chunk 0 (push-to-talk is one short utterance).
        using var content = new ByteArrayContent(audio);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var put = new HttpRequestMessage(HttpMethod.Put, $"{b}/voice/utterance/{id}/chunk/0")
        {
            Content = content,
        };
        put.Headers.TryAddWithoutValidation("X-Chunk-Sha256", Sha256Hex(audio));
        var putResp = await http.SendAsync(put, ct);
        putResp.EnsureSuccessStatusCode();

        // Complete -> server reassembles and transcribes the one chunk.
        var compResp = await http.PostAsync($"{b}/voice/utterance/{id}/complete",
            JsonBody(new { TotalChunks = 1, Mime = mime, SessionId = sessionId }), ct);
        var compBody = await compResp.Content.ReadAsStringAsync(ct);
        if (!compResp.IsSuccessStatusCode)
            throw new HttpRequestException($"voice/utterance complete failed: {(int)compResp.StatusCode} {compBody}");

        var comp = JsonSerializer.Deserialize<CompleteResp>(compBody, Json)
                   ?? throw new InvalidOperationException("voice/utterance complete returned no body");
        if (!string.Equals(comp.Status, "ok", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"transcription status={comp.Status}: {comp.Error}");

        // The cleaned transcript is what the web voice UI forwards to /chat; fall
        // back to the raw transcript only when the cleanup step produced nothing.
        var text = !string.IsNullOrWhiteSpace(comp.CleanedTranscript) ? comp.CleanedTranscript! : comp.Transcript;
        ClientLog.Write($"[DirectorVoiceClient] TranscribeUtterance OK: chars={text?.Length ?? 0}");
        return new TranscribeResult((text ?? "").Trim());
    }

    /// <summary>
    /// Ask the wingman a free-text question about a session and return the spoken
    /// answer (POST /sessions/{sid}/wingman/ask with the question and no mode, which the
    /// Director routes to the read-only full-power answer path - it reads content
    /// verbatim instead of summarizing). Throws on HTTP failure so the caller surfaces
    /// the real error. Returns empty string when the wingman had nothing to say.
    /// </summary>
    public async Task<string> AskWingmanAsync(
        string directorBase, string sessionId, string question, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] AskWingman: base={b}, sid={sessionId}, chars={question.Length}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/wingman/ask",
            JsonBody(new { Question = question }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"wingman/ask failed: {(int)resp.StatusCode} {body}");
        var r = JsonSerializer.Deserialize<ExplainResp>(body, Json);
        if (r is not null && !string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Error))
            throw new InvalidOperationException($"wingman/ask status={r.Status}: {r.Error}");
        var text = (r?.Answer ?? "").Trim();
        ClientLog.Write($"[DirectorVoiceClient] AskWingman OK: chars={text.Length}");
        return text;
    }

    // ===== TEXT-TO-SPEECH (OpenAI voice, shared with the web) ==============

    /// <summary>
    /// Turn reply text into spoken audio using the Director's OpenAI TTS voice
    /// (POST /tts). Sends only the text - no voice override - so the phone speaks
    /// with the SAME server-configured voice as the web voice page, not a robotic
    /// on-device engine. Returns the raw MP3 bytes for the caller to play. Throws
    /// on any HTTP/synthesis failure so the caller surfaces the real reason rather
    /// than silently going quiet.
    /// </summary>
    public async Task<byte[]> SynthesizeSpeechAsync(
        string directorBase, string text, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SynthesizeSpeech: base={b}, chars={text.Length}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/tts", JsonBody(new { Text = text }), ct);
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"tts failed: {(int)resp.StatusCode} {body}");
        }
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        if (bytes.Length == 0)
            throw new InvalidOperationException("tts returned empty audio");
        ClientLog.Write($"[DirectorVoiceClient] SynthesizeSpeech OK: bytes={bytes.Length}");
        return bytes;
    }

    // ===== 2. CHAT (send + follow) =========================================

    /// <summary>Send a transcript to the session and wait up to <paramref name="timeoutMs"/> for the turn.</summary>
    public async Task<ChatTurnResult> SendChatAsync(
        string directorBase, string sessionId, string text, int timeoutMs = 60_000, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SendChat: base={b}, sid={sessionId}, chars={text.Length}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/chat",
            JsonBody(new { Text = text, SessionId = sessionId, Voice = true, TimeoutMs = timeoutMs }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ParseChat(body, resp);
    }

    /// <summary>
    /// Poll the session's current state and latest reply without sending anything.
    /// <paramref name="wantProgress"/> asks the server for a spoken progress note
    /// (a Haiku call); the caller sets it only occasionally.
    /// </summary>
    public async Task<ChatTurnResult> PollChatAsync(
        string directorBase, string sessionId, bool wantProgress, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/chat",
            JsonBody(new { PollOnly = true, SessionId = sessionId, Voice = true, WantProgress = wantProgress }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return ParseChat(body, resp);
    }

    private static ChatTurnResult ParseChat(string body, HttpResponseMessage resp)
    {
        var c = JsonSerializer.Deserialize<ChatResp>(body, Json);
        if (c is null)
            throw new InvalidOperationException($"/chat returned unparseable body ({(int)resp.StatusCode})");
        return new ChatTurnResult
        {
            Status = c.Status ?? "",
            Summary = c.Summary ?? "",
            DisplayText = c.DisplayText ?? "",
            ProgressNote = c.ProgressNote ?? "",
            ActivityState = c.ActivityState ?? "",
            SessionName = c.SessionName ?? "",
            Error = c.Error,
        };
    }

    // ===== "WHAT'S HAPPENING" (on-demand briefing) =========================

    /// <summary>
    /// Ask the wingman for a fresh plain-language briefing of what the session is
    /// doing right now (POST /sessions/{sid}/wingman/ask with mode=explain - the
    /// SAME endpoint and prompt the web voice view's "What's happening?" button
    /// uses, so the two clients share one backend implementation, not two). Returns
    /// the answer text for the caller to read aloud; empty string when the wingman
    /// had nothing to report.
    /// </summary>
    public async Task<string> ExplainAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var briefing = await ExplainStructuredAsync(directorBase, sessionId, ct);
        return briefing.OnScreenText;
    }

    /// <summary>
    /// Structured briefing returned by /wingman/ask?mode=explain. The screen layer reads
    /// <see cref="OnScreenText"/>; voice mode reads <see cref="SpokenText"/>. Fields fall
    /// back gracefully when the model omitted them: empty <see cref="SpokenText"/> means
    /// the caller should TTS <see cref="OnScreenText"/> instead.
    /// </summary>
    public sealed record StructuredBriefing(
        string OnScreenText,
        string Headline,
        string WhatHappened,
        string WhatClaudeWants,
        string SpokenText);

    /// <summary>
    /// Structured-explain variant of <see cref="ExplainAsync"/>. Returns the full briefing
    /// payload so voice mode can TTS the spoken-version field instead of the on-screen
    /// (possibly markdown-laden) text. Same endpoint, same prompt, just keeping the extra
    /// fields the server now returns.
    /// </summary>
    public async Task<StructuredBriefing> ExplainStructuredAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] ExplainStructured: base={b}, sid={sessionId}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/wingman/ask",
            JsonBody(new { Question = "", Mode = "explain" }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"wingman/ask explain failed: {(int)resp.StatusCode} {body}");
        var r = JsonSerializer.Deserialize<ExplainResp>(body, Json);
        var onScreen = (r?.Answer ?? "").Trim();
        var headline = (r?.Headline ?? "").Trim();
        var whatHappened = (r?.WhatHappened ?? "").Trim();
        var whatClaudeWants = (r?.WhatClaudeWants ?? "").Trim();
        var spoken = (r?.Say ?? "").Trim();
        ClientLog.Write($"[DirectorVoiceClient] ExplainStructured OK: screenChars={onScreen.Length}, sayChars={spoken.Length}, headline=\"{headline}\"");
        return new StructuredBriefing(onScreen, headline, whatHappened, whatClaudeWants, spoken);
    }

    /// <summary>
    /// Ensure the wingman is active for a session (POST /sessions/{sid}/wingman-enabled).
    /// Voice mode requires the wingman for summarization; this is called when voice mode
    /// is entered to guarantee it is on regardless of the gateway default. Best-effort:
    /// a failure here must not block the user from talking, so it never throws.
    /// </summary>
    public async Task SetWingmanEnabledAsync(string directorBase, string sessionId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var b = directorBase.TrimEnd('/');
            using var http = NewClient();
            await http.PostAsync($"{b}/sessions/{sessionId}/wingman-enabled",
                JsonBody(new { Enabled = enabled }), ct);
            ClientLog.Write($"[DirectorVoiceClient] SetWingmanEnabled: sid={sessionId}, enabled={enabled}");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[DirectorVoiceClient] SetWingmanEnabled FAILED (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Set the session's walkie-talkie voice-mode flag on the owning Director (POST
    /// /sessions/{sid}/voice-mode) so every client - the desktop tile, the web view,
    /// and this roster - agrees the session is being talked to. Best-effort: a
    /// failure here must not block the user from talking, so it never throws.
    /// </summary>
    public async Task SetVoiceModeAsync(string directorBase, string sessionId, bool enabled, CancellationToken ct = default)
    {
        try
        {
            var b = directorBase.TrimEnd('/');
            using var http = NewClient();
            await http.PostAsync($"{b}/sessions/{sessionId}/voice-mode",
                JsonBody(new { Enabled = enabled }), ct);
            ClientLog.Write($"[DirectorVoiceClient] SetVoiceMode: sid={sessionId}, enabled={enabled}");
        }
        catch (Exception ex)
        {
            ClientLog.Write($"[DirectorVoiceClient] SetVoiceMode FAILED (non-fatal): {ex.Message}");
        }
    }

    /// <summary>
    /// Park or un-park a session in the FIFO voice queue on the owning Director (POST
    /// /sessions/{sid}/hold). Held sessions drop out of the FIFO rotation. Unlike
    /// voice-mode, this is an explicit user action with a queue consequence, so it
    /// THROWS on HTTP failure - the caller must know the hold did not take before it
    /// advances past the session.
    /// </summary>
    public async Task SetHoldAsync(string directorBase, string sessionId, bool onHold, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SetHold: base={b}, sid={sessionId}, onHold={onHold}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/hold",
            JsonBody(new { OnHold = onHold }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"hold failed: {(int)resp.StatusCode} {body}");
    }

    // ===== TERMINAL MIRROR + CONTROLS ======================================

    /// <summary>
    /// One incremental read of the session's raw terminal buffer for the read-only
    /// Terminal mirror. Calls GET /sessions/{sid}/buffer?raw=true&amp;since=&lt;cursor&gt; and
    /// returns the new raw text plus the cursor to pass on the next poll. Pass
    /// <paramref name="sinceCursor"/> &lt; 0 on the first call to dump the whole buffer.
    /// Throws on HTTP failure so the caller can show the real reason in the status line.
    /// </summary>
    public async Task<BufferSlice> GetBufferAsync(
        string directorBase, string sessionId, long sinceCursor, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        using var http = NewClient();
        // raw=false so the server returns ANSI-cleaned text (escape codes stripped),
        // not raw control bytes. A bounded line count keeps a full-snapshot poll cheap.
        var url = sinceCursor >= 0
            ? $"{b}/sessions/{sessionId}/buffer?raw=false&since={sinceCursor}"
            : $"{b}/sessions/{sessionId}/buffer?raw=false&lines=300";
        var resp = await http.GetAsync(url, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"buffer fetch failed: {(int)resp.StatusCode} {body}");
        var r = JsonSerializer.Deserialize<BufferResp>(body, Json)
                ?? throw new InvalidOperationException("/buffer returned unparseable body");
        return new BufferSlice(r.Text ?? "", r.NewCursor);
    }

    /// <summary>
    /// Send raw text (or a key escape sequence) to the session's PTY via
    /// POST /sessions/{sid}/prompt. With <paramref name="appendEnter"/> false the bytes
    /// are written verbatim (used for arrow keys ESC[A/B/C/D, Tab, a bare Enter "\r");
    /// with it true the server appends the submit newline (used for a typed line).
    /// Throws on HTTP failure so the caller surfaces it. The Terminal mirror stays
    /// read-only by construction; this is the only write path and it is user-driven.
    /// </summary>
    public async Task SendKeysAsync(
        string directorBase, string sessionId, string text, bool appendEnter, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SendKeys: base={b}, sid={sessionId}, chars={text?.Length ?? 0}, appendEnter={appendEnter}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/prompt",
            JsonBody(new { Text = text ?? "", AppendEnter = appendEnter }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"prompt failed: {(int)resp.StatusCode} {body}");
    }

    /// <summary>Send a single Escape (0x1b) to the session (POST /sessions/{sid}/escape).</summary>
    public async Task SendEscapeAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SendEscape: base={b}, sid={sessionId}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/escape", JsonBody(new { }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"escape failed: {(int)resp.StatusCode} {body}");
    }

    /// <summary>Send Ctrl+C (0x03, the Stop) to the session (POST /sessions/{sid}/interrupt).</summary>
    public async Task SendInterruptAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] SendInterrupt: base={b}, sid={sessionId}");
        using var http = NewClient();
        var resp = await http.PostAsync($"{b}/sessions/{sessionId}/interrupt", JsonBody(new { }), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"interrupt failed: {(int)resp.StatusCode} {body}");
    }

    // ===== WINGMAN CLEAN OUTPUT (parsed turns) =============================

    /// <summary>
    /// Fetch the session's parsed conversation (GET /sessions/{sid}/turns) and render
    /// it as clean, readable text for the Wingman tab: each widget as a short labelled
    /// block (user messages, the agent's replies, and what each tool did), de-noised
    /// from the raw terminal. Returns an empty string when there is nothing yet. Throws
    /// on HTTP failure so the caller can show the real reason.
    /// </summary>
    public async Task<string> GetTurnsTextAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        ClientLog.Write($"[DirectorVoiceClient] GetTurnsText: base={b}, sid={sessionId}");
        using var http = NewClient();
        var resp = await http.GetAsync($"{b}/sessions/{sessionId}/turns", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"turns fetch failed: {(int)resp.StatusCode} {body}");
        var r = JsonSerializer.Deserialize<TurnsResp>(body, Json)
                ?? throw new InvalidOperationException("/turns returned unparseable body");
        if (!string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase))
        {
            // Not an error to surface as a throw: the session may simply not be linked
            // to a Claude session id yet. Report it as a plain note so the tab shows
            // a truthful "nothing yet" rather than pretending to have content.
            ClientLog.Write($"[DirectorVoiceClient] GetTurnsText: status={r.Status}");
            return "";
        }
        var text = RenderTurns(r.Widgets);
        ClientLog.Write($"[DirectorVoiceClient] GetTurnsText OK: widgets={r.Widgets?.Count ?? 0}, chars={text.Length}");
        return text;
    }

    private static string RenderTurns(List<TurnWidget>? widgets)
    {
        if (widgets is null || widgets.Count == 0) return "";
        var sb = new StringBuilder();
        foreach (var w in widgets)
        {
            var kind = w.Kind ?? "";
            if (string.Equals(kind, "UserMessage", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine(sb, "You:", w.Content);
            }
            else if (string.Equals(kind, "Text", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine(sb, "Agent:", w.Content);
            }
            else if (string.Equals(kind, "Thinking", StringComparison.OrdinalIgnoreCase))
            {
                AppendLine(sb, "(thinking)", w.Content);
            }
            else
            {
                // A tool action: show a one-line "[Kind] header - subheader" plus the
                // body/result trimmed so the clean view reads like a narrative, not a dump.
                var head = string.IsNullOrWhiteSpace(w.Header) ? kind : w.Header;
                var sub = string.IsNullOrWhiteSpace(w.Subheader) ? "" : $" - {w.Subheader}";
                var label = $"[{kind}] {head}{sub}".Trim();
                var detail = !string.IsNullOrWhiteSpace(w.Content) ? w.Content : w.Result;
                if (w.IsError) label = "[ERROR] " + label;
                AppendLine(sb, label, detail);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static void AppendLine(StringBuilder sb, string label, string? body)
    {
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length > 600) trimmed = trimmed.Substring(0, 600) + " ...";
        if (string.IsNullOrWhiteSpace(trimmed))
            sb.AppendLine(label);
        else
            sb.AppendLine($"{label} {trimmed}");
        sb.AppendLine();
    }

    // ===== 3. RECAP =========================================================

    /// <summary>
    /// Get the session's recap text, generating it first when none is cached. The
    /// conductor reads this aloud before the answer so the user has context. Returns
    /// empty string when no recap could be produced (the caller then just reads the
    /// session name and the answer).
    /// </summary>
    public async Task<string> GetOrCreateRecapAsync(string directorBase, string sessionId, CancellationToken ct = default)
    {
        var b = directorBase.TrimEnd('/');
        using var http = NewClient();

        var getResp = await http.GetAsync($"{b}/sessions/{sessionId}/recap", ct);
        var getBody = await getResp.Content.ReadAsStringAsync(ct);
        if (getResp.IsSuccessStatusCode)
        {
            var r = JsonSerializer.Deserialize<RecapResp>(getBody, Json);
            if (r is not null && string.Equals(r.Status, "ok", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(r.Recap))
            {
                ClientLog.Write($"[DirectorVoiceClient] Recap (cached): sid={sessionId}, chars={r.Recap!.Length}");
                return r.Recap!.Trim();
            }
        }

        // None cached: ask the Director to generate one.
        var postResp = await http.PostAsync($"{b}/sessions/{sessionId}/recap", JsonBody(new { }), ct);
        var postBody = await postResp.Content.ReadAsStringAsync(ct);
        if (!postResp.IsSuccessStatusCode)
        {
            ClientLog.Write($"[DirectorVoiceClient] Recap generate failed: {(int)postResp.StatusCode}");
            return "";
        }
        var generated = JsonSerializer.Deserialize<RecapResp>(postBody, Json);
        var text = generated?.Recap ?? "";
        ClientLog.Write($"[DirectorVoiceClient] Recap generated: sid={sessionId}, chars={text.Length}");
        return text.Trim();
    }

    // ===== helpers ==========================================================

    private static StringContent JsonBody(object o)
        => new(JsonSerializer.Serialize(o), Encoding.UTF8, "application/json");

    private static string Sha256Hex(byte[] bytes)
        => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private sealed class BufferResp
    {
        public string? Text { get; set; }
        public long NewCursor { get; set; }
        public long TotalBytes { get; set; }
    }
    private sealed class TurnsResp
    {
        public string? Status { get; set; }
        public string? Error { get; set; }
        public List<TurnWidget>? Widgets { get; set; }
    }
    private sealed class TurnWidget
    {
        public string? Kind { get; set; }
        public string? Header { get; set; }
        public string? Subheader { get; set; }
        public string? Content { get; set; }
        public string? Result { get; set; }
        public bool IsError { get; set; }
        public bool IsPending { get; set; }
    }
    private sealed class RegisterResp { public string? UtteranceId { get; set; } }
    private sealed class CompleteResp
    {
        public string? Transcript { get; set; }
        public string? CleanedTranscript { get; set; }
        public string? Status { get; set; }
        public string? Error { get; set; }
    }
    private sealed class ChatResp
    {
        public string? Status { get; set; }
        public string? Summary { get; set; }
        public string? DisplayText { get; set; }
        public string? ProgressNote { get; set; }
        public string? ActivityState { get; set; }
        public string? SessionName { get; set; }
        public string? Error { get; set; }
    }
    private sealed class RecapResp
    {
        public string? Recap { get; set; }
        public string? Status { get; set; }
        public string? Error { get; set; }
    }
    private sealed class ExplainResp
    {
        public string? Answer { get; set; }
        public string? Status { get; set; }
        public string? Error { get; set; }
        public string? Headline { get; set; }
        public string? WhatHappened { get; set; }
        public string? WhatClaudeWants { get; set; }
        public string? Say { get; set; }
    }
}
