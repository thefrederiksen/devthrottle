using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// The authoritative database-backed registry of per-device credentials. The legacy
/// <c>devices.json</c> file is read only by <see cref="DeviceRegistryImporter"/> during the one-time
/// cutover. Every authentication and mutation reads the shared database, so separate Gateway
/// instances observe enrollment, rotation, revocation, and removal immediately.
/// </summary>
public sealed class DeviceRegistry : IDisposable
{
    public const string StatusActive = "active";
    public const string StatusRevoked = "revoked";
    public const string DefaultDeviceType = "workstation";
    public const string UnknownPlatform = "unknown";

    private const int KeyPrefixLength = 8;
    private const int KeyLast4Length = 4;
    private const int MaximumWriteAttempts = 8;

    private readonly GatewayDatabase _db;
    private readonly bool _ownsDatabase;
    private readonly bool _isHosted;
    private readonly bool _teamsReleased;
    private readonly string _storePath;
    private readonly object _enrollLock = new();
    private bool _disposed;

    public DeviceRegistry() : this(storePath: null)
    {
    }

    /// <summary>
    /// Compatibility constructor for isolated callers and tests. Runtime production wiring passes the
    /// host-owned <see cref="GatewayDatabase"/> through the other constructor.
    /// </summary>
    public DeviceRegistry(string? storePath)
    {
        _storePath = ResolveStorePath(storePath);
        _db = new GatewayDatabase(new SingleTenantContext(), _storePath + ".gateway.db");
        _ownsDatabase = true;
        _isHosted = false;
        InitializeAuthority();
    }

    /// <param name="db">The host-owned database shared by every Gateway replica.</param>
    /// <param name="storePath">The legacy JSON path used only by the one-time importer.</param>
    /// <param name="isHosted">Whether tenant-bound hosted credential rules must be enforced.</param>
    /// <param name="deferInitialize">
    /// When true the constructor stops after wiring, and the caller must call <see cref="Initialize"/> once
    /// the database is open. The hosted Gateway passes true because <see cref="InitializeAuthority"/> READS
    /// THE DATABASE - it runs the one-time import and then decides which credentials are valid - and that
    /// read used to sit in front of the listener bind. A slow database therefore delayed the bind, and when
    /// it delayed it past the platform's container-start deadline the platform stopped the SITE, taking the
    /// healthy container down with it (#2383, #2585).
    ///
    /// Nothing is served before Initialize runs: the Gateway's readiness gate answers 503 to every request
    /// but /healthz until both the database and this authority are up. Binding early is what stops the
    /// platform from giving up on the site; it is not permission to serve without knowing which device keys
    /// are valid.
    /// </param>
    /// <param name="teamsReleased">
    /// Whether Teams is released on this Gateway (<see cref="Teams.TeamsReleaseSwitch"/>). Only then may a hosted key
    /// be bound to a TEAM's tenant (devthrottle_internal#2311). Off, a hosted key is valid exactly as before: only when
    /// its account owns the tenant it is bound to.
    /// </param>
    public DeviceRegistry(GatewayDatabase db, string? storePath = null, bool isHosted = false,
        bool deferInitialize = false, bool teamsReleased = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storePath = ResolveStorePath(storePath);
        _isHosted = isHosted;
        _teamsReleased = teamsReleased;
        if (!deferInitialize)
            InitializeAuthority();
    }

    /// <summary>
    /// Run the one-time import and establish which device credentials are authoritative. Idempotent, and a
    /// no-op for a registry whose constructor already did it.
    /// </summary>
    public void Initialize()
    {
        if (_initialized)
            return;
        InitializeAuthority();
    }

    private bool _initialized;

    /// <summary>True once the credential authority has been established.</summary>
    public bool IsInitialized => _initialized;

    /// <summary>The legacy registry path used by the one-time importer.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Generate and commit a local device credential, replacing any prior row for the device.
    /// </summary>
    public DeviceRegistrationResponse Register(
        string deviceId,
        string machineName,
        string? platform = null,
        string? deviceType = null)
        => RegisterCore(
            deviceId,
            machineName,
            platform,
            deviceType,
            TenantId.Local.Value,
            accountSubject: null,
            preserveActiveRecord: false);

    /// <summary>
    /// Generate and commit a local device credential. Re-enrollment rotates the key while preserving
    /// the active row's binding and display metadata.
    /// </summary>
    public DeviceRegistrationResponse RegisterIfAbsent(
        string deviceId,
        string machineName,
        string? platform = null,
        string? deviceType = null)
        => RegisterCore(
            deviceId,
            machineName,
            platform,
            deviceType,
            TenantId.Local.Value,
            accountSubject: null,
            preserveActiveRecord: true);

    /// <summary>
    /// Atomically enroll or rotate a hosted device. The credential hash and verified tenant ownership
    /// commit in the same transaction; an unbound hosted key is never visible.
    /// </summary>
    /// <summary>
    /// TEST SEAM ONLY (null in production): fired with the account subject after a device is bound to a hosted
    /// tenant, so a non-production (test) hosted Gateway can auto-provision the entitlement that production
    /// requires at the paid enrollment endpoint - which the low-level test enroll paths bypass. Wired ONLY when
    /// the deployment is NOT a real hosted image (GatewayHostedMode.IsHostedImage is false), so a production
    /// hosted image never invokes it. Never touched on self-host.
    /// </summary>
    internal Action<string>? OnAccountBoundForTest;

    public DeviceRegistrationResponse RegisterForTenant(
        TenantId tenant,
        string accountSubject,
        string deviceId,
        string machineName,
        string? platform = null,
        string? deviceType = null)
    {
        if (!tenant.IsValid || tenant.IsLocal || tenant.IsSystem)
            throw new ArgumentException("A hosted account tenant is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("accountSubject is required", nameof(accountSubject));

        var response = RegisterCore(
            deviceId,
            machineName,
            platform,
            deviceType,
            tenant.Value,
            accountSubject.Trim(),
            preserveActiveRecord: true);
        // After the device write completes (RegisterCore has disposed its own context), let a test harness
        // provision the entitlement. No-op / null in production.
        OnAccountBoundForTest?.Invoke(accountSubject.Trim());
        return response;
    }

    /// <summary>
    /// Resolve a presented key once into a typed, immutable identity. No raw key or stored hash is
    /// returned. Database and registry-integrity failures are a typed unavailable result, never an
    /// unknown-key result and never a grant.
    /// </summary>
    public DeviceCredentialResolution ResolveCredential(string? key)
    {
        if (string.IsNullOrEmpty(key))
            return DeviceCredentialResolution.Unknown;

        try
        {
            var suppliedBytes = HashKeyBytes(key);
            var suppliedHash = HashBytesToHex(suppliedBytes);
            // Load-test Stage 0 (issue #1173): every authenticated request pays this uncached database
            // lookup; counting it puts a number on that cost beside the roster's own reads.
            Diagnostics.LoadTestMetrics.DeviceCredentialLookupObserved();
            using var ctx = _db.CreateUnscopedContext();
            var matches = ctx.DeviceCredentials
                .AsNoTracking()
                .Where(d => d.DeviceKeyHash == suppliedHash)
                .Take(2)
                .ToList();

            if (matches.Count == 0)
                return DeviceCredentialResolution.Unknown;

            if (matches.Count != 1)
            {
                FileLog.Write("[DeviceRegistry] ResolveCredential FAILED: duplicate credential hashes in the authoritative registry");
                return DeviceCredentialResolution.Unavailable;
            }

            var row = matches[0];
            var storedHash = DecodeHash(row.DeviceKeyHash);
            if (storedHash is null
                || storedHash.Length != suppliedBytes.Length
                || !CryptographicOperations.FixedTimeEquals(storedHash, suppliedBytes))
            {
                FileLog.Write("[DeviceRegistry] ResolveCredential FAILED: malformed credential hash in the authoritative registry");
                return DeviceCredentialResolution.Unavailable;
            }

            return Judge(ctx, row);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[DeviceRegistry] ResolveCredential FAILED: authoritative database unavailable ({ex.GetType().Name})");
            return DeviceCredentialResolution.Unavailable;
        }
    }

    /// <summary>
    /// The one judgement of a stored credential row: its identity, and whether it is active - its status is active,
    /// it carries no revocation, and on the hosted Gateway its binding is valid (its account owns the tenant, or the
    /// tenant is a team its person may run sessions in).
    /// </summary>
    private DeviceCredentialResolution Judge(GatewayDbContext ctx, DeviceCredentialEntity row)
    {
        var tenant = string.IsNullOrWhiteSpace(row.TenantId) ? null : row.TenantId;
        var malformedHostedBinding = _isHosted
            && (tenant is null
                || string.Equals(tenant, TenantId.Local.Value, StringComparison.Ordinal)
                || string.Equals(tenant, TenantId.System.Value, StringComparison.Ordinal)
                || string.IsNullOrWhiteSpace(row.AccountSubject));
        var ownsTenant = _isHosted && !malformedHostedBinding
            && ctx.Tenants.AsNoTracking().Any(t => t.Id == tenant && t.AccountSubject == row.AccountSubject);
        // A TEAM key is one accepted ONLY through the team half of the rule - its person owns no such tenant - and it
        // is named so on the identity, because a team key may say Hello for no Director id but its own (review F2).
        var teamBinding = _isHosted && !malformedHostedBinding && !ownsTenant
            && IsLiveTeamBinding(ctx, tenant, row.AccountSubject);
        var invalidHostedBinding = _isHosted && (malformedHostedBinding || (!ownsTenant && !teamBinding));

        var identity = new DeviceCredentialIdentity(
            row.DeviceId,
            tenant,
            string.IsNullOrWhiteSpace(row.DeviceType) ? DefaultDeviceType : row.DeviceType,
            row.Status,
            string.IsNullOrWhiteSpace(row.AccountSubject) ? null : row.AccountSubject,
            teamBinding);

        if (invalidHostedBinding
            || !string.Equals(row.Status, StatusActive, StringComparison.Ordinal)
            || row.RevokedAtUtc is not null)
            return new DeviceCredentialResolution(DeviceCredentialResolutionKind.Revoked, identity);

        return new DeviceCredentialResolution(DeviceCredentialResolutionKind.Active, identity);
    }

    /// <summary>
    /// The ACTIVE keys of one Director, by the Director's own id: every row whose registry id is
    /// <c>&lt;namespace&gt;|<paramref name="directorId"/></c> and that <see cref="ResolveCredential"/> would judge
    /// active, whoever it was issued to. A move names its Director this way (devthrottle_internal#2311); the caller
    /// filters by person. Normally one; enrollment with Teams released keeps it to one per person.
    /// </summary>
    public IReadOnlyList<DeviceCredentialIdentity> ActiveKeysOfDirector(string directorId)
    {
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        var suffix = "|" + directorId.Trim();
        using var ctx = _db.CreateUnscopedContext();
        var rows = ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.DeviceId.EndsWith(suffix) && d.Status == StatusActive && d.RevokedAtUtc == null)
            .ToList();
        var active = rows
            .Select(row => Judge(ctx, row))
            .Where(r => r.Kind == DeviceCredentialResolutionKind.Active)
            .Select(r => r.Identity!)
            .ToList();
        FileLog.Write($"[DeviceRegistry] ActiveKeysOfDirector: director={directorId.Trim()} active={active.Count} of {rows.Count} row(s)");
        return active;
    }

    /// <summary>
    /// Revoke every OTHER active key one person holds for one Director - the rows <c>&lt;namespace&gt;|<paramref
    /// name="directorId"/></c> of <paramref name="accountSubject"/> other than <paramref name="keepDeviceId"/>. Called
    /// when that Director is set up again, for a team or for the person's own account, so a Director holds exactly one
    /// working key and is in exactly one place (devthrottle_internal#2311). Returns the number revoked.
    /// </summary>
    public int RevokeOtherKeysOfDirector(string accountSubject, string directorId, string keepDeviceId, string reason)
    {
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("accountSubject is required", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(directorId))
            throw new ArgumentException("directorId is required", nameof(directorId));
        if (string.IsNullOrWhiteSpace(keepDeviceId))
            throw new ArgumentException("keepDeviceId is required", nameof(keepDeviceId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));

        var subject = accountSubject.Trim();
        var suffix = "|" + directorId.Trim();
        var why = reason.Trim();
        var when = DateTime.UtcNow;
        using var ctx = _db.CreateUnscopedContext();
        var changed = ctx.DeviceCredentials
            .Where(d => d.AccountSubject == subject && d.DeviceId.EndsWith(suffix) && d.DeviceId != keepDeviceId
                        && d.Status == StatusActive)
            .ExecuteUpdate(setters => setters
                .SetProperty(d => d.Status, StatusRevoked)
                .SetProperty(d => d.RevokedAtUtc, when)
                .SetProperty(d => d.RevokedReason, why));
        FileLog.Write($"[DeviceRegistry] RevokeOtherKeysOfDirector: director={directorId.Trim()} revoked={changed} (reason={why})");
        return changed;
    }

    public bool IsValidDeviceKey(string? key)
        => ResolveCredential(key).Kind == DeviceCredentialResolutionKind.Active;

    public string? DeviceTypeForKey(string? key)
    {
        var resolution = ResolveCredential(key);
        return resolution.Kind == DeviceCredentialResolutionKind.Active
            ? resolution.Identity?.DeviceType
            : null;
    }

    public string? TenantForKey(string? key)
    {
        var resolution = ResolveCredential(key);
        if (resolution.Kind != DeviceCredentialResolutionKind.Active)
            return null;

        var tenant = resolution.Identity?.TenantId;
        return string.Equals(tenant, TenantId.Local.Value, StringComparison.Ordinal) ? null : tenant;
    }

    /// <summary>
    /// Compatibility mutation for older self-host call sites. Hosted enrollment uses
    /// <see cref="RegisterForTenant"/> so ownership is never a second transaction.
    /// </summary>
    public bool SetAccountBinding(string deviceId, string accountSubject, string tenantId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("accountSubject is required", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(tenantId))
            throw new ArgumentException("tenantId is required", nameof(tenantId));

        int changed;
        using (var ctx = _db.CreateUnscopedContext())
        {
            changed = ctx.DeviceCredentials
                .Where(d => d.DeviceId == deviceId)
                .ExecuteUpdate(setters => setters
                    .SetProperty(d => d.AccountSubject, accountSubject.Trim())
                    .SetProperty(d => d.TenantId, tenantId.Trim()));
            FileLog.Write($"[DeviceRegistry] SetAccountBinding: device id={deviceId}, found={changed == 1}");
        }
        // After the device write commits and its context is disposed, let a test harness provision the
        // entitlement (no-op / null in production).
        if (changed == 1)
            OnAccountBoundForTest?.Invoke(accountSubject.Trim());
        return changed == 1;
    }

    public void SetCloudDeviceId(string deviceId, string cloudDeviceId)
        => SetCloudDeviceIdForTenant(TenantId.Local, deviceId, cloudDeviceId);

    public void SetCloudDeviceIdForTenant(TenantId tenant, string deviceId, string cloudDeviceId)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(cloudDeviceId))
            throw new ArgumentException("cloudDeviceId is required", nameof(cloudDeviceId));

        using var ctx = _db.CreateUnscopedContext();
        var changed = ctx.DeviceCredentials
            .Where(d => d.DeviceId == deviceId && d.TenantId == tenant.Value)
            .ExecuteUpdate(setters => setters.SetProperty(d => d.CloudDeviceId, cloudDeviceId));
        FileLog.Write($"[DeviceRegistry] SetCloudDeviceId: device id={deviceId}, mirrored to cloud id={cloudDeviceId}, found={changed == 1}");
    }

    public bool Remove(string deviceId)
        => RemoveForTenant(TenantId.Local, deviceId);

    public bool RemoveForTenant(TenantId tenant, string deviceId)
    {
        if (!tenant.IsValid || string.IsNullOrWhiteSpace(deviceId))
            return false;

        using var ctx = _db.CreateUnscopedContext();
        var removed = ctx.DeviceCredentials
            .Where(d => d.DeviceId == deviceId && d.TenantId == tenant.Value)
            .ExecuteDelete();
        FileLog.Write($"[DeviceRegistry] Remove: device id={deviceId}, removed={removed == 1}");
        return removed == 1;
    }

    /// <summary>Persist durable revocation tombstones for every active credential owned by a tenant.</summary>
    public int RevokeTenant(TenantId tenant, string reason, DateTime? revokedAtUtc = null)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));

        var when = revokedAtUtc ?? DateTime.UtcNow;
        using var ctx = _db.CreateUnscopedContext();
        using var transaction = ctx.Database.BeginTransaction();
        var changed = ctx.DeviceCredentials
            .Where(d => d.TenantId == tenant.Value && d.Status == StatusActive)
            .ExecuteUpdate(setters => setters
                .SetProperty(d => d.Status, StatusRevoked)
                .SetProperty(d => d.RevokedAtUtc, when)
                .SetProperty(d => d.RevokedReason, reason.Trim()));
        transaction.Commit();
        FileLog.Write($"[DeviceRegistry] RevokeTenant: revoked={changed}");
        return changed;
    }

    /// <summary>
    /// Put back device credentials that a WITHDRAWN rule tombstoned. Matches only rows revoked for exactly
    /// <paramref name="reason"/> strictly before <paramref name="revokedBeforeUtc"/>, and only for a tenant
    /// <paramref name="mayHoldHostedKeys"/> accepts today. The key hash was never cleared by the tombstone, so the
    /// same key the Director still holds resolves active again - nothing is asked of the device's owner.
    ///
    /// This is NOT a general un-revoke, and it deliberately does not weaken the policy in
    /// <see cref="Tenancy.TenantAccessRevoker"/> that a resubscribe never silently un-revokes a key. The
    /// cutoff is what keeps it narrow: a revocation made after the rule was withdrawn is never touched, however
    /// often this runs. Idempotent - a second run matches nothing.
    /// </summary>
    /// <returns>The number of credentials reinstated.</returns>
    public int ReinstateRevokedBefore(string reason, DateTime revokedBeforeUtc, Func<TenantId, bool> mayHoldHostedKeys)
    {
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));
        ArgumentNullException.ThrowIfNull(mayHoldHostedKeys);

        var trimmed = reason.Trim();
        using var ctx = _db.CreateUnscopedContext();
        var tenantIds = ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.Status == StatusRevoked && d.RevokedReason == trimmed
                        && d.RevokedAtUtc != null && d.RevokedAtUtc < revokedBeforeUtc && d.TenantId != null)
            .Select(d => d.TenantId!)
            .Distinct()
            .ToList();

        var reinstated = 0;
        var tenantsSkipped = 0;
        foreach (var id in tenantIds)
        {
            var tenant = new TenantId(id);
            if (!tenant.IsValid || !mayHoldHostedKeys(tenant))
            {
                tenantsSkipped++;
                continue;
            }

            using var transaction = ctx.Database.BeginTransaction();
            reinstated += ctx.DeviceCredentials
                .Where(d => d.TenantId == id && d.Status == StatusRevoked && d.RevokedReason == trimmed
                            && d.RevokedAtUtc != null && d.RevokedAtUtc < revokedBeforeUtc)
                .ExecuteUpdate(setters => setters
                    .SetProperty(d => d.Status, StatusActive)
                    .SetProperty(d => d.RevokedAtUtc, (DateTime?)null)
                    .SetProperty(d => d.RevokedReason, (string?)null));
            transaction.Commit();
        }

        FileLog.Write($"[DeviceRegistry] ReinstateRevokedBefore: reason={trimmed}, tenants={tenantIds.Count}, " +
                      $"reinstated={reinstated}, tenantsSkipped={tenantsSkipped}");
        return reinstated;
    }

    /// <summary>
    /// Tombstone every ACTIVE key one person holds in one tenant - their Directors set up for one team
    /// (devthrottle_internal#2311). Only that person's keys in that tenant: the team's other members, the person's
    /// other teams and their personal tenant are untouched. Durable, like <see cref="RevokeTenant"/>, so a key cut
    /// off when its person left the team stays cut off if they are invited back - they set the Director up again,
    /// exactly as the start-up quarantine would require after a restart.
    /// </summary>
    /// <returns>The number of keys revoked.</returns>
    public int RevokeTenantMember(TenantId tenant, string accountSubject, string reason, DateTime? revokedAtUtc = null)
    {
        if (!tenant.IsValid || tenant.IsLocal || tenant.IsSystem)
            throw new ArgumentException("A hosted tenant is required.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(accountSubject))
            throw new ArgumentException("accountSubject is required", nameof(accountSubject));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));

        var when = revokedAtUtc ?? DateTime.UtcNow;
        var subject = accountSubject.Trim();
        var why = reason.Trim();
        using var ctx = _db.CreateUnscopedContext();
        var changed = ctx.DeviceCredentials
            .Where(d => d.TenantId == tenant.Value && d.AccountSubject == subject && d.Status == StatusActive)
            .ExecuteUpdate(setters => setters
                .SetProperty(d => d.Status, StatusRevoked)
                .SetProperty(d => d.RevokedAtUtc, when)
                .SetProperty(d => d.RevokedReason, why));
        FileLog.Write($"[DeviceRegistry] RevokeTenantMember: tenant {tenant.ToLogString()} revoked={changed} (reason={why})");
        return changed;
    }

    /// <summary>
    /// Tombstone ONE key, by the device row it belongs to. Used when a Director moves to another team: its old key
    /// must never work again (devthrottle_internal#2311). Returns whether an active key was revoked.
    /// </summary>
    public bool RevokeDevice(string deviceId, string reason, DateTime? revokedAtUtc = null)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("reason is required", nameof(reason));

        var when = revokedAtUtc ?? DateTime.UtcNow;
        var why = reason.Trim();
        using var ctx = _db.CreateUnscopedContext();
        var changed = ctx.DeviceCredentials
            .Where(d => d.DeviceId == deviceId && d.Status == StatusActive)
            .ExecuteUpdate(setters => setters
                .SetProperty(d => d.Status, StatusRevoked)
                .SetProperty(d => d.RevokedAtUtc, when)
                .SetProperty(d => d.RevokedReason, why));
        FileLog.Write($"[DeviceRegistry] RevokeDevice: device id={deviceId}, revoked={changed == 1} (reason={why})");
        return changed == 1;
    }

    /// <summary>
    /// The person a device row's key was issued to - only while that key is ACTIVE (active status, no revocation) and
    /// bound to <paramref name="tenant"/>. Null when there is no such row, it names nobody, it is revoked, or it is bound
    /// to another tenant. Read by <see cref="Teams.TeamCallerOwnership.OwnerOf"/>, the one answer to "whose Director is
    /// this" (devthrottle_internal#2311, seam-director-key.md). Personally identifying: never logged.
    /// </summary>
    public string? AccountSubjectOfActiveDevice(string deviceId, TenantId tenant)
    {
        if (string.IsNullOrWhiteSpace(deviceId) || !tenant.IsValid)
            return null;
        using var ctx = _db.CreateUnscopedContext();
        var subject = ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId && d.TenantId == tenant.Value
                        && d.Status == StatusActive && d.RevokedAtUtc == null)
            .Select(d => d.AccountSubject)
            .FirstOrDefault();
        return string.IsNullOrWhiteSpace(subject) ? null : subject.Trim();
    }

    /// <summary>
    /// What a device row shows - machine name, platform and device type - or null when there is no such row. A
    /// Director that moves to another team keeps them on its new row (devthrottle_internal#2311).
    /// </summary>
    public DeviceDisplay? DisplayOfDevice(string deviceId)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return null;
        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.DeviceId == deviceId)
            .Select(d => new DeviceDisplay(d.MachineName, d.Platform, d.DeviceType))
            .FirstOrDefault();
    }

    public IReadOnlyList<ChildMirrorEntry> MirrorSnapshot()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.TenantId == TenantId.Local.Value)
            .Select(d => new ChildMirrorEntry(
                d.DeviceId,
                d.MachineName,
                d.Platform,
                d.DeviceType,
                d.CloudDeviceId))
            .ToList();
    }

    public IReadOnlyList<RegisteredDeviceDto> List()
    {
        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials
            .AsNoTracking()
            .OrderByDescending(d => d.IssuedAtUtc)
            .Select(d => ToDto(d))
            .ToList();
    }

    public IReadOnlyList<RegisteredDeviceDto> ListForTenant(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        using var ctx = _db.CreateUnscopedContext();
        return ctx.DeviceCredentials
            .AsNoTracking()
            .Where(d => d.TenantId == tenant.Value)
            .OrderByDescending(d => d.IssuedAtUtc)
            .Select(d => ToDto(d))
            .ToList();
    }

    /// <summary>
    /// Whether <paramref name="tenant"/> has a DIRECTOR's device on record - any device that is not a phone or a browser
    /// (devthrottle_internal#2306: where a fresh browser starts). A revoked device keeps its row and still counts; a
    /// device removed from "Your devices" loses its row (<see cref="RemoveForTenant"/>) and no longer does, so a person
    /// who removed their only Director reads as one who never had one. Phones and browsers enroll with their own types;
    /// every other writer records a Director as a workstation.
    /// </summary>
    public bool HasADirectorOnRecord(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        using var ctx = _db.CreateUnscopedContext();
        var found = ctx.DeviceCredentials
            .AsNoTracking()
            .Any(d => d.TenantId == tenant.Value
                      && d.DeviceType != Account.MobileDeviceEnrollmentService.PhoneDeviceType
                      && d.DeviceType != Account.MobileDeviceEnrollmentService.BrowserDeviceType);
        FileLog.Write($"[DeviceRegistry] HasADirectorOnRecord: tenant {tenant.ToLogString()} -> {found}");
        return found;
    }

    public int Count
    {
        get
        {
            using var ctx = _db.CreateUnscopedContext();
            return ctx.DeviceCredentials.Count();
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        if (_ownsDatabase)
            _db.Dispose();
    }

    private DeviceRegistrationResponse RegisterCore(
        string deviceId,
        string machineName,
        string? platform,
        string? deviceType,
        string tenantId,
        string? accountSubject,
        bool preserveActiveRecord)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));

        lock (_enrollLock)
        {
            Exception? lastFailure = null;
            for (var attempt = 1; attempt <= MaximumWriteAttempts; attempt++)
            {
                var key = GenerateDeviceKey();
                try
                {
                    using var ctx = _db.CreateUnscopedContext();
                    using var transaction = ctx.Database.BeginTransaction(IsolationLevel.Serializable);
                    var row = ctx.DeviceCredentials.SingleOrDefault(d => d.DeviceId == deviceId);

                    if (row is not null
                        && accountSubject is not null
                        && (!string.Equals(row.TenantId, tenantId, StringComparison.Ordinal)
                            || (!string.IsNullOrWhiteSpace(row.AccountSubject)
                                && !string.Equals(row.AccountSubject, accountSubject, StringComparison.Ordinal))))
                        throw new InvalidOperationException("The device identity is already owned by another tenant.");

                    var hash = HashKey(key);
                    if (ctx.DeviceCredentials.AsNoTracking()
                        .Any(d => d.DeviceKeyHash == hash && d.DeviceId != deviceId))
                        continue;

                    var preserve = row is not null
                        && preserveActiveRecord
                        && string.Equals(row.Status, StatusActive, StringComparison.Ordinal);

                    if (row is null)
                    {
                        row = new DeviceCredentialEntity { DeviceId = deviceId };
                        ctx.DeviceCredentials.Add(row);
                    }

                    row.DeviceKeyHash = hash;
                    row.KeyPrefix = MaskPrefix(key);
                    row.KeyLast4 = MaskLast4(key);
                    row.IssuedAtUtc = DateTime.UtcNow;
                    row.Status = StatusActive;
                    row.RevokedAtUtc = null;
                    row.RevokedReason = null;

                    if (!preserve)
                    {
                        row.MachineName = machineName ?? "";
                        row.Platform = NormalizePlatform(platform);
                        row.DeviceType = NormalizeDeviceType(deviceType);
                        row.CloudDeviceId = null;
                    }

                    if (accountSubject is not null)
                    {
                        row.AccountSubject = accountSubject;
                        row.TenantId = tenantId;
                    }
                    else if (!preserve)
                    {
                        row.AccountSubject = null;
                        row.TenantId = tenantId;
                    }

                    ctx.SaveChanges();
                    var count = ctx.DeviceCredentials.Count(d => d.TenantId == row.TenantId);
                    transaction.Commit();

                    FileLog.Write($"[DeviceRegistry] Register: device id={deviceId}, rotated={preserve}, deviceCount={count}");
                    return new DeviceRegistrationResponse
                    {
                        DeviceKey = key,
                        DeviceId = deviceId,
                        MachineName = row.MachineName,
                        Status = row.Status,
                        DeviceCount = count,
                    };
                }
                catch (Exception ex) when (IsRetryableWriteFailure(ex) && attempt < MaximumWriteAttempts)
                {
                    lastFailure = ex;
                }
            }

            FileLog.Write($"[DeviceRegistry] Register FAILED: authoritative write did not converge ({lastFailure?.GetType().Name ?? "unknown"})");
            throw new InvalidOperationException(
                "The authoritative device credential write could not be completed after concurrent updates.",
                lastFailure);
        }
    }

    private void InitializeAuthority()
    {
        // Set LAST, below, so a throw leaves this false and the readiness gate keeps refusing.
        var import = new DeviceRegistryImporter(_db, _storePath).Import();

        using (var ctx = _db.CreateUnscopedContext())
        {
            if (_isHosted)
            {
                var invalidIds = ctx.DeviceCredentials
                    .AsNoTracking()
                    .Where(d =>
                        d.TenantId == null
                        || d.TenantId == ""
                        || d.TenantId == TenantId.Local.Value
                        || d.TenantId == TenantId.System.Value
                        || d.AccountSubject == null
                        || d.AccountSubject == ""
                        || !ctx.Tenants.Any(t => t.Id == d.TenantId && t.AccountSubject == d.AccountSubject))
                    .Select(d => d.DeviceId)
                    .ToList();

                // A key bound to a TEAM's tenant fails the personal-account test above by design - a team's tenant
                // is no one person's row in the tenants table (devthrottle_internal#2311).
                // With Teams released it is kept while its person is still a member of that team in a role that may
                // run sessions there, and quarantined otherwise, exactly as a bad personal binding is - the same rule
                // ResolveCredential applies on every request.
                // With Teams NOT released it is left UNTOUCHED, never tombstoned: while dark it already resolves
                // revoked on every request (IsLiveTeamBinding is false), so nothing gets in, and switching Teams back
                // on restores it instead of leaving every member's Director to be set up again by hand.
                if (invalidIds.Count > 0)
                {
                    var bindings = ctx.DeviceCredentials
                        .AsNoTracking()
                        .Where(d => invalidIds.Contains(d.DeviceId))
                        .Select(d => new { d.DeviceId, d.TenantId, d.AccountSubject })
                        .ToList();
                    var keptForTeams = _teamsReleased
                        ? bindings
                            .Where(b => IsLiveTeamBinding(ctx, b.TenantId, b.AccountSubject))
                            .Select(b => b.DeviceId)
                            .ToHashSet(StringComparer.Ordinal)
                        : bindings
                            .Where(b => !string.IsNullOrWhiteSpace(b.TenantId) && !string.IsNullOrWhiteSpace(b.AccountSubject)
                                        && ctx.Teams.AsNoTracking().Any(t => t.Id == b.TenantId))
                            .Select(b => b.DeviceId)
                            .ToHashSet(StringComparer.Ordinal);
                    if (keptForTeams.Count > 0)
                    {
                        invalidIds = invalidIds.Where(id => !keptForTeams.Contains(id)).ToList();
                        FileLog.Write(_teamsReleased
                            ? $"[DeviceRegistry] InitializeAuthority: kept={keptForTeams.Count} hosted credential(s) bound to a team whose person may run sessions there"
                            : $"[DeviceRegistry] InitializeAuthority: left={keptForTeams.Count} hosted credential(s) bound to a team untouched (Teams not released)");
                    }
                }

                if (invalidIds.Count > 0)
                {
                    var now = DateTime.UtcNow;
                    ctx.DeviceCredentials
                        .Where(d => invalidIds.Contains(d.DeviceId))
                        .ExecuteUpdate(setters => setters
                            .SetProperty(d => d.Status, StatusRevoked)
                            .SetProperty(d => d.RevokedAtUtc, now)
                            .SetProperty(d => d.RevokedReason, "invalid_tenant_binding"));
                    FileLog.Write($"[DeviceRegistry] InitializeAuthority: quarantined={invalidIds.Count} hosted credential(s) with invalid tenant binding");
                }
            }
            else
            {
                ctx.DeviceCredentials
                    .Where(d => d.TenantId == null || d.TenantId == "")
                    .ExecuteUpdate(setters => setters.SetProperty(d => d.TenantId, TenantId.Local.Value));
            }
        }

        if (File.Exists(_storePath))
            ArchiveLegacyFile();

        FileLog.Write($"[DeviceRegistry] InitializeAuthority: database registry ready, imported={import.ImportedCount}, importSkipped={import.Skipped}");

        _initialized = true;
    }

    private void ArchiveLegacyFile()
    {
        var archivedPath = _storePath + ".migrated-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
        try
        {
            File.Move(_storePath, archivedPath);
            FileLog.Write("[DeviceRegistry] ArchiveLegacyFile: legacy registry archived after database commit");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[DeviceRegistry] ArchiveLegacyFile: rename deferred ({ex.GetType().Name}); the durable import marker prevents re-import");
        }
    }

    /// <summary>
    /// THE TEAM HALF OF A HOSTED KEY'S VALIDITY (devthrottle_internal#2311): a key bound to a team's tenant is valid
    /// while its person is a member of that team in a role the role table lets run sessions on their own computers.
    /// Removing the member, or making them a Collaborator, makes the key resolve revoked on the very next request.
    /// The role list is never written here - it is the role table's cell (<see cref="Teams.TeamPermissions"/>).
    /// Always false while Teams is not released, so a dark Gateway judges every key exactly as before.
    /// </summary>
    private bool IsLiveTeamBinding(GatewayDbContext ctx, string? tenant, string? accountSubject)
    {
        if (!_teamsReleased || string.IsNullOrWhiteSpace(tenant) || string.IsNullOrWhiteSpace(accountSubject))
            return false;
        var member = ctx.TeamMembers
            .AsNoTracking()
            .FirstOrDefault(m => m.TeamId == tenant && m.AccountSubject == accountSubject);
        return member is not null
            && Teams.TeamPermissions.Allows(member.Role, Teams.TeamAction.RunSessionsOnOwnComputers);
    }

    private static string ResolveStorePath(string? storePath)
        => string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.Config(), "director", "devices.json")
            : Path.GetFullPath(storePath);

    private static RegisteredDeviceDto ToDto(DeviceCredentialEntity row)
        => new()
        {
            DeviceId = row.DeviceId,
            MachineName = row.MachineName,
            IssuedAtUtc = row.IssuedAtUtc,
            Status = row.Status,
            KeyPrefix = row.KeyPrefix,
            KeyLast4 = row.KeyLast4,
        };

    private static string NormalizePlatform(string? platform)
        => string.IsNullOrWhiteSpace(platform) ? UnknownPlatform : platform.Trim();

    private static string NormalizeDeviceType(string? deviceType)
        => string.IsNullOrWhiteSpace(deviceType) ? DefaultDeviceType : deviceType.Trim();

    private static string GenerateDeviceKey()
    {
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private static string HashKey(string key)
        => HashBytesToHex(HashKeyBytes(key));

    private static string HashBytesToHex(byte[] hash)
        => Convert.ToHexString(hash).ToLowerInvariant();

    private static byte[] HashKeyBytes(string key)
        => SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(key));

    private static string MaskPrefix(string key)
        => key.Length <= KeyPrefixLength ? key : key[..KeyPrefixLength];

    private static string MaskLast4(string key)
        => key.Length < KeyLast4Length ? "" : key[^KeyLast4Length..];

    private static byte[]? DecodeHash(string hex)
    {
        try
        {
            return Convert.FromHexString(hex);
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static bool IsDatabaseFailure(Exception ex)
        => ex is DbException or InvalidOperationException or ObjectDisposedException;

    private static bool IsRetryableWriteFailure(Exception ex)
        => ex is DbUpdateException or DbUpdateConcurrencyException or DbException;
}

public enum DeviceCredentialResolutionKind
{
    Unknown,
    Active,
    Revoked,
    Unavailable,
}

/// <summary>
/// An authenticated device identity with no raw key and no stored key hash. <see cref="AccountSubject"/> is the
/// person the key was issued to (null for a self-host key that names nobody). It is personally identifying: never
/// log it, and never log this record whole. <see cref="IsTeamKey"/> is true when the key is valid only because its
/// person may run sessions in the team its tenant is (devthrottle_internal#2311) - such a key belongs to exactly one
/// Director, the one its row was enrolled for (<see cref="EnrolledDirectorId"/>).
/// </summary>
public sealed record DeviceCredentialIdentity(
    string DeviceId,
    string? TenantId,
    string DeviceType,
    string Status,
    string? AccountSubject = null,
    bool IsTeamKey = false)
{
    /// <summary>The Director id this key's row was enrolled for: the part of the registry id after its last
    /// <c>|</c> (a hosted row is <c>&lt;namespace&gt;|&lt;deviceId&gt;</c>). Null when the id carries no namespace.</summary>
    public string? EnrolledDirectorId
    {
        get
        {
            var bar = DeviceId.LastIndexOf('|');
            return bar < 0 || bar == DeviceId.Length - 1 ? null : DeviceId[(bar + 1)..];
        }
    }
}

/// <summary>The typed result of one authoritative credential lookup.</summary>
public readonly record struct DeviceCredentialResolution(
    DeviceCredentialResolutionKind Kind,
    DeviceCredentialIdentity? Identity)
{
    public static DeviceCredentialResolution Unknown { get; } =
        new(DeviceCredentialResolutionKind.Unknown, null);

    public static DeviceCredentialResolution Unavailable { get; } =
        new(DeviceCredentialResolutionKind.Unavailable, null);
}

/// <summary>What a device row shows about the device: its machine name, platform and device type.</summary>
public sealed record DeviceDisplay(string MachineName, string Platform, string DeviceType);

public sealed record ChildMirrorEntry(
    string DeviceId,
    string MachineName,
    string Platform,
    string DeviceType,
    string? CloudDeviceId);
