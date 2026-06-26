using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.AgentPlugins;

/// <summary>Loads external agent plugins from manifest-described DLLs.</summary>
public static class AgentPluginLoader
{
    public const int CurrentContractVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public static AgentPluginLoadResult LoadDirectory(
        string? directory,
        IReadOnlyCollection<IAgentPlugin>? existingPlugins = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            return new AgentPluginLoadResult([], []);

        var existing = existingPlugins ?? [];
        var plugins = new List<IAgentPlugin>();
        var diagnostics = new List<AgentPluginLoadDiagnostic>();
        var ids = new HashSet<string>(existing.Select(plugin => plugin.Id), StringComparer.OrdinalIgnoreCase);
        var kinds = new HashSet<AgentKind>(existing.Select(plugin => plugin.Kind));

        foreach (var manifestPath in Directory.EnumerateFiles(directory, "plugin.json", SearchOption.AllDirectories).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var manifest = ReadManifest(manifestPath);
                if (manifest is null)
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, "Manifest is empty or invalid."));
                    continue;
                }

                if (!manifest.Enabled)
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Info(manifestPath, "Plugin is disabled by manifest."));
                    continue;
                }

                if (manifest.ContractVersion != CurrentContractVersion)
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(
                        manifestPath,
                        $"Unsupported contractVersion {manifest.ContractVersion}; supported version is {CurrentContractVersion}."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Id))
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, "Manifest id is required."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Assembly))
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, "Manifest assembly is required."));
                    continue;
                }

                if (string.IsNullOrWhiteSpace(manifest.Type))
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, "Manifest type is required."));
                    continue;
                }

                var assemblyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifestPath)!, manifest.Assembly));
                if (!File.Exists(assemblyPath))
                {
                    diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, $"Assembly not found: {assemblyPath}"));
                    continue;
                }

                var plugin = CreatePlugin(assemblyPath, manifest.Type);
                var validation = ValidatePlugin(manifestPath, manifest, plugin, ids, kinds);
                if (validation is not null)
                {
                    diagnostics.Add(validation);
                    continue;
                }

                plugins.Add(plugin);
                ids.Add(plugin.Id);
                kinds.Add(plugin.Kind);
                diagnostics.Add(AgentPluginLoadDiagnostic.Info(manifestPath, $"Loaded plugin {plugin.Id} ({plugin.Kind})."));
            }
            catch (Exception ex)
            {
                FileLog.Write($"[AgentPluginLoader] Failed to load {manifestPath}: {ex.Message}");
                diagnostics.Add(AgentPluginLoadDiagnostic.Error(manifestPath, ex.Message));
            }
        }

        return new AgentPluginLoadResult(plugins, diagnostics);
    }

    private static AgentPluginManifest? ReadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        return JsonSerializer.Deserialize<AgentPluginManifest>(stream, JsonOptions);
    }

    private static IAgentPlugin CreatePlugin(string assemblyPath, string typeName)
    {
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(assemblyPath);
        var type = assembly.GetType(typeName, throwOnError: true)!;
        if (!typeof(IAgentPlugin).IsAssignableFrom(type))
            throw new InvalidOperationException($"Type {typeName} does not implement {nameof(IAgentPlugin)}.");

        if (Activator.CreateInstance(type) is not IAgentPlugin plugin)
            throw new InvalidOperationException($"Type {typeName} could not be constructed as an agent plugin.");

        return plugin;
    }

    private static AgentPluginLoadDiagnostic? ValidatePlugin(
        string manifestPath,
        AgentPluginManifest manifest,
        IAgentPlugin plugin,
        HashSet<string> ids,
        HashSet<AgentKind> kinds)
    {
        if (!string.Equals(manifest.Id, plugin.Id, StringComparison.OrdinalIgnoreCase))
            return AgentPluginLoadDiagnostic.Error(manifestPath, $"Manifest id '{manifest.Id}' does not match plugin id '{plugin.Id}'.");

        if (plugin.IsBuiltIn)
            return AgentPluginLoadDiagnostic.Error(manifestPath, "External plugins must return IsBuiltIn=false.");

        if (plugin.Kind == AgentKind.RawCli)
            return AgentPluginLoadDiagnostic.Error(manifestPath, "RawCli is reserved for Director's custom terminal mode.");

        if (ids.Contains(plugin.Id))
            return AgentPluginLoadDiagnostic.Error(manifestPath, $"Duplicate plugin id '{plugin.Id}'.");

        if (kinds.Contains(plugin.Kind))
            return AgentPluginLoadDiagnostic.Error(manifestPath, $"Duplicate agent kind '{plugin.Kind}'.");

        if (string.IsNullOrWhiteSpace(plugin.DisplayName))
            return AgentPluginLoadDiagnostic.Error(manifestPath, "Plugin display name is required.");

        if (plugin.Driver.Kind != plugin.Kind)
            return AgentPluginLoadDiagnostic.Error(manifestPath, "Plugin driver kind must match plugin kind.");

        return null;
    }
}

public sealed record AgentPluginManifest(
    string Id,
    int ContractVersion,
    string Assembly,
    string Type,
    bool Enabled = true);

public sealed record AgentPluginLoadResult(
    IReadOnlyList<IAgentPlugin> Plugins,
    IReadOnlyList<AgentPluginLoadDiagnostic> Diagnostics);

public sealed record AgentPluginLoadDiagnostic(
    string Severity,
    string ManifestPath,
    string Message)
{
    public static AgentPluginLoadDiagnostic Info(string manifestPath, string message) => new("info", manifestPath, message);

    public static AgentPluginLoadDiagnostic Error(string manifestPath, string message) => new("error", manifestPath, message);
}
