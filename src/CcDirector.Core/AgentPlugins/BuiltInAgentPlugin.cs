using CcDirector.Core.Agents;
using CcDirector.Core.Configuration;
using CcDirector.Core.Drivers;

namespace CcDirector.Core.AgentPlugins;

/// <summary>
/// Adapter from today's built-in catalog/driver tables to the plugin contract.
/// </summary>
public sealed class BuiltInAgentPlugin : IAgentPlugin
{
    public BuiltInAgentPlugin(
        string id,
        string configKey,
        AgentToolCatalogEntry catalogEntry,
        IAgentDriver driver,
        Func<AgentOptions, IAgent> agentFactory,
        bool supportsConversationHistory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(agentFactory);

        if (driver.Kind != catalogEntry.Tool)
            throw new ArgumentException($"Driver kind {driver.Kind} does not match catalog tool {catalogEntry.Tool}.", nameof(driver));

        Id = id;
        ConfigKey = configKey;
        Kind = catalogEntry.Tool;
        DisplayName = catalogEntry.DisplayName;
        Driver = driver;
        CommandPresets = catalogEntry.Presets;
        DefaultCommandPreset = catalogEntry.DefaultPreset;
        DefaultModel = catalogEntry.DefaultModel;
        SupportsConversationHistory = supportsConversationHistory;
        _agentFactory = agentFactory;
    }

    private readonly Func<AgentOptions, IAgent> _agentFactory;

    public string Id { get; }

    public string ConfigKey { get; }

    public AgentKind Kind { get; }

    public string DisplayName { get; }

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; }

    public bool SupportsConversationHistory { get; }

    public IReadOnlyList<AgentCommandPreset> CommandPresets { get; }

    public AgentCommandPreset DefaultCommandPreset { get; }

    public string DefaultModel { get; }

    public IAgent CreateAgent(AgentOptions options) => _agentFactory(options);
}
