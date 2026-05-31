using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Tools;

/// <summary>
/// Maps Claude Code skills to the cc-* tools they drive, by scanning each skill's markdown for
/// tool mentions, then merging hand-curated links from skill-tool-overrides.json. Every link is
/// tagged <see cref="SkillLinkSource"/> so the UI shows truthfully whether a link was discovered or
/// declared - we never invent a link the scanner could not find.
///
/// Skill sources scanned:
///   - Global: %USERPROFILE%\.claude\skills\&lt;name&gt;\(SKILL.md|skill.md)
///   - Repo:   the nearest .claude\skills found by walking up from the app/working directory.
/// Links are only emitted for tools that exist in the manifest, so the map always ties back to the
/// catalog the user is looking at.
/// </summary>
public sealed partial class SkillToolLinker
{
    private readonly IReadOnlyList<string> _skillRoots;
    private readonly HashSet<string> _knownTools;

    [GeneratedRegex(@"cc-[a-z0-9]+(?:-[a-z0-9]+)*", RegexOptions.IgnoreCase)]
    private static partial Regex ToolTokenRegex();

    /// <summary>Construct against the real global + repo skill directories and the embedded manifest.</summary>
    public SkillToolLinker() : this(DefaultSkillRoots(), ToolManifest.LoadEmbedded().Tools.Select(t => t.Name)) { }

    /// <summary>Construct against explicit skill roots and a known-tool set (used by tests).</summary>
    public SkillToolLinker(IReadOnlyList<string> skillRoots, IEnumerable<string> knownTools)
    {
        _skillRoots = skillRoots ?? throw new ArgumentNullException(nameof(skillRoots));
        _knownTools = new HashSet<string>(knownTools, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Build the full set of skill-to-tool links across all sources.</summary>
    public IReadOnlyList<SkillToolLink> BuildLinks()
    {
        FileLog.Write($"[SkillToolLinker] BuildLinks: roots={_skillRoots.Count}");
        try
        {
            // (tool, skill) -> link, so discovery and overrides dedupe on the pair.
            var links = new Dictionary<(string Tool, string Skill), SkillToolLink>();

            foreach (var root in _skillRoots)
                ScanRoot(root, links);

            MergeOverrides(links);

            var result = links.Values
                .OrderBy(l => l.ToolName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(l => l.Relation)
                .ThenBy(l => l.SkillName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            FileLog.Write($"[SkillToolLinker] BuildLinks: {result.Count} links");
            return result;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillToolLinker] BuildLinks FAILED: {ex.Message}");
            throw;
        }
    }

    /// <summary>Links for one tool.</summary>
    public IReadOnlyList<SkillToolLink> GetLinksForTool(string toolName)
        => BuildLinks().Where(l => string.Equals(l.ToolName, toolName, StringComparison.OrdinalIgnoreCase)).ToList();

    private void ScanRoot(string root, Dictionary<(string, string), SkillToolLink> links)
    {
        if (!Directory.Exists(root)) return;

        foreach (var skillDir in Directory.EnumerateDirectories(root))
        {
            var skillName = Path.GetFileName(skillDir);
            var file = FindSkillFile(skillDir);
            if (file is null) continue;

            string text;
            try { text = File.ReadAllText(file); }
            catch (Exception ex) { FileLog.Write($"[SkillToolLinker] read {file} failed: {ex.Message}"); continue; }

            // Count mentions of each known tool. "drives" when the tool is the skill's own
            // namesake, appears in the first 400 chars (the trigger/description region), or is
            // mentioned repeatedly; otherwise "uses".
            var head = text.Length > 400 ? text[..400] : text;
            var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (Match m in ToolTokenRegex().Matches(text))
            {
                var token = m.Value.ToLowerInvariant();
                if (!_knownTools.Contains(token)) continue;
                counts[token] = counts.GetValueOrDefault(token) + 1;
            }

            foreach (var (tool, count) in counts)
            {
                var inHead = ToolTokenRegex().Matches(head).Any(m => string.Equals(m.Value, tool, StringComparison.OrdinalIgnoreCase));
                var isNamesake = string.Equals(skillName, tool, StringComparison.OrdinalIgnoreCase)
                                 || string.Equals(skillName, StripCcPrefix(tool), StringComparison.OrdinalIgnoreCase);
                var relation = (isNamesake || inHead || count >= 3) ? SkillLinkRelation.Drives : SkillLinkRelation.Uses;

                var key = (tool, skillName);
                if (!links.ContainsKey(key))
                    links[key] = new SkillToolLink(tool, skillName, relation, SkillLinkSource.Discovered);
            }
        }
    }

    private void MergeOverrides(Dictionary<(string Tool, string Skill), SkillToolLink> links)
    {
        SkillToolOverrides? overrides;
        try
        {
            var json = ToolManifest.ReadEmbeddedResource("skill-tool-overrides.json");
            overrides = JsonSerializer.Deserialize<SkillToolOverrides>(json, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SkillToolLinker] overrides load failed: {ex.Message}");
            throw;
        }

        if (overrides is null) return;

        foreach (var (tool, entries) in overrides.Overrides)
        {
            if (!_knownTools.Contains(tool)) continue;
            foreach (var entry in entries)
            {
                var key = (tool, entry.Skill);
                if (links.ContainsKey(key)) continue; // discovery already covered this pair
                var relation = string.Equals(entry.Relation, "drives", StringComparison.OrdinalIgnoreCase)
                    ? SkillLinkRelation.Drives : SkillLinkRelation.Uses;
                links[key] = new SkillToolLink(tool, entry.Skill, relation, SkillLinkSource.Declared);
            }
        }
    }

    private static string? FindSkillFile(string skillDir)
    {
        foreach (var candidate in new[] { "SKILL.md", "skill.md" })
        {
            var path = Path.Combine(skillDir, candidate);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    private static string StripCcPrefix(string tool)
        => tool.StartsWith("cc-", StringComparison.OrdinalIgnoreCase) ? tool[3..] : tool;

    /// <summary>
    /// The default skill roots: the user's global skills dir plus the nearest repo-level
    /// <c>.claude/skills</c> found by walking up from the app and working directories.
    /// </summary>
    private static IReadOnlyList<string> DefaultSkillRoots()
    {
        var roots = new List<string>();

        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(userProfile))
            roots.Add(Path.Combine(userProfile, ".claude", "skills"));

        foreach (var seed in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            var repoSkills = FindNearestRepoSkills(seed);
            if (repoSkills is not null && !roots.Contains(repoSkills, StringComparer.OrdinalIgnoreCase))
                roots.Add(repoSkills);
        }

        return roots;
    }

    private static string? FindNearestRepoSkills(string startDir)
    {
        var dir = new DirectoryInfo(startDir);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, ".claude", "skills");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
