using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Configuration;

/// <summary>
/// The Gateway's factory agents switch (Website Business Factory, product track). Read from the
/// <c>factoryAgents</c> block of config.json:
///
/// <code>
/// { "factoryAgents": { "enabled": true } }
/// </code>
///
/// DEFAULT OFF. Only a JSON boolean <c>true</c> turns it on, so a missing block, a missing key, or a value
/// that is not a boolean leaves it off - the same opt-in rule as the stream switch (issue #1176). While it is
/// off the Gateway does not map the factory surface at all. The Gateway reads it once at construction; a
/// test passes an explicit override to the GatewayHost constructor instead.
/// </summary>
public static class FactoryAgentsConfig
{
    /// <summary>The config.json block that holds the switch.</summary>
    public const string BlockKey = "factoryAgents";

    /// <summary>The key inside the block.</summary>
    public const string EnabledKey = "enabled";

    /// <summary>Whether factory agents are switched on in this machine's config.json.</summary>
    public static bool IsEnabled()
    {
        var enabled = Parse(CcDirectorConfigService.ReadRaw());
        FileLog.Write($"[FactoryAgentsConfig] IsEnabled: {BlockKey}.{EnabledKey}={enabled}");
        return enabled;
    }

    /// <summary>The switch as written in one config document. Pure, so it is tested directly.</summary>
    public static bool Parse(JsonObject? root)
        => root?[BlockKey] is JsonObject block
           && block[EnabledKey] is JsonValue v
           && v.GetValueKind() == JsonValueKind.True;
}
