using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// The links this installer has made from an agent's own skills folder into the shared folder, and for which library -
/// for the PERSON'S INFORMATION ONLY (devthrottle_internal#2311, review rounds 4 and 5). One record per shared folder,
/// kept BESIDE it (<c>~/.agents/skills.devthrottle-links.json</c>), written under the shared-folder lock.
///
/// NOTHING READS IT TO DECIDE ANYTHING. In round 4 it authorised removing a link; round 5 found that a path, a target
/// and a library do not identify the link object the installer made - a person can remove it and make their own at
/// the same path, to the same target or another - and that removing first and saving the record second fails the
/// wrong way. So the installer no longer deletes, moves or replaces a link at all (see
/// <c>SkillDirectoryInstaller.ReconcileLinks</c>), and this record only says which links it made, when.
/// </summary>
internal sealed class SkillLinkRecord
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly List<Entry> _entries;

    private SkillLinkRecord(string path, List<Entry> entries)
    {
        _path = path;
        _entries = entries;
    }

    /// <summary>Where the record for <paramref name="sharedRoot"/> lives: beside it, never inside it.</summary>
    public static string PathFor(string sharedRoot)
    {
        var trimmed = Path.GetFullPath(sharedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(trimmed)!, Path.GetFileName(trimmed) + ".devthrottle-links.json");
    }

    public static SkillLinkRecord Load(string sharedRoot)
    {
        var path = PathFor(sharedRoot);
        if (!File.Exists(path))
            return new SkillLinkRecord(path, new List<Entry>());
        var entries = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException($"The skill link record {path} is empty.");
        return new SkillLinkRecord(path, entries);
    }

    /// <summary>Note that this installer made <paramref name="link"/> to <paramref name="target"/> for
    /// <paramref name="source"/>, replacing whatever was noted about that path before. Not saved until
    /// <see cref="Save"/>.</summary>
    public void Add(string link, string target, SkillSource source)
    {
        var full = Normalize(link);
        _entries.RemoveAll(e => SamePath(e.Link, full));
        _entries.Add(new Entry(full, Normalize(target), source.GatewayId, source.TenantId, source.TeamId));
    }

    /// <summary>True when the link at <paramref name="link"/> points at <paramref name="target"/> right now.</summary>
    public static bool PointsAt(string link, string target)
    {
        var info = new DirectoryInfo(link);
        if (info.LinkTarget is null)
            return false;
        var now = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Normalize(link))!, info.LinkTarget));
        return SamePath(Normalize(now), Normalize(target));
    }

    /// <summary>Write the record. Called once, after every link is made.</summary>
    public void Save()
    {
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_entries, Json));
        File.Move(temp, _path, overwrite: true);
        FileLog.Write($"[SkillLinkRecord] {_path} now records {_entries.Count} link(s)");
    }

    private static string Normalize(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool SamePath(string a, string b) =>
        string.Equals(a, b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>One link this installer made.</summary>
    internal sealed record Entry(
        [property: JsonPropertyName("link")] string Link,
        [property: JsonPropertyName("target")] string Target,
        [property: JsonPropertyName("gatewayId")] string GatewayId,
        [property: JsonPropertyName("tenantId")] string TenantId,
        [property: JsonPropertyName("teamId")] string? TeamId);
}
