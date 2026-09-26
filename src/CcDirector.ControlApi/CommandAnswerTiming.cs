using CcDirector.Core.Drivers;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// A GATEWAY COMMAND THAT TAKES LONG TO ANSWER SAYS SO (Voice Delivery mission, phase 6). On 25 September 2026 (the
/// phase 4 QA run, case2f) the delivery-state verb answered 61 seconds after it was received, and the log had only the
/// receipt and the result - nothing said the Gateway had stopped waiting half a minute earlier. Every command the
/// Gateway sends down the tunnel is now answered through here: past a few seconds it writes what it is answering and
/// the Gateway's limit, and when it ends, how long it took and what it answered (the phase 3 lines,
/// <see cref="SendWaitNotice"/>). A quick answer writes nothing.
///
/// What this covers is the Director's own part: from the handler receiving the command to the answer being handed back
/// to the connection. The time before the handler (the frame queued in the connection) and after it (the answer written
/// to the socket) is outside this process's view; the Gateway's own line for the command
/// (<c>DirectorCommandRouter</c>: the outcome, or TIMED OUT) is the other end of the same wait.
/// </summary>
internal static class CommandAnswerTiming
{
    /// <summary>The Gateway's wait for a Director command, in words: <c>DirectorCommandRouter.DefaultCommandTimeout</c> on the
    /// Gateway, which this assembly does not reference.</summary>
    internal const string GatewayLimit = "the Gateway stops waiting at 30s";

    /// <summary>Runs <paramref name="dispatch"/> and returns its answer untouched, saying so when the answer runs long.</summary>
    internal static async Task<DirectorCommandResult> AnswerAsync(DirectorCommand command, Func<Task<DirectorCommandResult>> dispatch)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(dispatch);

        var answering = dispatch();
        if (answering.IsCompleted) return await answering;

        var notice = new SendWaitNotice("GatewayStreamClient",
            $"the answer to verb={command.Verb}, sid={command.SessionId}, cmdId={command.CommandId}", GatewayLimit);
        using (var cts = new CancellationTokenSource())
        {
            if (await Task.WhenAny(answering, Task.Delay(SendWaitNotice.NoticeAfter, cts.Token)) != answering)
                notice.Check();
            cts.Cancel();
        }

        try
        {
            var result = await answering;
            notice.End($"answered {result.Status}");
            return result;
        }
        catch (Exception ex)
        {
            notice.End($"FAILED: {ex.Message}");
            throw;
        }
    }
}
