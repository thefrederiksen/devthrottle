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
        AgentPluginSettingsMetadata settings,
        AgentPluginDetectionMetadata detection,
        AgentPluginValidationMetadata validation,
        AgentPluginHistoryMetadata history)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentException.ThrowIfNullOrWhiteSpace(configKey);
        ArgumentNullException.ThrowIfNull(catalogEntry);
        ArgumentNullException.ThrowIfNull(driver);
        ArgumentNullException.ThrowIfNull(agentFactory);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(detection);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(history);

        if (driver.Kind != catalogEntry.Tool)
            throw new ArgumentException($"Driver kind {driver.Kind} does not match catalog tool {catalogEntry.Tool}.", nameof(driver));
        if (!string.Equals(settings.ConfigKey, configKey, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Settings config key {settings.ConfigKey} does not match plugin config key {configKey}.", nameof(settings));

        Id = id;
        ConfigKey = configKey;
        Kind = catalogEntry.Tool;
        DisplayName = catalogEntry.DisplayName;
        Driver = driver;
        CommandPresets = catalogEntry.Presets;
        DefaultCommandPreset = catalogEntry.DefaultPreset;
        DefaultModel = catalogEntry.DefaultModel;
        Settings = settings;
        Detection = detection;
        Validation = validation;
        History = history;
        SupportsConversationHistory = history.SupportsConversationHistory;
        _agentFactory = agentFactory;

        var agent = agentFactory(new AgentOptions());
        Launch = new AgentPluginLaunchMetadata(agent.SupportsPreassignedSessionId, agent.SupportsStudioMode);
    }

    private readonly Func<AgentOptions, IAgent> _agentFactory;

    public string Id { get; }

    public string ConfigKey { get; }

    public AgentKind Kind { get; }

    public string DisplayName { get; }

    public bool IsBuiltIn => true;

    public IAgentDriver Driver { get; }

    public bool SupportsConversationHistory { get; }

    public AgentPluginSettingsMetadata Settings { get; }

    public AgentPluginDetectionMetadata Detection { get; }

    public AgentPluginValidationMetadata Validation { get; }

    public AgentPluginHistoryMetadata History { get; }

    public AgentPluginLaunchMetadata Launch { get; }

    public IReadOnlyList<AgentCommandPreset> CommandPresets { get; }

    public AgentCommandPreset DefaultCommandPreset { get; }

    public string DefaultModel { get; }

    public IAgent CreateAgent(AgentOptions options) => _agentFactory(options);

    public AgentLaunchSpec BuildLaunchSpec(AgentPluginLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return CreateAgent(request.Options).BuildLaunchSpec(request.UserArgs, request.ResumeSessionId, request.StudioMode);
    }
}
