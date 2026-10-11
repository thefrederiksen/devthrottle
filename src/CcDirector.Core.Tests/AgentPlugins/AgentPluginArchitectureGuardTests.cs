using CcDirector.TestInfrastructure;
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
        var root = RepositorySourceIndex.Root;
        var offenders = new List<string>();

        foreach (var path in RepositorySourceIndex.Under("src", ".cs"))
        {
            var normalized = path.Replace('\\', '/');
            // Repository-RELATIVE, which is the documented contract: an absolute path would let a

            // checkout living under a folder ending in "Tests" disable this scan entirely, which is a

            // correctness property depending on where somebody cloned. The predicate anchors on a source

            // root as well, so this is belt and braces rather than the only defence.

            // This guard was the fourth carrier of a
            // copy-pasted ".Tests/" substring test, and it stayed GREEN through the suite split only
            // because nothing among the 2,750 files that moved into ".UnitTests" happened to match its
            // pattern - latent rather than satisfied.
            var relative = Path.GetRelativePath(root, path).Replace('\\', '/');
            if (TestProjectPath.IsTestProject(relative))
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
        var root = RepositorySourceIndex.Root;
        var adapterPath = Path.Combine(root, "src", "CcDirector.Core", "AgentPlugins", "BuiltInAgentPlugin.cs");

        Assert.False(File.Exists(adapterPath), "BuiltInAgentPlugin would allow new built-ins to bypass concrete plugin classes.");
    }
}
