using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>
/// WHAT THE WAKE READS AND WRITES (Factory Inbox mission, phase 2): the factory's wake row
/// (<see cref="FactoryInboxWakeEntity"/>) and the facts of its inbox the wake decides from.
///
/// THE WAKE IS DRIVEN FROM THE STORE, NEVER FROM MEMORY ALONE (the phase 2 plan's one design rule). Everything the wake
/// decides is read here at the moment it decides - which messages wait to be filed and since when, which bundles no Boss
/// has read, and which start is already held - so a Gateway restarted in the middle of a window, or of a start, loses
/// nothing and starts nothing twice.
///
/// ONE WRITER WINS. <see cref="TryUpdate"/> is a conditional update on the row's version: EF writes
/// <c>UPDATE ... WHERE id = @id AND version = @read</c>, and a writer whose version was overtaken changes nothing and is
/// told so. Taking a start's claim is such an update, so two Gateway processes cannot both take one.
/// </summary>
public sealed class FactoryInboxWakeStore
{
    private readonly GatewayDatabase _db;

    public FactoryInboxWakeStore(GatewayDatabase db)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
    }

    /// <summary>The factory's wake row, or null when the wake has never written one.</summary>
    public FactoryInboxWakeEntity? Find(TenantId tenant, string factory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        using var ctx = _db.CreateContext(tenant);
        return ctx.FactoryInboxWakes.AsNoTracking().FirstOrDefault(w => w.Factory == factory);
    }

    /// <summary>True when the factory's wake row records <paramref name="sessionId"/> as the Secretary it started - the
    /// only mark an undefined factory's Secretary carries, read by the start-door refusal (<see cref="SecretaryStartLimit"/>).</summary>
    public bool RecordsAsSecretary(TenantId tenant, string factory, string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        return Find(tenant, factory)?.SecretarySessionId is { } recorded
               && string.Equals(recorded, sessionId, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The factory's wake row, written now when there is none. Two writers creating it at once are held apart by the
    /// unique index on the factory: the one that loses reads the row the other wrote.
    /// </summary>
    public FactoryInboxWakeEntity Row(TenantId tenant, string factory, DateTime nowUtc)
    {
        if (Find(tenant, factory) is { } found) return found;
        try
        {
            using var ctx = _db.CreateContext(tenant);
            var row = new FactoryInboxWakeEntity { TenantId = tenant.Value, Factory = factory, Version = 0, UpdatedAtUtc = nowUtc };
            ctx.FactoryInboxWakes.Add(row);
            ctx.SaveChanges();
            FileLog.Write($"[FactoryInboxWakeStore] Row: created the wake row of {factory}");
            return row;
        }
        catch (DbUpdateException ex)
        {
            // Another writer created it between the read and the insert: theirs stands.
            FileLog.Write($"[FactoryInboxWakeStore] Row: {factory} was created by another writer first ({ex.InnerException?.Message ?? ex.Message}); reading theirs");
            return Find(tenant, factory)
                   ?? throw new InvalidOperationException($"The wake row of '{factory}' could not be created and is not there.", ex);
        }
    }

    /// <summary>
    /// Change the factory's wake row only if its version is still <paramref name="expectedVersion"/>: one conditional
    /// update in the database. True when this writer's change was written; false when another writer changed the row
    /// first, and then nothing was written.
    /// </summary>
    public bool TryUpdate(TenantId tenant, string factory, int expectedVersion, Action<FactoryInboxWakeEntity> change, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(change);
        using var ctx = _db.CreateContext(tenant);
        var row = ctx.FactoryInboxWakes.FirstOrDefault(w => w.Factory == factory);
        if (row is null || row.Version != expectedVersion)
        {
            FileLog.Write($"[FactoryInboxWakeStore] TryUpdate: {factory} is at version {row?.Version.ToString() ?? "(no row)"}, not {expectedVersion} - not written");
            return false;
        }
        change(row);
        row.Version = expectedVersion + 1;
        row.UpdatedAtUtc = nowUtc;
        try
        {
            ctx.SaveChanges();
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            FileLog.Write($"[FactoryInboxWakeStore] TryUpdate: {factory} version {expectedVersion} was overtaken by another writer - not written");
            return false;
        }
    }

    /// <summary>
    /// The facts of one factory's inbox the wake decides from: every message not yet handed to the Boss (sent, or received
    /// and held by a Secretary), oldest first, and every open bundle no Boss has read yet, oldest first.
    /// </summary>
    public FactoryInboxWakeFacts Facts(TenantId tenant, string factory)
    {
        using var ctx = _db.CreateContext(tenant);
        var toFile = ctx.FactoryInboxMessages.AsNoTracking()
            .Where(m => m.Factory == factory && m.BundleId == null
                        && (m.State == FactoryInboxWords.States.Sent || m.State == FactoryInboxWords.States.Received))
            .Select(m => new FactoryInboxWaitingMessage(m.Id, m.SentAtUtc, m.State, m.ClaimSessionId))
            .ToList()
            .OrderBy(m => m.SentAtUtc).ThenBy(m => m.Id).ToList();
        var unread = ctx.FactoryInboxBundles.AsNoTracking()
            .Where(b => b.Factory == factory && b.ClosedAtUtc == null && b.ReadBySessionId == null)
            .Select(b => new FactoryInboxUnreadBundle(b.Id, b.HandedAtUtc))
            .ToList()
            .OrderBy(b => b.HandedAtUtc).ThenBy(b => b.Id).ToList();
        return new FactoryInboxWakeFacts(toFile, unread);
    }

    /// <summary>Every factory of the account with something for the wake to look at: a message not yet handed to the Boss,
    /// or a bundle no Boss has read.</summary>
    public IReadOnlyList<string> FactoriesWithWork(TenantId tenant)
    {
        using var ctx = _db.CreateContext(tenant);
        var fromMessages = ctx.FactoryInboxMessages.AsNoTracking()
            .Where(m => m.BundleId == null
                        && (m.State == FactoryInboxWords.States.Sent || m.State == FactoryInboxWords.States.Received))
            .Select(m => m.Factory).Distinct().ToList();
        var fromBundles = ctx.FactoryInboxBundles.AsNoTracking()
            .Where(b => b.ClosedAtUtc == null && b.ReadBySessionId == null)
            .Select(b => b.Factory).Distinct().ToList();
        return fromMessages.Concat(fromBundles).Distinct(StringComparer.Ordinal).OrderBy(f => f, StringComparer.Ordinal).ToList();
    }
}

/// <summary>One message not yet handed to the Boss, as the wake reads it.</summary>
public sealed record FactoryInboxWaitingMessage(Guid Id, DateTime SentAtUtc, string State, string? ClaimSessionId);

/// <summary>One open bundle no Boss has read yet.</summary>
public sealed record FactoryInboxUnreadBundle(Guid Id, DateTime HandedAtUtc);

/// <summary>What the wake reads of one factory's inbox.</summary>
public sealed record FactoryInboxWakeFacts(IReadOnlyList<FactoryInboxWaitingMessage> ToFile, IReadOnlyList<FactoryInboxUnreadBundle> Unread);
