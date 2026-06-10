using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Enforces the v1 same-machine single-drain guard (issue #274, criterion 8). A machine has exactly
/// ONE slot-5 test Director - one build output, one Control API port, strictly sequential sub-agents
/// (#270 Constraint 1) - so two lists may NOT drain at the same time against the same machine. This
/// manager admits at most one active drain per machine key and REFUSES a second; v1 parallelism is
/// across DIFFERENT machines only (criterion 6 - different machine keys run concurrently here).
///
/// The cross-machine case (criterion 6) needs no coordination at this manager: two different machine
/// keys each get admitted independently, so two runners drain two lists concurrently without
/// interfering. The single-consumer claim (#273) keeps each list to one drainer regardless.
/// </summary>
public sealed class WorkListRunnerManager
{
    private readonly object _gate = new();

    // Machine key -> the list name currently being drained on that machine.
    private readonly Dictionary<string, string> _activeByMachine =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The outcome of asking to admit a drain on a machine.</summary>
    public enum AdmitResult
    {
        /// <summary>Admitted; the caller now holds the machine's single drain slot.</summary>
        Admitted,

        /// <summary>Refused; the machine is already draining another list (criterion 8).</summary>
        RefusedMachineBusy,
    }

    /// <summary>
    /// Try to admit a drain of <paramref name="listName"/> on <paramref name="machineKey"/>. Returns
    /// <see cref="AdmitResult.Admitted"/> when the machine is free, or
    /// <see cref="AdmitResult.RefusedMachineBusy"/> when it is already draining another list. On
    /// admit, the caller MUST call <see cref="Complete"/> when the drain finishes (a finally block).
    /// </summary>
    public AdmitResult TryAdmit(string machineKey, string listName)
    {
        if (string.IsNullOrWhiteSpace(machineKey))
            throw new ArgumentException("machine key is required", nameof(machineKey));
        if (string.IsNullOrWhiteSpace(listName))
            throw new ArgumentException("list name is required", nameof(listName));

        lock (_gate)
        {
            if (_activeByMachine.TryGetValue(machineKey, out var active))
            {
                FileLog.Write($"[WorkListRunnerManager] TryAdmit REFUSED: machine={machineKey} already draining {active}");
                return AdmitResult.RefusedMachineBusy;
            }

            _activeByMachine[machineKey] = listName;
            FileLog.Write($"[WorkListRunnerManager] TryAdmit: machine={machineKey} admitted list={listName}");
            return AdmitResult.Admitted;
        }
    }

    /// <summary>Release the machine's single drain slot. A no-op if the machine was not active.</summary>
    public void Complete(string machineKey)
    {
        if (string.IsNullOrWhiteSpace(machineKey)) return;
        lock (_gate)
        {
            if (_activeByMachine.Remove(machineKey))
                FileLog.Write($"[WorkListRunnerManager] Complete: machine={machineKey} drain slot released");
        }
    }

    /// <summary>The list currently draining on the machine, or null if the machine is free.</summary>
    public string? ActiveList(string machineKey)
    {
        lock (_gate)
            return _activeByMachine.TryGetValue(machineKey, out var name) ? name : null;
    }
}
