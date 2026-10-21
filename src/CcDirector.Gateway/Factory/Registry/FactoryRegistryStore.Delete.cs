using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Registry;

/// <summary>What deleting a factory removed: the schedules and triggers by id and name, and how many definition
/// versions and seat-brief versions went with it.</summary>
public sealed record FactoryDeleteResult(
    string Factory,
    string Title,
    IReadOnlyList<(string Id, string Name)> Schedules,
    IReadOnlyList<(Guid Id, string Name)> Triggers,
    int DefinitionVersions,
    int BriefVersions);

public sealed partial class FactoryRegistryStore
{
    /// <summary>
    /// DELETE AN ARCHIVED FACTORY FOR GOOD (issue #4324), in ONE transaction, so a failure leaves it exactly as it was.
    ///
    /// Removed: the registry entry, every version of its definition, every schedule and trigger that names it, and
    /// every version of its seats' briefs - what makes it a factory. Kept, as the account's history of what happened:
    /// the activity record, journals, memory, records, documents, goal numbers, and the runs of its schedules and
    /// triggers. Every reader lists factories from the registry, so nothing kept brings the factory back.
    ///
    /// The registry row is locked first, as a publish locks it, so a publish of the same factory on another replica
    /// waits and then finds no row. Throws <see cref="FactoryNotRegisteredException"/> when it is not registered and
    /// <see cref="FactoryViewValidationException"/> when it is not archived.
    ///
    /// <paramref name="expectedSchedules"/> are the schedule ids the owner's confirm named; when the factory's schedules
    /// are now different the answer is <see cref="FactoryOwnerActionConflictException"/> and nothing is deleted.
    /// <paramref name="rowsFor"/> composes the activity rows that record the act from what is about to be deleted; they
    /// are written in the same transaction, so the record never claims a delete that did not happen, nor misses one.
    /// </summary>
    public FactoryDeleteResult Delete(TenantId tenant, string factory, string deletedBy, IReadOnlyCollection<string> expectedSchedules,
        Func<FactoryDeleteResult, IReadOnlyList<AppendFactoryActivityRequest>> rowsFor)
    {
        ArgumentNullException.ThrowIfNull(expectedSchedules);
        ArgumentNullException.ThrowIfNull(rowsFor);
        FileLog.Write($"[FactoryRegistryStore] Delete: factory={factory}, by={deletedBy}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            using var tx = ctx.Database.BeginTransaction();
            var committing = false;
            try
            {
                var row = Row(ctx, factory);
                var id = row.Factory;
                ctx.FactoryRegistry.Where(f => f.Factory == id)
                    .ExecuteUpdate(set => set.SetProperty(f => f.RegisteredAtUtc, f => f.RegisteredAtUtc));
                ctx.Entry(row).Reload();
                if (row.ArchivedAtUtc is null)
                    throw Refuse($"{row.Title} is not archived. Archive it first: only an archived factory can be deleted.");

                var schedules = ctx.CronJobs.Where(j => j.Factory == id).ToList()
                    .OrderBy(j => j.Id, StringComparer.Ordinal).ToList();
                if (!schedules.Select(j => j.Id).ToHashSet(StringComparer.Ordinal).SetEquals(expectedSchedules))
                    throw new FactoryOwnerActionConflictException(
                        $"The schedules of {row.Title} changed after the confirm was shown. Nothing was deleted. Open it again to see what Delete removes now.");
                // A trigger's factory was written by its creator and may carry capitals; the registry id is lower case.
                var triggers = ctx.Triggers.Where(t => t.Factory.ToLower() == id).ToList()
                    .OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
                var definitions = ctx.FactoryDefinitions.Where(v => v.Factory == id).ExecuteDelete();
                var briefs = ctx.FactoryBriefs.Where(b => b.Factory == id).ExecuteDelete();
                var result = new FactoryDeleteResult(id, row.Title,
                    schedules.Select(j => (j.Id, j.Name)).ToList(),
                    triggers.Select(t => (t.Id, t.Name)).ToList(),
                    definitions, briefs);
                ctx.CronJobs.RemoveRange(schedules);
                ctx.Triggers.RemoveRange(triggers);
                ctx.FactoryRegistry.Remove(row);
                var now = DateTime.UtcNow;
                ctx.FactoryActivity.AddRange(rowsFor(result).Select(r => FactoryActivityRecord.NewRow(ctx, r, deletedBy, now)));
                ctx.SaveChanges();
                committing = true;
                tx.Commit();

                FileLog.Write($"[FactoryRegistryStore] Delete: deleted {id}; schedules={string.Join(",", schedules.Select(j => j.Id))}, " +
                              $"triggers={string.Join(",", triggers.Select(t => t.Name))}, definition versions={definitions}, brief versions={briefs}");
                return result;
            }
            catch (Exception ex)
            {
                // A commit that failed has already ended the transaction; there is nothing left to roll back.
                if (!committing) tx.Rollback();
                FileLog.Write($"[FactoryRegistryStore] Delete FAILED and was rolled back: factory={factory}: {ex.Message}");
                throw;
            }
        }
    }
}
