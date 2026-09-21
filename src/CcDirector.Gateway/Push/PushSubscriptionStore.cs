using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Push;

/// <summary>
/// The Gateway-side set of Web Push subscriptions - one per phone/browser that opted in to the
/// app-icon "needs you" dot. A subscription is the browser's push endpoint plus the two client
/// public keys (<c>p256dh</c> and <c>auth</c>) needed to encrypt a message to it, exactly the shape
/// a browser's <c>PushSubscription.toJSON()</c> produces.
///
/// PERSISTENCE (Hosted Gateway mission, Step 1b): subscriptions live in the EF data layer's
/// <c>push_subscriptions</c> table (SQLite locally), NOT the old hand-rolled
/// <c>push-subscriptions.json</c>. The public API and observable behavior are unchanged.
///
/// Keyed by endpoint, which is unique per subscription (SQLite's default BINARY collation compares it
/// ordinally, matching the legacy <c>Dictionary(StringComparer.Ordinal)</c>), so re-subscribing the same
/// device is an idempotent upsert rather than a duplicate. There is NO per-user column - the legacy shape
/// had none; <c>tenant_id</c> scopes the tenant and nothing more.
///
/// ONE-TIME IMPORT: on first run after the upgrade, if a legacy <c>push-subscriptions.json</c> exists and
/// the table is empty, every subscription is imported (through the shared recoverable-import helper), then
/// the JSON is renamed aside as a backup.
///
/// Threading: the Gateway is a single writer. Every operation runs under this store's write lock over a
/// fresh pooled context.
/// </summary>
public sealed class PushSubscriptionStore
{
    private readonly object _gate = new();
    private readonly GatewayDatabase _db;
    private readonly string _legacyJsonPath;
    private readonly Func<DateTime> _utcNow;

    /// <param name="db">The Gateway EF database this store reads and writes through.</param>
    /// <param name="legacyJsonPath">The legacy <c>push-subscriptions.json</c> path to import ONCE if it
    /// exists and the table is empty. REQUIRED (no silent default).</param>
    /// <exception cref="ArgumentNullException">The database is null.</exception>
    /// <exception cref="ArgumentException">The legacy path is null/empty/whitespace.</exception>
    /// <param name="utcNow">The clock a held count's age is measured on; the system clock when omitted.</param>
    public PushSubscriptionStore(GatewayDatabase db, string legacyJsonPath, Func<DateTime>? utcNow = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        if (string.IsNullOrWhiteSpace(legacyJsonPath))
            throw new ArgumentException("legacy json path is required", nameof(legacyJsonPath));
        _legacyJsonPath = legacyJsonPath;

        lock (_gate)
            ImportLegacyJsonIfNeeded();
    }

    /// <summary>
    /// The number of registered subscriptions in the current account.
    ///
    /// HELD, NOT COUNTED EACH TIME (devthrottle_internal#2199). The needs-you notifier asks this for every account
    /// every few seconds before it does anything else - a database round trip every time to learn a number that
    /// changes when a phone subscribes. The count is held per account and discarded by every write
    /// through this store (<see cref="Add"/>, <see cref="Remove"/>, the legacy import). The age ceiling covers the
    /// one writer this process cannot hear: a second Gateway process during a deploy, which could accept a
    /// subscription this one would otherwise never count.
    /// </summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                using var ctx = _db.CreateContext();
                var tenant = ctx.ActiveTenant ?? "";
                var now = _utcNow();
                if (_counts.TryGetValue(tenant, out var held) && now - held.ReadAtUtc < CountMaxAge)
                    return held.Count;
                var count = ctx.PushSubscriptions.Count();
                _counts[tenant] = (count, now);
                return count;
            }
        }
    }

    /// <summary>The longest a held count is served without asking the database. See <see cref="Count"/>.</summary>
    internal static readonly TimeSpan CountMaxAge = TimeSpan.FromSeconds(30);

    private readonly Dictionary<string, (int Count, DateTime ReadAtUtc)> _counts = new(StringComparer.Ordinal);

    /// <summary>
    /// Record (or refresh) a subscription. A repeat of the same endpoint replaces the prior keys and
    /// keeps one entry. Returns true when a NEW endpoint was added, false when an existing one was
    /// refreshed - the caller uses this to decide whether a fresh device should get an immediate
    /// "current count" push.
    /// </summary>
    public bool Add(string endpoint, string p256dh, string auth)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            throw new ArgumentException("endpoint is required", nameof(endpoint));
        if (string.IsNullOrWhiteSpace(p256dh))
            throw new ArgumentException("p256dh key is required", nameof(p256dh));
        if (string.IsNullOrWhiteSpace(auth))
            throw new ArgumentException("auth key is required", nameof(auth));

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var existing = ctx.PushSubscriptions.FirstOrDefault(e => e.Endpoint == endpoint);
            var isNew = existing is null;
            // A refresh replaces the keys AND restamps CreatedAtUtc, exactly as the legacy store did (it
            // replaced the whole record on every Add).
            if (existing is null)
            {
                ctx.PushSubscriptions.Add(new PushSubscriptionEntity
                {
                    Endpoint = endpoint,
                    TenantId = ctx.ActiveTenant!,
                    P256dh = p256dh,
                    Auth = auth,
                    CreatedAtUtc = DateTime.UtcNow,
                });
            }
            else
            {
                existing.P256dh = p256dh;
                existing.Auth = auth;
                existing.CreatedAtUtc = DateTime.UtcNow;
            }
            ctx.SaveChanges();
            _counts.Clear();
            FileLog.Write($"[PushSubscriptionStore] {(isNew ? "Added" : "Refreshed")} a subscription, total={ctx.PushSubscriptions.Count()}");
            return isNew;
        }
    }

    /// <summary>Drop a subscription by endpoint. Returns true when one was removed.</summary>
    public bool Remove(string endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
            return false;

        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            var existing = ctx.PushSubscriptions.FirstOrDefault(e => e.Endpoint == endpoint);
            if (existing is null)
                return false;
            ctx.PushSubscriptions.Remove(existing);
            ctx.SaveChanges();
            _counts.Clear();
            FileLog.Write($"[PushSubscriptionStore] Removed a subscription, total={ctx.PushSubscriptions.Count()}");
            return true;
        }
    }

    /// <summary>A snapshot of every stored subscription.</summary>
    public IReadOnlyList<StoredPushSubscription> All()
    {
        lock (_gate)
        {
            using var ctx = _db.CreateContext();
            return ctx.PushSubscriptions.AsNoTracking().ToList().Select(ToRecord).ToList();
        }
    }

    private static StoredPushSubscription ToRecord(PushSubscriptionEntity e) => new()
    {
        Endpoint = e.Endpoint,
        P256dh = e.P256dh,
        Auth = e.Auth,
        CreatedAtUtc = e.CreatedAtUtc,
    };

    // ---- one-time legacy JSON import --------------------------------------------------------------

    /// <summary>
    /// Import a legacy <c>push-subscriptions.json</c> exactly once, through the shared recoverable-import
    /// plumbing (<see cref="LegacyJsonImport.Recoverable"/>): import only when the file exists AND the table
    /// is empty; recover a lingering file idempotently; rename aside best-effort after a successful import.
    /// </summary>
    private void ImportLegacyJsonIfNeeded()
        => LegacyJsonImport.Recoverable(
            _legacyJsonPath,
            "[PushSubscriptionStore]",
            isPopulated: () => { using var ctx = _db.CreateContext(); return ctx.PushSubscriptions.Any(); },
            importCommitted: ImportRowsFromLegacyJson);

    /// <summary>
    /// Parse the legacy file (a top-level array of subscriptions) and insert every one inside a transaction.
    /// Mirrors the old load: skip an empty endpoint, last-wins on a duplicate endpoint. Fail-loud and
    /// all-or-nothing - a parse error throws and imports nothing (the file is left in place).
    /// </summary>
    private void ImportRowsFromLegacyJson()
    {
        using var ctx = _db.CreateContext();

        List<StoredPushSubscription>? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<List<StoredPushSubscription>>(
                File.ReadAllText(_legacyJsonPath),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
        catch (Exception ex)
        {
            FileLog.Write($"[PushSubscriptionStore] Import FAILED: legacy file {_legacyJsonPath} could not be read: {ex.Message}");
            throw new InvalidOperationException(
                $"The legacy push-subscriptions file '{_legacyJsonPath}' could not be parsed for the one-time " +
                $"import: {ex.Message}. The Gateway will not start with a partial import. Fix or move the file " +
                "aside and restart.", ex);
        }

        // A null root (the JSON literal "null") is unreadable, not an empty store - fail loud and leave the
        // file in place, exactly like a parse error, matching the other stores' import contract.
        if (parsed is null)
        {
            FileLog.Write($"[PushSubscriptionStore] Import FAILED: legacy file {_legacyJsonPath} deserialized to a null document");
            throw new InvalidOperationException(
                $"The legacy push-subscriptions file '{_legacyJsonPath}' could not be parsed for the one-time " +
                "import: the document is null. The Gateway will not start with a partial import. Fix or move " +
                "the file aside and restart.");
        }

        // Reproduce the old load's row handling, INCLUDING last-wins on a duplicate endpoint (the old
        // in-memory Dictionary keyed by endpoint overwrote), so the imported set matches.
        var toImport = new Dictionary<string, StoredPushSubscription>(StringComparer.Ordinal);
        foreach (var s in parsed)
        {
            if (string.IsNullOrWhiteSpace(s.Endpoint))
                continue;
            toImport[s.Endpoint] = s;
        }

        using var tx = ctx.Database.BeginTransaction();
        foreach (var s in toImport.Values)
        {
            ctx.PushSubscriptions.Add(new PushSubscriptionEntity
            {
                Endpoint = s.Endpoint,
                TenantId = ctx.ActiveTenant!,
                P256dh = s.P256dh,
                Auth = s.Auth,
                CreatedAtUtc = s.CreatedAtUtc,
            });
        }
        ctx.SaveChanges();
        tx.Commit();
        _counts.Clear();

        FileLog.Write($"[PushSubscriptionStore] Import: {toImport.Count} subscription(s) imported from {_legacyJsonPath}");
    }
}

/// <summary>One stored Web Push subscription: the browser push endpoint and the client keys needed
/// to encrypt a message to it. Mirrors a browser <c>PushSubscription.toJSON()</c> (endpoint + keys).</summary>
public sealed class StoredPushSubscription
{
    public string Endpoint { get; set; } = "";

    /// <summary>The client P-256 ECDH public key (base64url), from <c>subscription.keys.p256dh</c>.</summary>
    public string P256dh { get; set; } = "";

    /// <summary>The client auth secret (base64url), from <c>subscription.keys.auth</c>.</summary>
    public string Auth { get; set; } = "";

    public DateTime CreatedAtUtc { get; set; }
}
