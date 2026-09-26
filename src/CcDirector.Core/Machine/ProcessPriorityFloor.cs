using System.ComponentModel;
using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Machine;

/// <summary>
/// THE DIRECTOR RUNS AT NORMAL PRIORITY, HOWEVER IT WAS STARTED (Voice Delivery mission, phase 6).
///
/// THE MEASUREMENT. In the phase 4 QA run (case2f, 25 September 2026) the prompt verb answered 32 seconds after the
/// Gateway asked - its budget is 20 and the Gateway waits 30 - and the "what became of delivery X" verb, a read of one
/// small file, answered after 61 seconds. The Director had been started by a Windows scheduled task, and a scheduled
/// task starts its program at priority 7 unless told otherwise: BELOW NORMAL. The live process read Below Normal. The
/// machine was kept busy by 24 threads at Normal priority on 24 processors, and Windows gives a ready Below Normal
/// thread the processor only when no Normal thread wants it, or when its starvation boost comes round, about once every
/// four seconds. So every timer, every continuation and every plain read in the Director waited seconds for a turn.
/// The same verb code under the same load answered at 20.0 seconds at Normal and 33.8 at Below Normal, where its
/// one-second timer ran up to 20.3 seconds late. The thread pool was not the cause (its floor, <see cref="ThreadPoolFloor"/>,
/// is 104 workers), nor any lock, nor the file.
///
/// WHY THE DIRECTOR FIXES IT ITSELF rather than every launcher remembering a flag: the Director is the one program
/// that knows it has deadlines - the prompt verb's 20-second answer, the Gateway's 30-second wait, the tunnel's silence
/// tolerance - and it is started more than one way (by hand, by the Launcher, by a scheduled task for a test or an
/// isolated agent Director). Every agent it starts afterwards inherits its priority, so they run at Normal too, as they
/// do under a Director started by hand.
///
/// NEVER LOWERS. A Director somebody started at Above Normal or High was started there on purpose.
/// </summary>
public static class ProcessPriorityFloor
{
    /// <summary>The lowest priority class the Director runs at.</summary>
    public const ProcessPriorityClass Floor = ProcessPriorityClass.Normal;

    /// <summary>Raise this process to <see cref="Floor"/> if it was started below it, and log what happened.</summary>
    /// <returns>True when the process now runs at or above the floor.</returns>
    public static bool Apply()
    {
        var me = Process.GetCurrentProcess();
        return Apply(() => me.PriorityClass, p => me.PriorityClass = p);
    }

    /// <summary>The testable body of <see cref="Apply()"/>, with the process's priority passed in as two delegates.</summary>
    internal static bool Apply(Func<ProcessPriorityClass> get, Action<ProcessPriorityClass> set)
    {
        var current = get();
        if (!IsBelowFloor(current))
        {
            FileLog.Write($"[ProcessPriorityFloor] Apply: this process runs at {current}, at or above {Floor} - nothing changed.");
            return true;
        }

        try
        {
            set(Floor);
        }
        catch (Win32Exception ex)
        {
            // A REFUSAL IS REPORTED, NOT SWALLOWED. On Linux and macOS an unprivileged process may not raise its own
            // priority once it has been lowered. The Director keeps running as it was started, and the line says what
            // that costs and how to start it properly.
            FileLog.Write($"[ProcessPriorityFloor] Apply REFUSED by the system: this process was started at {current} and " +
                          $"raising it to {Floor} failed ({ex.Message}). On a busy machine its answers to the Gateway will be " +
                          $"late - the prompt verb past its 20-second budget, reads past the Gateway's 30-second wait. " +
                          "Start the Director at normal priority.");
            return false;
        }

        FileLog.Write($"[ProcessPriorityFloor] Apply: this process was started at {current} priority (a Windows scheduled " +
                      $"task's default) and now runs at {Floor}. Below {Floor}, a machine kept busy at {Floor} gives it the " +
                      "processor only every few seconds, and its answers to the Gateway arrive after the Gateway has stopped waiting.");
        return true;
    }

    private static bool IsBelowFloor(ProcessPriorityClass priority) =>
        priority is ProcessPriorityClass.Idle or ProcessPriorityClass.BelowNormal;
}
