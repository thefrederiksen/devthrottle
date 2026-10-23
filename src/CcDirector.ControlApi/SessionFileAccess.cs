using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #4078: what one path is for one session, and whether that session may open it. This is the
/// successor of the working-folder-only rule (<see cref="ControlEndpoints.ResolveSessionFile"/>) for the file
/// viewer, and it is the ONE place the decision is made: the file read, the folder listing and the Chat's
/// "not on disk" hint all ask here.
///
/// A path is ADMITTED when any of these holds:
///   1. it is inside the session's working folder (the July rule, unchanged, real path included);
///   2. its exact path, after normalisation, is named in the session's own conversation;
///   3. it is a FILE whose REAL parent folder is named there by its exact path. The real parent, not the
///      parent as spelled: a symbolic link inside a named folder, or a named link to a folder, leads to files
///      in a folder nobody named, and those are refused.
/// Naming a file never admits its siblings or its folder, and naming a folder never admits the folders inside it.
///
/// BEING NAMED IS NOT THE PROTECTION. One "find ~" in a tool result names every file in the home folder, and
/// anyone who can see the Chat can also prompt the agent, so a viewer can get almost any path named. What
/// actually stands between a viewer and the owner's credentials is the list of refusals below, which apply to
/// every path however it was admitted. Treat a change to them as a change to the security boundary.
///
/// A path is REFUSED, whatever admitted it, when:
///   - its real identity cannot be established (an unreadable reparse point, a link cycle), or it names a
///     Windows alternate data stream;
///   - it, or what it really is after every symbolic link and junction is followed, is on the secrets list
///     (<see cref="SessionFileSecrets.IsSecret"/>);
///   - its real location is a hidden file or folder directly under a home folder of the user (HOME and the
///     real home both), or anything inside one (<see cref="SessionFileSecrets.IsHiddenUnderHome"/>). A list
///     of names will always miss some tool's credentials; this catches every tool that keeps them in
///     ~/.something. The one exception is a working folder that is itself inside such a hidden folder: its
///     own files still open. A working folder that IS the home folder gets the rule (issue #4087);
///   - it is a file with more than one name (a hard link), which no path test can place.
///
/// A file is served by its REAL path, so a link swapped after this decision cannot redirect the read.
/// Existence is only ever reported for an admitted path, so asking about an unnamed path outside the working
/// folder says "not allowed" whether or not anything is there.
/// </summary>
internal static class SessionFileAccess
{
    // The sentences a person reads. The Director writes them; the Gateway and every client pass them on as
    // they are (the client is dumb).
    internal const string NotOnDiskSentence =
        "This file is not on disk on the session's computer. It may not have been created or pulled yet.";
    internal const string NotAllowedSentence =
        "This file is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";
    internal const string SecretSentence =
        "This file is not allowed for this session: it is on the list of secrets that are never opened remotely.";
    internal const string HardLinkSentence =
        "This file is not allowed for this session: it has more than one name on disk, so it cannot be shown to be the file that was named.";
    internal const string UnresolvableSentence =
        "This file is not allowed for this session: where it really leads on disk could not be worked out.";
    internal const string FolderSentence =
        "This is a folder, not a file.";
    internal const string FolderNotListableSentence =
        "This folder is not allowed for this session: it is outside the session's working folder and the agent did not name it in this conversation.";

    /// <summary>The decision for one path. <see cref="FullPath"/> is set only when the path may be served or listed.</summary>
    internal sealed record Decision(string Code, string Sentence, string? FullPath)
    {
        public PathStatusDto ToDto(string requested) => new() { Path = requested, Code = Code, Sentence = Sentence };
    }

    /// <summary>Decide what <paramref name="requested"/> is for this session. Never throws for a bad path.</summary>
    public static Decision Decide(string? workingDirectory, string? requested, SessionNamedPaths named, SessionFileSecrets secrets)
    {
        ArgumentNullException.ThrowIfNull(named);
        ArgumentNullException.ThrowIfNull(secrets);
        var decision = DecideCore(workingDirectory, requested, named, secrets);
        FileLog.Write($"[SessionFileAccess] Decide: path={requested}, code={decision.Code}, served={decision.FullPath ?? "(none)"}");
        return decision;
    }

    private static Decision DecideCore(string? workingDirectory, string? requested, SessionNamedPaths named, SessionFileSecrets secrets)
    {
        if (string.IsNullOrWhiteSpace(requested))
            return NotAllowed(NotAllowedSentence);

        var full = FullPathOf(workingDirectory, requested);
        if (full is null)
            return NotAllowed(NotAllowedSentence);

        var real = ControlEndpoints.ResolveRealPath(full);
        if (real is null)
            return NotAllowed(UnresolvableSentence);

        // The secrets list is checked on the REAL path first of all, before anything else can admit the path.
        if (secrets.IsSecret(real) || secrets.IsSecret(full))
            return NotAllowed(SecretSentence);

        var isFolder = Directory.Exists(real);
        var insideWorkingFolder = IsInsideWorkingFolder(workingDirectory, full);

        // Every hidden file or folder directly under a home folder is a secret, unless the path is inside a
        // working folder that is itself inside that hidden folder. A session working IN the home folder gets
        // the rule too (issue #4087).
        var exemptFolder = insideWorkingFolder ? workingDirectory : null;
        if (secrets.IsHiddenUnderHome(real, exemptFolder) || secrets.IsHiddenUnderHome(full, exemptFolder))
            return NotAllowed(SecretSentence);

        var namedItself = named.Contains(full);
        var fileInNamedFolder = !isFolder && Path.GetDirectoryName(real) is { } realParent && named.Contains(realParent);
        if (!insideWorkingFolder && !namedItself && !fileInNamedFolder)
            return NotAllowed(isFolder ? FolderNotListableSentence : NotAllowedSentence);

        if (isFolder)
            return new Decision(PathStatusCodes.Folder, FolderSentence, full);
        if (!File.Exists(real))
            return new Decision(PathStatusCodes.NotOnDisk, NotOnDiskSentence, null);
        if (ControlEndpoints.RefuseUnlessSingleName(real) is null)
            return NotAllowed(HardLinkSentence);
        return new Decision(PathStatusCodes.File, "", real);
    }

    /// <summary>
    /// The contents of a folder this session may list: a folder inside the working folder, or a folder the
    /// conversation names by its exact path. Files and folders are both returned; a folder outside the working
    /// folder lists its FILES only, because the folders inside it are not themselves named and would only
    /// refuse when opened. An entry that opening would refuse is left out. Throws <see cref="UnauthorizedAccessException"/> with
    /// the Director's sentence when the folder may not be listed, and <see cref="DirectoryNotFoundException"/>
    /// when it is not a folder.
    /// </summary>
    public static DirectoryListingDto ListFolder(string? workingDirectory, string? requested, SessionNamedPaths named, SessionFileSecrets secrets)
    {
        var decision = Decide(workingDirectory, requested, named, secrets);
        if (decision.Code == PathStatusCodes.NotAllowed)
            throw new UnauthorizedAccessException(decision.Sentence);
        if (decision.Code != PathStatusCodes.Folder || decision.FullPath is null)
            throw new DirectoryNotFoundException($"This is not a folder: {requested}");

        var folder = decision.FullPath;
        var insideWorkingFolder = IsInsideWorkingFolder(workingDirectory, folder);

        // Every entry is offered only if opening it would be admitted: the same decision the click will meet,
        // so a listing never shows a secret's name, a dotfile under the home folder, or a link that leads out.
        var entries = new List<DirEntryDto>();
        if (insideWorkingFolder)
        {
            foreach (var dir in Directory.EnumerateDirectories(folder))
            {
                if (DecideCore(workingDirectory, dir, named, secrets).Code == PathStatusCodes.NotAllowed)
                    continue;
                entries.Add(new DirEntryDto { Name = Path.GetFileName(dir), Path = dir, IsFile = false });
            }
        }
        foreach (var file in Directory.EnumerateFiles(folder))
        {
            if (DecideCore(workingDirectory, file, named, secrets).Code == PathStatusCodes.NotAllowed)
                continue;
            entries.Add(new DirEntryDto { Name = Path.GetFileName(file), Path = file, IsFile = true });
        }

        // Folders first, then files, each by name - the order every file browser uses.
        var ordered = entries
            .OrderBy(e => e.IsFile)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Going up is offered only while the parent is still inside the working folder; a named folder outside
        // it has no browsable parent (naming a folder never admits the one above it).
        string? parentPath = null;
        if (insideWorkingFolder
            && Path.GetDirectoryName(folder) is { } parent
            && IsInsideWorkingFolder(workingDirectory, parent))
            parentPath = parent;

        FileLog.Write($"[SessionFileAccess] ListFolder: folder={folder}, entries={ordered.Count}, insideWorkingFolder={insideWorkingFolder}");
        return new DirectoryListingDto { CurrentPath = folder, ParentPath = parentPath, Entries = ordered };
    }

    // Inside the working folder, or the working folder itself. The July helper admits only what lies BENEATH
    // the root (it served files, and the root is never a file); a folder listing must also admit the root.
    private static bool IsInsideWorkingFolder(string? workingDirectory, string full)
    {
        if (ControlEndpoints.ResolveInsideWorkingDirectory(workingDirectory, full) is not null)
            return true;
        if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
            return false;
        var realRoot = ControlEndpoints.ResolveRealPath(workingDirectory);
        var realFull = ControlEndpoints.ResolveRealPath(full);
        return realRoot is not null && realFull is not null
            && string.Equals(
                ControlEndpoints.NormalizeDirectoryPath(realRoot),
                ControlEndpoints.NormalizeDirectoryPath(realFull),
                ControlEndpoints.PathIdentityComparison);
    }

    /// <summary>
    /// True when a Windows path names an alternate data stream ("x.pem::$DATA", "notes.txt:hidden"): a colon
    /// anywhere after the drive. Such a path reads the same bytes as the file under a name no rule here
    /// recognises (".pem::$DATA" is not ".pem"), so it is refused outright. On other systems a colon is an
    /// ordinary character in a file name. Exposed with the platform as a parameter so both answers are testable.
    /// </summary>
    internal static bool NamesAlternateDataStream(string path, bool windows)
    {
        if (!windows || string.IsNullOrEmpty(path))
            return false;
        var start = path.Length >= 2 && path[1] == ':' ? 2 : 0;
        return path.IndexOf(':', start) >= 0;
    }

    private static Decision NotAllowed(string sentence) => new(PathStatusCodes.NotAllowed, sentence, null);

    // An absolute path stays itself; a relative one resolves against the working folder (as the July rule did).
    // Null for anything that is not a usable path.
    private static string? FullPathOf(string? workingDirectory, string requested)
    {
        if (NamesAlternateDataStream(requested, OperatingSystem.IsWindows()))
            return null;
        try
        {
            if (Path.IsPathFullyQualified(requested))
                return Path.GetFullPath(requested);
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Path.IsPathFullyQualified(workingDirectory))
                return null;
            return Path.GetFullPath(requested, workingDirectory);
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
