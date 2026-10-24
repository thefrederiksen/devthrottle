using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace CcDirector.Core.Pi;

/// <summary>One Pi session file as its header describes it: the id pi wrote it under, where it is, and
/// when pi created it.</summary>
public sealed record PiSessionFile(string Id, string Path, DateTime CreatedUtc);

/// <summary>
/// Finds a Pi session's transcript file BY ITS SESSION ID. The Director launches pi with
/// <c>--session-id &lt;id&gt;</c> (<see cref="Agents.PiAgent"/>) and pi names the file after it:
/// <c>~/.pi/agent/sessions/&lt;cwd-slug&gt;/&lt;timestamp&gt;_&lt;id&gt;.jsonl</c>. So the transcript is known
/// from birth and there is nothing to guess.
///
/// This replaces a newest-file-for-the-repo scan (issue #2670). That scan bound a fresh session to the
/// PREVIOUS Pi session's file in the same repo - pi writes its own file only on the first message, so at
/// launch the newest file is always somebody else's - and then cached that answer for the life of the
/// session. The Cockpit showed, and the wingman narrated, a review from five weeks earlier for a session
/// that had not said a word.
///
/// The one time pi picks an id of its own is <c>/new</c> (the Director's context clear), which starts a
/// fresh session file. <see cref="FindCreatedAfter(string, DateTime, string?)"/> finds that file - the one
/// whose session record was CREATED after the clear, in this repo, under an id that is not the one just
/// left - and <see cref="PiSessionRebinder"/> relinks the session to it at the next turn end.
/// </summary>
public static class PiSessionLocator
{
    /// <summary>Agent session id -> file path. An id names exactly one file, so an entry stays valid for as
    /// long as the file exists; a file that has gone (pi archived it) drops the entry and rescans.</summary>
    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The transcript file of the Pi session with this id, or null when pi has not written it yet
    /// (pi creates the file on the first message) or the id is unknown.</summary>
    public static string? Resolve(string? agentSessionId) => Resolve(agentSessionId, SessionsDirectory());

    /// <summary>As <see cref="Resolve(string?)"/>, against an explicit sessions directory (for tests).</summary>
    public static string? Resolve(string? agentSessionId, string sessionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId))
            return null;
        if (Cache.TryGetValue(agentSessionId, out var cached) && File.Exists(cached))
            return cached;

        var found = FindById(agentSessionId, sessionsDirectory);
        if (found != null)
            Cache[agentSessionId] = found;
        return found;
    }

    /// <summary>The live transcript files for one working directory, newest written first.</summary>
    public static List<(string AgentSessionId, DateTime LastWriteUtc)> ListForRepo(string repoPath) =>
        ListForRepo(repoPath, SessionsDirectory());

    /// <summary>As <see cref="ListForRepo(string)"/>, against an explicit sessions directory for replay tests.</summary>
    public static List<(string AgentSessionId, DateTime LastWriteUtc)> ListForRepo(string repoPath, string sessionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(sessionsDirectory))
            return [];
        var target = NormalizePath(repoPath);
        try
        {
            return EnumerateSessionFiles(sessionsDirectory)
                .Select(file => (File: file, Header: ReadHeader(file.FullName)))
                .Where(item => item.Header is not null && NormalizePath(item.Header.Cwd) == target)
                .Select(item => (item.Header!.Id, item.File.LastWriteTimeUtc))
                .OrderByDescending(item => item.LastWriteTimeUtc)
                .ToList();
        }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>The file named <c>&lt;timestamp&gt;_&lt;agentSessionId&gt;.jsonl</c> anywhere under the sessions
    /// directory, or null. pi's <c>_archived</c> folders are skipped: an archived session is not a live one.</summary>
    public static string? FindById(string agentSessionId, string sessionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(agentSessionId) || !Directory.Exists(sessionsDirectory))
            return null;

        var suffix = "_" + agentSessionId + ".jsonl";
        try
        {
            return EnumerateSessionFiles(sessionsDirectory)
                .FirstOrDefault(f => f.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                ?.FullName;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// The session file pi started AFTER a context clear: the newest-created file whose session record says
    /// this repo, was created at or after <paramref name="createdAfterUtc"/>, and carries an id other than
    /// <paramref name="excludeAgentSessionId"/> (the id the session was on when it was cleared). Null while
    /// pi has not written it yet.
    ///
    /// Judged by the header's CREATION timestamp, never by last-write time: a second Pi session in the
    /// same repo keeps writing its own file while this one is cleared, and a last-write test would pick it.
    /// A file created after the clear can only be the cleared session's own.
    /// </summary>
    public static PiSessionFile? FindCreatedAfter(string repoPath, DateTime createdAfterUtc, string? excludeAgentSessionId)
        => FindCreatedAfter(repoPath, createdAfterUtc, excludeAgentSessionId, SessionsDirectory());

    /// <summary>As <see cref="FindCreatedAfter(string, DateTime, string?)"/>, against an explicit sessions
    /// directory (for tests).</summary>
    public static PiSessionFile? FindCreatedAfter(string repoPath, DateTime createdAfterUtc, string? excludeAgentSessionId, string sessionsDirectory)
    {
        if (string.IsNullOrWhiteSpace(repoPath) || !Directory.Exists(sessionsDirectory))
            return null;

        var target = NormalizePath(repoPath);
        PiSessionFile? newest = null;
        try
        {
            foreach (var file in EnumerateSessionFiles(sessionsDirectory))
            {
                var header = ReadHeader(file.FullName);
                if (header is null || header.CreatedUtc < createdAfterUtc)
                    continue;
                if (NormalizePath(header.Cwd) != target)
                    continue;
                if (!string.IsNullOrEmpty(excludeAgentSessionId)
                    && string.Equals(header.Id, excludeAgentSessionId, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (newest is null || header.CreatedUtc > newest.CreatedUtc)
                    newest = new PiSessionFile(header.Id, file.FullName, header.CreatedUtc);
            }
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        return newest;
    }

    private static IEnumerable<FileInfo> EnumerateSessionFiles(string sessionsDirectory)
        => new DirectoryInfo(sessionsDirectory)
            .EnumerateFiles("*.jsonl", SearchOption.AllDirectories)
            .Where(f => !f.FullName.Contains("_archived", StringComparison.OrdinalIgnoreCase));

    private sealed record Header(string Id, string Cwd, DateTime CreatedUtc);

    /// <summary>The session record on a pi session file's first line - id, cwd, creation time - or null when
    /// the line is missing, torn, or not a session record.</summary>
    private static Header? ReadHeader(string path)
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
            if (!(root.TryGetProperty("type", out var t) && t.GetString() == "session"))
                return null;
            if (!root.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String)
                return null;
            if (!root.TryGetProperty("cwd", out var cwdEl) || cwdEl.ValueKind != JsonValueKind.String)
                return null;
            if (!root.TryGetProperty("timestamp", out var tsEl) || tsEl.ValueKind != JsonValueKind.String)
                return null;
            if (!DateTime.TryParse(tsEl.GetString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var created))
                return null;
            return new Header(idEl.GetString()!, cwdEl.GetString()!, created);
        }
        catch (IOException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string SessionsDirectory()
        => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".pi", "agent", "sessions");

    private static string NormalizePath(string p)
    {
        try { return System.IO.Path.GetFullPath(p).TrimEnd('\\', '/').ToLowerInvariant(); }
        catch { return p.TrimEnd('\\', '/').ToLowerInvariant(); }
    }
}
