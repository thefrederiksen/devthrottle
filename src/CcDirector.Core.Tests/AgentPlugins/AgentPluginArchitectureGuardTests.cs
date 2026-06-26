using System.Text.RegularExpressions;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Core.Tests.AgentPlugins;

public sealed class AgentPluginArchitectureGuardTests
{
    private static readonly string[] ConcreteAgentTypes =
    [
        nameof(ClaudeAgent),
        nameof(PiAgent),
        nameof(CodexAgent),
        nameof(GeminiAgent),
        nameof(OpenCodeAgent),
        nameof(CursorAgent),
        nameof(GrokAgent),
        nameof(CopilotAgent),
    ];

    [Fact]
    public void EveryCatalogCliHasConcreteBuiltInPluginClass()
    {
        foreach (var entry in AgentToolCatalog.Entries)
        {
            var plugin = AgentPluginRegistry.Get(entry.Tool);

            Assert.True(plugin.IsBuiltIn);
            Assert.EndsWith("AgentPlugin", plugin.GetType().Name, StringComparison.Ordinal);
            Assert.NotEqual("BuiltInAgentPlugin", plugin.GetType().Name);
            Assert.Equal("CcDirector.Core.AgentPlugins", plugin.GetType().Namespace);
        }
    }

    [Fact]
    public void ProductionCodeCreatesConcreteCliAgentsOnlyInsideAgentPlugins()
    {
        var root = FindRepoRoot();
        var src = Path.Combine(root, "src");
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var normalized = path.Replace('\\', '/');
            if (normalized.Contains(".Tests/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (normalized.Contains("/AgentPlugins/", StringComparison.OrdinalIgnoreCase))
                continue;
            if (normalized.EndsWith("/RawCliAgent.cs", StringComparison.OrdinalIgnoreCase))
                continue;

            var text = File.ReadAllText(path);
            foreach (var typeName in ConcreteAgentTypes)
            {
                if (Regex.IsMatch(text, $@"new\s+{Regex.Escape(typeName)}\s*\("))
                    offenders.Add(Path.GetRelativePath(root, path) + " -> " + typeName);
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void LegacyBuiltInAgentPluginAdapterDoesNotExist()
    {
        var root = FindRepoRoot();
        var adapterPath = Path.Combine(root, "src", "CcDirector.Core", "AgentPlugins", "BuiltInAgentPlugin.cs");

        Assert.False(File.Exists(adapterPath), "BuiltInAgentPlugin would allow new built-ins to bypass concrete plugin classes.");
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root from test output directory.");
    }
}
