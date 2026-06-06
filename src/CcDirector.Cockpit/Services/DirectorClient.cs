using System.Net.Http.Headers;
using System.Net.Http.Json;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Cockpit.Services;

/// <summary>One queued prompt, as returned by GET /sessions/{sid}/queue on a Director.</summary>
public sealed class QueueItem
{
    public string Id { get; set; } = "";
    public string Text { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class QueueResponse
{
    public List<QueueItem> Items { get; set; } = new();
}

/// <summary>One screenshot on a Director's machine, as returned by GET /screenshots. The
/// Director pre-formats <see cref="TimeLabel"/> ("MMM d, h:mm tt") so the Cockpit shows the
/// same label as the desktop without re-deriving it.</summary>
public sealed class ScreenshotInfo
{
    public string FileName { get; set; } = "";
    /// <summary>Absolute on-disk path on the Director's machine. Injected into the composer at
    /// the cursor so the owning Claude session can read the image directly (no upload).</summary>
    public string Path { get; set; } = "";
    public string TimeLabel { get; set; } = "";
    public DateTimeOffset LastWriteUtc { get; set; }
    public long SizeBytes { get; set; }
}

/// <summary>The shape of GET /screenshots: the resolved folder + its image files, newest first.</summary>
public sealed class ScreenshotsResponse
{
    public string Directory { get; set; } = "";
    /// <summary>Full folder count, regardless of any ?count cap on Items. 0 on Directors that
    /// predate the field - treat Items.Count as the total then.</summary>
    public int Total { get; set; }
    public List<ScreenshotInfo> Items { get; set; } = new();
}

/// <summary>
/// Talks DIRECT to an owning Director (never through the Gateway) for the write/act path:
/// prompts, interrupt, escape, the prompt queue, and image upload. The base URL is the
/// session's TailnetEndpoint (e.g. https://&lt;machine&gt;.&lt;tailnet&gt;.ts.net:7887), fronted by
/// Tailscale Serve - so every call rides the tailnet, never localhost. Matches the design's
/// "reads aggregate through the Gateway; writes/streams go direct to the Director" rule.
/// </summary>
public sealed class DirectorClient
{
    private readonly HttpClient _http;
    private readonly ILogger<DirectorClient> _log;

    public DirectorClient(HttpClient http, ILogger<DirectorClient> log)
    {
        _http = http;
        _log = log;
    }

    private static Uri Url(string directorBase, string path) =>
        new(new Uri(directorBase.TrimEnd('/') + "/"), path.TrimStart('/'));

    public async Task SendPromptAsync(string directorBase, string sid, string text, CancellationToken ct = default)
    {
        _log.LogDebug("SendPrompt sid={Sid} len={Len}", sid, text.Length);
        var resp = await _http.PostAsJsonAsync(Url(directorBase, $"sessions/{sid}/prompt"),
            new { text, appendEnter = true }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// Raw PTY keystroke(s) - NO trailing Enter. <c>appendEnter:false</c> routes to
    /// <c>session.SendInput(bytes)</c>, so control sequences (Esc \x1b, Ctrl+C \x03, arrows,
    /// the slash-command UI) reach the terminal exactly as typed. Used by the live terminal's
    /// keystroke forwarding; not logged per-keystroke to avoid noise.
    /// </summary>
    public async Task SendInputAsync(string directorBase, string sid, string data, CancellationToken ct = default)
    {
        var resp = await _http.PostAsJsonAsync(Url(directorBase, $"sessions/{sid}/prompt"),
            new { text = data, appendEnter = false }, ct);
        resp.EnsureSuccessStatusCode();
    }

    public async Task InterruptAsync(string directorBase, string sid, CancellationToken ct = default)
        => (await _http.PostAsync(Url(directorBase, $"sessions/{sid}/interrupt"), null, ct)).EnsureSuccessStatusCode();

    public async Task EscapeAsync(string directorBase, string sid, CancellationToken ct = default)
        => (await _http.PostAsync(Url(directorBase, $"sessions/{sid}/escape"), null, ct)).EnsureSuccessStatusCode();

    public async Task<List<QueueItem>> GetQueueAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync<QueueResponse>(Url(directorBase, $"sessions/{sid}/queue"), ct);
        return r?.Items ?? new List<QueueItem>();
    }

    public async Task<List<QueueItem>> EnqueueAsync(string directorBase, string sid, string text, CancellationToken ct = default)
    {
        _log.LogDebug("Enqueue sid={Sid} len={Len}", sid, text.Length);
        var resp = await _http.PostAsJsonAsync(Url(directorBase, $"sessions/{sid}/queue"), new { text }, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<QueueResponse>(cancellationToken: ct);
        return r?.Items ?? new List<QueueItem>();
    }

    public async Task<List<QueueItem>> DeleteQueueItemAsync(string directorBase, string sid, string itemId, CancellationToken ct = default)
    {
        var resp = await _http.DeleteAsync(Url(directorBase, $"sessions/{sid}/queue/{itemId}"), ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<QueueResponse>(cancellationToken: ct);
        return r?.Items ?? new List<QueueItem>();
    }

    public async Task<List<QueueItem>> SendQueueItemAsync(string directorBase, string sid, string itemId, CancellationToken ct = default)
    {
        var resp = await _http.PostAsync(Url(directorBase, $"sessions/{sid}/queue/{itemId}/send"), null, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<QueueResponse>(cancellationToken: ct);
        return r?.Items ?? new List<QueueItem>();
    }

    /// <summary>
    /// Forward a dropped screenshot to the owning Director as multipart/form-data (field
    /// "file"). The Director saves it to its Screenshots directory and returns the absolute
    /// <c>path</c> on disk; we return that path so the caller can hand it to Claude (the
    /// upload endpoint itself does NOT inject anything into the session). The browser streams
    /// the bytes to the Cockpit server (Blazor InputFile); the Cockpit forwards them here, so
    /// the Director never needs CORS for a browser cross-origin upload.
    /// </summary>
    /// <returns>The absolute path the Director saved the image to (empty if not reported).</returns>
    public async Task<string> UploadImageAsync(string directorBase, string sid, Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        _log.LogDebug("UploadImage sid={Sid} file={File}", sid, fileName);
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(fileContent, "file", fileName);
        var resp = await _http.PostAsync(Url(directorBase, $"sessions/{sid}/upload-image"), form, ct);
        resp.EnsureSuccessStatusCode();
        var r = await resp.Content.ReadFromJsonAsync<UploadImageResponse>(cancellationToken: ct);
        return r?.Path ?? "";
    }

    // ===== Screenshots gallery (folder lives on the Director's machine) =====

    /// <summary>
    /// List the screenshots in the Director's screenshots folder, newest first
    /// (<c>GET /screenshots</c>). The folder is per-machine, not per-session, so this takes only
    /// the Director base. The image bytes themselves are loaded browser-direct via
    /// <see cref="ScreenshotUrl"/>; this call returns just the metadata + time labels.
    /// <paramref name="count"/> caps the items (the folder can hold thousands); &lt;=0 fetches
    /// everything. Old Directors ignore the param and return all items with Total=0.
    /// </summary>
    public async Task<ScreenshotsResponse> GetScreenshotsAsync(string directorBase, int count = 0, CancellationToken ct = default)
    {
        var path = count > 0 ? $"screenshots?count={count}" : "screenshots";
        var r = await _http.GetFromJsonAsync<ScreenshotsResponse>(Url(directorBase, path), ct);
        return r ?? new ScreenshotsResponse();
    }

    /// <summary>Delete one screenshot file from the Director's disk (<c>DELETE /screenshots/file</c>).</summary>
    public async Task DeleteScreenshotAsync(string directorBase, string fileName, CancellationToken ct = default)
    {
        _log.LogDebug("DeleteScreenshot file={File}", fileName);
        var resp = await _http.DeleteAsync(Url(directorBase, $"screenshots/file?name={Uri.EscapeDataString(fileName)}"), ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>
    /// The browser-facing URL for one screenshot's bytes (<c>GET /screenshots/file</c>), used as
    /// an <c>&lt;img src&gt;</c> and by the Copy action. Points STRAIGHT at the owning Director's
    /// tailnet endpoint - the same browser-direct path the live terminal's WebSocket uses - not
    /// through the Cockpit, so thumbnails never round-trip the Blazor circuit.
    /// </summary>
    public static string ScreenshotUrl(string directorBase, string fileName)
        => $"{directorBase.TrimEnd('/')}/screenshots/file?name={Uri.EscapeDataString(fileName)}";

    /// <summary>Kill a session (<c>DELETE /sessions/{sid}</c>) on its owning Director.</summary>
    public async Task DeleteSessionAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        _log.LogDebug("DeleteSession sid={Sid}", sid);
        var resp = await _http.DeleteAsync(Url(directorBase, $"sessions/{sid}"), ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Rename a session (<c>PATCH /sessions/{sid}</c> with <c>{ name }</c>).</summary>
    public async Task RenameSessionAsync(string directorBase, string sid, string name, CancellationToken ct = default)
    {
        _log.LogDebug("RenameSession sid={Sid} name={Name}", sid, name);
        var resp = await _http.PatchAsJsonAsync(Url(directorBase, $"sessions/{sid}"), new { name }, ct);
        resp.EnsureSuccessStatusCode();
    }

    /// <summary>Read a Director's raw settings JSON (<c>GET /settings</c>).</summary>
    public async Task<string> GetSettingsAsync(string directorBase, CancellationToken ct = default)
        => await _http.GetStringAsync(Url(directorBase, "settings"), ct);

    /// <summary>Write a Director's settings (<c>PUT /settings</c>) - the Director re-applies live.</summary>
    public async Task PutSettingsAsync(string directorBase, string json, CancellationToken ct = default)
    {
        _log.LogDebug("PutSettings len={Len}", json.Length);
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        var resp = await _http.PutAsync(Url(directorBase, "settings"), content, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ===== Awareness (Phase 3): recap + turn summaries, read over REST =====

    /// <summary>The cached "what's happening" recap (<c>GET /recap</c>). Status is "not_cached"
    /// until one is generated. Fast/free - just reads the cache.</summary>
    public async Task<RecapResponse?> GetRecapAsync(string directorBase, string sid, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<RecapResponse>(Url(directorBase, $"sessions/{sid}/recap"), ct);

    /// <summary>Generate a fresh recap (<c>POST /recap</c>). SLOW - an opus call (~90s); the
    /// caller shows progress and never blocks the live terminal (that is a separate stream).</summary>
    public async Task<RecapResponse?> GenerateRecapAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        _log.LogDebug("GenerateRecap sid={Sid}", sid);
        var resp = await _http.PostAsync(Url(directorBase, $"sessions/{sid}/recap"), null, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<RecapResponse>(cancellationToken: ct);
    }

    /// <summary>The session's turn-by-turn summaries (<c>GET /turn-summaries</c>) - the arc of
    /// the session. Fast/free - reads cached summaries.</summary>
    public async Task<TurnSummariesResponse?> GetTurnSummariesAsync(string directorBase, string sid, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<TurnSummariesResponse>(Url(directorBase, $"sessions/{sid}/turn-summaries"), ct);

    /// <summary>The plain-text handover prompt that a target session would receive
    /// (<c>GET /handover-context</c>) - shown as a preview before dispatching a handover.</summary>
    public async Task<string> GetHandoverContextAsync(string directorBase, string sid, CancellationToken ct = default)
        => await _http.GetStringAsync(Url(directorBase, $"sessions/{sid}/handover-context"), ct);

    // ===== Brief (the full-page session view: ASK / DID / NEEDS YOU) =====

    /// <summary>
    /// The session Brief (<c>GET /sessions/{sid}/brief</c>). Returns null on 404 - either an
    /// old Director build without the endpoint or a session the Director no longer knows;
    /// callers degrade to <see cref="GetSummaryAsync"/> (which disambiguates the two). The
    /// first call after a new turn runs the Director-side condensation (~1-2s); subsequent
    /// calls hit its cache.
    /// </summary>
    public async Task<BriefResponse?> GetBriefAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url(directorBase, $"sessions/{sid}/brief"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<BriefResponse>(cancellationToken: ct);
    }

    /// <summary>
    /// The transcript summary (<c>GET /sessions/{sid}/summary</c>) - the Brief's degrade
    /// target on old Directors (it ships LastUserPrompt/LastAssistantText since the final
    /// build). Returns null on 404.
    /// </summary>
    public async Task<SessionSummaryDto?> GetSummaryAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url(directorBase, $"sessions/{sid}/summary"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionSummaryDto>(cancellationToken: ct);
    }

    /// <summary>
    /// The last lines of the session's CURRENT SCREEN - the Brief's live "what is Claude
    /// doing right now" peek while a session works. Reads the server-side parsed grid
    /// (<c>GET /buffer/html</c>, on every Director build) rather than the linear cleaned
    /// byte stream, because the TUI's constant repaints flatten into "spinner spinner
    /// spinner" noise in the stream while the grid is always the coherent screen.
    /// </summary>
    public async Task<string> GetScreenTailAsync(string directorBase, string sid, int lines, CancellationToken ct = default)
    {
        var r = await _http.GetFromJsonAsync<BufferHtmlResponse>(
            Url(directorBase, $"sessions/{sid}/buffer/html"), ct);
        if (string.IsNullOrEmpty(r?.GridHtml)) return "";

        var rows = r.GridHtml
            .Split("<div class=\"line\">", StringSplitOptions.RemoveEmptyEntries)
            .Select(row => System.Net.WebUtility.HtmlDecode(
                System.Text.RegularExpressions.Regex.Replace(row, "<[^>]+>", "",
                    System.Text.RegularExpressions.RegexOptions.None, TimeSpan.FromMilliseconds(100))).TrimEnd())
            .Where(t => !string.IsNullOrWhiteSpace(t));
        return string.Join("\n", rows.TakeLast(lines));
    }

    // ===== Wingman turn briefs (TURN_BRIEFING.md) =====

    /// <summary>The latest stored TurnBrief (<c>GET /turnbriefs/latest</c>). Null on 404 -
    /// no brief yet, or an old Director without the endpoint; callers degrade.</summary>
    public async Task<TurnBriefDto?> GetLatestTurnBriefAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url(directorBase, $"sessions/{sid}/turnbriefs/latest"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TurnBriefDto>(cancellationToken: ct);
    }

    /// <summary>All stored TurnBriefs, newest first (<c>GET /turnbriefs</c>) - the wingman
    /// tab's turn timeline. Null on 404 (old Director).</summary>
    public async Task<TurnBriefsResponse?> GetTurnBriefsAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url(directorBase, $"sessions/{sid}/turnbriefs"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<TurnBriefsResponse>(cancellationToken: ct);
    }

    /// <summary>The session's token usage (<c>GET /sessions/{sid}/usage</c>): totals, current
    /// context size, per-turn deltas - computed Director-side from the transcript JSONL. Null
    /// on 404 (old Director, or no transcript yet); the panel hides the tokens block.</summary>
    public async Task<SessionUsageDto?> GetSessionUsageAsync(string directorBase, string sid, CancellationToken ct = default)
    {
        var resp = await _http.GetAsync(Url(directorBase, $"sessions/{sid}/usage"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<SessionUsageDto>(cancellationToken: ct);
    }

    /// <summary>"This brief is wrong" (D7) - stores the report as a labeled example.</summary>
    public async Task PostBriefFeedbackAsync(string directorBase, string sid, int turnNumber, string note, CancellationToken ct = default)
    {
        _log.LogDebug("BriefFeedback sid={Sid} turn={Turn}", sid, turnNumber);
        var resp = await _http.PostAsJsonAsync(Url(directorBase, $"sessions/{sid}/turnbriefs/feedback"),
            new TurnBriefFeedbackRequest { TurnNumber = turnNumber, Note = note }, ct);
        resp.EnsureSuccessStatusCode();
    }

    // ===== Power tools (Phase 4) =====

    /// <summary>A read-only git summary for the session's repo (<c>GET /git</c>): branch,
    /// dirty, ahead/behind, last commit. (Per-file status needs a Director endpoint that does
    /// not exist yet.)</summary>
    public async Task<GitSnapshot?> GetGitAsync(string directorBase, string sid, CancellationToken ct = default)
        => await _http.GetFromJsonAsync<GitSnapshot>(Url(directorBase, $"sessions/{sid}/git"), ct);

    /// <summary>
    /// Broadcast a prompt to several sessions on ONE Director (<c>POST /fanout-local</c>). The
    /// session ids must all belong to <paramref name="directorBase"/>; the Cockpit groups a
    /// fleet-wide selection by Director and calls this once per Director. <c>waitForIdle:false</c>
    /// returns as soon as the text is delivered, so the UI does not block.
    /// </summary>
    public async Task<FanoutResponse?> FanoutAsync(string directorBase, List<string> sessionIds, string text, CancellationToken ct = default)
    {
        _log.LogDebug("Fanout count={Count} len={Len}", sessionIds.Count, text.Length);
        var resp = await _http.PostAsJsonAsync(Url(directorBase, "fanout-local"),
            new { sessionIds, text, appendEnter = true, waitForIdle = false }, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadFromJsonAsync<FanoutResponse>(cancellationToken: ct);
    }
}

/// <summary>The shape of <c>POST /sessions/{sid}/upload-image</c>: the absolute on-disk path
/// the Director saved the image to, plus the generated file name.</summary>
public sealed class UploadImageResponse
{
    public string Path { get; set; } = "";
    public string FileName { get; set; } = "";
}

/// <summary>The slice of <c>GET /sessions/{sid}/buffer/html</c> the Brief's screen tail
/// needs: the parsed CURRENT-SCREEN grid as one &lt;div class="line"&gt; per row.</summary>
public sealed class BufferHtmlResponse
{
    public string GridHtml { get; set; } = "";
}
