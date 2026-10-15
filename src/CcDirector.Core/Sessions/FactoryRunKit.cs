using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// THE KIT A SEAT OF A DEFINED FACTORY STARTS WITH (Factory Definition mission, phase 5, design section 4). A sibling
/// of <see cref="FactoryMemoryFiles"/>, on the same rules.
///
/// The Gateway composes the kit and the Director writes it, word for word, before the agent starts: the seat's brief,
/// grants, rules, files and records, and for a Claude Code seat its factory skills. A child of a seat gets a rules-only
/// kit. The folder is under storage, keyed by session id - <c>&lt;storage&gt;/factory-run/&lt;session id&gt;/</c> -
/// and NEVER inside the checkout or a pooled worktree. A seat with no repository works in the folder's <c>work/</c>.
///
/// THE PATH REACHES THE AGENT IN AN ENVIRONMENT VARIABLE (<see cref="DirectoryEnvVar"/>), as the memory folder's does,
/// and <see cref="InheritedSessionEnvironment"/> strips an inherited one, so a session that is not a seat never reads
/// another seat's kit as its own.
///
/// A RESTORE KEEPS THE FOLDER. A restored seat must resume in the folder it ran in - Claude Code finds a conversation
/// by its folder - so <see cref="Refresh"/> rewrites the kit files in place from the same version and keeps
/// <c>work/</c> exactly as the seat left it.
///
/// THE CALLER MAY ADD FILES (<c>extraFiles</c>). An extra file may not take a path the kit already uses, or the work
/// folder. What a check found (phase 5b) is added by <see cref="WithCheck"/> instead, because <c>kit.json</c> must list
/// it: <c>check.json</c> beside the kit, named in <c>kit.json</c>, both written before the agent starts.
/// </summary>
public static class FactoryRunKit
{
    /// <summary>The environment variable that carries a seat's kit folder to its agent.</summary>
    public const string DirectoryEnvVar = "CC_FACTORY_KIT_DIR";

    /// <summary>The folder inside the kit a seat with no repository works in. Never written by the kit.</summary>
    public const string WorkFolderName = "work";

    /// <summary>
    /// The file phase 5b's check writes beside the kit, which a refresh KEEPS like <c>work/</c>: a restored check seat
    /// still has the check it was started for. When the caller passes a new one among its extra files, that one is
    /// written over it.
    /// </summary>
    public const string CheckFileName = "check.json";

    /// <summary>The kit's description: factory, seat, version, kind, and on a seat kit the seat's secret names.</summary>
    public const string KitFileName = "kit.json";

    /// <summary>The seat's rules, merged: <c>{"permissions": {"deny": [...], "ask": [...]}}</c>.</summary>
    public const string RulesFileName = "rules.json";

    /// <summary>The folder of a Claude Code seat's factory skills, a plugin passed with <c>--plugin-dir</c>.</summary>
    public const string PluginFolderName = "claude-plugin";

    /// <summary>The <c>source</c> kit.json gives <see cref="CheckFileName"/>: the check that started the seat wrote it.</summary>
    public const string CheckSource = "check";

    // One line ending on every machine, so check.json and the rewritten kit.json are the same bytes wherever they are
    // written (the default follows the machine).
    private static readonly JsonSerializerOptions CheckJson = new(JsonSerializerDefaults.Web) { WriteIndented = true, NewLine = "\n" };

    private static readonly JsonSerializerOptions KitJson = new() { WriteIndented = true, NewLine = "\n" };

    /// <summary>
    /// The kit with what a check found added (phase 5b, design 3.4): <see cref="CheckFileName"/> holding
    /// <paramref name="check"/> whole, and <c>kit.json</c> listing it with the check's path as where it came from. Every
    /// other file is as the Gateway composed it. Throws when the check names no check or found nothing to do, when the
    /// kit has no <c>kit.json</c> with a <c>files</c> list to name it in, or already holds a <see cref="CheckFileName"/>.
    /// </summary>
    public static FactoryKitResponse WithCheck(FactoryKitResponse kit, FactoryCheckDto check)
    {
        ArgumentNullException.ThrowIfNull(kit);
        ArgumentNullException.ThrowIfNull(check);
        FileLog.Write($"[FactoryRunKit] WithCheck: factory={kit.Factory}, seat={kit.Seat}, version={kit.Version}, check={check.CheckPath}, count={check.Count}, start={check.Start}");
        if (string.IsNullOrWhiteSpace(check.CheckPath))
            throw new InvalidOperationException($"What the check found names no check path, so {CheckFileName} was not written.");
        if (check.Count <= 0)
            throw new InvalidOperationException($"The check {check.CheckPath} found {check.Count} things to do, and a check starts a seat only when it finds work, so {CheckFileName} was not written.");
        var files = Listing(kit, new JsonArray(check.CheckPath)).ToList();
        files.Add(new FactoryKitFileDto { Path = CheckFileName, Text = CheckText(check) });
        return new FactoryKitResponse { Factory = kit.Factory, Seat = kit.Seat, Version = kit.Version, Kind = kit.Kind, Files = files };
    }

    /// <summary>
    /// The kit for a restored seat whose folder still holds the <see cref="CheckFileName"/> a check wrote when it
    /// started: <c>kit.json</c> lists it again, and the file itself is left exactly as it is on disk - neither read nor
    /// rewritten (<see cref="Refresh"/> keeps it). Its <c>from</c> is empty: the check's path is inside the file.
    /// </summary>
    public static FactoryKitResponse ListingKeptCheck(FactoryKitResponse kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        FileLog.Write($"[FactoryRunKit] ListingKeptCheck: factory={kit.Factory}, seat={kit.Seat}, version={kit.Version}");
        return new FactoryKitResponse
        {
            Factory = kit.Factory, Seat = kit.Seat, Version = kit.Version, Kind = kit.Kind, Files = Listing(kit, new JsonArray()).ToList(),
        };
    }

    /// <summary>True when a kit folder holds a <see cref="CheckFileName"/>. The file is not read.</summary>
    public static bool HoldsCheck(string kitDirectory) => File.Exists(Path.Combine(kitDirectory, CheckFileName));

    /// <summary><see cref="CheckFileName"/>'s exact text for <paramref name="check"/>.</summary>
    public static string CheckText(FactoryCheckDto check) => JsonSerializer.Serialize(check, CheckJson) + "\n";

    /// <summary>The kit's files with <c>kit.json</c> rewritten to list <see cref="CheckFileName"/>, in path order.</summary>
    private static IEnumerable<FactoryKitFileDto> Listing(FactoryKitResponse kit, JsonArray from)
    {
        if (kit.Files.Any(f => string.Equals(f.Path, CheckFileName, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"The kit already holds a {CheckFileName}, so what the check found could not be written beside it.");
        var kitJson = kit.Files.SingleOrDefault(f => f.Path == KitFileName)
            ?? throw new InvalidOperationException($"The kit has no {KitFileName} to list {CheckFileName} in, so it was not written.");
        JsonNode? parsed;
        try
        {
            parsed = JsonNode.Parse(kitJson.Text);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"The kit's {KitFileName} as downloaded could not be read as JSON, so {CheckFileName} was not listed in it: {ex.Message}", ex);
        }
        if (parsed is not JsonObject doc || doc["files"] is not JsonArray listed)
            throw new InvalidOperationException($"The kit's {KitFileName} has no list of files to name {CheckFileName} in, so it was not written.");

        var entries = listed.Select(n => n!.DeepClone()).ToList();
        entries.Add(new JsonObject
        {
            ["path"] = CheckFileName,
            ["source"] = CheckSource,
            ["from"] = from,
        });
        doc["files"] = new JsonArray(entries
            .OrderBy(n => n!["path"]?.GetValue<string>(), StringComparer.Ordinal)
            .ToArray());

        var rewritten = doc.ToJsonString(KitJson) + "\n";
        return kit.Files.Select(f => f.Path == KitFileName ? new FactoryKitFileDto { Path = KitFileName, Text = rewritten } : f);
    }

    /// <summary>A session's kit folder under the real storage root, or under <paramref name="root"/> in a test.</summary>
    public static string DirectoryFor(Guid sessionId, string? root = null)
        => Path.Combine(root ?? CcStorage.FactoryRun(), sessionId.ToString());

    /// <summary>The <c>work/</c> folder of a kit folder.</summary>
    public static string WorkDirectoryOf(string kitDirectory) => Path.Combine(kitDirectory, WorkFolderName);

    /// <summary>
    /// Write a session's kit, replacing anything already in its folder, create its <c>work/</c> folder, and return the
    /// folder. Each file's text is written exactly as the Gateway composed it.
    /// </summary>
    public static string Write(Guid sessionId, FactoryKitResponse kit, IReadOnlyList<FactoryKitFileDto>? extraFiles = null,
        string? root = null)
    {
        ArgumentNullException.ThrowIfNull(kit);
        var dir = DirectoryFor(sessionId, root);
        FileLog.Write($"[FactoryRunKit] Write: session={sessionId}, factory={kit.Factory}, seat={kit.Seat}, version={kit.Version}, kind={kit.Kind}, files={kit.Files.Count}, extra={extraFiles?.Count ?? 0}, dir={dir}");
        var files = Checked(kit, extraFiles);

        // A fresh copy every time. A leftover from an earlier attempt with the same id would otherwise show the agent
        // a file the definition no longer has.
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);
        Place(dir, files);
        Directory.CreateDirectory(WorkDirectoryOf(dir));

        FileLog.Write($"[FactoryRunKit] Write: session={sessionId} placed {files.Count} files in {dir}");
        return dir;
    }

    /// <summary>
    /// Rewrite a restored seat's kit IN PLACE: every file but <c>work/</c> and <see cref="CheckFileName"/> is removed and
    /// the kit is written again from <paramref name="kit"/> (the same version the seat started with); <c>work/</c> is
    /// kept as it is, and created when it is missing - a seat whose folder was deleted gets an empty one. <paramref name="kitDirectory"/> must be a folder directly under the kit root
    /// (<see cref="WhyNotAKitFolder"/>); the caller checks it first.
    /// </summary>
    public static string Refresh(string kitDirectory, FactoryKitResponse kit, IReadOnlyList<FactoryKitFileDto>? extraFiles = null,
        string? root = null)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (WhyNotAKitFolder(kitDirectory, root) is { } refusal)
            throw new InvalidOperationException(refusal);
        var dir = Path.GetFullPath(kitDirectory);
        FileLog.Write($"[FactoryRunKit] Refresh: factory={kit.Factory}, seat={kit.Seat}, version={kit.Version}, kind={kit.Kind}, files={kit.Files.Count}, dir={dir}");
        var files = Checked(kit, extraFiles);

        Directory.CreateDirectory(dir);
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir))
        {
            if (string.Equals(Path.GetFileName(entry), WorkFolderName, StringComparison.OrdinalIgnoreCase)
                && Directory.Exists(entry))
                continue;
            if (string.Equals(Path.GetFileName(entry), CheckFileName, StringComparison.OrdinalIgnoreCase)
                && File.Exists(entry))
                continue;
            if (Directory.Exists(entry)) Directory.Delete(entry, recursive: true);
            else File.Delete(entry);
        }
        Place(dir, files);
        Directory.CreateDirectory(WorkDirectoryOf(dir));

        FileLog.Write($"[FactoryRunKit] Refresh: placed {files.Count} files in {dir}, work and {CheckFileName} kept");
        return dir;
    }

    /// <summary>
    /// Null when <paramref name="kitDirectory"/> is a folder directly under the kit root, else the sentence that says
    /// why it is not one. A restore hands a kit folder back from the Gateway; the Director deletes that folder when the
    /// session ends, so a path anywhere else - outside the root, the root itself, or one that climbs out with
    /// <c>..</c> - is refused rather than trusted.
    /// </summary>
    public static string? WhyNotAKitFolder(string? kitDirectory, string? root = null)
    {
        var rootDir = Path.GetFullPath(root ?? CcStorage.FactoryRun());
        if (string.IsNullOrWhiteSpace(kitDirectory))
            return "No kit folder was named.";
        if (!Path.IsPathRooted(kitDirectory))
            return $"The kit folder '{kitDirectory}' is not a full path; a seat's kit folder is always a folder directly under {rootDir}.";
        var segments = kitDirectory.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        if (segments.Any(s => s is ".." or "."))
            return $"The kit folder '{kitDirectory}' climbs with '..' or '.', so it is not trusted; a seat's kit folder is always a folder directly under {rootDir}.";
        var full = Path.GetFullPath(kitDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var parent = Path.GetDirectoryName(full);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (parent is null || !string.Equals(parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                rootDir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), comparison))
            return $"The kit folder '{kitDirectory}' is not directly under {rootDir}, where this Director keeps every seat's kit.";
        return null;
    }

    /// <summary>Remove a session's kit folder. A failure is logged and never thrown: the folder is a copy.</summary>
    public static void Delete(string? kitDirectory, string? root = null)
    {
        if (string.IsNullOrWhiteSpace(kitDirectory)) return;
        // Only ever a folder under the kit root: this deletes recursively, and a path that is not one is never touched.
        if (WhyNotAKitFolder(kitDirectory, root) is { } refusal)
        {
            FileLog.Write($"[FactoryRunKit] Delete REFUSED: {refusal}");
            return;
        }
        try
        {
            if (Directory.Exists(kitDirectory))
            {
                Directory.Delete(kitDirectory, recursive: true);
                FileLog.Write($"[FactoryRunKit] Delete: removed {kitDirectory}");
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryRunKit] Delete FAILED: could not remove {kitDirectory}: {ex.Message}");
        }
    }

    /// <summary>The kit's files and the extra ones, each path checked: relative, inside the folder, never the work
    /// folder, and used once.</summary>
    private static List<FactoryKitFileDto> Checked(FactoryKitResponse kit, IReadOnlyList<FactoryKitFileDto>? extraFiles)
    {
        var all = new List<FactoryKitFileDto>(kit.Files);
        if (extraFiles is not null) all.AddRange(extraFiles);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in all)
        {
            if (WhyNotAKitPath(file.Path) is { } bad)
                throw new InvalidOperationException(bad);
            if (!used.Add(file.Path))
                throw new InvalidOperationException($"The kit names '{file.Path}' twice, so it was not written.");
        }
        return all;
    }

    private static string? WhyNotAKitPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "The kit holds a file with no path, so it was not written.";
        if (path.Contains('\\') || path.StartsWith('/') || Path.IsPathRooted(path))
            return $"The kit file '{path}' is not a relative path with forward slashes, so the kit was not written.";
        var segments = path.Split('/');
        if (segments.Any(s => s.Length == 0 || s is "." or ".."))
            return $"The kit file '{path}' has an empty, '.' or '..' part, so the kit was not written.";
        if (string.Equals(segments[0], WorkFolderName, StringComparison.OrdinalIgnoreCase))
            return $"The kit file '{path}' is inside {WorkFolderName}/, which is the seat's own folder, so the kit was not written.";
        return null;
    }

    private static void Place(string dir, IEnumerable<FactoryKitFileDto> files)
    {
        foreach (var file in files)
        {
            var target = Path.Combine(dir, file.Path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllText(target, file.Text ?? "", new UTF8Encoding(false));
        }
    }
}
