using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.AgentPlugins;

public sealed class AgentPluginLoaderTests : IDisposable
{
    private readonly string _dir;

    public AgentPluginLoaderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-director-plugin-loader-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void LoadDirectory_LoadsManifestDescribedPlugin()
    {
        WriteManifest("""
        {
          "id": "external-codex",
          "contractVersion": 1,
          "assembly": "__TEST_ASSEMBLY__",
          "type": "CcDirector.Core.Tests.AgentPlugins.AgentPluginLoaderTests+ExternalCodexPlugin"
        }
        """);

        var result = AgentPluginLoader.LoadDirectory(_dir);

        var plugin = Assert.Single(result.Plugins);
        Assert.Equal("external-codex", plugin.Id);
        Assert.Equal(AgentKind.Codex, plugin.Kind);
        Assert.False(plugin.IsBuiltIn);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "info" && diagnostic.Message.Contains("Loaded plugin"));
    }

    [Fact]
    public void LoadDirectory_RejectsDuplicateAgentKind()
    {
        WriteManifest("""
        {
          "id": "external-codex",
          "contractVersion": 1,
          "assembly": "__TEST_ASSEMBLY__",
          "type": "CcDirector.Core.Tests.AgentPlugins.AgentPluginLoaderTests+ExternalCodexPlugin"
        }
        """);

        var result = AgentPluginLoader.LoadDirectory(_dir, [AgentPluginRegistry.Get(AgentKind.Codex)]);

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Message.Contains("Duplicate agent kind"));
    }

    [Fact]
    public void LoadDirectory_RejectsUnsupportedContractVersion()
    {
        WriteManifest("""
        {
          "id": "external-codex",
          "contractVersion": 99,
          "assembly": "__TEST_ASSEMBLY__",
          "type": "CcDirector.Core.Tests.AgentPlugins.AgentPluginLoaderTests+ExternalCodexPlugin"
        }
        """);

        var result = AgentPluginLoader.LoadDirectory(_dir);

        Assert.Empty(result.Plugins);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Severity == "error" && diagnostic.Message.Contains("Unsupported contractVersion"));
    }

    private void WriteManifest(string template)
    {
        var assemblyPath = typeof(ExternalCodexPlugin).Assembly.Location.Replace("\\", "\\\\");
        File.WriteAllText(Path.Combine(_dir, "plugin.json"), template.Replace("__TEST_ASSEMBLY__", assemblyPath));
    }

    public sealed class ExternalCodexPlugin : IAgentPlugin
    {
        private static readonly IReadOnlyList<AgentCommandPreset> Presets =
        [
            new(AgentToolCatalog.StandardPresetName, ""),
        ];

        private static readonly AgentPluginSettingsMetadata SettingsMetadata = new(
            "External Codex",
            "external-codex",
            _ => "external-codex",
            (_, _) => { });

        public string Id => "external-codex";

        public string ConfigKey => "external-codex";

        public AgentKind Kind => AgentKind.Codex;

        public string DisplayName => "External Codex";

        public bool IsBuiltIn => false;

        public IAgentDriver Driver { get; } = AgentDrivers.For(AgentKind.Codex);

        public bool SupportsConversationHistory => false;

        public AgentPluginSettingsMetadata Settings => SettingsMetadata;

        public AgentPluginDetectionMetadata Detection { get; } = new([new AgentPluginDetectionCandidate("external-codex")], "test");

        public AgentPluginValidationMetadata Validation { get; } = new("--version", TimeSpan.FromSeconds(1));

        public AgentPluginHistoryMetadata History { get; } = new(AgentHistoryProviderKind.None, false, "No test history.");

        public AgentPluginLaunchMetadata Launch { get; } = new(false, false);

        public IReadOnlyList<AgentCommandPreset> CommandPresets => Presets;

        public AgentCommandPreset DefaultCommandPreset => Presets[0];

        public string DefaultModel => "";

        public IAgent CreateAgent(AgentOptions options) => new ExternalCodexAgent();

        public AgentLaunchSpec BuildLaunchSpec(AgentPluginLaunchRequest request) => new("--external", null);
    }

    private sealed class ExternalCodexAgent : IAgent
    {
        public AgentKind Kind => AgentKind.Codex;

        public string ExecutablePath => "external-codex";

        public bool SupportsPreassignedSessionId => false;

        public bool SupportsStudioMode => false;

        public AgentLaunchSpec BuildLaunchSpec(string? userArgs, string? resumeSessionId, bool studioMode) =>
            new("--external", null);
    }
}
