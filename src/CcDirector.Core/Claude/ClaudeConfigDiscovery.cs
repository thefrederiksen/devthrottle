using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Claude;

/// <summary>
/// Discovers all Claude Code configuration files: CLAUDE.md files, skills, MCP servers, and settings.
/// </summary>
public sealed class ClaudeConfigDiscovery
{
    private static readonly string HomePath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static readonly string ClaudeDir = Path.Combine(HomePath, ".claude");

    /// <summary>
    /// Discovers all Claude configuration for a given project (repo) path.
    /// </summary>
    public ClaudeConfigTree Discover(string? repoPath)
    {
        FileLog.Write($"[ClaudeConfigDiscovery] Discover: repoPath={repoPath ?? "(null)"}");

        var tree = new ClaudeConfigTree();

        // CLAUDE.md files
        DiscoverClaudeMdFiles(tree, repoPath);

        // Skills
        DiscoverSkills(tree, repoPath);

        // MCP Servers
        DiscoverMcpServers(tree, repoPath);

        // Settings files
        DiscoverSettings(tree, repoPath);

        FileLog.Write($"[ClaudeConfigDiscovery] Discover complete: " +
                       $"claudeMd={tree.ClaudeMdFiles.Count}, " +
                       $"globalSkills={tree.GlobalSkills.Count}, " +
                       $"projectSkills={tree.ProjectSkills.Count}, " +
                       $"mcpServers={tree.McpServers.Count}, " +
                       $"settings={tree.SettingsFiles.Count}");

        return tree;
    }

    private void DiscoverClaudeMdFiles(ClaudeConfigTree tree, string? repoPath)
    {
        // Global CLAUDE.md
        var globalMd = Path.Combine(ClaudeDir, "CLAUDE.md");
        if (File.Exists(globalMd))
            tree.ClaudeMdFiles.Add(new ConfigFileEntry("Global", globalMd, "User-level instructions for all projects"));

        // Project CLAUDE.md (root of repo)
        if (!string.IsNullOrEmpty(repoPath))
        {
            var projectMd = Path.Combine(repoPath, "CLAUDE.md");
            if (File.Exists(projectMd))
                tree.ClaudeMdFiles.Add(new ConfigFileEntry("Project", projectMd, "Project-level instructions checked into the repo"));
        }

        // Project-settings CLAUDE.md (~/.claude/projects/{sanitized}/CLAUDE.md)
        if (!string.IsNullOrEmpty(repoPath))
        {
            var sanitized = SanitizeProjectPath(repoPath);
            var projectSettingsMd = Path.Combine(ClaudeDir, "projects", sanitized, "CLAUDE.md");
            if (File.Exists(projectSettingsMd))
                tree.ClaudeMdFiles.Add(new ConfigFileEntry("Project Settings", projectSettingsMd, "Private project settings (not committed)"));
        }

        // Memory files (~/.claude/projects/{sanitized}/memory/)
        if (!string.IsNullOrEmpty(repoPath))
        {
            var sanitized = SanitizeProjectPath(repoPath);
            var memoryDir = Path.Combine(ClaudeDir, "projects", sanitized, "memory");
            if (Directory.Exists(memoryDir))
            {
                foreach (var file in Directory.GetFiles(memoryDir, "*.md").OrderBy(f => f))
                {
                    var name = Path.GetFileName(file);
                    tree.ClaudeMdFiles.Add(new ConfigFileEntry($"Memory: {name}", file, "Auto-memory file"));
                }
            }
        }
    }

    private void DiscoverSkills(ClaudeConfigTree tree, string? repoPath)
    {
        // Global skills
        var globalSkillsDir = Path.Combine(ClaudeDir, "skills");
        if (Directory.Exists(globalSkillsDir))
        {
            foreach (var dir in Directory.GetDirectories(globalSkillsDir).OrderBy(d => d))
            {
                var skillMd = Path.Combine(dir, "skill.md");
                if (!File.Exists(skillMd)) continue;

                var name = Path.GetFileName(dir);
                var (description, _) = ParseSkillFrontmatter(skillMd);
                tree.GlobalSkills.Add(new SkillEntry(name, skillMd, description, "global"));
            }
        }

        // Project skills
        if (!string.IsNullOrEmpty(repoPath))
        {
            var projectSkillsDir = Path.Combine(repoPath, ".claude", "skills");
            if (Directory.Exists(projectSkillsDir))
            {
                foreach (var dir in Directory.GetDirectories(projectSkillsDir).OrderBy(d => d))
                {
                    var skillMd = Path.Combine(dir, "skill.md");
                    if (!File.Exists(skillMd)) continue;

                    var name = Path.GetFileName(dir);
                    var (description, _) = ParseSkillFrontmatter(skillMd);
                    tree.ProjectSkills.Add(new SkillEntry(name, skillMd, description, "project"));
                }
            }
        }
    }

    private void DiscoverMcpServers(ClaudeConfigTree tree, string? repoPath)
    {
        // Global MCP settings
        var globalMcp = Path.Combine(ClaudeDir, "mcp-settings.json");
        if (File.Exists(globalMcp))
        {
            ParseMcpFile(globalMcp, "global", tree);
        }

        // Project MCP settings
        if (!string.IsNullOrEmpty(repoPath))
        {
            var projectMcp = Path.Combine(repoPath, ".mcp.json");
            if (File.Exists(projectMcp))
                ParseMcpFile(projectMcp, "project", tree);
        }
    }

    private void DiscoverSettings(ClaudeConfigTree tree, string? repoPath)
    {
        // Global settings
        var globalSettings = Path.Combine(ClaudeDir, "settings.json");
        if (File.Exists(globalSettings))
            tree.SettingsFiles.Add(new ConfigFileEntry("Global Settings", globalSettings, "Claude Code global settings (hooks, permissions)"));

        // Project settings
        if (!string.IsNullOrEmpty(repoPath))
        {
            var projectSettings = Path.Combine(repoPath, ".claude", "settings.local.json");
            if (File.Exists(projectSettings))
                tree.SettingsFiles.Add(new ConfigFileEntry("Project Settings", projectSettings, "Project-specific settings overrides"));
        }
    }

    private static void ParseMcpFile(string path, string scope, ClaudeConfigTree tree)
    {
        try
        {
            var json = File.ReadAllText(path);
            using var doc = JsonDocument.Parse(json);

            if (doc.RootElement.TryGetProperty("mcpServers", out var servers) &&
                servers.ValueKind == JsonValueKind.Object)
            {
                foreach (var server in servers.EnumerateObject())
                {
                    var command = "";
                    if (server.Value.TryGetProperty("command", out var cmdProp))
                        command = cmdProp.GetString() ?? "";

                    var args = new List<string>();
                    if (server.Value.TryGetProperty("args", out var argsProp) &&
                        argsProp.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var arg in argsProp.EnumerateArray())
                            args.Add(arg.GetString() ?? "");
                    }

                    tree.McpServers.Add(new McpServerEntry(
                        server.Name,
                        command,
                        args,
                        scope,
                        path));
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDiscovery] ParseMcpFile FAILED for {path}: {ex.Message}");
        }
    }

    private static (string description, string body) ParseSkillFrontmatter(string skillMdPath)
    {
        try
        {
            var lines = File.ReadAllLines(skillMdPath);
            if (lines.Length < 3 || lines[0].Trim() != "---")
                return ("", "");

            string description = "";
            int frontmatterEnd = -1;

            for (int i = 1; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line == "---")
                {
                    frontmatterEnd = i;
                    break;
                }
                if (line.StartsWith("description:", StringComparison.OrdinalIgnoreCase))
                {
                    description = ExtractYamlValue(line, "description:");
                }
            }

            var body = "";
            if (frontmatterEnd >= 0 && frontmatterEnd + 1 < lines.Length)
                body = string.Join("\n", lines[(frontmatterEnd + 1)..]).Trim();

            return (description, body);
        }
        catch
        {
            return ("", "");
        }
    }

    private static string ExtractYamlValue(string line, string prefix)
    {
        var value = line.Substring(prefix.Length).Trim();
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            value = value[1..^1];
        }
        return value;
    }

    private static string SanitizeProjectPath(string path)
    {
        // One source of truth for claude.exe's project-folder encoding (every
        // non-alphanumeric char becomes a dash - see GetProjectFolder).
        return ClaudeSessionReader.GetProjectFolder(path);
    }

    /// <summary>
    /// Removes an MCP server from a config file. Returns true if successful.
    /// </summary>
    public bool RemoveMcpServer(string configPath, string serverName)
    {
        FileLog.Write($"[ClaudeConfigDiscovery] RemoveMcpServer: {serverName} from {configPath}");
        try
        {
            var json = File.ReadAllText(configPath);
            using var doc = JsonDocument.Parse(json);

            if (!doc.RootElement.TryGetProperty("mcpServers", out var servers))
                return false;

            var root = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "mcpServers")
                {
                    var updatedServers = new Dictionary<string, JsonElement>();
                    foreach (var server in servers.EnumerateObject())
                    {
                        if (server.Name != serverName)
                            updatedServers[server.Name] = server.Value;
                    }
                    root["mcpServers"] = updatedServers;
                }
                else
                {
                    root[prop.Name] = prop.Value;
                }
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var newJson = JsonSerializer.Serialize(root, options);
            File.WriteAllText(configPath, newJson);

            FileLog.Write($"[ClaudeConfigDiscovery] RemoveMcpServer: removed {serverName}");
            return true;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[ClaudeConfigDiscovery] RemoveMcpServer FAILED: {ex.Message}");
            return false;
        }
    }
}

/// <summary>
/// Complete tree of discovered Claude configuration.
/// </summary>
public sealed class ClaudeConfigTree
{
    public List<ConfigFileEntry> ClaudeMdFiles { get; } = new();
    public List<SkillEntry> GlobalSkills { get; } = new();
    public List<SkillEntry> ProjectSkills { get; } = new();
    public List<McpServerEntry> McpServers { get; } = new();
    public List<ConfigFileEntry> SettingsFiles { get; } = new();
}

/// <summary>
/// A configuration file entry (CLAUDE.md, settings.json, etc.).
/// </summary>
public sealed class ConfigFileEntry
{
    public string Label { get; }
    public string FilePath { get; }
    public string Description { get; }

    public ConfigFileEntry(string label, string filePath, string description)
    {
        Label = label;
        FilePath = filePath;
        Description = description;
    }
}

/// <summary>
/// A skill (slash command) entry.
/// </summary>
public sealed class SkillEntry
{
    public string Name { get; }
    public string FilePath { get; }
    public string Description { get; }
    public string Scope { get; } // "global" or "project"

    public SkillEntry(string name, string filePath, string description, string scope)
    {
        Name = name;
        FilePath = filePath;
        Description = description;
        Scope = scope;
    }
}

/// <summary>
/// An MCP server configuration entry.
/// </summary>
public sealed class McpServerEntry
{
    public string Name { get; }
    public string Command { get; }
    public List<string> Args { get; }
    public string Scope { get; } // "global" or "project"
    public string ConfigPath { get; }

    public McpServerEntry(string name, string command, List<string> args, string scope, string configPath)
    {
        Name = name;
        Command = command;
        Args = args;
        Scope = scope;
        ConfigPath = configPath;
    }
}
