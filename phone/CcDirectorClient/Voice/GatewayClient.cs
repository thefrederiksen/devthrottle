using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// One session nested under a Director on the Exes page. Mirrors the
/// <c>sessions[]</c> shape returned by the Gateway's GET /exes/list.
/// </summary>
public sealed record ExesSession(
    string SessionId,
    string Name,
    string Agent,
    string ActivityState,
    string StatusColor,
    string RepoPath);

/// <summary>
/// One Director process physically running on the Gateway's computer, with its
/// sessions nested. Mirrors the <c>directors[]</c> shape of GET /exes/list.
/// </summary>
public sealed record ExesDirector(
    string DirectorId,
    int Pid,
    int? Slot,
    string ExePath,
    string ControlEndpoint,
    string DirectorUrl,
    string Version,
    DateTime? StartedAt,
    string Source,
    string? SessionError,
    List<ExesSession> Sessions);

/// <summary>
/// One local build slot (1-4). <see cref="Running"/> is non-null when a running
/// Director's exe resolves to this slot's file.
/// </summary>
public sealed record ExesSlot(
    int Slot,
    bool Exists,
    string ExePath,
    DateTime? LastBuiltUtc,
    long SizeBytes,
    ExesSlotRunning? Running);

/// <summary>The PID + directorId of the Director currently occupying a slot.</summary>
public sealed record ExesSlotRunning(int Pid, string DirectorId);

/// <summary>
/// Full parsed payload of GET /exes/list: the machine, the repo root (empty when
/// slot management is unavailable), the running Directors and the build slots.
/// </summary>
public sealed record ExesData(
    string MachineName,
    string RepoRoot,
    List<ExesDirector> Directors,
    List<ExesSlot> Slots);

/// <summary>
/// One recording row on the Transcripts page. Mirrors the shape returned by
/// GET /ingest/recordings (a RecordingListItem serialized to camelCase): the
/// recording id, human-readable title/subtitle/summary, when it started, its
/// processing state, segment count + total duration, whether a transcript text
/// is stored, and whether it has been promoted into the vault.
/// </summary>
public sealed class RecordingItem
{
    public string RecordingId { get; set; } = "";
    public string Title { get; set; } = "";
    public string Subtitle { get; set; } = "";
    public string Summary { get; set; } = "";
    public string StartedAt { get; set; } = "";
    public string State { get; set; } = "";
    public int Segments { get; set; }
    public long DurationMs { get; set; }
    public bool HasTranscript { get; set; }
    public bool InVault { get; set; }
}

/// <summary>
/// The shared STT dictation glossary, as edited on the Dictionary page. Mirrors
/// the shape returned by GET /ingest/dictionary: the vocabulary biased into
/// speech-to-text, the known mistranscription corrections (correct term -> list
/// of wrong spellings), and the cleanup profiles (name -> cleanupEnabled).
/// </summary>
public sealed class DictionaryModel
{
    public List<string> Vocabulary { get; init; } = new();
    public Dictionary<string, List<string>> CommonMistranscriptions { get; init; } = new();
    public Dictionary<string, bool> Profiles { get; init; } = new();
}

/// <summary>
/// Reads the session roster from the Gateway (GET /sessions), which aggregates
/// every session across all Directors and stamps each with the owning Director's
/// Tailnet base URL. This is the conductor's and the talk screen's source of
/// truth for what sessions exist and which need the user.
/// </summary>
public sealed class GatewayClient
{
    private readonly string _baseUrl;
    private readonly string _token;

    public GatewayClient(string baseUrl, string token = "")
    {
        _baseUrl = (baseUrl ?? "").TrimEnd('/');
        _token = token ?? "";
    }

    /// <summary>
    /// Fetch the current roster. Throws on a network or HTTP failure so the UI can
    /// show the real reason instead of a silently empty list. Exited/failed
    /// sessions are filtered out by <see cref="RosterParser"/>.
    /// </summary>
    public async Task<List<SessionInfo>> GetRosterAsync(CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetRoster: base={_baseUrl}");
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);

        var resp = await http.GetAsync($"{_baseUrl}/sessions", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /sessions failed: {(int)resp.StatusCode} {body}");

        var roster = RosterParser.Parse(body);
        ClientLog.Write($"[GatewayClient] GetRoster OK: count={roster.Count}");
        return roster;
    }

    // ===== Exes page (Director executables + build slots) ===================

    /// <summary>
    /// Fetch the Exes view (GET /exes/list): the Directors physically running on
    /// the Gateway's PC with their sessions, plus the local build slots 1-4.
    /// Throws on a network or HTTP failure so the page can show the real reason.
    /// </summary>
    public async Task<ExesData> GetExesAsync(CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetExes: base={_baseUrl}");
        using var http = NewClient(TimeSpan.FromSeconds(20));
        var resp = await http.GetAsync($"{_baseUrl}/exes/list", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /exes/list failed: {(int)resp.StatusCode} {body}");

        var data = ParseExes(body);
        ClientLog.Write($"[GatewayClient] GetExes OK: directors={data.Directors.Count}, slots={data.Slots.Count}");
        return data;
    }

    /// <summary>
    /// Kill a Director and all of its sessions (DELETE /directors/{id} with
    /// {"force":true}). Throws on HTTP failure so the caller can surface it.
    /// </summary>
    public async Task KillDirectorAsync(string directorId, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] KillDirector: id={directorId}");
        using var http = NewClient(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Delete,
            $"{_baseUrl}/directors/{Uri.EscapeDataString(directorId)}")
        {
            Content = new StringContent("{\"force\":true}", Encoding.UTF8, "application/json"),
        };
        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"DELETE /directors/{directorId} failed: {(int)resp.StatusCode} {body}");
        ClientLog.Write($"[GatewayClient] KillDirector OK: id={directorId}");
    }

    /// <summary>
    /// Build slot n then launch it (POST /exes/slots/{n}/build-start). The build is
    /// slow (about a minute), so this uses a long timeout. Returns the launched
    /// process id. Throws on HTTP failure with the server's real error text.
    /// </summary>
    public async Task<int> BuildStartSlotAsync(int slot, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] BuildStartSlot: slot={slot}");
        using var http = NewClient(TimeSpan.FromMinutes(8));
        var resp = await http.PostAsync($"{_baseUrl}/exes/slots/{slot}/build-start", null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));

        var pid = ExtractPid(body);
        ClientLog.Write($"[GatewayClient] BuildStartSlot OK: slot={slot}, pid={pid}");
        return pid;
    }

    /// <summary>
    /// Delete a slot's built exe (DELETE /exes/slots/{n}). Throws on HTTP failure
    /// with the server's real error text.
    /// </summary>
    public async Task DeleteSlotAsync(int slot, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] DeleteSlot: slot={slot}");
        using var http = NewClient(TimeSpan.FromSeconds(30));
        var resp = await http.DeleteAsync($"{_baseUrl}/exes/slots/{slot}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));
        ClientLog.Write($"[GatewayClient] DeleteSlot OK: slot={slot}");
    }

    // ===== Transcripts page (phone recordings) ==============================

    /// <summary>
    /// Fetch all recordings (GET /ingest/recordings) and parse them into the list
    /// the Transcripts page renders. Parses case-insensitively; missing fields
    /// default to empty/zero. Throws on a network or HTTP failure so the page can
    /// show the real reason instead of a silently empty list.
    /// </summary>
    public async Task<List<RecordingItem>> GetRecordingsAsync(CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetRecordings: base={_baseUrl}");
        using var http = NewClient(TimeSpan.FromSeconds(20));
        var resp = await http.GetAsync($"{_baseUrl}/ingest/recordings", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /ingest/recordings failed: {(int)resp.StatusCode} {body}");

        var list = ParseRecordings(body);
        ClientLog.Write($"[GatewayClient] GetRecordings OK: count={list.Count}");
        return list;
    }

    /// <summary>
    /// Fetch the cleaned transcript text (GET /ingest/recording/{id}/transcript)
    /// as plain text. Throws on a network or HTTP failure so the caller can surface
    /// the real reason rather than showing a blank transcript.
    /// </summary>
    public async Task<string> GetTranscriptTextAsync(string recordingId, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetTranscriptText: id={recordingId}");
        using var http = NewClient(TimeSpan.FromSeconds(20));
        var resp = await http.GetAsync(
            $"{_baseUrl}/ingest/recording/{Uri.EscapeDataString(recordingId)}/transcript", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET transcript failed: {(int)resp.StatusCode} {body}");

        ClientLog.Write($"[GatewayClient] GetTranscriptText OK: id={recordingId}, chars={body.Length}");
        return body;
    }

    /// <summary>
    /// Update the human-readable metadata (PATCH /ingest/recording/{id}/meta) and
    /// return the updated record the server echoes back. Throws on HTTP failure
    /// with the server's real error text.
    /// </summary>
    public async Task<RecordingItem> UpdateRecordingMetaAsync(
        string recordingId, string title, string subtitle, string summary, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] UpdateRecordingMeta: id={recordingId}");
        var payload = JsonSerializer.Serialize(new { title, subtitle, summary });
        using var http = NewClient(TimeSpan.FromSeconds(20));
        using var req = new HttpRequestMessage(
            HttpMethod.Patch,
            $"{_baseUrl}/ingest/recording/{Uri.EscapeDataString(recordingId)}/meta")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));

        var item = ParseRecording(JsonDocument.Parse(body).RootElement);
        ClientLog.Write($"[GatewayClient] UpdateRecordingMeta OK: id={recordingId}");
        return item;
    }

    /// <summary>
    /// Promote a recording into the vault (POST /ingest/recording/{id}/promote).
    /// Throws on HTTP failure with the server's real error text.
    /// </summary>
    public async Task PromoteRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] PromoteRecording: id={recordingId}");
        using var http = NewClient(TimeSpan.FromSeconds(60));
        var resp = await http.PostAsync(
            $"{_baseUrl}/ingest/recording/{Uri.EscapeDataString(recordingId)}/promote", null, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));
        ClientLog.Write($"[GatewayClient] PromoteRecording OK: id={recordingId}");
    }

    /// <summary>
    /// Delete the transient local transcript + audio (DELETE /ingest/recording/{id}).
    /// A promoted vault copy is kept by the server. Throws on HTTP failure with the
    /// server's real error text.
    /// </summary>
    public async Task DeleteRecordingAsync(string recordingId, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] DeleteRecording: id={recordingId}");
        using var http = NewClient(TimeSpan.FromSeconds(30));
        var resp = await http.DeleteAsync(
            $"{_baseUrl}/ingest/recording/{Uri.EscapeDataString(recordingId)}", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));
        ClientLog.Write($"[GatewayClient] DeleteRecording OK: id={recordingId}");
    }

    // Parse the recordings array. Parses case-insensitively (the Gateway emits
    // camelCase). A non-array body is treated as no recordings.
    private static List<RecordingItem> ParseRecordings(string body)
    {
        var list = new List<RecordingItem>();
        using var doc = JsonDocument.Parse(body);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
        foreach (var el in doc.RootElement.EnumerateArray())
            list.Add(ParseRecording(el));
        return list;
    }

    // Parse one recording object. Tolerates either casing of the field name and
    // missing fields (default to empty/zero).
    private static RecordingItem ParseRecording(JsonElement el) => new()
    {
        RecordingId = GetStringEither(el, "recordingId"),
        Title = GetStringEither(el, "title"),
        Subtitle = GetStringEither(el, "subtitle"),
        Summary = GetStringEither(el, "summary"),
        StartedAt = GetStringEither(el, "startedAt"),
        State = GetStringEither(el, "state"),
        Segments = GetIntEither(el, "segments"),
        DurationMs = GetLongEither(el, "durationMs"),
        HasTranscript = GetBoolEither(el, "hasTranscript"),
        InVault = GetBoolEither(el, "inVault"),
    };

    // ===== Dictionary page (shared STT dictation glossary) ==================

    /// <summary>
    /// Fetch the shared dictation glossary (GET /ingest/dictionary) and parse it into
    /// a mutable model. Parses case-insensitively; missing fields default to empty.
    /// Throws on a network or HTTP failure so the page can show the real reason.
    /// </summary>
    public async Task<DictionaryModel> GetDictionaryAsync(CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] GetDictionary: base={_baseUrl}");
        using var http = NewClient(TimeSpan.FromSeconds(20));
        var resp = await http.GetAsync($"{_baseUrl}/ingest/dictionary", ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"GET /ingest/dictionary failed: {(int)resp.StatusCode} {body}");

        var model = ParseDictionary(body);
        ClientLog.Write($"[GatewayClient] GetDictionary OK: vocab={model.Vocabulary.Count}, mistrans={model.CommonMistranscriptions.Count}, profiles={model.Profiles.Count}");
        return model;
    }

    /// <summary>
    /// Save the whole edited glossary (PUT /ingest/dictionary) in the camelCase shape
    /// the Gateway expects (profiles as { name: { cleanupEnabled: bool } }) and return
    /// the saved model the server echoes back. Throws on HTTP failure with the
    /// server's real error text.
    /// </summary>
    public async Task<DictionaryModel> SaveDictionaryAsync(DictionaryModel model, CancellationToken ct = default)
    {
        ClientLog.Write($"[GatewayClient] SaveDictionary: vocab={model.Vocabulary.Count}, mistrans={model.CommonMistranscriptions.Count}, profiles={model.Profiles.Count}");
        var payload = SerializeDictionary(model);
        using var http = NewClient(TimeSpan.FromSeconds(30));
        using var req = new HttpRequestMessage(HttpMethod.Put, $"{_baseUrl}/ingest/dictionary")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        var resp = await http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException(ExtractError(body, (int)resp.StatusCode));

        var saved = ParseDictionary(body);
        ClientLog.Write($"[GatewayClient] SaveDictionary OK: vocab={saved.Vocabulary.Count}, mistrans={saved.CommonMistranscriptions.Count}, profiles={saved.Profiles.Count}");
        return saved;
    }

    // Parse case-insensitively (the Gateway emits camelCase, but be safe). Missing
    // fields default to empty. A profile's value is the cleanupEnabled flag, read
    // from { cleanupEnabled: bool }.
    private static DictionaryModel ParseDictionary(string body)
    {
        var model = new DictionaryModel();
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        if (TryGetProperty(root, "vocabulary", out var vocab) && vocab.ValueKind == JsonValueKind.Array)
        {
            foreach (var v in vocab.EnumerateArray())
                if (v.ValueKind == JsonValueKind.String)
                {
                    var term = v.GetString();
                    if (!string.IsNullOrEmpty(term)) model.Vocabulary.Add(term);
                }
        }

        if (TryGetProperty(root, "commonMistranscriptions", out var mis) && mis.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in mis.EnumerateObject())
            {
                var variants = new List<string>();
                if (prop.Value.ValueKind == JsonValueKind.Array)
                    foreach (var w in prop.Value.EnumerateArray())
                        if (w.ValueKind == JsonValueKind.String)
                        {
                            var wrong = w.GetString();
                            if (!string.IsNullOrEmpty(wrong)) variants.Add(wrong);
                        }
                model.CommonMistranscriptions[prop.Name] = variants;
            }
        }

        if (TryGetProperty(root, "profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in profiles.EnumerateObject())
            {
                var cleanupEnabled = true;
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && TryGetProperty(prop.Value, "cleanupEnabled", out var ce)
                    && ce.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    cleanupEnabled = ce.GetBoolean();
                model.Profiles[prop.Name] = cleanupEnabled;
            }
        }

        return model;
    }

    // Serialize to the camelCase shape the Gateway expects, with profiles expanded
    // back into { name: { cleanupEnabled: bool } }.
    private static string SerializeDictionary(DictionaryModel model)
    {
        var profiles = new Dictionary<string, object>();
        foreach (var kv in model.Profiles)
            profiles[kv.Key] = new { cleanupEnabled = kv.Value };

        var payload = new
        {
            vocabulary = model.Vocabulary,
            commonMistranscriptions = model.CommonMistranscriptions,
            profiles,
        };
        return JsonSerializer.Serialize(payload);
    }

    // Property lookup that tolerates either casing of the field name (the Gateway
    // emits camelCase; this stays robust if that ever changes).
    private static bool TryGetProperty(JsonElement el, string camelName, out JsonElement value)
    {
        if (el.TryGetProperty(camelName, out value)) return true;
        var pascal = char.ToUpperInvariant(camelName[0]) + camelName.Substring(1);
        return el.TryGetProperty(pascal, out value);
    }

    private HttpClient NewClient(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        if (!string.IsNullOrWhiteSpace(_token))
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _token);
        return http;
    }

    private static ExesData ParseExes(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        var machineName = GetString(root, "machineName");
        var repoRoot = GetString(root, "repoRoot");

        var directors = new List<ExesDirector>();
        if (root.TryGetProperty("directors", out var dirs) && dirs.ValueKind == JsonValueKind.Array)
        {
            foreach (var d in dirs.EnumerateArray())
            {
                var sessions = new List<ExesSession>();
                if (d.TryGetProperty("sessions", out var sess) && sess.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in sess.EnumerateArray())
                        sessions.Add(new ExesSession(
                            GetString(s, "sessionId"),
                            GetString(s, "name"),
                            GetString(s, "agent"),
                            GetString(s, "activityState"),
                            GetString(s, "statusColor"),
                            GetString(s, "repoPath")));
                }

                directors.Add(new ExesDirector(
                    GetString(d, "directorId"),
                    GetInt(d, "pid") ?? 0,
                    GetNullableInt(d, "slot"),
                    GetString(d, "exePath"),
                    GetString(d, "controlEndpoint"),
                    GetString(d, "directorUrl"),
                    GetString(d, "version"),
                    GetDateTime(d, "startedAt"),
                    GetString(d, "source"),
                    GetNullableString(d, "sessionError"),
                    sessions));
            }
        }

        var slots = new List<ExesSlot>();
        if (root.TryGetProperty("slots", out var sl) && sl.ValueKind == JsonValueKind.Array)
        {
            foreach (var s in sl.EnumerateArray())
            {
                ExesSlotRunning? running = null;
                if (s.TryGetProperty("running", out var r) && r.ValueKind == JsonValueKind.Object)
                    running = new ExesSlotRunning(GetInt(r, "pid") ?? 0, GetString(r, "directorId"));

                slots.Add(new ExesSlot(
                    GetInt(s, "slot") ?? 0,
                    GetBool(s, "exists"),
                    GetString(s, "exePath"),
                    GetDateTime(s, "lastBuiltUtc"),
                    GetLong(s, "sizeBytes") ?? 0L,
                    running));
            }
        }

        return new ExesData(machineName, repoRoot, directors, slots);
    }

    // The slot routes return a problem-details ("detail") or a plain {"error":...}
    // JSON body on failure; surface whichever is present rather than just the code.
    private static string ExtractError(string body, int statusCode)
    {
        if (!string.IsNullOrWhiteSpace(body))
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.Object)
                {
                    if (root.TryGetProperty("detail", out var detail) && detail.ValueKind == JsonValueKind.String)
                        return detail.GetString() ?? $"HTTP {statusCode}";
                    if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.String)
                        return error.GetString() ?? $"HTTP {statusCode}";
                }
            }
            catch (JsonException)
            {
                // Not JSON: fall through to the raw body below.
            }
            return body;
        }
        return $"HTTP {statusCode}";
    }

    private static int ExtractPid(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return 0;
        using var doc = JsonDocument.Parse(body);
        return GetInt(doc.RootElement, "pid") ?? 0;
    }

    // Casing-tolerant readers used by the recordings parser (the Gateway emits
    // camelCase; these stay robust if that ever changes).
    private static string GetStringEither(JsonElement el, string camelName)
        => TryGetProperty(el, camelName, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static int GetIntEither(JsonElement el, string camelName)
        => TryGetProperty(el, camelName, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    private static long GetLongEither(JsonElement el, string camelName)
        => TryGetProperty(el, camelName, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;

    private static bool GetBoolEither(JsonElement el, string camelName)
        => TryGetProperty(el, camelName, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static string GetString(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : "";

    private static string? GetNullableString(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v)) return null;
        return v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static bool GetBool(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False && v.GetBoolean();

    private static int? GetInt(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static int? GetNullableInt(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number) return null;
        return v.TryGetInt32(out var n) ? n : null;
    }

    private static long? GetLong(JsonElement el, string name)
        => el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static DateTime? GetDateTime(JsonElement el, string name)
    {
        if (!el.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.String) return null;
        return v.TryGetDateTime(out var dt) ? dt : null;
    }
}
