using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Triggers;

/// <summary>
/// THE STATE EACH FACTORY CHECK KEEPS BETWEEN RUNS, over the append-only <c>factory_check_state</c> table (Factory
/// Definition mission, phase 5b, design 1.4). Tenant-partitioned by construction: every method takes the account and
/// opens a context scoped to it.
///
/// A START'S STATE IS ITS NEWEST ROW. Rows are only ever added: a check's report adds one when the contract read it as
/// a success AND its content differs from the newest row; a person's reset adds an empty one marked as a reset. So
/// the history of what a check believed is always there to read, and a failed check never changes it.
///
/// Writes are serialised by one lock, the Gateway being a single writer per replica; across replicas the key
/// (tenant, factory, owner, sequence) refuses a second row of the same number.
/// </summary>
public sealed class FactoryCheckStateStore
{
    /// <summary>The most a state may hold: 64 KB of UTF-8.</summary>
    public const int MaxContentBytes = 64 * 1024;

    /// <summary>The owner key of a definition trigger's state.</summary>
    public static string TriggerOwner(Guid triggerId) => "trigger:" + triggerId.ToString("D");

    /// <summary>The owner key of a checked schedule's state.</summary>
    public static string ScheduleOwner(string scheduleId) => "cron:" + scheduleId;

    /// <summary>True when the text is a well-formed owner key: <c>trigger:&lt;guid&gt;</c> or <c>cron:&lt;id&gt;</c>.</summary>
    public static bool IsOwnerKey(string? key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > 80) return false;
        if (key.StartsWith("trigger:", StringComparison.Ordinal)) return Guid.TryParse(key["trigger:".Length..], out _);
        return key.StartsWith("cron:", StringComparison.Ordinal) && key.Length > "cron:".Length;
    }

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;

    public FactoryCheckStateStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The newest state row of one start, or null when it has none.</summary>
    public FactoryCheckStateEntity? Newest(TenantId tenant, string factory, string ownerKey)
    {
        using var ctx = _db.CreateContext(tenant);
        return NewestIn(ctx, factory, ownerKey);
    }

    /// <summary>The state a check is handed: the newest row's content, or the empty string on a first run.</summary>
    public string StateFor(TenantId tenant, string factory, string ownerKey)
        => Newest(tenant, factory, ownerKey)?.Content ?? "";

    /// <summary>
    /// Store the state a successful check left, when it differs from the newest row. Returns the row written, or null
    /// when the content is unchanged and nothing was written. A content above <see cref="MaxContentBytes"/> is a caller
    /// error: the contract fails such a check before its state can reach here.
    /// </summary>
    public FactoryCheckStateEntity? RecordIfChanged(TenantId tenant, string factory, string ownerKey, string content,
        string writtenBy, string? runRef, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (Encoding.UTF8.GetByteCount(content) > MaxContentBytes)
            throw new ArgumentException($"a check's state is at most {MaxContentBytes} bytes", nameof(content));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var newest = NewestIn(ctx, factory, ownerKey);
            if (string.Equals(newest?.Content ?? "", content, StringComparison.Ordinal))
            {
                FileLog.Write($"[FactoryCheckStateStore] RecordIfChanged: factory={factory}, owner={ownerKey}, unchanged, nothing written");
                return null;
            }
            var row = Append(ctx, factory, ownerKey, newest, content, writtenBy, runRef, reset: false, nowUtc);
            FileLog.Write($"[FactoryCheckStateStore] RecordIfChanged: factory={factory}, owner={ownerKey}, sequence={row.Sequence}, bytes={Encoding.UTF8.GetByteCount(content)}, by={writtenBy}");
            return row;
        }
    }

    /// <summary>
    /// A person's way out of a stuck state: append an empty row marked as a reset, so the next run of the check starts
    /// as a first run. Always written, even when the state is already empty, so the reset is on the record.
    /// </summary>
    /// <param name="person">Who reset it, or null when the person's identifier is not known: the row says
    /// <c>person</c> alone then.</param>
    public FactoryCheckStateEntity Reset(TenantId tenant, string factory, string ownerKey, string? person, DateTime nowUtc)
    {
        FileLog.Write($"[FactoryCheckStateStore] Reset: tenant={tenant.ToLogString()}, factory={factory}, owner={ownerKey}, by={person ?? "(not named)"}");
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var newest = NewestIn(ctx, factory, ownerKey);
            return Append(ctx, factory, ownerKey, newest, "", person is null ? "person" : "person " + person, runRef: null,
                reset: true, nowUtc);
        }
    }

    private static FactoryCheckStateEntity? NewestIn(GatewayDbContext ctx, string factory, string ownerKey)
        => ctx.FactoryCheckStates.AsNoTracking()
            .Where(s => s.Factory == factory && s.OwnerKey == ownerKey)
            .OrderByDescending(s => s.Sequence)
            .FirstOrDefault();

    private static FactoryCheckStateEntity Append(GatewayDbContext ctx, string factory, string ownerKey,
        FactoryCheckStateEntity? newest, string content, string writtenBy, string? runRef, bool reset, DateTime nowUtc)
    {
        var row = new FactoryCheckStateEntity
        {
            Factory = factory,
            OwnerKey = ownerKey,
            Sequence = (newest?.Sequence ?? 0) + 1,
            Content = content,
            WrittenUtc = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc),
            WrittenBy = writtenBy.Length <= 256 ? writtenBy : writtenBy[..256],
            RunRef = runRef,
            Reset = reset,
        };
        row.TenantId = ctx.ActiveTenant!;
        ctx.FactoryCheckStates.Add(row);
        ctx.SaveChanges();
        return row;
    }
}
