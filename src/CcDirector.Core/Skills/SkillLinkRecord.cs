using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// The links this installer has made from an agent's own skills folder into the shared folder, and for which library
/// (devthrottle_internal#2311, review finding SK-F10). One record per shared folder, kept BESIDE it
/// (<c>~/.agents/skills.devthrottle-links.json</c>), shared by every Director on the computer and only ever read or
/// written under the shared-folder lock.
///
/// WHY. A link carries no marker of its own, and where it points is not evidence of who made it: a person can make
/// <c>~/.claude/skills/hand-made</c> pointing at <c>~/.agents/skills/hand-made</c> as easily as this installer can.
/// Withdrawal used to remove any link into the shared folder whose target was gone or was this source's - so a
/// person's link whose target was briefly missing was deleted, and the skill stayed unreachable after it came back.
/// Now a link is changed or removed ONLY when this record says this installer made it, for this library, and it
/// still points where the record says. Links made before the record existed are not in it, so they are left alone:
/// a leftover link is harmless, a deleted one of the person's is not.
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

    /// <summary>What the record says about the link at <paramref name="link"/>, or null when this installer did not
    /// record making it.</summary>
    public Entry? Find(string link)
    {
        var full = Normalize(link);
        return _entries.FirstOrDefault(e => SamePath(e.Link, full));
    }

    /// <summary>Record that this installer made <paramref name="link"/> to <paramref name="target"/> for
    /// <paramref name="source"/>, replacing whatever it said about that link before. Saved at once.</summary>
    public void Set(string link, string target, SkillSource source)
    {
        var full = Normalize(link);
        _entries.RemoveAll(e => SamePath(e.Link, full));
        _entries.Add(new Entry(full, Normalize(target), source.GatewayId, source.TenantId, source.TeamId));
        Save();
    }

    /// <summary>Forget <paramref name="link"/>. Saved at once.</summary>
    public void Remove(string link)
    {
        var full = Normalize(link);
        if (_entries.RemoveAll(e => SamePath(e.Link, full)) > 0)
            Save();
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

    private void Save()
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
        [property: JsonPropertyName("teamId")] string? TeamId)
    {
        /// <summary>The library the link was made for.</summary>
        [JsonIgnore]
        public SkillSourceStamp Stamp => new(GatewayId, TenantId, TeamId);
    }
}
