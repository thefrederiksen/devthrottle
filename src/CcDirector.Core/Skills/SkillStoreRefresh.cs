using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// Fills the Director's materialized skill store from the Gateway - the network half of installing
/// skills where each agent looks for them. Runs OFF the launch path, exactly like the skill index it
/// sits beside; <see cref="SkillDirectoryInstaller.InstallFor"/> is the synchronous half that reads
/// what this wrote.
///
/// The store mirrors what the Gateway currently SERVES. Only skills the register reports as ENABLED
/// are materialized, and a skill that disappears or is switched off is deleted from the store, so the
/// next session launch removes it from every agent's directory. That is what keeps a file on disk
/// from outliving the decision that withdrew it.
///
/// A failure leaves the previous store exactly as it was and says so. It never half-writes: each
/// skill directory is rebuilt whole, and a skill whose fetch fails keeps the copy that was already
/// there rather than being replaced by a partial one.
/// </summary>
public sealed class SkillStoreRefresh
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    private readonly HttpClient _client;
    private readonly string? _storeRootOverride;
    private readonly string? _gatewayUrlOverride;
    private readonly string? _tokenOverride;
    private readonly HeldGatewayAnswers _held;

    public SkillStoreRefresh() : this(null) { }

    /// <summary>Creates the refresher; the parameters exist so tests can point it at a temporary
    /// store, a hermetic Gateway, and their own held answers.</summary>
    public SkillStoreRefresh(
        string? storeRoot = null, HttpClient? client = null, string? gatewayUrl = null, string? token = null,
        HeldGatewayAnswers? held = null)
    {
        _held = held ?? HeldGatewayAnswers.Shared;
        _storeRootOverride = storeRoot;
        _client = client ?? SharedClient;
        _gatewayUrlOverride = gatewayUrl;
        _tokenOverride = token;
    }

    private string StoreRoot() => _storeRootOverride ?? SkillDirectoryInstaller.StoreRoot();

    /// <summary>Fetch every enabled skill and materialize it into the store. Returns how many skills
    /// the store now holds, or -1 when nothing was changed: no Gateway is configured, or the register
    /// could not be read.</summary>
    public async Task<int> RefreshAsync(CancellationToken ct = default)
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
            FileLog.Write("[SkillStoreRefresh] RefreshAsync: no gateway.url configured -> keeping the current store");
            return -1;
        }

        var baseUrl = gatewayUrl.TrimEnd('/');
        var register = await GetRegisterAsync(baseUrl + "/gateway/skills", token, ct).ConfigureAwait(false);
        if (register?.Skills is null)
        {
            // A register we could not read - or one with no skill list in it at all - says nothing about
            // which skills exist. Reconciling against it as if it were empty would delete every skill in the
            // store because one request failed. Only an explicit empty list means "no skills".
            FileLog.Write("[SkillStoreRefresh] RefreshAsync: the register could not be read -> keeping the current store");
            return -1;
        }
        var wanted = register.Skills
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Id))
            .ToList();

        var store = StoreRoot();
        Directory.CreateDirectory(store);

        // WHOSE library this is, exactly as the Gateway stated it for the key that fetched it. Every skill
        // materialized below records it with its bytes (review finding SK-F6), and the store records it as a whole
        // (SK-F2). Null when the Gateway does not say.
        var source = SourceNamedBy(register.Source);

        int unchanged = 0, refreshed = 0;
        foreach (var row in wanted)
        {
            // The register names the version the Gateway serves, and a version is immutable once
            // published: the same version with the same content hash IS the same bytes. When the
            // store already holds exactly that, there is nothing to download and nothing to rewrite.
            // Until 22 September 2026 every enabled skill was fetched and its directory deleted and
            // rewritten on every cycle, sixty times an hour, whether or not anything had changed.
            if (IsAlreadyMaterialized(store, row, source))
            {
                unchanged++;
                continue;
            }

            var detail = await GetAsync<VersionDetail>(
                $"{baseUrl}/gateway/skills/{Uri.EscapeDataString(row.Id)}/versions/{row.Version}", token, ct)
                .ConfigureAwait(false);
            if (detail is null)
            {
                // Leave whatever is already materialized for this skill in place: a skill we could not
                // read is not the same as a skill that was withdrawn, and replacing it with nothing
                // would quietly remove a working capability because one request failed.
                //
                // UNLESS it was fetched for ANOTHER library (review finding SK-F6). A Director that moves from a
                // team to the person's own account, or between Gateways, still holds the old library's bytes under
                // the same names. Kept, they would be placed and stamped as the new library's - the team's skill
                // wearing the person's ownership. Those bytes are not this library's to keep, so they leave the
                // store; the skill arrives on the next cycle that can read it.
                DropIfFetchedForAnotherLibrary(store, row.Id, row.Version, source);
                continue;
            }

            SkillDirectoryInstaller.Materialize(store, ToBundle(row.Id, detail), source);
            refreshed++;
        }

        // Anything in the store the register no longer serves is gone: switched off, archived, or
        // never ours. Reconcile, never add.
        var keep = new HashSet<string>(wanted.Select(s => s.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var directory in Directory.GetDirectories(store))
        {
            var name = Path.GetFileName(directory);
            if (!keep.Contains(name))
            {
                Directory.Delete(directory, recursive: true);
                FileLog.Write($"[SkillStoreRefresh] dropped '{name}' from the store - the Gateway no longer serves it");
            }
        }

        // The library's source, recorded WITH the store, so placement stamps the identity that fetched the library
        // and never a separate local record that can be missing (devthrottle_internal#2311, review finding SK-F2).
        // A Gateway that does not state it leaves NO record, and placement then changes nothing - it never guesses
        // an owner.
        if (source is not null)
        {
            source.WriteTo(store);
        }
        else
        {
            SkillSource.ClearAt(store);
            FileLog.Write("[SkillStoreRefresh] RefreshAsync: the register does not say which account these skills " +
                          "belong to (a Gateway older than the source stamp) - no source recorded, so placement will " +
                          "change nothing until the Gateway is updated");
        }

        FileLog.Write($"[SkillStoreRefresh] RefreshAsync: store now holds {wanted.Count} skills " +
                      $"({unchanged} already at the served version, {refreshed} downloaded)");
        return wanted.Count;
    }

    /// <summary>The source the register named, or null when it names none in full.</summary>
    private static SkillSource? SourceNamedBy(RegisterSource? named) =>
        named is not null && !string.IsNullOrWhiteSpace(named.GatewayId) && !string.IsNullOrWhiteSpace(named.TenantId)
            ? new SkillSource(named.GatewayId.Trim(), named.TenantId.Trim(),
                string.IsNullOrWhiteSpace(named.TeamId) ? null : named.TeamId.Trim())
            : null;

    /// <summary>After a failed read of <paramref name="id"/>: keep what the store holds for it when those bytes were
    /// fetched for <paramref name="source"/>, and remove them when they were fetched for any other library.</summary>
    private static void DropIfFetchedForAnotherLibrary(string store, string id, int version, SkillSource? source)
    {
        var directory = Path.Combine(store, id);
        if (!Directory.Exists(directory))
        {
            FileLog.Write($"[SkillStoreRefresh] could not read '{id}' v{version} - nothing stored for it yet; it arrives on a later cycle");
            return;
        }
        var recorded = SkillSource.RecordedIn(directory);
        if (source is null ? recorded is null : source.Is(recorded))
        {
            FileLog.Write($"[SkillStoreRefresh] could not read '{id}' v{version} - keeping what is already stored");
            return;
        }
        Directory.Delete(directory, recursive: true);
        FileLog.Write($"[SkillStoreRefresh] could not read '{id}' v{version}, and what the store held for it was fetched for " +
                      $"{recorded?.Describe() ?? "no recorded library"}, not for {source?.Describe() ?? "an unnamed library"} - " +
                      "dropped from the store rather than placed as this library's; it arrives on a later cycle");
    }

    /// <summary>
    /// True when the store already holds <paramref name="row"/>'s id at the served version, and at the
    /// served content hash when the register states one, fetched for <paramref name="source"/>. The marker is
    /// written LAST by <see cref="SkillDirectoryInstaller.Materialize"/>, so its presence means the directory was
    /// completed. A missing marker, a version that differs, a hash that differs, or bytes fetched for another
    /// library all mean "fetch": the same version of the same id can be served by two libraries (review finding
    /// SK-F6), and the bytes must carry the source they really came from.
    /// </summary>
    internal static bool IsAlreadyMaterialized(string store, RegisterRow row, SkillSource? source)
    {
        var marker = Path.Combine(store, row.Id, SkillDirectoryInstaller.MarkerFileName);
        if (!File.Exists(marker))
            return false;

        string[] lines;
        try
        {
            lines = File.ReadAllLines(marker);
        }
        catch (IOException ex)
        {
            FileLog.Write($"[SkillStoreRefresh] could not read the marker for '{row.Id}' ({ex.Message}) - fetching it again");
            return false;
        }
        if (lines.Length < 2)
            return false;

        if (!string.Equals(lines[0].Trim(), row.Id, StringComparison.OrdinalIgnoreCase))
            return false;
        if (!int.TryParse(lines[1].Trim(), out var installedVersion) || installedVersion != row.Version)
            return false;

        var installedHash = lines.Length >= 3 ? lines[2].Trim() : "";
        if (!string.IsNullOrWhiteSpace(row.ContentHash)
            && !string.Equals(installedHash, row.ContentHash.Trim(), StringComparison.Ordinal))
            return false;

        var recorded = SkillSource.ReadStamp(lines);
        return source is null ? recorded is null : source.Is(recorded);
    }

    private static SkillBundle ToBundle(string id, VersionDetail detail)
    {
        var files = (detail.Files ?? new List<FileRow>()).Select(f =>
        {
            var isBase64 = string.Equals(f.Encoding?.Trim(), "base64", StringComparison.OrdinalIgnoreCase);
            var bytes = isBase64
                ? Convert.FromBase64String(f.Content ?? "")
                : System.Text.Encoding.UTF8.GetBytes(f.Content ?? "");
            return new SkillFileBytes(f.FileName ?? "", bytes, f.Executable);
        }).ToList();

        return new SkillBundle(
            id,
            detail.Version,
            detail.ContentHash ?? "",
            detail.Summary ?? "",
            detail.Triggers ?? new List<string>(),
            detail.BodyMarkdown ?? "",
            files,
            detail.License,
            detail.Compatibility,
            detail.AllowedTools,
            detail.Metadata);
    }

    /// <summary>
    /// The register, read through the held answers: it is polled every minute and changes only when a
    /// skill is published or switched, so an unchanged register is answered "not changed" and the copy
    /// already held is parsed again. Version details are NOT held - each is read once, when it changes.
    /// </summary>
    private async Task<RegisterResponse?> GetRegisterAsync(string url, string? token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var answer = await _held.SendAsync(_client, request, ct).ConfigureAwait(false);
        if (!answer.IsSuccess)
            return null;
        if (!string.Equals(answer.MediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[SkillStoreRefresh] GET {url} answered '{answer.MediaType}' instead of JSON - this Gateway " +
                          "does not serve the skill library yet. The store is left as it is.");
            return null;
        }
        return JsonSerializer.Deserialize<RegisterResponse>(answer.Body, JsonOpts);
    }

    private async Task<T?> GetAsync<T>(string url, string? token, CancellationToken ct) where T : class
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);

        using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            FileLog.Write($"[SkillStoreRefresh] GET {url} -> HTTP {(int)response.StatusCode}");
            return null;
        }

        // A 2XX IS NOT PROOF THE GATEWAY UNDERSTOOD THE REQUEST. This Gateway serves the Cockpit's
        // single-page app and answers UNKNOWN page paths with its HTML shell, HTTP 200 - which is
        // exactly what a Gateway too old to know about skills returns here. Believed at face value it
        // would be written to disk AS A SKILL. So the promised content type is asserted, always.
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[SkillStoreRefresh] GET {url} answered '{mediaType}' instead of JSON - this Gateway " +
                          "does not serve the skill library yet. Nothing materialized.");
            return null;
        }

        return await response.Content.ReadFromJsonAsync<T>(JsonOpts, ct).ConfigureAwait(false);
    }

    private sealed class RegisterResponse
    {
        [JsonPropertyName("skills")] public List<RegisterRow>? Skills { get; set; }

        /// <summary>Which library this is, for the caller's key. Absent on a Gateway older than the rule.</summary>
        [JsonPropertyName("source")] public RegisterSource? Source { get; set; }
    }

    private sealed class RegisterSource
    {
        [JsonPropertyName("gatewayId")] public string? GatewayId { get; set; }
        [JsonPropertyName("tenantId")] public string? TenantId { get; set; }
        [JsonPropertyName("teamId")] public string? TeamId { get; set; }
    }

    internal sealed class RegisterRow
    {
        public string Id { get; set; } = "";
        public int Version { get; set; }
        public bool Enabled { get; set; } = true;
        /// <summary>The served version's content hash, when the Gateway states it. Empty on a Gateway
        /// that does not, in which case the version alone decides.</summary>
        public string? ContentHash { get; set; }
    }

    private sealed class VersionDetail
    {
        public int Version { get; set; }
        public string? Summary { get; set; }
        public List<string>? Triggers { get; set; }
        public string? BodyMarkdown { get; set; }
        public List<FileRow>? Files { get; set; }
        public string? License { get; set; }
        public string? Compatibility { get; set; }
        public string? AllowedTools { get; set; }
        public Dictionary<string, string>? Metadata { get; set; }
        public string? ContentHash { get; set; }
    }

    private sealed class FileRow
    {
        public string? FileName { get; set; }
        public string? Content { get; set; }
        public string? Encoding { get; set; }
        public bool Executable { get; set; }
    }
}
