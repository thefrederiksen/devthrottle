using CcDirector.Core.Utilities;

namespace CcDirector.Core.Agents;

/// <summary>
/// One selectable command-line preset for an agent tool: a friendly name plus the exact
/// argument string it contributes. An empty <see cref="Arguments"/> means "no extra flags"
/// (the standard launch). Presets are the safe, named alternatives a user picks between in
/// the Tools UI without hand-typing flags; a free-text override is offered alongside them.
/// </summary>
/// <param name="Name">Friendly preset name shown in the UI (e.g. "Standard").</param>
/// <param name="Arguments">The argument string this preset contributes (may be empty).</param>
public sealed record AgentCommandPreset(string Name, string Arguments);

/// <summary>
/// The built-in recommended defaults for one known agent tool: its display name, the ordered
/// list of command-line presets (the first is the recommended default), and the recommended
/// default model. This is the catalog entry the Tools page pre-populates a tool from.
/// </summary>
/// <param name="Tool">Which agent CLI this entry describes.</param>
/// <param name="DisplayName">Human-readable tool name.</param>
/// <param name="Presets">
/// Ordered command-line presets. <c>Presets[0]</c> is the recommended/default preset. For every
/// agent that has a full-permission flag, that IS the default, so the tool launches able to work
/// without stopping for approval; "Standard" is offered as the non-default alternative.
/// </param>
/// <param name="DefaultModel">
/// Recommended default model for this tool, or empty when the tool has no model argument.
/// </param>
public sealed record AgentToolCatalogEntry(
    AgentKind Tool,
    string DisplayName,
    IReadOnlyList<AgentCommandPreset> Presets,
    string DefaultModel)
{
    /// <summary>The recommended/default command-line preset (the first in the list).</summary>
    public AgentCommandPreset DefaultPreset => Presets[0];
}

/// <summary>
/// The built-in catalog of known agent CLI tools. Each entry ships a recommended default
/// command line (as the first preset) and a recommended default model, so the machine-level
/// Tools page can pre-populate a tool the user adds without the user hand-typing flags.
///
/// Design decision (the unattended-permissions change): for EVERY agent that has a
/// full-permission command-line flag, the recommended default preset is now the one that carries
/// it, so a freshly detected tool launches ready to work without stopping for approvals. A fleet
/// of agents that must be approved by hand on each tool call does not run at all - that is the
/// whole point of the product - so "asks for permission" is the opt-in, not the default. The
/// always-visible command-line preview strip on the Agents tab (issue #436) is the safety net that
/// makes the active permission flag impossible to miss, and "Standard" remains selectable for every
/// tool.
///
/// Claude Code specifically (superseding issues #436 and #391): the default is the NEW
/// <c>--permission-mode auto</c> mode, NOT <c>--dangerously-skip-permissions</c>. Bypass remains
/// available as its own named preset for anyone who wants it.
///
/// Every flag below was read from the installed tool's own <c>--help</c> output, not inferred.
/// Pi and OpenCode are deliberately absent: neither exposes ANY full-permission flag on its command
/// line (Pi's <c>--approve</c> only trusts project-local files; OpenCode carries permissions in its
/// config file), so there is nothing to default them to and they are left on Standard.
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>The name of the recommended standard preset, common to every tool.</summary>
    public const string StandardPresetName = "Standard";

    /// <summary>
    /// The name of Claude's automatic-permissions preset - the recommended default. Adds
    /// <see cref="ClaudeAutomaticModeArg"/>.
    /// </summary>
    public const string ClaudeAutomaticPresetName = "Automatic";

    /// <summary>
    /// Claude's automatic permission mode. Verified from <c>claude --help</c>, which lists
    /// <c>--permission-mode</c> choices "acceptEdits", "auto", "bypassPermissions", "manual",
    /// "dontAsk", "plan".
    /// </summary>
    public const string ClaudeAutomaticModeArg = "--permission-mode auto";

    /// <summary>
    /// The name of Claude's skip-all-permissions preset. Deliberately NOT the default: Anthropic
    /// does not recommend <c>--dangerously-skip-permissions</c>, so neither do we. It stays a
    /// first-class choice for anyone who wants it.
    /// </summary>
    public const string ClaudeSkipPermissionsPresetName = "Skip permissions";

    /// <summary>
    /// The model a freshly configured Claude Code launches with: <c>opus</c>, the alias Claude Code
    /// resolves to the newest Opus. It must never be empty. With no <c>--model</c> flag Claude Code
    /// picks the account's own fallback, and on 6 November 2026 that was Haiku 4.5: every session
    /// and factory run on a machine whose entry was set up blank ran on the weakest model, and
    /// nothing on screen said so. The owner's ruling: Opus is the default, everywhere.
    /// </summary>
    public const string ClaudeDefaultModel = "opus";

    /// <summary>
    /// The name this same preset carried before the automatic mode existed. It ALWAYS meant
    /// <see cref="ClaudeSkipPermissionsArg"/> and it still does - only the label changed, because
    /// "Automatic (skip permissions)" now reads as the automatic mode, which it is not. Recognized
    /// when reading a persisted config so an existing entry keeps the behaviour it already had,
    /// rather than being migrated or dropping through the unknown-name fallback.
    /// </summary>
    public const string LegacyClaudeAutomaticPresetName = "Automatic (skip permissions)";

    /// <summary>The exact Claude flag the bypass preset adds (and the standard preset omits).</summary>
    public const string ClaudeSkipPermissionsArg = "--dangerously-skip-permissions";

    /// <summary>The name of the Cursor permission-bypass preset (issue #517).</summary>
    public const string CursorAutomaticPresetName = "Automatic (yolo)";

    /// <summary>The name of the Gemini auto-accept preset.</summary>
    public const string GeminiAutomaticPresetName = "Automatic (yolo)";

    /// <summary>
    /// Gemini's auto-accept-everything flag. Verified from <c>gemini --help</c>:
    /// "-y, --yolo  Automatically accept all actions (aka YOLO mode)".
    /// </summary>
    public const string GeminiYoloArg = "--yolo";

    /// <summary>The name of the Grok auto-approve preset.</summary>
    public const string GrokAutomaticPresetName = "Automatic (approve all)";

    /// <summary>
    /// Grok's auto-approve flag. Verified from <c>grok --help</c>:
    /// "--always-approve  Auto-approve all tool executions".
    /// </summary>
    public const string GrokAlwaysApproveArg = "--always-approve";

    /// <summary>The name of the Codex full-access preset.</summary>
    public const string CodexFullAccessPresetName = "Full access";

    /// <summary>The exact Codex flag for full filesystem/network access with no confirmation prompts.</summary>
    public const string CodexFullAccessArg = "--dangerously-bypass-approvals-and-sandbox";

    /// <summary>
    /// The exact Cursor flag the automatic preset adds (and the standard preset omits).
    /// Cursor's permission-bypass equivalent of Claude's --dangerously-skip-permissions
    /// is <c>--force</c> (assumption A2).
    /// </summary>
    public const string CursorForceArg = "--force";

    /// <summary>The name of the GitHub Copilot opt-in permission-bypass preset (issue #625).</summary>
    public const string CopilotAutomaticPresetName = "Automatic (yolo)";

    /// <summary>
    /// The exact GitHub Copilot flag the automatic preset adds (and the standard preset omits).
    /// Verified from <c>copilot --help</c>: "--allow-all  Enable all permissions (equivalent to
    /// --allow-all-tools --allow-all-paths --allow-all-urls)".
    /// </summary>
    public const string CopilotAllowAllArg = "--allow-all";

    /// <summary>
    /// The command-line argument that lets a tool work WITHOUT stopping for approval, per agent -
    /// the argument its recommended default preset carries. Null for an agent with no such flag
    /// (Pi, OpenCode) and for kinds outside the catalog, which have nothing to apply.
    ///
    /// This is the ONE place that answers "what makes this agent run unattended", so the launch
    /// defaults, the New Session dialog and the presets below cannot drift apart.
    /// </summary>
    public static string? UnattendedPermissionArg(AgentKind tool) => tool switch
    {
        AgentKind.ClaudeCode => ClaudeAutomaticModeArg,
        AgentKind.Codex => CodexFullAccessArg,
        AgentKind.Gemini => GeminiYoloArg,
        AgentKind.Grok => GrokAlwaysApproveArg,
        AgentKind.Cursor => CursorForceArg,
        AgentKind.Copilot => CopilotAllowAllArg,
        _ => null,
    };

    /// <summary>
    /// EVERY permission-affecting argument this agent understands, not just the default one. Used to
    /// decide whether a resolved command line ALREADY settles the permission question, so applying
    /// the unattended default never bolts a second, conflicting permission flag onto a line that a
    /// user deliberately configured. Empty for an agent with no permission flags.
    /// </summary>
    public static IReadOnlyList<string> KnownPermissionArgs(AgentKind tool) => tool switch
    {
        // "--permission-mode" (without a value) covers auto, bypassPermissions, plan, acceptEdits,
        // dontAsk and manual in one check - any of them is a deliberate choice we must not override.
        AgentKind.ClaudeCode => ["--permission-mode", ClaudeSkipPermissionsArg],
        AgentKind.Codex => [CodexFullAccessArg, "--sandbox", "--ask-for-approval", "-s ", "-a "],
        AgentKind.Gemini => [GeminiYoloArg, "-y", "--approval-mode"],
        AgentKind.Grok => [GrokAlwaysApproveArg, "--yolo", "--permission-mode", "--sandbox"],
        AgentKind.Cursor => [CursorForceArg, "--yolo", "-f", "--sandbox"],
        AgentKind.Copilot => [CopilotAllowAllArg, "--allow-all-tools", "--deny-tool"],
        _ => [],
    };

    /// <summary>
    /// Resolve a persisted preset name to the name this catalog uses today, so a config written by
    /// an older build resolves DELIBERATELY instead of falling through the unknown-name fallback.
    /// Unknown names are returned unchanged (the caller's own fallback then applies).
    ///
    /// Claude's <see cref="LegacyClaudeAutomaticPresetName"/> maps to
    /// <see cref="ClaudeSkipPermissionsPresetName"/> - the SAME preset under its new label, carrying
    /// the same flag. This is a rename, NOT a migration: an existing entry keeps launching exactly
    /// as it did. Nothing on disk is rewritten; the new default applies only to entries created from
    /// here on.
    /// </summary>
    public static string CanonicalPresetName(AgentKind tool, string presetName)
    {
        if (tool == AgentKind.ClaudeCode
            && string.Equals(presetName, LegacyClaudeAutomaticPresetName, StringComparison.OrdinalIgnoreCase))
        {
            FileLog.Write($"[AgentToolCatalog] CanonicalPresetName: legacy \"{presetName}\" -> \"{ClaudeSkipPermissionsPresetName}\" (same flag, new label)");
            return ClaudeSkipPermissionsPresetName;
        }

        return presetName;
    }

    private static readonly IReadOnlyList<AgentToolCatalogEntry> CatalogEntries = BuildCatalog();

    /// <summary>The known agent tools, in display order, with their recommended defaults.</summary>
    public static IReadOnlyList<AgentToolCatalogEntry> Entries => CatalogEntries;

    /// <summary>Look up the catalog entry for one tool. Throws if the tool is not in the catalog.</summary>
    public static AgentToolCatalogEntry GetEntry(AgentKind tool)
    {
        FileLog.Write($"[AgentToolCatalog] GetEntry: tool={tool}");
        foreach (var entry in CatalogEntries)
        {
            if (entry.Tool == tool)
                return entry;
        }

        throw new NotSupportedException($"[AgentToolCatalog] Tool {tool} is not in the agent tool catalog.");
    }

    /// <summary>True when the tool has a built-in catalog entry.</summary>
    public static bool Contains(AgentKind tool)
    {
        foreach (var entry in CatalogEntries)
        {
            if (entry.Tool == tool)
                return true;
        }

        return false;
    }

    private static IReadOnlyList<AgentToolCatalogEntry> BuildCatalog()
    {
        // Claude Code: "Automatic" (--permission-mode auto) is the recommended default (index 0).
        // "Bypass permissions" (--dangerously-skip-permissions) is still offered - it is simply no
        // longer what a freshly configured Claude launches with.
        var claude = new AgentToolCatalogEntry(
            AgentKind.ClaudeCode,
            "Claude Code",
            new[]
            {
                new AgentCommandPreset(ClaudeAutomaticPresetName, ClaudeAutomaticModeArg),
                new AgentCommandPreset(ClaudeSkipPermissionsPresetName, ClaudeSkipPermissionsArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            ClaudeDefaultModel);

        // Pi and OpenCode expose NO full-permission flag on their command lines (read from their own
        // --help), so they get the standard preset only - there is no honest default to give them.
        var pi = StandardOnly(AgentKind.Pi, "Pi");
        var openCode = StandardOnly(AgentKind.OpenCode, "OpenCode");

        var codex = new AgentToolCatalogEntry(
            AgentKind.Codex,
            "Codex",
            new[]
            {
                new AgentCommandPreset(CodexFullAccessPresetName, CodexFullAccessArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        var gemini = new AgentToolCatalogEntry(
            AgentKind.Gemini,
            "Gemini",
            new[]
            {
                new AgentCommandPreset(GeminiAutomaticPresetName, GeminiYoloArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        // Cursor (issue #517): --force is Cursor's permission-bypass equivalent.
        var cursor = new AgentToolCatalogEntry(
            AgentKind.Cursor,
            "Cursor",
            new[]
            {
                new AgentCommandPreset(CursorAutomaticPresetName, CursorForceArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        var grok = new AgentToolCatalogEntry(
            AgentKind.Grok,
            "Grok",
            new[]
            {
                new AgentCommandPreset(GrokAutomaticPresetName, GrokAlwaysApproveArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        // GitHub Copilot (issue #625): --allow-all enables every permission.
        var copilot = new AgentToolCatalogEntry(
            AgentKind.Copilot,
            "GitHub Copilot",
            new[]
            {
                new AgentCommandPreset(CopilotAutomaticPresetName, CopilotAllowAllArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        return new[] { claude, pi, codex, gemini, openCode, cursor, grok, copilot };
    }

    private static AgentToolCatalogEntry StandardOnly(AgentKind tool, string displayName) =>
        new(tool, displayName, new[] { new AgentCommandPreset(StandardPresetName, "") }, "");
}
