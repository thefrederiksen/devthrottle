using System.Net.Http.Headers;
using System.Net.Http.Json;

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
    /// "file"), which saves it and hands the path to Claude Code. The browser streams the
    /// bytes to the Cockpit server (Blazor InputFile); the Cockpit forwards them here, so the
    /// Director never needs CORS for a browser cross-origin upload.
    /// </summary>
    public async Task UploadImageAsync(string directorBase, string sid, Stream content, string fileName, string contentType, CancellationToken ct = default)
    {
        _log.LogDebug("UploadImage sid={Sid} file={File}", sid, fileName);
        using var form = new MultipartFormDataContent();
        var fileContent = new StreamContent(content);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        form.Add(fileContent, "file", fileName);
        var resp = await _http.PostAsync(Url(directorBase, $"sessions/{sid}/upload-image"), form, ct);
        resp.EnsureSuccessStatusCode();
    }
}
