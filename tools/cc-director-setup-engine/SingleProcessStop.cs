using System.Diagnostics;
using CcDirector.Core.Utilities;

namespace CcDirector.Setup.Engine;

/// <summary>
/// STOP ONE PROCESS. NEVER ITS TREE. This type exists so that the rule is a place rather than an
/// argument.
///
/// The Director's ownership of the launcher's update stops the launcher and swaps its binary. On
/// Windows the launcher is the Director's PARENT process, so a stop aimed at the launcher that killed
/// its process tree would end the Director performing the swap - halfway through, with the launcher
/// binary already renamed aside and nothing left running to put it back or to start what was
/// installed. The uninstaller's stop (<see cref="LauncherStopper"/>) kills trees on purpose, because
/// there the whole install is going away; the two stops otherwise look identical and the wrong one is
/// one word away.
///
/// That one word used to be a boolean argument inside the swap's own file, guarded by a test that read
/// the source text for it. A source-text guard is the weakest kind of proof and it could only ever see
/// the spelling it was written for. Putting the kill HERE, in a type whose entire purpose is the
/// single-process form, means the file that performs the swap contains no process kill at all - so
/// there is no argument in it to flip, and the guard can assert an absence that is complete for that
/// file rather than a string that happens to be the current spelling.
/// </summary>
public static class SingleProcessStop
{
    /// <summary>
    /// Ask <paramref name="pid"/> to close, then insist - on that process alone. True when it is gone
    /// (including when it had already exited before this was called).
    ///
    /// Politeness first is not decoration: a healthy launcher asked to close shuts its own state down
    /// cleanly, and killing it first would deny it that. Insisting second is not optional either - the
    /// launchers that most need replacing are old enough to ignore a polite request.
    /// </summary>
    public static bool Stop(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            try
            {
                process.CloseMainWindow();
                if (process.WaitForExit(3000)) return true;
            }
            catch (Exception ex)
            {
                FileLog.Write($"[SingleProcessStop] polite stop of pid={pid} failed: {ex.Message}");
            }

            // The one kill, and the only place this argument is written for the swap path.
            process.Kill(entireProcessTree: false);
            return process.WaitForExit(5000);
        }
        catch (ArgumentException)
        {
            return true;   // already gone
        }
        catch (Exception ex)
        {
            FileLog.Write($"[SingleProcessStop] stopping pid={pid} failed: {ex.Message}");
            return false;
        }
    }
}
