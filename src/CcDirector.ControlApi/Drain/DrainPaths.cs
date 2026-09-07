using CcDirector.Core.Storage;

namespace CcDirector.ControlApi.Drain;

/// <summary>
/// Where a drain's documents live, and what each one is called.
///
/// ONE DIRECTORY PER RESTART, ON THE TARGET MACHINE, under the data root:
///
/// <code>
/// &lt;data root&gt;/vault/handovers/director-restart/&lt;timestamp&gt;-&lt;director name&gt;/
///     &lt;short id&gt; - &lt;session name&gt;.md      (one per session that wrote one)
/// </code>
///
/// It is on the TARGET machine deliberately: the disk survives the restart and the sessions do not, so
/// afterwards anything on that machine can find the documents without needing the session that wrote
/// them. The index itself is NOT a file here - it is the workspace object on the Gateway, which is
/// readable when this machine is down. That is the change Phase 3 made and this phase writes into.
///
/// The file NAME is load-bearing, and this is the whole reason it is computed in one place: the drain
/// tells each seat exactly where to write, and then watches that exact path. A seat's document arriving
/// is the mechanical signal that it drained, so a name the drain and the seat compute differently is a
/// seat that will look unreachable while its document sits on disk.
/// </summary>
public static class DrainPaths
{
    /// <summary>The folder holding every restart's directory.</summary>
    public static string RestartsRoot => Path.Combine(CcStorage.VaultHandovers(), "director-restart");

    /// <summary>
    /// The directory for one drain: a sortable timestamp and the Director's name, which is how a person
    /// finds the right one months later among several.
    /// </summary>
    /// <param name="startedLocal">When the drain started, in local time - it is read by a person standing
    /// at this machine.</param>
    /// <param name="directorName">The Director's display name.</param>
    public static string DirectoryFor(DateTime startedLocal, string? directorName)
        => Path.Combine(RestartsRoot, $"{startedLocal:yyyy-MM-ddTHHmm}-{Sanitize(directorName ?? "Director")}");

    /// <summary>
    /// The handover file one seat is told to write, and the exact path the drain watches for it.
    /// </summary>
    /// <param name="directory">The drain's directory.</param>
    /// <param name="sessionId">The seat's session id; its first eight characters name the file.</param>
    /// <param name="sessionName">The seat's name.</param>
    public static string HandoverFor(string directory, string sessionId, string? sessionName)
        => Path.Combine(directory, HandoverFileName(sessionId, sessionName));

    /// <summary>The file name alone. Public so the drain message and the watcher use one definition.</summary>
    /// <param name="sessionId">The seat's session id.</param>
    /// <param name="sessionName">The seat's name.</param>
    public static string HandoverFileName(string sessionId, string? sessionName)
    {
        var shortId = ShortId(sessionId);
        var name = Sanitize(sessionName ?? "session");
        return $"{shortId} - {name}.md";
    }

    /// <summary>The first eight characters of a session id, which is how the fleet refers to one in
    /// conversation and how the hand-written index named every document.</summary>
    /// <param name="sessionId">The session id.</param>
    public static string ShortId(string? sessionId)
    {
        var id = sessionId ?? "";
        return id.Length >= 8 ? id[..8] : id;
    }

    /// <summary>
    /// Make a name safe for a file, and BOUND it. A session name can be long, and the drain directory
    /// path is already deep; a name that pushed the path past the platform limit would fail at the moment
    /// the seat tried to write, which is the worst possible moment to discover it.
    /// </summary>
    /// <param name="value">The raw name.</param>
    public static string Sanitize(string value)
    {
        const int MaxNameChars = 80;
        var invalid = Path.GetInvalidFileNameChars();
        var chars = new List<char>(value.Length);
        foreach (var c in value.Trim())
        {
            if (c < 32) continue;
            chars.Add(Array.IndexOf(invalid, c) >= 0 ? '-' : c);
        }
        var cleaned = new string(chars.ToArray()).Trim().TrimEnd('.');
        if (cleaned.Length == 0) cleaned = "session";
        if (cleaned.Length > MaxNameChars) cleaned = cleaned[..MaxNameChars].TrimEnd();
        return cleaned;
    }
}
