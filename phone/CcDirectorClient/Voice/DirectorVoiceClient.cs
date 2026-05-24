using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CcDirectorClient.Voice;

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
/// The reply is read aloud by native Android TTS in the caller, not fetched as
/// audio, so this client only ever deals in text.
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
    /// </summary>
    public async Task<string> TranscribeUtteranceAsync(
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
        return (text ?? "").Trim();
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
}
