using System.Text.Json;

namespace CcDirector.Setup.Engine;

/// <summary>
/// Persists the last successful GitHub release-information response together with its
/// HTTP entity tag (the value of the response ETag header), under the per-user setup
/// config directory. The cache lets <see cref="ReleaseSource"/> send a conditional
/// request (If-None-Match) so a repeated or returning install gets an HTTP 304 Not
/// Modified, which GitHub does NOT charge against the unauthenticated rate-limit
/// budget. On a 304 the cached body is served without a second network read.
///
/// The cache is best-effort and non-authoritative: a missing, unreadable, or corrupt
/// cache simply means no conditional request is sent and the fetch proceeds normally.
/// </summary>
public sealed class ReleaseInfoCache
{
    private readonly string _path;

    /// <summary>Production location: %LOCALAPPDATA%\cc-director\config\setup\release-info-cache.json.</summary>
    public ReleaseInfoCache()
        : this(Path.Combine(InstallLayout.Default().SetupStateDir, "release-info-cache.json"))
    {
    }

    /// <summary>Test/explicit location.</summary>
    public ReleaseInfoCache(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("Cache path must not be empty.", nameof(path));
        _path = path;
    }

    private sealed record Entry(string ETag, string Body);

    /// <summary>
    /// The cached entry, or null when no usable cache exists. Returns null (never throws)
    /// on a missing or corrupt cache file - a bad cache must not break the fetch.
    /// </summary>
    public (string ETag, string Body)? Read()
    {
        if (!File.Exists(_path))
            return null;

        try
        {
            var json = File.ReadAllText(_path);
            var entry = JsonSerializer.Deserialize<Entry>(json);
            if (entry is null || string.IsNullOrEmpty(entry.ETag) || string.IsNullOrEmpty(entry.Body))
                return null;

            return (entry.ETag, entry.Body);
        }
        catch (Exception ex)
        {
            // The cache is best-effort and non-authoritative: a corrupt, locked, or otherwise
            // unreadable file must NOT break the install - it just means no conditional request is
            // sent and the fetch proceeds normally. Logged, never thrown (this is the documented
            // contract above and mirrors how the workspace/named-session stores skip bad files).
            EngineLog.Write($"[ReleaseInfoCache] Read: ignoring unusable cache ({ex.GetType().Name}: {ex.Message})");
            return null;
        }
    }

    /// <summary>Persist the entity tag and the release JSON body. Creates the config dir if missing.</summary>
    public void Write(string etag, string body)
    {
        if (string.IsNullOrEmpty(etag) || string.IsNullOrEmpty(body))
            return; // nothing worth caching without both halves

        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(new Entry(etag, body));
        File.WriteAllText(_path, json);
        EngineLog.Write($"[ReleaseInfoCache] Stored release info: etag={etag}, bytes={body.Length}");
    }
}
