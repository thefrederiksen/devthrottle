using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Secrets;
using CcDirector.Core.Utilities;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace CcDirector.Gateway.Api;

/// <summary>
/// The per-work-item title + status surface the Cockpit Lists view reads (issue #275, moved behind
/// the Gateway for the React rebuild, issue #970). The Blazor Cockpit fetched this from GitHub
/// itself with a bearer token held on the Cockpit server. The React Cockpit is a browser
/// single-page application that must hold NO secret, so the resolve moves onto the Gateway: the
/// browser calls this endpoint same-origin (root-relative) and the GitHub token never leaves the
/// Gateway host.
///
///   GET /gateway/lists/item-status?source={source}&amp;id={id}
///        -> { title: string|null, status: "queued"|"running"|"done"|"needs-human"|"failed"|"unknown", detail: string|null }
///
/// Status is DERIVED from the item's <c>flow:*</c> label (never stored), so the badge always follows
/// the label. A non-github source resolves to "queued" without a network call. An unreachable GitHub
/// or a missing token resolves to "unknown" with a human-readable detail (the no-fallback rule: the
/// row shows the failure explicitly rather than a wrong "queued").
/// </summary>
internal static class ItemStatusEndpoint
{
    public static void Map(IEndpointRouteBuilder app, GitHubItemStatusResolver resolver)
    {
        app.MapGet("/gateway/lists/item-status", async (string? source, string? id, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(id))
                return Results.BadRequest(new { error = "source and id query parameters are required" });

            var info = await resolver.ResolveAsync(source, id, ct);
            return Results.Json(new
            {
                title = info.Title,
                status = ToWire(info.Status),
                detail = info.Detail,
            });
        });
    }

    /// <summary>
    /// Stable lowercase wire name for each status. The browser maps these to its badge labels, so the
    /// contract is these exact strings (kept in step with the client-core WorkItemStatus type).
    /// </summary>
    internal static string ToWire(GatewayWorkItemStatus status) => status switch
    {
        GatewayWorkItemStatus.Queued => "queued",
        GatewayWorkItemStatus.Running => "running",
        GatewayWorkItemStatus.Done => "done",
        GatewayWorkItemStatus.NeedsHuman => "needs-human",
        GatewayWorkItemStatus.Failed => "failed",
        _ => "unknown",
    };
}

/// <summary>
/// The per-item status badge, derived live from a github item's <c>flow:*</c> label. Mirrors the
/// Cockpit's WorkItemStatus (issue #275); duplicated here because the Gateway does not reference the
/// Cockpit project - the Gateway now owns the resolve.
/// </summary>
internal enum GatewayWorkItemStatus
{
    Queued,
    Running,
    Done,
    NeedsHuman,
    Failed,
    Unknown,
}

/// <summary>The GitHub-derived view of one work-list item: its display title plus flow-derived status.</summary>
internal sealed record WorkItemInfo(string? Title, GatewayWorkItemStatus Status, string? Detail);

/// <summary>
/// Resolves a work-list item's title + status from GitHub, with the token held on the Gateway (issue
/// #970). Ported from the Cockpit's GitHubItemStatusClient. The HttpClient and the token provider are
/// injected so the resolve can be exercised in tests without touching the real credentials file or
/// api.github.com; <see cref="CreateDefault"/> wires the production surface.
/// </summary>
internal sealed class GitHubItemStatusResolver
{
    private const string TokenEntry = "github-token";

    private readonly HttpClient _http;
    private readonly Func<(string? token, string? error)> _tokenProvider;
    private readonly string _owner;
    private readonly string _repo;

    public GitHubItemStatusResolver(
        HttpClient http, Func<(string? token, string? error)> tokenProvider, string owner, string repo)
    {
        _http = http;
        _tokenProvider = tokenProvider;
        _owner = owner;
        _repo = repo;
    }

    /// <summary>
    /// Production wiring: a real HttpClient against api.github.com, the token read from the shared
    /// credentials file at point of use, and the canonical repo (overridable for private forks via
    /// DEVTHROTTLE_GITHUB_OWNER / DEVTHROTTLE_GITHUB_REPO).
    /// </summary>
    public static GitHubItemStatusResolver CreateDefault()
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri("https://api.github.com/"),
            Timeout = TimeSpan.FromSeconds(10),
        };
        return new GitHubItemStatusResolver(
            http,
            ReadStoreToken,
            ReadRepositorySetting("DEVTHROTTLE_GITHUB_OWNER", "devthrottle"),
            ReadRepositorySetting("DEVTHROTTLE_GITHUB_REPO", "devthrottle"));
    }

    /// <summary>
    /// Resolve title + status for one item ref. A non-github source resolves to Queued (no call). A
    /// github item is fetched and mapped from its flow label. Never throws for an unreachable GitHub
    /// or a missing token: it returns Unknown + a human-readable detail so the list still renders and
    /// the failure is shown explicitly rather than masked as a wrong "queued".
    /// </summary>
    public async Task<WorkItemInfo> ResolveAsync(string source, string id, CancellationToken ct = default)
    {
        if (!string.Equals(source, "github", StringComparison.OrdinalIgnoreCase))
            return new WorkItemInfo(Title: null, Status: GatewayWorkItemStatus.Queued, Detail: $"{source} item (not runnable in v1)");

        var (token, tokenError) = _tokenProvider();
        if (token is null)
        {
            FileLog.Write($"[GitHubItemStatusResolver] github id={id} - no token: {tokenError}");
            return new WorkItemInfo(Title: null, Status: GatewayWorkItemStatus.Unknown, Detail: tokenError);
        }

        try
        {
            return await FetchAsync(id, token, ct);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            FileLog.Write($"[GitHubItemStatusResolver] github id={id} transport failure: {ex.Message}");
            return new WorkItemInfo(Title: null, Status: GatewayWorkItemStatus.Unknown, Detail: $"GitHub unreachable: {ex.Message}");
        }
    }

    private async Task<WorkItemInfo> FetchAsync(string id, string token, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"repos/{_owner}/{_repo}/issues/{Uri.EscapeDataString(id)}");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        req.Headers.UserAgent.ParseAdd("devthrottle-gateway");
        req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var resp = await _http.SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            return new WorkItemInfo(Title: null, Status: GatewayWorkItemStatus.Unknown, Detail: $"GitHub issue #{id} not found in {_owner}/{_repo}");
        if (!resp.IsSuccessStatusCode)
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            return new WorkItemInfo(Title: null, Status: GatewayWorkItemStatus.Unknown,
                Detail: $"GitHub returned {(int)resp.StatusCode} for #{id}: {Truncate(body, 120)}");
        }

        var stream = await resp.Content.ReadAsStreamAsync(ct);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;

        var labels = new List<string>();
        if (root.TryGetProperty("labels", out var labelsEl) && labelsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var label in labelsEl.EnumerateArray())
            {
                if (label.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                {
                    var name = n.GetString();
                    if (!string.IsNullOrEmpty(name)) labels.Add(name);
                }
            }
        }

        return new WorkItemInfo(title, MapStatus(labels), Detail: null);
    }

    /// <summary>
    /// Map an issue's flow:* labels to a status badge. When several flow labels are present the
    /// terminal/escalation states win over the in-flight ones, which win over queued.
    /// </summary>
    internal static GatewayWorkItemStatus MapStatus(IReadOnlyCollection<string> labels)
    {
        bool Has(string label) => labels.Any(l => string.Equals(l, label, StringComparison.OrdinalIgnoreCase));

        if (Has("flow:done")) return GatewayWorkItemStatus.Done;
        if (Has("flow:needs-human")) return GatewayWorkItemStatus.NeedsHuman;
        if (Has("flow:failed")) return GatewayWorkItemStatus.Failed;
        // The loop is still on the item while it is in QA (qa-failed is a transient mid-loop bounce).
        if (Has("flow:ready-qa") || Has("flow:qa-failed")) return GatewayWorkItemStatus.Running;
        // No flow label, or flow:ready-dev / flow:in-progress / flow:rejected: not yet drained.
        return GatewayWorkItemStatus.Queued;
    }

    /// <summary>
    /// Read the GitHub token from the cc-secrets store at point of use (so the secret only enters the process
    /// when a github item is actually resolved). Returns a null token + a fixable message when the store or the
    /// entry is absent - no silent fallback to an empty token.
    /// </summary>
    private static (string? token, string? error) ReadStoreToken()
    {
        try
        {
            return (SecretStoreReader.ReadSecret(TokenEntry), null);
        }
        catch (SecretNotAvailableException ex)
        {
            return (null, $"GitHub token not available ({ex.Message}); per-item GitHub status unavailable.");
        }
    }

    private static string ReadRepositorySetting(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }

    private static string Truncate(string s, int max)
        => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "...";
}
