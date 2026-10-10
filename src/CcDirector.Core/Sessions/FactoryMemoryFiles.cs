using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// THE COPY OF A FACTORY'S MEMORY A FACTORY SESSION STARTS WITH (Factory Memory mission, phase 3a, section 5.4).
///
/// Before a factory session's agent starts, the Director downloads the factory's notes and writes them here: one
/// file per note, an <c>index.md</c> that lists them, and a small machine-readable record of the version of each
/// note as it was read. The folder is under storage, keyed by session id, and NEVER inside the checkout or a pooled
/// worktree: files there would be untracked, dirty the tree, could be committed by the agent, and two sessions in
/// one shared checkout would overwrite each other's copies.
///
/// THE PATH REACHES THE AGENT IN AN ENVIRONMENT VARIABLE (<see cref="DirectoryEnvVar"/>), the way the preamble path
/// does. That is what makes it work for every agent kind, including those that receive no start-up text, and for an
/// account that replaced that text. The line in the start-up text (<see cref="StartupLine"/>) is a courtesy.
///
/// THE VERSIONS RECORD IS WHAT LETS <c>factory memory set</c> SEND THE VERSION IT LAST READ. The command line reads
/// it and keeps it current as the agent reads and writes, so two agents that both learned something cannot silently
/// overwrite one another: the one whose read is stale is refused with the current text and told to merge.
/// </summary>
public static class FactoryMemoryFiles
{
    /// <summary>The environment variable that carries a factory session's notes folder to its agent.</summary>
    public const string DirectoryEnvVar = "CC_FACTORY_MEMORY_DIR";

    /// <summary>The file that lists the notes, for a person or an agent reading the folder.</summary>
    public const string IndexFileName = "index.md";

    /// <summary>The version of each note as this session last read it. Written by the Director at start and kept
    /// current by the command line; the shape is <c>{"factory": "...", "versions": {"name": 3}}</c>.</summary>
    public const string ReadVersionsFileName = ".read-versions.json";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    // Names Windows will not open as a file whatever the extension. A note may be called anything; its file may not.
    private static readonly HashSet<string> ReservedFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "con", "prn", "aux", "nul",
        "com1", "com2", "com3", "com4", "com5", "com6", "com7", "com8", "com9",
        "lpt1", "lpt2", "lpt3", "lpt4", "lpt5", "lpt6", "lpt7", "lpt8", "lpt9",
        // Ours: the index has this name, and a note called "index" must not overwrite it.
        "index",
    };

    private const int MaxFileStemLength = 80;

    /// <summary>A session's notes folder under the real storage root, or under <paramref name="root"/> in a test.</summary>
    public static string DirectoryFor(Guid sessionId, string? root = null)
        => Path.Combine(root ?? CcStorage.FactoryMemory(), sessionId.ToString());

    /// <summary>
    /// Write a factory's notes for one session, replacing anything already in its folder, and return the folder.
    /// Each note's text is written word for word - what an agent wrote is kept exactly as written.
    /// </summary>
    public static string Write(Guid sessionId, string factory, IReadOnlyList<FactoryMemoryNoteDto> notes,
        DateTime downloadedAtUtc, string? root = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        ArgumentNullException.ThrowIfNull(notes);

        var dir = DirectoryFor(sessionId, root);
        FileLog.Write($"[FactoryMemoryFiles] Write: session={sessionId}, factory={factory}, notes={notes.Count}, dir={dir}");

        // A fresh copy every time. A leftover from an earlier attempt with the same id would otherwise show the
        // agent a note the factory no longer has.
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var placed = new List<(FactoryMemoryNoteDto Note, string File)>();
        foreach (var note in notes.OrderBy(n => n.Name, StringComparer.Ordinal))
        {
            var file = FileNameFor(note.Name, used);
            File.WriteAllText(Path.Combine(dir, file), note.Text ?? "", new UTF8Encoding(false));
            placed.Add((note, file));
        }

        File.WriteAllText(Path.Combine(dir, IndexFileName), RenderIndex(factory, placed, downloadedAtUtc), new UTF8Encoding(false));

        var record = new Dictionary<string, object>
        {
            ["factory"] = factory,
            ["versions"] = placed.ToDictionary(p => p.Note.Name, p => p.Note.Version, StringComparer.Ordinal),
        };
        File.WriteAllText(Path.Combine(dir, ReadVersionsFileName), JsonSerializer.Serialize(record, JsonOpts), new UTF8Encoding(false));

        FileLog.Write($"[FactoryMemoryFiles] Write: session={sessionId} placed {placed.Count} notes in {dir}");
        return dir;
    }

    /// <summary>Remove a session's notes folder. Called when the session is removed and when its create failed, so a
    /// session that is gone leaves no copy behind. A failure is logged and never thrown: the folder is a copy.</summary>
    public static void DeleteFor(Guid sessionId, string? root = null)
    {
        var dir = DirectoryFor(sessionId, root);
        try
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
                FileLog.Write($"[FactoryMemoryFiles] DeleteFor: removed {dir}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryMemoryFiles] DeleteFor FAILED: could not remove {dir}: {ex.Message}");
        }
    }

    /// <summary>
    /// The one line the start-up text carries for a factory session whose notes are in place, or null for any other
    /// session. A courtesy: the environment variable is what makes the memory work.
    /// </summary>
    public static string? StartupLine(Session session)
    {
        ArgumentNullException.ThrowIfNull(session);
        if (session.Factory is not { } factory || session.FactoryMemoryDirectory is not { } dir)
            return null;
        return $"Factory memory: this session belongs to the factory '{factory}'. Its notes, as they stood when this " +
               $"session started, are in {dir} (index.md lists them; the path is also in {DirectoryEnvVar}). Read them " +
               "before you begin. When you learn something the next run should know, write it with " +
               "cc-devthrottle factory memory set <name> \"<text>\" - one idea per note.";
    }

    /// <summary>A file name for a note: safe on every platform, unique in the folder, and recognisably the note's.</summary>
    internal static string FileNameFor(string name, HashSet<string> used)
    {
        var sb = new StringBuilder();
        foreach (var c in name ?? "")
            sb.Append(c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_' or '.' ? c : '_');
        var stem = sb.ToString().Trim('.', ' ', '_');
        if (stem.Length > MaxFileStemLength)
            stem = stem[..MaxFileStemLength];
        if (stem.Length == 0)
            stem = "note";
        if (ReservedFileNames.Contains(stem) || ReservedFileNames.Contains(stem.Split('.')[0]))
            stem = "note-" + stem;

        var candidate = stem + ".md";
        for (var n = 2; used.Contains(candidate); n++)
            candidate = $"{stem}-{n}.md";
        used.Add(candidate);
        return candidate;
    }

    private static string RenderIndex(string factory, List<(FactoryMemoryNoteDto Note, string File)> placed, DateTime downloadedAtUtc)
    {
        var sb = new StringBuilder();
        sb.Append("# Factory memory: ").Append(factory).Append("\n\n");
        sb.Append("These are the notes of the factory `").Append(factory).Append("` as they stood when this session started (")
          .Append(downloadedAtUtc.ToString("yyyy-MM-dd HH:mm:ss")).Append(" UTC). They are a copy: editing a file here changes nothing.\n\n");
        sb.Append("- Read the current text of a note: `cc-devthrottle factory memory get <name>`\n");
        sb.Append("- Write a note: `cc-devthrottle factory memory set <name> \"<text>\"`\n");
        sb.Append("- Every note: `cc-devthrottle factory memory list`\n\n");
        if (placed.Count == 0)
        {
            sb.Append("This factory has no notes yet.\n");
            return sb.ToString();
        }
        sb.Append("| Note | File | Version | Written by | When (UTC) |\n");
        sb.Append("|---|---|---|---|---|\n");
        foreach (var (note, file) in placed)
        {
            var by = string.IsNullOrEmpty(note.AuthorId) ? note.AuthorKind : $"{note.AuthorKind} {note.AuthorId}";
            sb.Append("| ").Append(Cell(note.Name)).Append(" | ").Append(file).Append(" | ").Append(note.Version)
              .Append(" | ").Append(Cell(by)).Append(" | ").Append(note.WrittenAtUtc.ToString("yyyy-MM-dd HH:mm")).Append(" |\n");
        }
        return sb.ToString();
    }

    private static string Cell(string? s) => (s ?? "").Replace("|", "\\|").Replace("\r", " ").Replace("\n", " ");
}
