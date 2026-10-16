using System.Collections.Concurrent;
using CcDirector.Core.History;
using CcDirector.Core.Sessions;
using CcDirector.Core.Utilities;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #4078: the absolute paths a session's own conversation names. The owner's rule is "if a path is
/// listed by an agent I can see remotely, I can download it", so a path in this set may be opened even though
/// it lies outside the session's working folder.
///
/// The set holds EXACT paths, never prefixes. Naming <c>D:\briefs\a.pdf</c> admits that file and nothing
/// else: not <c>D:\briefs\b.pdf</c> beside it, not the folder <c>D:\briefs</c> above it. (A folder that is
/// itself named may be listed, and the files directly in it opened - that is decided in
/// <see cref="SessionFileAccess"/>, which asks this set about the folder by its own exact path.)
///
/// Paths are found with the same <see cref="LinkDetector"/> the desktop uses and the Chat's detector ports,
/// called the way the Chat calls it (no repository root, no existence check), so the set is the paths a
/// person can actually click. Every part of every message counts - text, tool calls, tool results - because
/// the terminal view shows all of them as links too.
/// </summary>
internal sealed class SessionNamedPaths
{
    private readonly HashSet<string> _paths;

    private SessionNamedPaths(HashSet<string> paths) => _paths = paths;

    /// <summary>A session whose conversation names nothing.</summary>
    public static SessionNamedPaths Empty { get; } = new(new HashSet<string>(PathComparer));

    /// <summary>How many distinct paths the conversation names. For diagnostics and tests.</summary>
    public int Count => _paths.Count;

    // Ignoring case only where the filesystem itself does (Windows), exactly like the lexical containment test.
    private static StringComparer PathComparer => StringComparer.FromComparison(ControlEndpoints.PathContainmentComparison);

    /// <summary>The set named by these pieces of conversation text.</summary>
    public static SessionNamedPaths FromTexts(IEnumerable<string?> texts)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var text in texts)
        {
            if (string.IsNullOrWhiteSpace(text))
                continue;
            foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
            {
                foreach (var match in LinkDetector.FindAllLinkMatches(line, repoPath: null, pathExistsCheck: null))
                {
                    if (match.Type != LinkDetector.LinkType.Path)
                        continue;
                    var normalised = Normalise(match.Text);
                    if (normalised is not null)
                        paths.Add(normalised);
                }
            }
        }
        return new SessionNamedPaths(paths);
    }

    /// <summary>The set named anywhere in this conversation.</summary>
    public static SessionNamedPaths FromHistory(ConversationHistory history) =>
        FromTexts(history.Messages.SelectMany(m => m.Parts).Select(p => p.Text));

    /// <summary>True when the conversation names exactly this path (after normalisation).</summary>
    public bool Contains(string path)
    {
        var normalised = Normalise(path);
        return normalised is not null && _paths.Contains(normalised);
    }

    /// <summary>
    /// The one form a path is compared in: absolute only (a relative path names nothing without a folder to
    /// resolve it against, and the working-folder rule already covers those), resolved by
    /// <see cref="Path.GetFullPath(string)"/> so "." and ".." segments and doubled separators collapse, and
    /// with a trailing separator removed so a folder named "hosting\" and "hosting" are one folder. Null for
    /// anything that is not a usable absolute path.
    /// </summary>
    public static string? Normalise(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;
        try
        {
            return ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(path));
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (NotSupportedException)
        {
            return null;
        }
    }
}

/// <summary>
/// Issue #4078: the named-path set for each live session, rebuilt only when the session's transcript changes.
/// Reading and scanning a long transcript costs real time, and the Chat asks about every link it shows, so the
/// set is kept per session and keyed on the transcript file's identity (path, length, last write). A session
/// whose conversation has no file (Gemini reads its terminal buffer) is scanned every time - its buffer is the
/// only source and it has no cheap change signal.
/// </summary>
internal sealed class SessionNamedPathsCache
{
    private readonly ConcurrentDictionary<Guid, (string Signature, SessionNamedPaths Paths)> _bySession = new();

    /// <summary>The one cache the Director's file verbs share.</summary>
    public static SessionNamedPathsCache Shared { get; } = new();

    /// <summary>The paths this session's conversation names, now.</summary>
    public SessionNamedPaths For(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        var transcript = SessionHistoryReader.ResolveTranscriptPath(session);
        var signature = Signature(transcript);
        if (signature is not null
            && _bySession.TryGetValue(session.Id, out var cached)
            && cached.Signature == signature)
            return cached.Paths;

        var paths = SessionNamedPaths.FromHistory(SessionHistoryReader.ReadAll(session));
        FileLog.Write($"[SessionNamedPathsCache] For: session={session.Id}, transcript={transcript ?? "(none)"}, named={paths.Count}");
        if (signature is not null)
            _bySession[session.Id] = (signature, paths);
        return paths;
    }

    private static string? Signature(string? transcript)
    {
        if (transcript is null)
            return null;
        var info = new FileInfo(transcript);
        if (!info.Exists)
            return null;
        return $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
    }
}
