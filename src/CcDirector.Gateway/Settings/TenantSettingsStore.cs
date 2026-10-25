using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Settings;

/// <summary>
/// The Gateway-owned, restart-surviving store of per-tenant setting OVERRIDES (issue #2017): one
/// <c>tenant_settings</c> row per (tenant, key). This is the per-tenant home the hosted deny (issue #1863)
/// required before the AI / voice / notification settings could be served on the shared Gateway.
///
/// EXPLICIT TENANT ON EVERY CALL - the coordination boundary with the MTR mission is that this layer performs
/// NO ambient or static tenant inference: every read and write takes the <see cref="TenantId"/> the route
/// already resolved (via <c>ResolveReadTenant</c>). It goes through <see cref="GatewayDatabase.CreateContext(TenantId)"/>,
/// never the ambient <see cref="GatewayDatabase.CreateContext()"/>. On hosted a blank/unresolved tenant is a
/// route-level 403 BEFORE this store is reached; a <see cref="TenantId"/> cannot be blank by construction, so
/// no value here can ever be attributed to the wrong tenant, and a tenant with no override row simply has no
/// row (the resolver then returns the operator global default - never another tenant's value).
///
/// This store deals in OPAQUE strings keyed by <see cref="TenantSettingKeys"/>; the typed
/// <see cref="TenantSettingsResolver"/> above it owns parsing, validation, and the fall-back to the global
/// default. Threading: the Gateway is a single writer; every operation runs under this store's write lock over
/// a fresh pooled context.
///
/// READS ARE REMEMBERED (issue #4405). The per-account timers read the same few keys about 25 times a minute for
/// every account, most of them with no override at all, and every read waited on the database under the lock above -
/// a third of the hosted Gateway's database traffic. <see cref="Get"/> and the existing-value path of
/// <see cref="GetOrAdd"/> now answer from memory, absent values included, while the account's
/// <see cref="TenantSettingsGenerations"/> number is unchanged and the value is younger than <see cref="MaxAge"/>.
/// Every write anywhere moves that number, so this process never serves a value older than its own last write.
/// </summary>
public sealed class TenantSettingsStore
{
    /// <summary>
    /// The longest a remembered value is trusted. It bounds only what this process cannot see: a write made by
    /// ANOTHER Gateway process, which happens during the minute two containers overlap in a deploy. Every write in
    /// this process is seen at once through <see cref="TenantSettingsGenerations"/>.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly Func<long> _nowMs;
    private readonly ConcurrentDictionary<(string Tenant, string Key), Remembered> _remembered = new();

    /// <param name="db">The Gateway EF database this store reads and writes through. Required.</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    public TenantSettingsStore(GatewayDatabase db) : this(db, () => Environment.TickCount64)
    {
    }

    /// <summary>The same store with its clock passed in, so a test can age a remembered value past <see cref="MaxAge"/>.</summary>
    internal TenantSettingsStore(GatewayDatabase db, Func<long> nowMs)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _nowMs = nowMs ?? throw new ArgumentNullException(nameof(nowMs));
    }

    /// <summary>One value as read, null for no override, with the account's number it was read under.</summary>
    private sealed record Remembered(string? Value, long Generation, long ReadAtMs);

    private bool TryRemembered(TenantId tenant, string key, out string? value)
    {
        // The same refusal GatewayDatabase.CreateContext gives, made here because memory is now asked first.
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (_remembered.TryGetValue((tenant.Value, key), out var hit)
            && hit.Generation == TenantSettingsGenerations.Current(tenant.Value)
            && _nowMs() - hit.ReadAtMs < MaxAge.TotalMilliseconds)
        {
            value = hit.Value;
            return true;
        }
        value = null;
        return false;
    }

    private void Remember(TenantId tenant, string key, string? value, long generation) =>
        _remembered[(tenant.Value, key)] = new Remembered(value, generation, _nowMs());

    /// <summary>
    /// The raw override value for one tenant's key, or null when that tenant has set no override for it.
    /// Scoped to <paramref name="tenant"/> by the global query filter - a tenant can never read another
    /// tenant's row.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid or the key is null/empty.</exception>
    public string? Get(TenantId tenant, string key)
    {
        RequireKey(key);
        if (TryRemembered(tenant, key, out var remembered)) return remembered;
        lock (_gate)
        {
            var generation = TenantSettingsGenerations.Current(tenant.Value);
            using var ctx = _db.CreateContext(tenant);
            var value = ctx.TenantSettings.AsNoTracking().FirstOrDefault(e => e.Key == key)?.Value;
            Remember(tenant, key, value, generation);
            return value;
        }
    }

    /// <summary>
    /// Every override this tenant has set, as a key-&gt;value map (a detached copy). Scoped to
    /// <paramref name="tenant"/>. Used by the settings snapshot so the page reads all overrides in one query.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid.</exception>
    public IReadOnlyDictionary<string, string> GetAll(TenantId tenant)
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            return ctx.TenantSettings.AsNoTracking().ToList()
                .ToDictionary(e => e.Key, e => e.Value, StringComparer.Ordinal);
        }
    }

    /// <summary>
    /// Set (upsert) one tenant's override for a key, written through to the database before returning. The
    /// row is stamped with the EXPLICIT <paramref name="tenant"/>, never the ambient one. <paramref name="nowUtc"/>
    /// is injected so tests are deterministic.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid, the key is null/empty, or the value is null.</exception>
    public void Set(TenantId tenant, string key, string value, DateTime nowUtc)
    {
        RequireKey(key);
        if (value is null) throw new ArgumentNullException(nameof(value));

        var updatedAtUtc = nowUtc.ToUniversalTime();
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.TenantSettings.FirstOrDefault(e => e.Key == key);
            if (existing is null)
            {
                ctx.TenantSettings.Add(new TenantSettingEntity
                {
                    TenantId = tenant.Value,
                    Key = key,
                    Value = value,
                    UpdatedAtUtc = updatedAtUtc,
                });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAtUtc = updatedAtUtc;
            }
            ctx.SaveChanges();
            FileLog.Write($"[TenantSettingsStore] Set: tenant={tenant.ToLogString()} key={key} valueLength={value.Length}");
        }
    }

    /// <summary>
    /// The value of one tenant's key, creating it with <paramref name="create"/> when there is none - and NEVER
    /// replacing one that exists. Unlike <see cref="Set"/> this does not upsert: when two processes create at the
    /// same moment (two containers overlapping during a restart), the database's primary key lets exactly one
    /// row in, the other insert fails, and the loser reads and returns the winner's value. So every caller gets
    /// the one value that was stored.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid or the key is not a setting key.</exception>
    public string GetOrAdd(TenantId tenant, string key, Func<string> create, DateTime nowUtc)
    {
        RequireKey(key);
        if (create is null) throw new ArgumentNullException(nameof(create));
        if (TryRemembered(tenant, key, out var remembered) && remembered is not null) return remembered;
        lock (_gate)
        {
            using (var ctx = _db.CreateContext(tenant))
            {
                var generation = TenantSettingsGenerations.Current(tenant.Value);
                var existing = ctx.TenantSettings.AsNoTracking().FirstOrDefault(e => e.Key == key);
                if (existing is not null)
                {
                    Remember(tenant, key, existing.Value, generation);
                    return existing.Value;
                }

                var value = create();
                ctx.TenantSettings.Add(new TenantSettingEntity
                {
                    TenantId = tenant.Value,
                    Key = key,
                    Value = value,
                    UpdatedAtUtc = nowUtc.ToUniversalTime(),
                });
                try
                {
                    ctx.SaveChanges();
                    FileLog.Write($"[TenantSettingsStore] GetOrAdd: tenant={tenant.ToLogString()} key={key} created");
                    return value;
                }
                catch (DbUpdateException ex)
                {
                    FileLog.Write($"[TenantSettingsStore] GetOrAdd: tenant={tenant.ToLogString()} key={key} was created " +
                                  $"by another process at the same moment ({ex.InnerException?.Message ?? ex.Message}) - reading its value");
                }
            }
            using var reread = _db.CreateContext(tenant);
            return reread.TenantSettings.AsNoTracking().First(e => e.Key == key).Value;
        }
    }

    /// <summary>
    /// Clear one tenant's override for a key so the resolver falls back to the operator global default. A no-op
    /// when no override exists. Returns true when a row was removed.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid or the key is null/empty.</exception>
    public bool Remove(TenantId tenant, string key)
    {
        RequireKey(key);
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            var existing = ctx.TenantSettings.FirstOrDefault(e => e.Key == key);
            if (existing is null) return false;
            ctx.TenantSettings.Remove(existing);
            ctx.SaveChanges();
            FileLog.Write($"[TenantSettingsStore] Remove: tenant={tenant.ToLogString()} key={key}");
            return true;
        }
    }

    /// <summary>
    /// Set and remove several of one tenant's keys in ONE save, so the values change together or not at all. A null
    /// value removes that key.
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is invalid, or a key is not a per-tenant setting key.</exception>
    public void Apply(TenantId tenant, IReadOnlyDictionary<string, string?> changes, DateTime nowUtc)
    {
        if (changes is null) throw new ArgumentNullException(nameof(changes));
        lock (_gate)
        {
            using var ctx = _db.CreateContext(tenant);
            ApplyIn(ctx, tenant, changes, nowUtc);
            ctx.SaveChanges();
            FileLog.Write($"[TenantSettingsStore] Apply: tenant={tenant.ToLogString()} keys={string.Join(",", changes.Keys)}");
        }
    }

    /// <summary>
    /// Stage the same changes on a context the caller owns, so they commit in the caller's transaction together with
    /// other rows. Nothing is saved here.
    /// </summary>
    internal static void ApplyIn(GatewayDbContext ctx, TenantId tenant, IReadOnlyDictionary<string, string?> changes, DateTime nowUtc)
    {
        var updatedAtUtc = nowUtc.ToUniversalTime();
        foreach (var (key, value) in changes)
        {
            RequireKey(key);
            var existing = ctx.TenantSettings.FirstOrDefault(e => e.Key == key);
            if (value is null)
            {
                if (existing is not null) ctx.TenantSettings.Remove(existing);
            }
            else if (existing is null)
            {
                ctx.TenantSettings.Add(new TenantSettingEntity
                {
                    TenantId = tenant.Value,
                    Key = key,
                    Value = value,
                    UpdatedAtUtc = updatedAtUtc,
                });
            }
            else
            {
                existing.Value = value;
                existing.UpdatedAtUtc = updatedAtUtc;
            }
        }
    }

    private static void RequireKey(string key)
    {
        if (string.IsNullOrEmpty(key))
            throw new ArgumentException("A setting key is required.", nameof(key));
        if (!TenantSettingKeys.All.Contains(key))
            throw new ArgumentException($"'{key}' is not a per-tenant setting key.", nameof(key));
    }
}
