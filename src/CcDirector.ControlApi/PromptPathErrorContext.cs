using CcDirector.Core.ErrorReports;
using CcDirector.Core.Input;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// ONE FAILED PROMPT IS ONE INCIDENT (the Error Logging mission, issue #3675, step 4b). On 9 October 2026 one phone
/// prompt refused six times reached the error store as eighteen unlinked rows - the failed delivery, the command's own
/// failure and the wait lines, written by different classes with nothing in common. Every error line a prompt command
/// causes now carries that command's id as its correlation id, and its session id, because the command's handler opens
/// this context around the whole of it - the prompt verb, the send under it, and the late outcome that runs on after the
/// verb has answered.
///
/// Only the commands that send a prompt open one: <c>prompt</c>, and <c>create</c>, whose first prompt is typed after
/// the session starts. Every other verb's errors keep grouping as they did - one row with a count - because a verb that
/// fails on every poll would otherwise spend the hourly report budget one command at a time.
/// </summary>
internal static class PromptPathErrorContext
{
    public const string PromptVerb = "prompt";
    public const string CreateVerb = "create";

    /// <summary>What the user was doing when a <c>create</c> command's first prompt failed.</summary>
    public const string StartSessionAction = "start a session with its first prompt";

    /// <summary>
    /// The context for <paramref name="command"/>, or null for a verb that sends no prompt. Every row it covers is marked
    /// NOT user-visible: the one row a person sees, the failed delivery, marks itself
    /// (<see cref="PromptDeliveryFailures.RecordFailedDelivery"/>).
    /// </summary>
    public static ErrorContext? ForCommand(DirectorCommand? command) => command?.Verb switch
    {
        PromptVerb => ErrorContext.Begin(correlationId: command.CommandId, sessionId: command.SessionId,
            action: PromptDeliveryFailures.SendPromptAction, userVisible: false),
        CreateVerb => ErrorContext.Begin(correlationId: command.CommandId, action: StartSessionAction, userVisible: false),
        _ => null,
    };
}
