using System.Collections.Concurrent;

namespace CcDirector.Core.Grok;

/// <summary>
/// Finds the current Grok CLI session transcript for a Director session. Grok has no hook that
/// pushes the active session to us, and the transcript path is not known at launch, so we resolve
/// it by scanning <c>~/.grok/sessions</c>.
///
/// Grok groups sessions under a directory whose name is the percent-encoded absolute working
/// directory, for example <c>D:\ReposFred\devthrottle</c> becomes
/// <c>D%3A%5CReposFred%5Cdevthrottle</c>. Inside that per-cwd directory each Grok session has its
/// own <c>&lt;session-id&gt;</c> subdirectory holding <c>chat_history.jsonl</c> (the conversation),
/// plus <c>events.jsonl</c>, <c>summary.json</c>, and so on (ignored here).
///
/// Rather than reproduce Grok's exact escape set, we match by decoding each per-cwd directory name
/// (<see cref="Uri.UnescapeDataString"/>) and comparing the normalized path to the Director
/// session's repo path. Decoding reverses any percent-encoding the same way regardless of which
/// characters the encoder chose to escape, so the match does not depend on guessing Grok's encoder.
///
/// First-cut heuristic: the newest <c>chat_history.jsonl</c> (by file modified time) among the
/// session subdirectories of the matching cwd directory. Two Grok sessions in the same repo at once
/// is the known ambiguity (see the plan); the active session is normally the newest file, so this
/// resolves it in practice. The result is cached per session id (re-scanned only if the cached file
/// disappears) so the History tab's poll is cheap.
/// </summary>
public static class GrokSessionLocator
{
    private static readonly ConcurrentDictionary<Guid, string> Cache = new();

    /// <summary>Resolve (and cache) the chat_history.jsonl for a session, or null if none matches the repo.</summary>
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
    /// Scan a Grok sessions directory for the newest <c>chat_history.jsonl</c> whose per-cwd
    /// directory decodes to <paramref name="repoPath"/>. Exposed (with an explicit directory) for testing.
    /// </summary>
    public static string? Scan(string repoPath, string sessionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(sessionsDirectory))
            return null;

        var target = NormalizePath(repoPath);

        List<FileInfo> candidates;
        try
        {
            candidates = new DirectoryInfo(sessionsDirectory)
                .EnumerateDirectories()
                .Where(cwdDir => DecodedDirectoryMatches(cwdDir.Name, target))
                .SelectMany(cwdDir => cwdDir.EnumerateDirectories())
                .Select(sessionDir => new FileInfo(Path.Combine(sessionDir.FullName, "chat_history.jsonl")))
                .Where(file => file.Exists)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .ToList();
        }
        catch
        {
            return null;
        }

        return candidates.Count == 0 ? null : candidates[0].FullName;
    }

    /// <summary>True when the percent-encoded per-cwd directory name decodes to the target path.</summary>
    private static bool DecodedDirectoryMatches(string encodedName, string normalizedTarget)
    {
        string decoded;
        try { decoded = Uri.UnescapeDataString(encodedName); }
        catch { return false; }
        return NormalizePath(decoded) == normalizedTarget;
    }

    private static string SessionsDirectory()
        => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".grok", "sessions");

    private static string NormalizePath(string p)
    {
        try { return Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
