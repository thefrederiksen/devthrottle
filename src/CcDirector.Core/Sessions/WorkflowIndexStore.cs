using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>One cached workflow-index value: the rendered index text and when it was cached.</summary>
public sealed record WorkflowIndexCacheEntry(
    [property: JsonPropertyName("index")] string Index,
    [property: JsonPropertyName("cachedAtUtc")] DateTime CachedAtUtc);

/// <summary>
/// The Director-side reader of the Gateway's workflow CATALOG INDEX (Workflows mission, phase 5).
/// Workflows are fleet-wide, Gateway-stored units of conduct; the index is the few-line discoverability
/// block that rides the fleet preamble (the <c>[WORKFLOW_INDEX]</c> placeholder) - one line per
/// published workflow, so every session knows the catalog exists and how to fetch a workflow's
/// conduct, without any session paying the cost of the conduct bodies it never uses.
///
/// Structurally a sibling of <see cref="InjectedTextStore"/>, split the same way so a synchronous
/// launch never blocks on the network:
///   - <see cref="RefreshAsync"/> fetches <c>GET /gateway/workflows</c> off the launch path (the host
///     calls it on Gateway-connect and on the same refresh timer as the injected text), renders the
///     index text once, and writes the on-disk cache.
///   - <see cref="ActiveIndex"/> reads that cache synchronously at session launch.
///
/// Degraded behaviour: the last-known cache; when nothing has ever been cached, the EMPTY string -
/// a fresh Director that has never reached the Gateway simply injects no index (the agent can still
/// discover workflows through <c>cc-devthrottle actions</c>), which is exactly what sessions got
/// before this feature existed.
/// </summary>
public sealed class WorkflowIndexStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Gateway path the catalog is read from, appended to <c>gateway.url</c>.</summary>
    public const string GatewayPath = "/gateway/workflows";

    /// <summary>One summary line per workflow; a longer summary is cut so the whole index stays a
    /// few-line block, not a page.</summary>
    public const int MaxSummaryChars = 160;

    /// <summary>Rendered workflow ids are cut at this length - an id is a slug, and a runaway one
    /// must not inflate every session's preamble.</summary>
    public const int MaxIdChars = 64;

    /// <summary>At most this many workflows render; beyond it the index says how many more exist.
    /// The index is discoverability, not the catalog - the CLI lists everything.</summary>
    public const int MaxIndexEntries = 50;

    /// <summary>
    /// A cache older than this injects NOTHING. The index is authored data reaching every session's
    /// context, so a workflow unpublished on the Gateway (perhaps for bad content) must not keep
    /// riding a Director whose refreshes have been failing for days. Losing the index costs only
    /// discoverability - agents can still pull the catalog with the CLI - so suppressing a day-stale
    /// cache is cheap; the refresh timer keeps a healthy Director far inside this window.
    /// </summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);

    private readonly string _cachePath;
    private readonly HttpClient _client;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;
    private readonly HeldGatewayAnswers _held;

    /// <summary>The store over the real Director cache file and the real <c>gateway.url</c>.</summary>
    public WorkflowIndexStore() : this(null) { }

    /// <summary>Creates the store; parameters mirror <see cref="InjectedTextStore"/> (tests inject a
    /// temporary cache path, a stub client, a hermetic gateway url + token, and their own held answers).</summary>
    public WorkflowIndexStore(string? cachePath = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null,
        HeldGatewayAnswers? held = null)
    {
        _held = held ?? HeldGatewayAnswers.Shared;
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? CcStorage.WorkflowIndexCache() : cachePath;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    /// <summary>
    /// The index text that will ride the next session's preamble, read synchronously from the
    /// last-known cache. Empty when nothing has ever been cached - and empty again when the cache is
    /// older than <see cref="MaxCacheAge"/>, so content revoked on the Gateway cannot keep riding a
    /// Director whose refreshes are failing. Never a network call.
    /// </summary>
    public string ActiveIndex()
    {
        var cached = ReadCache();
        if (cached is null)
            return "";
        if (DateTime.UtcNow - cached.CachedAtUtc > MaxCacheAge)
        {
            FileLog.Write($"[WorkflowIndexStore] ActiveIndex: cache from {cached.CachedAtUtc:o} is older " +
                          "than the staleness ceiling -> injecting no index until a refresh succeeds");
            return "";
        }
        return cached.Index;
    }

    /// <summary>
    /// Refresh the Director's last-known workflow index from the Gateway: fetch the published catalog,
    /// render the index block once, and write it to the on-disk cache. The ASYNC, network path - the
    /// host calls it off the launch path. When no Gateway is configured this is a logged no-op that
    /// keeps the current cache.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        string? gatewayUrl;
        string? token;
        if (_gatewayUrlOverride is not null)
        {
            gatewayUrl = _gatewayUrlOverride.Trim();
            token = _tokenOverride;
        }
        else
        {
            var config = GatewayConfig.Load();
            gatewayUrl = config.Url?.Trim();
            token = config.Token;
        }

        if (string.IsNullOrWhiteSpace(gatewayUrl))
        {
            FileLog.Write("[WorkflowIndexStore] RefreshAsync: no gateway.url configured -> keeping the last-known cache");
            return;
        }

        var endpoint = $"{gatewayUrl.TrimEnd('/')}{GatewayPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FileLog.Write($"[WorkflowIndexStore] RefreshAsync: GET {endpoint}");
        var answer = await _held.SendAsync(_client, request, ct).ConfigureAwait(false);
        answer.EnsureSuccess(endpoint);
        var payload = JsonSerializer.Deserialize<CatalogResponse>(answer.Body, JsonOpts);
        if (payload is null)
            throw new InvalidOperationException($"Gateway returned an empty body from {endpoint}");

        var index = BuildIndexText(payload.Workflows ?? new List<CatalogWorkflow>());
        WriteCache(new WorkflowIndexCacheEntry(index, DateTime.UtcNow));
        FileLog.Write($"[WorkflowIndexStore] RefreshAsync: {payload.Workflows?.Count ?? 0} workflow(s) indexed (cached)");
    }

    /// <summary>
    /// Render the catalog into the index block. Pure and static so the format is pinned by test.
    /// Empty catalog renders the empty string (no header floating over nothing). ASCII only.
    /// </summary>
    public static string BuildIndexText(IReadOnlyList<CatalogWorkflow> workflows)
    {
        var inForce = workflows.Where(w => w.Enabled != false).ToList();
        if (inForce.Count == 0)
            return "";

        var text = new StringBuilder();
        text.Append("[Workflows] Named ways of working this fleet defines - usable by ANY agent. Before taking\n");
        text.Append("on work that matches one, fetch its conduct and FOLLOW it:  cc-devthrottle workflow instructions <id>\n");
        var rendered = 0;
        foreach (var workflow in inForce)
        {
            if (rendered == MaxIndexEntries)
            {
                text.Append($"  ...and {inForce.Count - MaxIndexEntries} more - list them all with: cc-devthrottle workflow list\n");
                break;
            }
            // ONE line per workflow is the index's structural promise, and the line is PRINTABLE text
            // only. Summaries and ids are authored data, so whitespace runs (newlines that could
            // dress a summary up as extra preamble lines) collapse to a single space, other control
            // characters (including ANSI escapes) are stripped outright, and both pieces are
            // length-capped so no author can inflate every session's preamble.
            var summary = Sanitize(workflow.Summary ?? "", MaxSummaryChars);
            var id = Sanitize(workflow.Id, MaxIdChars);
            text.Append($"  - {id}: {summary}\n");
            rendered++;
        }
        // The maintenance invitation (owner ruling 2026-07-18): agents are told they may IMPROVE
        // these, not just obey them - an agent that finds a workflow wrong fixes the workflow
        // instead of silently working around it. One fixed line, rendered only under a non-empty
        // index so an empty catalog still injects nothing.
        text.Append("  Improve one, or add a new one: cc-devthrottle workflow pull / push / publish (drafts are private; publish is fleet-wide, instantly)\n");
        return text.ToString().TrimEnd('\n');
    }

    /// <summary>Collapse whitespace runs to one space, strip non-printable characters, cap length.</summary>
    private static string Sanitize(string value, int maxChars)
    {
        var printable = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsWhiteSpace(ch))
                printable.Append(' ');
            else if (!char.IsControl(ch))
                printable.Append(ch);
        }
        var collapsed = string.Join(' ',
            printable.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length > maxChars)
            collapsed = collapsed[..maxChars].TrimEnd() + "...";
        return collapsed;
    }

    /// <summary>
    /// The last-known cached value on disk, or null when nothing has been cached yet - and null,
    /// LOUDLY logged, when the file is unreadable or corrupt. This sits on every session's launch
    /// path, so a broken cache degrades to the documented "no index" state (agents still reach the
    /// catalog through the CLI) instead of turning session launches into 500s. The next successful
    /// refresh rewrites the file and heals it.
    /// </summary>
    public WorkflowIndexCacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = File.ReadAllText(_cachePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<WorkflowIndexCacheEntry>(json, JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[WorkflowIndexStore] ReadCache FAILED ({_cachePath}); injecting no index " +
                          $"until a refresh rewrites it: {ex.Message}");
            return null;
        }
    }

    /// <summary>Write the cache ATOMICALLY (temp file + move): a session launch reading concurrently
    /// sees the old complete file or the new complete file, never a truncated one. Used by
    /// <see cref="RefreshAsync"/> and by tests to seed a state.</summary>
    public void WriteCache(WorkflowIndexCacheEntry entry)
    {
        var dir = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_cachePath}");
        Directory.CreateDirectory(dir);
        var temp = _cachePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOpts));
        File.Move(temp, _cachePath, overwrite: true);
    }

    /// <summary>The slice of <c>GET /gateway/workflows</c> the index needs.</summary>
    public sealed record CatalogWorkflow(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("summary")] string? Summary,
        // Null (an older Gateway that omits the field) means enabled - only an explicit false, the
        // owner's own switch, removes a workflow from every session's briefing.
        [property: JsonPropertyName("enabled")] bool? Enabled = null);

    private sealed record CatalogResponse(
        [property: JsonPropertyName("workflows")] List<CatalogWorkflow>? Workflows);
}
