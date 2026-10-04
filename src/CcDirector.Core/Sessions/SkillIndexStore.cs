using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Sessions;

/// <summary>One cached skill-index value: the rendered index text and when it was cached.</summary>
public sealed record SkillIndexCacheEntry(
    [property: JsonPropertyName("index")] string Index,
    [property: JsonPropertyName("cachedAtUtc")] DateTime CachedAtUtc);

/// <summary>
/// The Director-side reader of the Gateway's SKILL REGISTER (the central skill library,
/// devthrottle_internal issue 995). Skills are fleet-wide capabilities held on the Gateway; the index
/// is the few-line discoverability block that rides the fleet preamble (the <c>[SKILL_INDEX]</c>
/// placeholder) - one line per published skill, so every session knows what the fleet can do and how
/// to fetch a skill's instructions, without any session paying for the bodies it never uses.
///
/// THIS IS WHAT REPLACES INSTALLING SKILL FILES ON EVERY MACHINE. Nothing is written to the machine:
/// the briefing names what exists and the agent fetches the one it needs. It is also why the library
/// reaches EVERY agent family - the preamble is delivered through the Claude hook, the Control API
/// preamble endpoint, and Pi's preamble file, so no family is left with nothing the way the
/// installer's Claude-only skill copies left them.
///
/// Structurally a sibling of <see cref="WorkflowIndexStore"/>, split the same way so a synchronous
/// launch never blocks on the network:
///   - <see cref="RefreshAsync"/> fetches <c>GET /gateway/skills</c> off the launch path, renders the
///     index text once, and writes the on-disk cache.
///   - <see cref="ActiveIndex"/> reads that cache synchronously at session launch.
///
/// Degraded behaviour: the last-known cache; when nothing has ever been cached, the EMPTY string - a
/// fresh Director that has never reached the Gateway injects no index, and an agent can still reach
/// the library with <c>cc-devthrottle skill list</c>.
/// </summary>
public sealed class SkillIndexStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(10) };

    /// <summary>The Gateway path the register is read from, appended to <c>gateway.url</c>.</summary>
    public const string GatewayPath = "/gateway/skills";

    /// <summary>One summary line per skill; a longer summary is cut so the whole index stays a
    /// few-line block, not a page.</summary>
    public const int MaxSummaryChars = 120;

    /// <summary>Rendered skill ids are cut at this length - an id is a slug, and a runaway one must
    /// not inflate every session's preamble.</summary>
    public const int MaxIdChars = 64;

    /// <summary>At most this many skills render; beyond it the index says how many more exist. The
    /// index is discoverability, not the register - the command line lists everything.</summary>
    public const int MaxIndexEntries = 40;

    /// <summary>
    /// A cache older than this injects NOTHING. The index is authored data reaching every session's
    /// context, so a skill switched off on the Gateway must not keep riding a Director whose
    /// refreshes have been failing for days. Losing the index costs only discoverability - agents can
    /// still pull the register with the command line - so suppressing a day-stale cache is cheap.
    /// </summary>
    public static readonly TimeSpan MaxCacheAge = TimeSpan.FromHours(24);

    private readonly string _cachePath;
    private readonly HttpClient _client;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;
    private readonly HeldGatewayAnswers _held;

    /// <summary>The store over the real Director cache file and the real <c>gateway.url</c>.</summary>
    public SkillIndexStore() : this(null) { }

    /// <summary>Creates the store; parameters mirror <see cref="WorkflowIndexStore"/> (tests inject a
    /// temporary cache path, a stub client, a hermetic gateway url + token, and their own held answers).</summary>
    public SkillIndexStore(string? cachePath = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null,
        HeldGatewayAnswers? held = null)
    {
        _held = held ?? HeldGatewayAnswers.Shared;
        _cachePath = string.IsNullOrWhiteSpace(cachePath) ? CcStorage.SkillIndexCache() : cachePath;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    /// <summary>
    /// The index text that will ride the next session's preamble, read synchronously from the
    /// last-known cache. Empty when nothing has ever been cached - and empty again when the cache is
    /// older than <see cref="MaxCacheAge"/>. Never a network call.
    /// </summary>
    public string ActiveIndex()
    {
        var cached = ReadCache();
        if (cached is null)
            return "";
        if (DateTime.UtcNow - cached.CachedAtUtc > MaxCacheAge)
        {
            FileLog.Write($"[SkillIndexStore] ActiveIndex: cache from {cached.CachedAtUtc:o} is older " +
                          "than the staleness ceiling -> injecting no index until a refresh succeeds");
            return "";
        }
        return cached.Index;
    }

    /// <summary>
    /// Refresh the Director's last-known skill index from the Gateway: fetch the published register,
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
            FileLog.Write("[SkillIndexStore] RefreshAsync: no gateway.url configured -> keeping the last-known cache");
            return;
        }

        var endpoint = $"{gatewayUrl.TrimEnd('/')}{GatewayPath}";
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        FileLog.Write($"[SkillIndexStore] RefreshAsync: GET {endpoint}");
        var answer = await _held.SendAsync(_client, request, ct).ConfigureAwait(false);
        answer.EnsureSuccess(endpoint);
        var payload = JsonSerializer.Deserialize<RegisterResponse>(answer.Body, JsonOpts);
        if (payload is null)
            throw new InvalidOperationException($"Gateway returned an empty body from {endpoint}");

        var index = BuildIndexText(payload.Skills ?? new List<RegisterSkill>());
        WriteCache(new SkillIndexCacheEntry(index, DateTime.UtcNow));
        FileLog.Write($"[SkillIndexStore] RefreshAsync: {payload.Skills?.Count ?? 0} skill(s) indexed (cached)");
    }

    /// <summary>
    /// Render the register into the index block. Pure and static so the format is pinned by test.
    /// Empty register renders the empty string (no header floating over nothing). ASCII only.
    ///
    /// The block states the ONE thing an agent must understand: these lines are all it gets for free,
    /// and the body is read only at the moment of use. It also says plainly that the library is an
    /// ADDITIONAL source and the agent's own local skills still work and win - a central library that
    /// reads as if it replaced a machine's own skills would be a lie.
    ///
    /// The footer says these skills are WRITTEN TO DISK where the agent looks and refreshed from the
    /// Gateway. It used to say nothing was installed on this machine, which stopped being true the day
    /// the Director began materializing skill directories - and this sentence is injected into every
    /// agent in every session, so an inaccuracy here is one the whole fleet is told. The freshness half
    /// stays because it is the point: a file on disk that goes stale is exactly what a central library
    /// exists to prevent.
    /// </summary>
    public static string BuildIndexText(IReadOnlyList<RegisterSkill> skills)
    {
        var available = skills.Where(s => s.Enabled != false).ToList();
        if (available.Count == 0)
            return "";

        var text = new StringBuilder();
        text.Append("[Skills] Capabilities this fleet holds centrally, usable by ANY agent. The lines below are all\n");
        text.Append("you get for free - fetch a skill IN FULL only when you are about to use it, and follow what it\n");
        text.Append("says:  cc-devthrottle skill get <id>\n");
        var rendered = 0;
        foreach (var skill in available)
        {
            if (rendered == MaxIndexEntries)
            {
                text.Append($"  ...and {available.Count - MaxIndexEntries} more - list them all with: cc-devthrottle skill list\n");
                break;
            }
            // ONE line per skill is the index's structural promise, and the line is PRINTABLE text
            // only. Summaries and ids are authored data, so whitespace runs (newlines that could dress
            // a summary up as extra preamble lines) collapse to a single space, other control
            // characters (including ANSI escapes) are stripped outright, and both pieces are
            // length-capped so no author can inflate every session's preamble.
            var summary = Sanitize(skill.Summary ?? "", MaxSummaryChars);
            var id = Sanitize(skill.Id, MaxIdChars);
            text.Append($"  - {id}: {summary}\n");
            rendered++;
        }
        text.Append("  These are also written to disk where you look for skills, and refreshed from the Gateway, so\n");
        text.Append("  they are always current. Your own local skills still work and take precedence. Add or improve\n");
        text.Append("  one: cc-devthrottle skill pull / push / publish (drafts are private; publish is fleet-wide,\n");
        text.Append("  instantly)\n");
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
    /// path, so a broken cache degrades to the documented "no index" state instead of turning session
    /// launches into failures. The next successful refresh rewrites the file and heals it.
    /// </summary>
    public SkillIndexCacheEntry? ReadCache()
    {
        try
        {
            if (!File.Exists(_cachePath))
                return null;

            var json = File.ReadAllText(_cachePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            return JsonSerializer.Deserialize<SkillIndexCacheEntry>(json, JsonOpts);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[SkillIndexStore] ReadCache FAILED ({_cachePath}); injecting no index " +
                          $"until a refresh rewrites it: {ex.Message}");
            return null;
        }
    }

    /// <summary>Write the cache ATOMICALLY (temp file + move): a session launch reading concurrently
    /// sees the old complete file or the new complete file, never a truncated one.</summary>
    public void WriteCache(SkillIndexCacheEntry entry)
    {
        var dir = Path.GetDirectoryName(_cachePath)
            ?? throw new InvalidOperationException($"Cannot determine directory for path: {_cachePath}");
        Directory.CreateDirectory(dir);
        var temp = _cachePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(entry, JsonOpts));
        File.Move(temp, _cachePath, overwrite: true);
    }

    /// <summary>The slice of <c>GET /gateway/skills</c> the index needs. Deliberately three fields:
    /// anything more would be paid for by every session on every machine.</summary>
    public sealed record RegisterSkill(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("summary")] string? Summary,
        // Null (an older Gateway that omits the field) means available - only an explicit false, the
        // owner's own switch, removes a skill from every session's briefing.
        [property: JsonPropertyName("enabled")] bool? Enabled = null);

    private sealed record RegisterResponse(
        [property: JsonPropertyName("skills")] List<RegisterSkill>? Skills);
}
