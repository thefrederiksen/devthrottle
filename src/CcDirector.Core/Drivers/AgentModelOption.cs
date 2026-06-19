namespace CcDirector.Core.Drivers;

/// <summary>
/// One selectable model an agent driver knows about. Mirrors <see cref="AgentSlashCommand"/>:
/// the driver is the single source of truth for what models a tool offers, the same way it
/// owns the tool's slash commands. The Edit Agent dialog's model picker is populated from
/// <see cref="IAgentDriver.KnownModels"/>, so model knowledge lives with the driver, not the UI.
/// </summary>
/// <param name="Id">
/// The value passed after the driver's <see cref="IAgentDriver.ModelFlag"/> (e.g. <c>opus[1m]</c>).
/// Never empty - "use the tool's own default" is represented by an unset model, not an option here.
/// </param>
/// <param name="DisplayName">Human-readable label shown in the picker (e.g. "Opus 4.8 (1M context)").</param>
/// <param name="Description">One-line help shown under the label.</param>
/// <param name="Badge">Optional short tag shown beside the label (e.g. "1M context"); empty for none.</param>
public sealed record AgentModelOption(
    string Id,
    string DisplayName,
    string Description,
    string Badge = "");
