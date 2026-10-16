namespace CcDirector.Core.ApiMode.Pi;

/// <summary>
/// One command line for Pi's standard input. <see cref="Line"/> is the JSON to write, without the line
/// feed (the transport adds it). <see cref="Id"/> is the id inside it, which Pi repeats on its response.
/// <see cref="PromptId"/> is the Director's prompt id, set only for a <c>prompt</c> command.
/// </summary>
public sealed record PiCommand(string Id, string Type, string Line, string? PromptId = null);

/// <summary>What Pi answered to a command that is not a prompt (<c>get_state</c>, <c>abort</c>, ...).
/// <see cref="DataJson"/> is the response's <c>data</c> exactly as Pi wrote it, or null when it had none.</summary>
public sealed record PiCommandResult(string CommandId, string Command, bool Success, string? Error, string? DataJson);

/// <summary>
/// What the dialect made of one input.
/// </summary>
/// <param name="Events">Session events to report upward, in order.</param>
/// <param name="CommandsToWrite">Lines the dialect needs written to Pi now (the cancellation of an extension's
/// dialog, so it can never hang a turn).</param>
/// <param name="Defects">Things that mean our own code is wrong (Pi could not parse a line we wrote, a response
/// to a command we never wrote, a prompt written while another was in flight). Each is already logged; the
/// caller raises it as an error. Never shown as a receipt.</param>
/// <param name="Result">Pi's answer to a command that is not a prompt, when this input was one.</param>
/// <param name="StopReason">Set when this Pi process must be stopped (a resume that found no conversation).
/// The reason is carried into <see cref="CcDirector.Gateway.Contracts.ApiMode.ConnectionEnded"/>.</param>
public sealed record PiDialectStep(
    IReadOnlyList<CcDirector.Gateway.Contracts.ApiMode.SessionEvent> Events,
    IReadOnlyList<PiCommand> CommandsToWrite,
    IReadOnlyList<string> Defects,
    PiCommandResult? Result,
    string? StopReason);
