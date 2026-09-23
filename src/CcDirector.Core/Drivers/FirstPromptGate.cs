using CcDirector.Core.Agents;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Drivers;

/// <summary>How <see cref="FirstPromptGate.WaitUntilAcceptingInputAsync"/> ended.</summary>
public enum FirstPromptGateOutcome
{
    /// <summary>The agent read a keystroke and drew it in its composer: it is accepting input now.</summary>
    Ready,

    /// <summary>The session exited before it accepted input.</summary>
    Exited,

    /// <summary>The limit passed first. Nothing but (possibly) the one probe character was typed.</summary>
    TimedOut,
}

/// <summary>What the gate came to.</summary>
/// <param name="Outcome">Ready, exited, or timed out.</param>
/// <param name="Detail">The outcome as a sentence, for the log and for a failed-delivery reason.</param>
/// <param name="ProbeMayRemain">True when the probe character was typed and was not proven erased, so it may
/// appear in the composer later. The caller marks the composer so the next send clears it first.</param>
public readonly record struct FirstPromptGateResult(FirstPromptGateOutcome Outcome, string Detail, bool ProbeMayRemain);

/// <summary>
/// THE GATE IN FRONT OF A NEW SESSION'S FIRST PROMPT (issue #3290).
///
/// WHY THIS EXISTS. A spawn with a first prompt - `session spawn --prompt`, a Gateway schedule, a restore - used to
/// wait for "a startup burst of more than 1500 bytes, then one quiet 750 millisecond poll", and on a timer after
/// that it typed regardless. On 23 September 2026 every Claude Code first prompt in the Director logs went out at
/// about 1550 bytes and then saw ZERO bytes of echo for four seconds, twice: Claude Code 2.1.280 draws its composer
/// (with a grey "Try ..." suggestion) before it starts reading keystrokes. What happened to the typed text then:
///  - Claude Code holds keystrokes typed during startup and, once it is ready, drops them into the composer with the
///    control bytes removed ("Removed 1 invisible character - review and press Enter to send"). Our Enter was one of
///    those bytes, so the prompt sat in the composer unsubmitted.
///  - The echo check's recovery pressed Escape and typed the prompt again. Both copies were held, the Escape was
///    removed, and the composer got the prompt twice run together - or, when only the control bytes survived, it
///    showed "nothing left to send" and an empty composer.
/// No amount of waiting for output fixes this, because a session that is not reading input and a session that is
/// idle look identical in the byte stream. Only a keystroke that comes back proves the agent is reading.
///
/// WHAT THE GATE DOES, in order, and nothing is typed before step 2:
///  1. Wait until two consecutive screen frames show the agent's composer drawn (<see cref="ComposerDrawn"/>): not
///     working, no menu or dialog, the composer recognised by <see cref="DoorbellSafety"/>.
///  2. Type ONE probe character (<see cref="Probe"/>) and wait until the composer holds exactly that character
///     (<see cref="DoorbellSafety.ComposerHoldsExactly"/>). The probe is typed once and never retyped: if the agent
///     is still starting, it is held with everything else and arrives when the agent is ready, which is exactly the
///     moment this is waiting for.
///  3. Press Backspace once and wait until two consecutive frames show the composer drawn without the probe.
/// Then the caller sends the prompt through the ordinary submit path, into an agent that has just proven it reads.
///
/// NOT A RETRY LOOP. Nothing is typed twice. If the limit passes first the gate says so, the caller records a failed
/// delivery that rides the session row to every screen, and the prompt is not typed at all.
///
/// SCOPE: Claude Code and Codex only - the two agents whose composer <see cref="DoorbellSafety"/> can read from real
/// captured screens. Every other agent keeps the older wait (<see cref="CanProve"/> is false for it).
/// </summary>
public static class FirstPromptGate
{
    /// <summary>The one character typed to prove the agent reads input. A plain letter: no agent binds it to a mode or
    /// a command in an empty composer, as '/', '!', '#', '?' and '@' are bound in Claude Code.</summary>
    public const string Probe = "x";

    /// <summary>
    /// The shortest wait the gate is given. The request's own wait (thirty seconds by default) was sized for the old
    /// rule, which typed when it ran out. The gate never types blind, so a longer limit costs a healthy machine
    /// nothing - it returns the moment the probe comes back - and gives a loaded one the time it needs before the
    /// failure is reported. Measured on 23 September 2026 on a machine at 100 per cent processor: Claude Code drew its
    /// composer 45 to 103 seconds after the spawn and read no keystroke until its SessionStart hook had run, later
    /// still. A first limit of two minutes failed all three sessions started that way.
    /// </summary>
    public static readonly TimeSpan MinimumWait = TimeSpan.FromMinutes(10);

    /// <summary>How often the screen is read while waiting.</summary>
    public static readonly TimeSpan DefaultPoll = TimeSpan.FromMilliseconds(250);

    private static readonly byte[] ProbeBytes = [(byte)'x'];
    private static readonly byte[] BackspaceBytes = [0x7F];

    /// <summary>True for the agents whose composer can be read from the screen, and so can be gated.</summary>
    public static bool CanProve(AgentKind agent) => agent is AgentKind.ClaudeCode or AgentKind.Codex;

    /// <summary>
    /// The composer is drawn and waiting: the screen shows no working marker, and the agent's composer is recognised
    /// as empty or holding text (Claude Code's grey "Try ..." suggestion reads as text, since the rows carry no
    /// colour). A menu, a dialog, or an unrecognised screen is not drawn.
    /// </summary>
    public static bool ComposerDrawn(AgentKind agent, ScreenFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        var rows = frame.Rows ?? [];
        if (rows.Count == 0 || rows.All(string.IsNullOrWhiteSpace)) return false;
        if (DoorbellSafety.ShowsWorking(rows)) return false;
        return DoorbellSafety.ReadComposer(agent, frame) is ComposerReading.Empty or ComposerReading.HoldsText;
    }

    /// <summary>
    /// What the last screen read showed, for the failure reason: the composer reading and the last few non-blank rows,
    /// each cut short. The reason rides the session row, so this is what the owner sees when a first prompt fails.
    /// </summary>
    internal static string Describe(AgentKind agent, ScreenFrame? frame)
    {
        if (frame is null) return "The screen was never read.";
        var rows = (frame.Rows ?? []).Where(r => !string.IsNullOrWhiteSpace(r)).TakeLast(4)
            .Select(r => { var t = r.Trim(); return t.Length > 80 ? t[..80] + "..." : t; });
        return $"Last screen: composer {DoorbellSafety.ReadComposer(agent, frame)}, cursor " +
               $"{(frame.CursorVisible ? $"at row {frame.CursorRow} column {frame.CursorCol}" : "hidden")}; " +
               $"bottom rows: {string.Join(" | ", rows)}";
    }

    /// <summary>
    /// Wait until the agent is proven to be reading input (see the class summary for the three steps).
    /// </summary>
    /// <param name="agent">The agent in the terminal; must satisfy <see cref="CanProve"/>.</param>
    /// <param name="takeFrame">Reads one frame of the live screen.</param>
    /// <param name="write">Writes bytes to the terminal. Called at most twice: the probe, then one Backspace.</param>
    /// <param name="exited">True once the session has exited.</param>
    /// <param name="limit">How long to wait in total.</param>
    /// <param name="beginInput">Called immediately before the probe is typed; the handle it returns is disposed when
    /// the gate ends, so nothing else types into the composer while the probe is in it.</param>
    /// <param name="poll">How often to read the screen; <see cref="DefaultPoll"/> when null.</param>
    /// <param name="pause">How to wait between reads; tests pass one that does not sleep.</param>
    /// <param name="utcNow">The clock; tests pass one they advance.</param>
    public static async Task<FirstPromptGateResult> WaitUntilAcceptingInputAsync(
        AgentKind agent,
        Func<ScreenFrame> takeFrame,
        Action<byte[]> write,
        Func<bool> exited,
        TimeSpan limit,
        Func<Task<IDisposable>>? beginInput = null,
        TimeSpan? poll = null,
        Func<TimeSpan, Task>? pause = null,
        Func<DateTime>? utcNow = null)
    {
        ArgumentNullException.ThrowIfNull(takeFrame);
        ArgumentNullException.ThrowIfNull(write);
        ArgumentNullException.ThrowIfNull(exited);
        if (!CanProve(agent))
            throw new NotSupportedException($"[FirstPromptGate] {agent} has no composer reader; the gate cannot prove it reads input.");

        var step = poll ?? DefaultPoll;
        var wait = pause ?? (d => Task.Delay(d));
        var clock = utcNow ?? (() => DateTime.UtcNow);
        var started = clock();
        var deadline = started + limit;
        string Elapsed() => $"{(clock() - started).TotalSeconds:F1}s";

        // Step 1: the composer is drawn, in two consecutive frames.
        ScreenFrame? lastFrame = null;
        var drawnInARow = 0;
        while (drawnInARow < 2)
        {
            if (exited()) return new(FirstPromptGateOutcome.Exited, "the session exited before its composer was drawn", false);
            if (clock() >= deadline)
                return new(FirstPromptGateOutcome.TimedOut,
                    $"the agent's composer was not drawn within {limit.TotalSeconds:F0}s (a dialog, a menu, or an agent still starting); " +
                    $"nothing was typed. {Describe(agent, lastFrame)}", false);
            lastFrame = takeFrame();
            drawnInARow = ComposerDrawn(agent, lastFrame) ? drawnInARow + 1 : 0;
            if (drawnInARow < 2) await wait(step);
        }
        FileLog.Write($"[FirstPromptGate] {agent}: composer drawn after {Elapsed()} - typing the probe");

        using var input = beginInput is null ? null : await beginInput();

        // Step 2: one probe character, typed once, until the composer holds exactly it.
        write(ProbeBytes);
        while (true)
        {
            await wait(step);
            if (exited()) return new(FirstPromptGateOutcome.Exited, "the session exited while the probe was in its composer", false);
            lastFrame = takeFrame();
            if (DoorbellSafety.ComposerHoldsExactly(agent, lastFrame, Probe)) break;
            if (clock() >= deadline)
                return new(FirstPromptGateOutcome.TimedOut,
                    $"the composer was drawn but the agent did not read a keystroke within {limit.TotalSeconds:F0}s " +
                    $"(the probe character '{Probe}' never appeared); the prompt was not typed. {Describe(agent, lastFrame)}", true);
        }
        FileLog.Write($"[FirstPromptGate] {agent}: the probe came back after {Elapsed()} - erasing it");

        // Step 3: one Backspace, until two consecutive frames show the composer without the probe.
        write(BackspaceBytes);
        var clearInARow = 0;
        while (clearInARow < 2)
        {
            await wait(step);
            if (exited()) return new(FirstPromptGateOutcome.Exited, "the session exited while the probe was being erased", false);
            var frame = takeFrame();
            lastFrame = frame;
            clearInARow = ComposerDrawn(agent, frame) && !DoorbellSafety.ComposerHoldsExactly(agent, frame, Probe)
                ? clearInARow + 1
                : 0;
            if (clearInARow < 2 && clock() >= deadline)
                return new(FirstPromptGateOutcome.TimedOut,
                    $"the agent read the probe but its Backspace was not seen within {limit.TotalSeconds:F0}s; the prompt was not typed. " +
                    Describe(agent, lastFrame), true);
        }
        FileLog.Write($"[FirstPromptGate] {agent}: accepting input after {Elapsed()}");
        return new(FirstPromptGateOutcome.Ready, $"the agent read a keystroke after {Elapsed()}", false);
    }
}
