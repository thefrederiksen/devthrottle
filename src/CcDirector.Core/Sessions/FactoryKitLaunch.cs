using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Sessions;

/// <summary>
/// What a written kit holds that changes how its agent is started: the seat's rules, its secret names, and whether it
/// carries factory skills. Read from the kit folder by <see cref="FactoryKitLaunch.Read"/>.
/// </summary>
/// <param name="Permissions">The <c>permissions</c> object of <c>rules.json</c>, exactly as the Gateway composed it.</param>
/// <param name="HasRules">True when it holds at least one <c>deny</c> or <c>ask</c> rule.</param>
/// <param name="Secrets">The secret names in <c>kit.json</c>; empty for a rules-only kit.</param>
/// <param name="HasPlugin">True when the kit holds the <c>claude-plugin/</c> folder of factory skills.</param>
public sealed record FactoryKitContents(JsonObject Permissions, bool HasRules, IReadOnlyList<string> Secrets, bool HasPlugin);

/// <summary>
/// What a seat's kit adds to its agent's launch line: the seat's own settings file (null when the kit carries no rule)
/// and the factory-skill plugin folder (null when it carries none). Both null for any agent but Claude Code.
/// </summary>
public sealed record FactoryKitLaunchPlan(string? SettingsPath, string? PluginDirectory);

/// <summary>
/// RULES, SKILLS AND SECRETS AT LAUNCH (Factory Definition mission, phase 5, design sections 5 and 6; phase 0 proofs
/// (a) and (c)). The kit is written by <see cref="FactoryRunKit"/>; this class reads it and decides what the start
/// does with it, before the agent starts:
///
///   - an agent that cannot take the kit's rules or skills is refused with the reason in
///     <see cref="FactoryRulesSupport"/> / <see cref="FactorySkillSupport"/> - the same table the Gateway's publish and
///     spawn door read, so the two can never disagree;
///   - every secret the seat lists must be held on this machine and allowed for run, or the start is refused naming it;
///   - a Claude Code seat with rules gets its OWN settings file in the kit, passed as its one <c>--settings</c>, and a
///     Claude Code seat with factory skills gets <c>--plugin-dir</c>. Nothing is written into the shared hooks file,
///     the checkout or the user's skills folders.
///
/// Every function here but <see cref="PlanFor"/> is pure; <see cref="PlanFor"/> writes only inside the kit folder.
/// </summary>
public static class FactoryKitLaunch
{
    /// <summary>
    /// Read the kit's rules, secret names and plugin folder. Throws <see cref="InvalidOperationException"/> with a
    /// sentence when <c>rules.json</c> or <c>kit.json</c> is missing or not the shape the Gateway writes: a seat whose
    /// rules cannot be read is never started as though it had none.
    /// </summary>
    public static FactoryKitContents Read(string kitDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kitDirectory);
        var rulesPath = Path.Combine(kitDirectory, FactoryRunKit.RulesFileName);
        var kitPath = Path.Combine(kitDirectory, FactoryRunKit.KitFileName);
        if (!File.Exists(rulesPath))
            throw new InvalidOperationException($"the kit has no {FactoryRunKit.RulesFileName}, so its rules cannot be read");
        if (!File.Exists(kitPath))
            throw new InvalidOperationException($"the kit has no {FactoryRunKit.KitFileName}, so its secrets cannot be read");

        var permissions = ReadPermissions(File.ReadAllText(rulesPath));
        var hasRules = permissions.Any(p => p.Value is JsonArray list && list.Count > 0);
        var secrets = ReadSecrets(File.ReadAllText(kitPath));
        var hasPlugin = Directory.Exists(Path.Combine(kitDirectory, FactoryRunKit.PluginFolderName));

        FileLog.Write($"[FactoryKitLaunch] Read: dir={kitDirectory}, hasRules={hasRules}, secrets={secrets.Count}, hasPlugin={hasPlugin}");
        return new FactoryKitContents(permissions, hasRules, secrets, hasPlugin);
    }

    /// <summary>
    /// Null when <paramref name="agent"/> can take everything the kit carries; otherwise the sentence that refuses the
    /// start, carrying the table's own reason. Rules are asked first, then skills. A kit with neither never consults
    /// the tables, so a session on any agent with an empty kit starts as it would without one.
    /// </summary>
    public static string? WhyTheAgentCannotTakeIt(AgentKind agent, FactoryKitContents kit)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (kit.HasRules && FactoryRulesSupport.For(agent) is { CanTake: false } rules)
            return $"No session was started: {rules.Reason}";
        if (kit.HasPlugin && FactorySkillSupport.For(agent) is { CanLoad: false } skills)
            return $"No session was started: {skills.Reason}";
        return null;
    }

    /// <summary>
    /// Null when <paramref name="machine"/> holds every secret in <paramref name="secrets"/> and each is allowed for
    /// run; otherwise the sentence that refuses the start, about the first secret in the seat's order that fails. A
    /// cc-secrets that could not answer is said as such, never read as "not held".
    /// </summary>
    public static string? WhyTheSecretsRefuse(string seat, string machine, IReadOnlyList<string> secrets, FactoryMachineAnswer answer)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(answer);
        if (secrets.Count == 0) return null;
        if (answer.SecretsUnavailable is { } unavailable)
            return $"The seat {seat} needs the secret '{secrets[0]}', and {machine} could not say whether it holds it, " +
                   $"so no session was started: {unavailable}";
        foreach (var name in secrets)
        {
            var found = answer.Secrets.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
            if (found is null || !found.Held)
                return $"The seat {seat} needs the secret '{name}', which {machine} does not hold, so no session was " +
                       "started. Add it there with cc-secrets.";
            if (!found.AllowedForRun)
                return $"The seat {seat} needs the secret '{name}' on {machine}, so no session was started: " +
                       (found.NotAllowedReason ?? $"Secret '{name}' is not allowed for 'run'.");
        }
        return null;
    }

    /// <summary>
    /// What the kit adds to the launch line of an agent of <paramref name="agent"/>. For Claude Code with rules it
    /// writes the seat's own <c>settings.json</c> into the kit (<see cref="ClaudeHookInstaller.WriteSessionSettings"/>,
    /// the hooks of <paramref name="hooksDirectory"/> plus the rules), and names the plugin folder when the kit has
    /// one. Every other agent gets nothing here: <see cref="WhyTheAgentCannotTakeIt"/> has already refused one whose
    /// kit carries anything.
    /// </summary>
    public static FactoryKitLaunchPlan PlanFor(AgentKind agent, string kitDirectory, FactoryKitContents kit,
        string hooksDirectory, bool forWindows)
    {
        ArgumentNullException.ThrowIfNull(kit);
        if (agent != AgentKind.ClaudeCode)
            return new FactoryKitLaunchPlan(null, null);

        var settings = kit.HasRules
            ? ClaudeHookInstaller.WriteSessionSettings(kitDirectory, hooksDirectory, forWindows, kit.Permissions)
            : null;
        var plugin = kit.HasPlugin ? Path.Combine(kitDirectory, FactoryRunKit.PluginFolderName) : null;
        FileLog.Write($"[FactoryKitLaunch] PlanFor: agent={agent}, settings={settings ?? "(shared)"}, plugin={plugin ?? "(none)"}");
        return new FactoryKitLaunchPlan(settings, plugin);
    }

    /// <summary>
    /// The Claude Code launch line: <paramref name="args"/>, then the ONE <c>--settings</c> - the seat's own file when
    /// the plan has one, else the shared file, else none - then <c>--plugin-dir</c> when the plan names a plugin. With
    /// no plan, or a plan that adds nothing, this is exactly the line a session that is not a seat gets.
    /// </summary>
    public static string ClaudeArguments(string args, string? sharedSettingsPath, FactoryKitLaunchPlan? plan)
    {
        var settings = plan?.SettingsPath ?? sharedSettingsPath;
        if (!string.IsNullOrEmpty(settings))
            args = $"{args} --settings \"{settings}\"".Trim();
        if (plan?.PluginDirectory is { } plugin)
            args = $"{args} --plugin-dir \"{plugin}\"".Trim();
        return args;
    }

    // rules.json is {"permissions": {"deny": [...], "ask": [...]}} (FactoryKitComposer.Rules). Every list in it must be
    // a list of strings; anything else is a kit this Director cannot honour, and is said so.
    private static JsonObject ReadPermissions(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the kit's {FactoryRunKit.RulesFileName} is not JSON ({ex.Message}), so its rules cannot be read", ex);
        }
        if (root is not JsonObject doc || doc["permissions"] is not JsonObject permissions)
            throw new InvalidOperationException($"the kit's {FactoryRunKit.RulesFileName} has no 'permissions' object, so its rules cannot be read");
        foreach (var (key, value) in permissions)
        {
            if (value is not JsonArray list || list.Any(r => r is not JsonValue v || v.GetValueKind() != JsonValueKind.String))
                throw new InvalidOperationException($"the kit's {FactoryRunKit.RulesFileName} has a '{key}' that is not a list of rules, so its rules cannot be read");
        }
        return permissions;
    }

    // kit.json carries "secrets" on a seat kit only; a rules-only kit has none.
    private static IReadOnlyList<string> ReadSecrets(string text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(text); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"the kit's {FactoryRunKit.KitFileName} is not JSON ({ex.Message}), so its secrets cannot be read", ex);
        }
        if (root is not JsonObject doc)
            throw new InvalidOperationException($"the kit's {FactoryRunKit.KitFileName} is not an object, so its secrets cannot be read");
        if (doc["secrets"] is null)
            return Array.Empty<string>();
        if (doc["secrets"] is not JsonArray list || list.Any(r => r is not JsonValue v || v.GetValueKind() != JsonValueKind.String))
            throw new InvalidOperationException($"the kit's {FactoryRunKit.KitFileName} has 'secrets' that is not a list of names, so its secrets cannot be read");
        return list.Select(n => n!.GetValue<string>()).ToList();
    }
}
