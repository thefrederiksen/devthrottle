using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Running;

/// <summary>
/// Production <see cref="ICronWorkListRunner"/> (epic #479, #484): when a cron job's action names a
/// work list, this triggers the shipped #274 drain on the job's target Director. It mirrors the
/// <c>POST /lists/{name}/run</c> endpoint's guards - synchronous pre-checks (no list / empty /
/// already-claimed / no director / machine busy) return a clean outcome and never launch a drain -
/// then launches the actual drain in the BACKGROUND (a cron sweep must not block for the hours a
/// drain can take), releasing the machine's single-drain slot when it finishes.
/// </summary>
public sealed class DirectorCronWorkListRunner : ICronWorkListRunner
{
    private readonly WorkListStore _store;
    private readonly IDirectorTargetResolver _resolver;
    private readonly WorkListRunnerManager _manager;
    private readonly ICronWorkListDrainLauncher _launcher;

    public DirectorCronWorkListRunner(
        WorkListStore store,
        IDirectorTargetResolver resolver,
        WorkListRunnerManager manager,
        ICronWorkListDrainLauncher launcher)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
        _launcher = launcher ?? throw new ArgumentNullException(nameof(launcher));
    }

    public async Task<CronWorkListOutcome> TriggerAsync(CronJobDto job, CancellationToken ct)
    {
        if (job is null)
            throw new ArgumentNullException(nameof(job));
        var listName = job.Action.WorkListName;
        if (string.IsNullOrWhiteSpace(listName))
            throw new ArgumentException("job is not a work-list action", nameof(job));

        // Synchronous pre-checks (AC3): a clean outcome, no drain launched, no duplicate claim.
        var list = _store.Get(listName);
        if (list is null)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: no such list={listName}");
            return CronWorkListOutcome.NoSuchList;
        }
        if (list.Items.Count == 0)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: list={listName} is empty; nothing to drain");
            return CronWorkListOutcome.EmptyList;
        }
        if (!string.IsNullOrEmpty(list.Consumer))
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: list={listName} already claimed by {list.Consumer}");
            return CronWorkListOutcome.AlreadyClaimed;
        }

        // Resolve the target MACHINE to a Director (launching one if none is running, #503).
        var machine = job.Target.Machine;
        var target = await _resolver.ResolveAsync(machine, ct);
        if (string.IsNullOrEmpty(target.Endpoint))
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: no director on machine={machine}: {target.Error}");
            return CronWorkListOutcome.NoSuchDirector;
        }
        var endpoint = target.Endpoint;

        var machineKey = string.IsNullOrWhiteSpace(machine) ? endpoint : machine;
        if (_manager.TryAdmit(machineKey, listName) == WorkListRunnerManager.AdmitResult.RefusedMachineBusy)
        {
            FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: machine={machineKey} busy (active={_manager.ActiveList(machineKey)})");
            return CronWorkListOutcome.MachineBusy;
        }

        // Admitted: launch the drain in the background so the cron sweep is not blocked for the
        // hours a drain can take. The machine slot is released when the drain finishes.
        var consumer = $"cron:{job.Id}:{Guid.NewGuid():N}";
        var repoPath = job.Action.RepoPath;
        FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: launching drain list={listName} on machine={machineKey}, consumer={consumer}");

        _ = Task.Run(async () =>
        {
            // Detached background task: it owns its try/catch so a drain failure is logged, never
            // unobserved, and the machine slot is always released.
            try
            {
                await _launcher.LaunchAsync(endpoint, repoPath, listName, consumer, ct);
                FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: drain complete list={listName}");
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorCronWorkListRunner] job={job.Id}: drain FAILED list={listName}: {ex.Message}");
            }
            finally
            {
                _manager.Complete(machineKey);
            }
        }, CancellationToken.None);

        return CronWorkListOutcome.Started;
    }
}
