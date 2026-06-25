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
/// First-cut heuristic: newest-for-cwd by file modified time. Two Codex sessions in the same
/// repo at once is the known ambiguity (see the plan); the active session is normally the
/// newest file, so this resolves it in practice.
/// </summary>
public static class CodexRolloutLocator
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();

    /// <summary>Resolve (and cache) the rollout for a session, or null if none matches the repo.</summary>
    public static string? Resolve(Guid sessionId, string repoPath)
    {
        if (Cache.TryGetValue(sessionId, out var cached) && File.Exists(cached))
            return cached;

        var found = Scan(repoPath, SessionsDirectory());
        if (found != null)
            Cache[sessionId] = found;
        return found;
    }

    /// <summary>
    /// Scan a sessions directory for the newest rollout whose session_meta cwd matches
    /// <paramref name="repoPath"/>. Exposed (with an explicit directory) for testing.
    /// </summary>
    public static string? Scan(string repoPath, string sessionsDirectory)
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

        // Newest first; the active session's rollout is normally the newest matching file, so
        // we usually inspect only one or two session_meta lines.
        foreach (var file in files)
        {
            var cwd = ReadSessionCwd(file.FullName);
            if (cwd != null && NormalizePath(cwd) == target)
                return file.FullName;
        }
        return null;
    }

    /// <summary>Read the cwd from a rollout's first line (its session_meta), or null.</summary>
    private static string? ReadSessionCwd(string path)
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
            return p.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String
                ? cwd.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string SessionsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex", "sessions");

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
