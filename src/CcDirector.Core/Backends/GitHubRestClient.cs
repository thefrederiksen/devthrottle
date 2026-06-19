using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Raised when the GitHub REST API returns a non-success status. Carries the
/// HTTP status so the backend can surface an explicit, fixable message instead
/// of swallowing the failure.
/// </summary>
public sealed class GitHubApiException : Exception
{
    public HttpStatusCode StatusCode { get; }
    public GitHubApiException(HttpStatusCode status, string message) : base(message) => StatusCode = status;
}

/// <summary>
/// Real <see cref="IGitHubClient"/> over the GitHub REST API using a bearer token.
/// One HttpClient per session is fine - the backend owns and disposes it.
/// </summary>
public sealed class GitHubRestClient : IGitHubClient, IDisposable
{
    private const string ApiBase = "https://api.github.com";
    private readonly HttpClient _http;
    private bool _disposed;

    public GitHubRestClient(string token, HttpMessageHandler? handler = null)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new ArgumentException("GitHub token is required.", nameof(token));

        _http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _http.BaseAddress = new Uri(ApiBase);
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("cc-director");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    public async Task<GhIssue> CreateIssueAsync(string owner, string repo, string title, string body, CancellationToken ct)
    {
        using var doc = await PostJsonAsync($"/repos/{owner}/{repo}/issues",
            new { title, body }, ct);
        var root = doc.RootElement;
        return new GhIssue(root.GetProperty("number").GetInt64(), root.GetProperty("html_url").GetString() ?? "");
    }

    public async Task<string> UploadFileAsync(string owner, string repo, string branch, string path, byte[] content, string commitMessage, CancellationToken ct)
    {
        await EnsureBranchAsync(owner, repo, branch, ct);

        var payload = new
        {
            message = commitMessage,
            content = Convert.ToBase64String(content),
            branch,
        };
        using var doc = await PutJsonAsync($"/repos/{owner}/{repo}/contents/{EncodePath(path)}", payload, ct);
        var fileEl = doc.RootElement.GetProperty("content");
        var downloadUrl = fileEl.GetProperty("download_url").GetString();
        if (string.IsNullOrEmpty(downloadUrl))
            throw new InvalidOperationException($"GitHub Contents API did not return a download_url for {path}.");
        return downloadUrl;
    }

    /// <summary>
    /// Ensure <paramref name="branch"/> exists, creating it from the repository's
    /// default branch when it does not. No-op when the branch is already present.
    /// </summary>
    private async Task EnsureBranchAsync(string owner, string repo, string branch, CancellationToken ct)
    {
        if (await RefExistsAsync(owner, repo, $"heads/{branch}", ct))
            return;

        var defaultBranch = await GetDefaultBranchAsync(owner, repo, ct);
        var sha = await GetRefShaAsync(owner, repo, $"heads/{defaultBranch}", ct);

        using var content = JsonContent.Create(new { @ref = $"refs/heads/{branch}", sha });
        using var resp = await _http.PostAsync($"/repos/{owner}/{repo}/git/refs", content, ct);
        await EnsureSuccessAsync(resp, ct);
        FileLog.Write($"[GitHubRestClient] Created branch {branch} from {defaultBranch} in {owner}/{repo}");
    }

    private async Task<bool> RefExistsAsync(string owner, string repo, string gitRef, CancellationToken ct)
    {
        using var resp = await _http.GetAsync($"/repos/{owner}/{repo}/git/ref/{gitRef}", ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return false;
        await EnsureSuccessAsync(resp, ct);
        return true;
    }

    private async Task<string> GetDefaultBranchAsync(string owner, string repo, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/repos/{owner}/{repo}", ct);
        return doc.RootElement.GetProperty("default_branch").GetString()
            ?? throw new InvalidOperationException($"GitHub did not report a default_branch for {owner}/{repo}.");
    }

    private async Task<string> GetRefShaAsync(string owner, string repo, string gitRef, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/repos/{owner}/{repo}/git/ref/{gitRef}", ct);
        return doc.RootElement.GetProperty("object").GetProperty("sha").GetString()
            ?? throw new InvalidOperationException($"GitHub ref {gitRef} has no object sha in {owner}/{repo}.");
    }

    // Encode each path segment but keep the slashes that separate them, so a path
    // like "feedback/screenshots/x.png" maps to a valid Contents API URL.
    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    public async Task<GhComment> PostCommentAsync(string owner, string repo, long issueNumber, string body, CancellationToken ct)
    {
        using var doc = await PostJsonAsync($"/repos/{owner}/{repo}/issues/{issueNumber}/comments",
            new { body }, ct);
        return ParseComment(doc.RootElement);
    }

    public async Task<IReadOnlyList<GhComment>> ListCommentsAsync(string owner, string repo, long issueNumber, DateTimeOffset? sinceUtc, CancellationToken ct)
    {
        var url = $"/repos/{owner}/{repo}/issues/{issueNumber}/comments?per_page=100";
        if (sinceUtc is { } since)
            url += $"&since={Uri.EscapeDataString(since.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"))}";

        using var doc = await GetJsonAsync(url, ct);
        var list = new List<GhComment>();
        foreach (var el in doc.RootElement.EnumerateArray())
            list.Add(ParseComment(el));
        return list;
    }

    public async Task<IReadOnlyList<GhRun>> ListRunsAsync(string owner, string repo, string eventName, DateTimeOffset createdAfterUtc, CancellationToken ct)
    {
        var createdFilter = Uri.EscapeDataString(">" + createdAfterUtc.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        var url = $"/repos/{owner}/{repo}/actions/runs?created={createdFilter}&per_page=20";
        if (!string.IsNullOrEmpty(eventName))
            url += $"&event={Uri.EscapeDataString(eventName)}";

        using var doc = await GetJsonAsync(url, ct);
        var list = new List<GhRun>();
        if (doc.RootElement.TryGetProperty("workflow_runs", out var runs))
            foreach (var el in runs.EnumerateArray())
                list.Add(ParseRun(el));
        return list;
    }

    public async Task<GhRun> GetRunAsync(string owner, string repo, long runId, CancellationToken ct)
    {
        using var doc = await GetJsonAsync($"/repos/{owner}/{repo}/actions/runs/{runId}", ct);
        return ParseRun(doc.RootElement);
    }

    public async Task CancelRunAsync(string owner, string repo, long runId, CancellationToken ct)
    {
        using var resp = await _http.PostAsync($"/repos/{owner}/{repo}/actions/runs/{runId}/cancel", content: null, ct);
        // 202 Accepted on success; 409 if the run already finished - treat the latter as a no-op.
        if (resp.StatusCode == HttpStatusCode.Conflict) return;
        await EnsureSuccessAsync(resp, ct);
    }

    public async Task DispatchWorkflowAsync(string owner, string repo, string workflowFile, string gitRef, IReadOnlyDictionary<string, string> inputs, CancellationToken ct)
    {
        using var content = JsonContent.Create(new { @ref = gitRef, inputs });
        using var resp = await _http.PostAsync($"/repos/{owner}/{repo}/actions/workflows/{workflowFile}/dispatches", content, ct);
        await EnsureSuccessAsync(resp, ct); // 204 No Content on success
    }

    private static GhComment ParseComment(JsonElement el) => new(
        el.GetProperty("id").GetInt64(),
        el.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
        el.GetProperty("html_url").GetString() ?? "",
        el.GetProperty("created_at").GetDateTimeOffset(),
        el.TryGetProperty("user", out var u) && u.TryGetProperty("login", out var l) ? l.GetString() ?? "" : "");

    private static GhRun ParseRun(JsonElement el) => new(
        el.GetProperty("id").GetInt64(),
        el.GetProperty("status").GetString() ?? "unknown",
        el.TryGetProperty("conclusion", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null,
        el.GetProperty("html_url").GetString() ?? "",
        el.GetProperty("created_at").GetDateTimeOffset(),
        el.TryGetProperty("display_title", out var d) ? d.GetString() ?? "" : "");

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(url, ct);
        await EnsureSuccessAsync(resp, ct);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<JsonDocument> PostJsonAsync(string url, object payload, CancellationToken ct)
    {
        using var content = JsonContent.Create(payload);
        using var resp = await _http.PostAsync(url, content, ct);
        await EnsureSuccessAsync(resp, ct);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private async Task<JsonDocument> PutJsonAsync(string url, object payload, CancellationToken ct)
    {
        using var content = JsonContent.Create(payload);
        using var resp = await _http.PutAsync(url, content, ct);
        await EnsureSuccessAsync(resp, ct);
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        if (resp.IsSuccessStatusCode) return;

        string detail;
        try { detail = await resp.Content.ReadAsStringAsync(ct); }
        catch { detail = string.Empty; }

        var hint = resp.StatusCode switch
        {
            HttpStatusCode.Unauthorized =>
                " GitHub rejected the token (401). Check GITHUB_TOKEN in credentials.env - it may be expired or invalid.",
            HttpStatusCode.Forbidden =>
                " GitHub returned 403. Either the token lacks repo/actions/issues scope, or you hit the rate limit (retry shortly).",
            HttpStatusCode.NotFound =>
                " GitHub returned 404. Check the owner/repo and that the token can see this repository.",
            _ => string.Empty
        };

        var message = $"GitHub API {(int)resp.StatusCode} {resp.StatusCode} for {resp.RequestMessage?.RequestUri}.{hint}";
        FileLog.Write($"[GitHubRestClient] {message} body={Truncate(detail, 500)}");
        throw new GitHubApiException(resp.StatusCode, message);
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
    }
}
