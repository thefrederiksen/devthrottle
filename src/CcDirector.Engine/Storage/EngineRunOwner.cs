using System.ComponentModel;
using System.Diagnostics;

namespace CcDirector.Engine.Storage;

/// <summary>
/// The Director process that started a run. Several Directors can open one engine.db (when
/// CC_VAULT_PATH is set at the user level every Director on the machine shares it), so a run
/// records who owns it and only a provably gone owner's run is ever failed by someone else.
/// A process is named by its id AND its start time, because an id alone is reused by Windows.
/// </summary>
public sealed record EngineRunOwner(string Director, string Machine, int ProcessId, DateTime ProcessStartedAtUtc)
{
    public static EngineRunOwner ForCurrentProcess(string director)
    {
        using var process = Process.GetCurrentProcess();
        return new EngineRunOwner(director, Environment.MachineName, process.Id, process.StartTime.ToUniversalTime());
    }
}

public enum OwnerLiveness
{
    /// <summary>The owning process is still running.</summary>
    Running,

    /// <summary>Proven gone: no process has that id, or the process with that id started at another time.</summary>
    Gone,

    /// <summary>Cannot be decided from here (another machine, or the process could not be inspected). Its runs are left alone.</summary>
    Unknown
}

public static class ProcessOwnerLiveness
{
    /// <summary>
    /// Asks the operating system whether the owner is still running. Only an owner on THIS machine
    /// can be proven gone; anything else answers Unknown so its runs are kept.
    /// </summary>
    public static OwnerLiveness Probe(EngineRunOwner owner)
    {
        if (!string.Equals(owner.Machine, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
            return OwnerLiveness.Unknown;

        Process process;
        try
        {
            process = Process.GetProcessById(owner.ProcessId);
        }
        catch (ArgumentException)
        {
            // No process with this id is running.
            return OwnerLiveness.Gone;
        }

        using (process)
        {
            DateTime startedAtUtc;
            try
            {
                startedAtUtc = process.StartTime.ToUniversalTime();
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                // Exited between the two calls, or not ours to inspect: undecidable, so keep its runs.
                return OwnerLiveness.Unknown;
            }

            return Math.Abs((startedAtUtc - owner.ProcessStartedAtUtc).TotalSeconds) < 1
                ? OwnerLiveness.Running
                : OwnerLiveness.Gone;
        }
    }
}
