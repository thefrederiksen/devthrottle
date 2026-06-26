using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// Discovers slash command skills from global and project skill directories.
/// </summary>
public sealed class SlashCommandProvider
{
    private static readonly string GlobalSkillsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".claude", "skills");

    private readonly Dictionary<string, List<SlashCommandItem>> _cache = new();

    /// <summary>
    /// Returns all Claude slash commands for compatibility with existing callers.
    /// </summary>
    public List<SlashCommandItem> GetCommands(string? repoPath) =>
        GetCommands(AgentKind.ClaudeCode, repoPath);

    /// <summary>
    /// Returns slash commands for the selected agent driver and working directory.
    /// Results are cached per agent and repository path.
    /// </summary>
    public List<SlashCommandItem> GetCommands(AgentKind agentKind, string? repoPath)
    {
        FileLog.Write($"[SlashCommandProvider] GetCommands: agent={agentKind}, repoPath={repoPath ?? "(null)"}");

        var cacheKey = $"{agentKind}:{repoPath ?? "__global__"}";
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        var commands = new Dictionary<string, SlashCommandItem>(StringComparer.OrdinalIgnoreCase);
        var driver = AgentPluginRegistry.Contains(agentKind)
            ? AgentPluginRegistry.Get(agentKind).Driver
            : AgentDrivers.For(agentKind);
        AddDriverCommands(driver, commands);

        if (agentKind == AgentKind.ClaudeCode)
        {
            ScanDirectory(GlobalSkillsPath, "global", commands, "");

            if (!string.IsNullOrEmpty(repoPath))
            {
                var projectSkillsPath = Path.Combine(repoPath, ".claude", "skills");
                ScanDirectory(projectSkillsPath, "project", commands, "");
            }
        }
        else if (agentKind == AgentKind.Pi)
        {
            ScanPiCommands(repoPath, commands);
        }

        var result = commands.Values.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _cache[cacheKey] = result;

        FileLog.Write($"[SlashCommandProvider] GetCommands: agent={agentKind}, found {result.Count} commands ({driver.SlashCommands.Count} driver built-in)");
        return result;
    }

    /// <summary>
    /// Returns only custom skill commands (global + project), excluding built-in.
    /// </summary>
    public List<SlashCommandItem> GetCustomSkills(string? repoPath)
    {
        return GetCommands(repoPath).Where(c => !c.IsBuiltIn).ToList();
    }

    /// <summary>
    /// Returns only built-in commands.
    /// </summary>
    public List<SlashCommandItem> GetBuiltInCommands()
    {
        return BuiltInSlashCommands.All.ToList();
    }

    /// <summary>
    /// Clears the cache, forcing re-scan on next call.
    /// </summary>
    public void InvalidateCache()
    {
        FileLog.Write("[SlashCommandProvider] InvalidateCache");
        _cache.Clear();
    }

    /// <summary>
    /// Clears the cache for a specific repository path across all agent drivers.
    /// </summary>
    public void InvalidateCache(string? repoPath)
    {
        var repoKey = repoPath ?? "__global__";
        var suffix = $":{repoKey}";
        foreach (var key in _cache.Keys.Where(key => key.EndsWith(suffix, StringComparison.Ordinal)).ToList())
            _cache.Remove(key);
    }

    /// <summary>
    /// Clears the cache for a specific agent driver and repository path.
    /// </summary>
    public void InvalidateCache(AgentKind agentKind, string? repoPath)
    {
        var cacheKey = $"{agentKind}:{repoPath ?? "__global__"}";
        _cache.Remove(cacheKey);
    }

    private static void AddDriverCommands(IAgentDriver driver, Dictionary<string, SlashCommandItem> commands)
    {
        foreach (var command in driver.SlashCommands)
        {
            var name = command.NormalizedName;
            commands[name] = new SlashCommandItem(
                name,
                command.Description,
                command.Source,
                command.Documentation,
                command.Category,
                command.DriverKind,
                command.IsTerminalOnly);
        }
    }

    private static void ScanPiCommands(string? repoPath, Dictionary<string, SlashCommandItem> commands)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        ScanDirectory(Path.Combine(home, ".pi", "agent", "skills"), "global", commands, "skill:");
        ScanDirectory(Path.Combine(home, ".agents", "skills"), "global", commands, "skill:");
        ScanPromptDirectory(Path.Combine(home, ".pi", "agent", "prompts"), "global", commands);

        if (string.IsNullOrWhiteSpace(repoPath))
            return;

        ScanDirectory(Path.Combine(repoPath, ".pi", "skills"), "project", commands, "skill:");
        ScanDirectory(Path.Combine(repoPath, ".agents", "skills"), "project", commands, "skill:");
        ScanPromptDirectory(Path.Combine(repoPath, ".pi", "prompts"), "project", commands);
    }

    private static void ScanPromptDirectory(string promptsDir, string source, Dictionary<string, SlashCommandItem> commands)
    {
        if (!Directory.Exists(promptsDir))
            return;

        foreach (var promptFile in Directory.GetFiles(promptsDir, "*.md", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileNameWithoutExtension(promptFile);
            var description = ParsePromptDescription(promptFile);
            commands[name] = new SlashCommandItem(name, description, source, string.Empty, "Prompt", AgentKind.Pi);
        }
    }

    private static void ScanDirectory(string skillsDir, string source, Dictionary<string, SlashCommandItem> commands, string namePrefix)
    {
        if (!Directory.Exists(skillsDir))
            return;

        foreach (var dir in Directory.GetDirectories(skillsDir))
        {
            var skillMd = FindSkillFile(dir);
            if (skillMd is null)
                continue;

            var item = ParseSkillFile(skillMd, source, namePrefix);
            if (item != null)
            {
                // Project skills override global skills with the same name.
                commands[item.Name] = item;
            }
        }
    }

    private static string? FindSkillFile(string dir)
    {
        var upper = Path.Combine(dir, "SKILL.md");
        if (File.Exists(upper))
            return upper;

        var lower = Path.Combine(dir, "skill.md");
        return File.Exists(lower) ? lower : null;
    }

    private static SlashCommandItem? ParseSkillFile(string skillMdPath, string source, string namePrefix)
    {
        try
        {
            var allText = File.ReadAllText(skillMdPath);
            var lines = allText.Split('\n');

            if (lines.Length < 3 || lines[0].Trim() != "---")
                return null;

            string? name = null;
            string? description = null;
            int frontmatterEnd = -1;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "---")
                {
                    frontmatterEnd = i;
                    break;
                }

                if (line.StartsWith("name:", StringComparison.OrdinalIgnoreCase))
                {
                    name = ExtractYamlValue(line, "name:");
                }
                else if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = ExtractYamlValue(line, "description:");
                }
            }

            if (string.IsNullOrEmpty(name))
                return null;

            // Extract body after frontmatter
            var documentation = string.Empty;
            if (frontmatterEnd >= 0 && frontmatterEnd + 1 < lines.Length)
            {
                documentation = string.Join("\n", lines[(frontmatterEnd + 1)..]).Trim();
            }

            return new SlashCommandItem(namePrefix + name, description ?? string.Empty, source, documentation);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SlashCommandProvider] ParseSkillFile FAILED for {skillMdPath}: {ex.Message}");
            return null;
        }
    }

    private static string ParsePromptDescription(string promptFile)
    {
        try
        {
            var lines = File.ReadAllLines(promptFile);
            if (lines.Length > 0 && lines[0].Trim() == "---")
            {
                for (var i = 1; i < lines.Length; i++)
                {
                    var line = lines[i].Trim();
                    if (line == "---")
                        break;

                    if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                        return ExtractYamlValue(line, "description:");
                }
            }

            return lines.FirstOrDefault(line => !string.IsNullOrWhiteSpace(line))?.Trim() ?? string.Empty;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SlashCommandProvider] ParsePromptDescription FAILED for {promptFile}: {ex.Message}");
            return string.Empty;
        }
    }

    private static string ExtractYamlValue(string line, string prefix)
    {
        var value = line.Substring(prefix.Length).Trim();
        // Remove surrounding quotes if present
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        return value;
    }
}
