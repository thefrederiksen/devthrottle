using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi.Restart;

/// <summary>How the launcher step ended.</summary>
public enum LauncherRestartStepVerdict
{
    /// <summary>The launcher accepted the guarded restart. This process is about to be stopped.</summary>
    Accepted,

    /// <summary>The machine was checked again and a guarded restart must not be sent. The launcher was
    /// NOT asked.</summary>
    CapabilityRefused,

    /// <summary>The launcher was asked and did not accept.</summary>
    LauncherRefused,
}

/// <summary>What the launcher step found.</summary>
/// <param name="Verdict">How it ended.</param>
/// <param name="Refusal">Why a guarded restart was not sent, or was not accepted, in plain words. Null
/// when it was accepted.</param>
/// <param name="Answer">The launcher's answer, when it was asked at all.</param>
public sealed record LauncherRestartStepResult(
    LauncherRestartStepVerdict Verdict, string? Refusal, LauncherRestartAnswer? Answer);

/// <summary>
/// THE ONE WAY THIS DIRECTOR ASKS ITS OWN LAUNCHER TO RESTART IT: check the machine AGAIN, and only then
/// ask for a restart only if the Director is empty.
///
/// It was the last two steps of <see cref="DirectorRestartCycle"/>, inline. It is a class of its own
/// because the smart shutdown (mission document "Smart Director Restart", section 5.3 item 9) restarts
/// through the same launcher, and two callers each holding their own copy of "re-check, then ask" is two
/// copies of the one rule that makes only-if-empty a guarantee. The cycle's comment says why the
/// re-check is not a duplicate of the Gateway's; that reasoning is this class's too.
///
/// It reports nothing and it words no workspace: each caller wraps the refusal in its own sentence.
/// </summary>
public static class DirectorLauncherRestartStep
{
    /// <summary>Re-check the machine, then ask the launcher.</summary>
    /// <param name="gateway">The Gateway seam.</param>
    /// <param name="machine">This machine, as the restart route names it.</param>
    /// <param name="exePath">This process's executable, sent to the route's slot guard as evidence.</param>
    /// <param name="beforeAsk">Run after the re-check passed and BEFORE the launcher is asked. A
    /// successful ask may never return to this process, so whatever must be said first is said here.</param>
    /// <param name="ct">Cancellation.</param>
    public static async Task<LauncherRestartStepResult> RunAsync(
        IRestartCycleGateway gateway, string machine, string? exePath, Func<Task>? beforeAsk, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        FileLog.Write($"[DirectorLauncherRestartStep] RunAsync: machine={machine}");

        var capability = await gateway.CheckCapabilityAsync(machine, ct).ConfigureAwait(false);
        if (DirectorRestartGate.Refusal(capability) is { } refusal)
        {
            FileLog.Write($"[DirectorLauncherRestartStep] RunAsync: the launcher is NOT asked: {refusal}");
            return new LauncherRestartStepResult(LauncherRestartStepVerdict.CapabilityRefused, refusal, null);
        }

        if (beforeAsk is not null) await beforeAsk().ConfigureAwait(false);

        var answer = await gateway.AskOwnLauncherRestartOnlyIfEmptyAsync(machine, exePath, ct).ConfigureAwait(false);
        if (answer.Status is < 200 or >= 300)
        {
            FileLog.Write($"[DirectorLauncherRestartStep] RunAsync: the launcher refused, HTTP {answer.Status}");
            return new LauncherRestartStepResult(
                LauncherRestartStepVerdict.LauncherRefused,
                $"the launcher did not accept the guarded restart (HTTP {answer.Status}): {answer.Body}",
                answer);
        }

        FileLog.Write("[DirectorLauncherRestartStep] RunAsync: the launcher accepted");
        return new LauncherRestartStepResult(LauncherRestartStepVerdict.Accepted, null, answer);
    }
}
