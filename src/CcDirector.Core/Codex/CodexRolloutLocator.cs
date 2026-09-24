using System.Collections.Concurrent;
using System.Text.Json;

namespace CcDirector.Core.Codex;

/// <summary>
/// Finds the current Codex rollout file for a session. Codex has no hook that pushes the
/// active session to us, and the rollout path is not known at launch, so we resolve it by
/// scanning <c>~/.codex/sessions</c> for the newest <c>rollout-*.jsonl</c> whose
/// <c>session_meta.cwd</c> matches the session's repo path. The result is cached per session
/// id (re-scanned only if the cached file disappears) so the History tab's poll is cheap.
///
/// First-cut heuristic: newest-for-cwd by file modified time. For live Director sessions,
/// resolution is also launch-time scoped so a brand-new Codex terminal does not bind to an
/// older rollout from the same repo while waiting for its own rollout file to appear.
/// </summary>
public static class CodexRolloutLocator
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();
    private static readonly TimeSpan LaunchClockSkew = TimeSpan.FromMinutes(5);

    /// <summary>Resolve (and cache) the rollout for a session, or null if none matches the repo.</summary>
    public static string? Resolve(Guid sessionId, string repoPath, DateTimeOffset? notBefore = null)
    {
        var target = NormalizePath(repoPath);
        if (Cache.TryGetValue(sessionId, out var cached) && File.Exists(cached))
        {
            var cachedInfo = new FileInfo(cached);
            var cachedMeta = ReadSessionMeta(cached);
            if (cachedMeta != null
                && NormalizePath(cachedMeta.Cwd) == target
                && IsWithinLaunchWindow(cachedMeta.Timestamp, cachedInfo, notBefore))
            {
                return cached;
            }

            Cache.TryRemove(sessionId, out _);
        }

        var found = Scan(repoPath, SessionsDirectory(), notBefore);
        if (found != null)
            Cache[sessionId] = found;
        return found;
    }

    /// <summary>
    /// Scan a sessions directory for the newest rollout whose session_meta cwd matches
    /// <paramref name="repoPath"/>. Exposed (with an explicit directory) for testing.
    /// </summary>
    public static string? Scan(string repoPath, string sessionsDirectory)
        => Scan(repoPath, sessionsDirectory, notBefore: null);

    /// <summary>The newest rollout for a repo using the default <c>~/.codex/sessions</c> directory.
    /// Used by the context gauge, which has only the repo path (no Director session id or launch
    /// time), so it takes the newest matching rollout - the active session's, in the common case.</summary>
    public static string? ResolveByRepo(string repoPath)
        => Scan(repoPath, SessionsDirectory(), notBefore: null);

    /// <summary>
    /// Scan a sessions directory for the newest rollout whose session_meta cwd matches
    /// <paramref name="repoPath"/> and whose metadata/write timestamp is not older than
    /// <paramref name="notBefore"/>. Exposed (with an explicit directory) for testing.
    /// </summary>
    public static string? Scan(string repoPath, string sessionsDirectory, DateTimeOffset? notBefore)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(sessionsDirectory))
            return null;

        var target = NormalizePath(repoPath);

        List<FileInfo> files;
        try
        {
            files = new DirectoryInfo(sessionsDirectory)
                .EnumerateFiles("rollout-*.jsonl", SearchOption.AllDirectories)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return null;
        }

        // Without a launch time, keep the repo-level behavior: newest matching rollout wins.
        // With a launch time, bind to the rollout whose session_meta timestamp is closest to that
        // Director session's creation. Multiple Codex sessions can run in the same repo; choosing
        // "newest for cwd" makes an older Director session read a newer sibling session's history.
        var launchUtc = notBefore?.ToUniversalTime();
        var launchCandidates = new List<(string Path, DateTimeOffset Timestamp)>();
        foreach (var file in files)
        {
            var meta = ReadSessionMeta(file.FullName);
            if (meta != null
                && NormalizePath(meta.Cwd) == target
                && IsWithinLaunchWindow(meta.Timestamp, file, notBefore))
            {
                if (launchUtc is null)
                    return file.FullName;
                launchCandidates.Add((file.FullName, CandidateTimestamp(meta.Timestamp, file)));
            }
        }
        return launchCandidates
            .OrderBy(c => Math.Abs((c.Timestamp - launchUtc!.Value).TotalMilliseconds))
            .Select(c => c.Path)
            .FirstOrDefault();
    }

    /// <summary>Read cwd and timestamp from a rollout's first line (its session_meta), or null.</summary>
    /// <summary>
    /// Every rollout of <paramref name="repoPath"/> whose session began inside the launch window after
    /// <paramref name="notBefore"/>, read from the date folders that can hold it. The arrival proof watches all of them
    /// until it has seen its own prompt in one (issue #3290, review finding 4): "the rollout closest to launch" binds two
    /// Codex sessions started seconds apart in one repository to the SAME file on a loaded machine.
    /// </summary>
    public static IReadOnlyList<string> AllForRepo(string repoPath, DateTimeOffset notBefore)
    {
        var root = SessionsDirectory();
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(root)) return [];
        var target = NormalizePath(repoPath);
        var found = new List<string>();
        for (var day = notBefore.LocalDateTime.Date.AddDays(-1); day <= DateTime.Now.Date.AddDays(1); day = day.AddDays(1))
        {
            var dir = Path.Combine(root, day.ToString("yyyy"), day.ToString("MM"), day.ToString("dd"));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in new DirectoryInfo(dir).EnumerateFiles("rollout-*.jsonl"))
            {
                if (file.LastWriteTimeUtc < notBefore.UtcDateTime - LaunchClockSkew) continue;
                var meta = MetaCache.GetOrAdd(file.FullName, p => ReadSessionMeta(p));
                if (meta is null) MetaCache.TryRemove(file.FullName, out _);
                else if (NormalizePath(meta.Cwd) == target && IsWithinLaunchWindow(meta.Timestamp, file, notBefore))
                    found.Add(file.FullName);
            }
        }
        return found;
    }

    private static readonly ConcurrentDictionary<string, CodexSessionMeta?> MetaCache = new(StringComparer.OrdinalIgnoreCase);

    private static CodexSessionMeta? ReadSessionMeta(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var first = reader.ReadLine();
            if (string.IsNullOrEmpty(first))
                return null;

            using var doc = JsonDocument.Parse(first);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;
            if (!(root.TryGetProperty("type", out var t) && t.GetString() == "session_meta"))
                return null;
            if (!root.TryGetProperty("payload", out var p) || p.ValueKind != JsonValueKind.Object)
                return null;
            if (!p.TryGetProperty("cwd", out var cwd) || cwd.ValueKind != JsonValueKind.String)
                return null;

            var cwdValue = cwd.GetString();
            if (string.IsNullOrWhiteSpace(cwdValue))
                return null;

            var timestamp = ParseTimestamp(root);
            if (timestamp is null)
                timestamp = ParseTimestamp(p);

            return new CodexSessionMeta(cwdValue, timestamp);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsWithinLaunchWindow(DateTimeOffset? metadataTimestamp, FileInfo file, DateTimeOffset? notBefore)
    {
        if (notBefore is null)
            return true;

        var candidateTimestamp = CandidateTimestamp(metadataTimestamp, file);
        return candidateTimestamp >= notBefore.Value.ToUniversalTime() - LaunchClockSkew;
    }

    private static DateTimeOffset CandidateTimestamp(DateTimeOffset? metadataTimestamp, FileInfo file)
        => metadataTimestamp ?? new DateTimeOffset(file.LastWriteTimeUtc, TimeSpan.Zero);

    private static DateTimeOffset? ParseTimestamp(JsonElement obj)
        => obj.TryGetProperty("timestamp", out var ts)
           && ts.ValueKind == JsonValueKind.String
           && DateTimeOffset.TryParse(ts.GetString(), out var parsed)
            ? parsed
            : null;

    private static string SessionsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }

    private sealed record CodexSessionMeta(string Cwd, DateTimeOffset? Timestamp);
}
