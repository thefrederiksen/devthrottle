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
/// Ordered command-line presets. <c>Presets[0]</c> is the recommended/default preset.
/// For Claude Code the default is "Automatic (skip permissions)" (issue #436): it adds
/// <c>--dangerously-skip-permissions</c>. The "Standard" command line is offered as the
/// non-default alternative.
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
/// Design decision (issue #436, supersedes issue #391): Claude Code's recommended default is
/// now "Automatic (skip permissions)" - it DOES contain <c>--dangerously-skip-permissions</c>,
/// so a freshly detected/configured Claude tool launches in skip-permissions mode. This reverses
/// the safe default chosen in #391; the always-visible command-line preview strip on the Agents
/// tab (issue #436) is the safety net that makes the skip-permissions flag impossible to miss.
/// The "Standard" command line (no skip-permissions) remains available as the non-default preset.
/// </summary>
public static class AgentToolCatalog
{
    /// <summary>The name of the recommended standard preset, common to every tool.</summary>
    public const string StandardPresetName = "Standard";

    /// <summary>The name of the Claude opt-in skip-all-permissions preset.</summary>
    public const string ClaudeAutomaticPresetName = "Automatic (skip permissions)";

    /// <summary>The exact Claude flag the automatic preset adds (and the standard preset omits).</summary>
    public const string ClaudeSkipPermissionsArg = "--dangerously-skip-permissions";

    /// <summary>The name of the Cursor opt-in permission-bypass preset (issue #517).</summary>
    public const string CursorAutomaticPresetName = "Automatic (yolo)";

    /// <summary>The name of the Codex opt-in full-access preset.</summary>
    public const string CodexFullAccessPresetName = "Full access";

    /// <summary>The exact Codex flags for full filesystem and network access with no approval prompts.</summary>
    public const string CodexFullAccessArg = "--sandbox danger-full-access --ask-for-approval never";

    /// <summary>
    /// The exact Cursor flag the automatic preset adds (and the standard preset omits).
    /// Cursor's permission-bypass equivalent of Claude's --dangerously-skip-permissions
    /// is <c>--force</c> (assumption A2).
    /// </summary>
    public const string CursorForceArg = "--force";

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
        // Claude Code (issue #436, supersedes #391): "Automatic (skip permissions)" is the
        // recommended default (index 0), so a freshly configured Claude launches with
        // --dangerously-skip-permissions. The STANDARD (no skip-permissions) preset is offered
        // as the non-default alternative.
        var claude = new AgentToolCatalogEntry(
            AgentKind.ClaudeCode,
            "Claude Code",
            new[]
            {
                new AgentCommandPreset(ClaudeAutomaticPresetName, ClaudeSkipPermissionsArg),
                new AgentCommandPreset(StandardPresetName, ""),
            },
            "");

        // The other agents have a single standard preset and no recommended model argument
        // (their model is selected inside the tool, not via a Director-passed flag in v1).
        var pi = StandardOnly(AgentKind.Pi, "Pi");
        var codex = new AgentToolCatalogEntry(
            AgentKind.Codex,
            "Codex",
            new[]
            {
                new AgentCommandPreset(StandardPresetName, ""),
                new AgentCommandPreset(CodexFullAccessPresetName, CodexFullAccessArg),
            },
            "");
        var gemini = StandardOnly(AgentKind.Gemini, "Gemini");
        var openCode = StandardOnly(AgentKind.OpenCode, "OpenCode");

        // Cursor (issue #517): Standard (no flags) is the default; "Automatic (yolo)"
        // is the opt-in permission-bypass preset that adds --force (assumption A2).
        var cursor = new AgentToolCatalogEntry(
            AgentKind.Cursor,
            "Cursor",
            new[]
            {
                new AgentCommandPreset(StandardPresetName, ""),
                new AgentCommandPreset(CursorAutomaticPresetName, CursorForceArg),
            },
            "");

        var grok = StandardOnly(AgentKind.Grok, "Grok");

        return new[] { claude, pi, codex, gemini, openCode, cursor, grok };
    }

    private static AgentToolCatalogEntry StandardOnly(AgentKind tool, string displayName) =>
        new(tool, displayName, new[] { new AgentCommandPreset(StandardPresetName, "") }, "");
}
