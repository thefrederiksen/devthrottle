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
    public DeviceRegistry(GatewayDatabase db, string? storePath = null, bool isHosted = false,
        bool deferInitialize = false)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storePath = ResolveStorePath(storePath);
        _isHosted = isHosted;
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

            var tenant = string.IsNullOrWhiteSpace(row.TenantId) ? null : row.TenantId;
            var invalidHostedBinding = _isHosted
                && (tenant is null
                    || string.Equals(tenant, TenantId.Local.Value, StringComparison.Ordinal)
                    || string.Equals(tenant, TenantId.System.Value, StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(row.AccountSubject)
                    || !ctx.Tenants.AsNoTracking().Any(t =>
                        t.Id == tenant && t.AccountSubject == row.AccountSubject));

            var identity = new DeviceCredentialIdentity(
                row.DeviceId,
                tenant,
                string.IsNullOrWhiteSpace(row.DeviceType) ? DefaultDeviceType : row.DeviceType,
                row.Status);

            if (invalidHostedBinding
                || !string.Equals(row.Status, StatusActive, StringComparison.Ordinal)
                || row.RevokedAtUtc is not null)
                return new DeviceCredentialResolution(DeviceCredentialResolutionKind.Revoked, identity);

            return new DeviceCredentialResolution(DeviceCredentialResolutionKind.Active, identity);
        }
        catch (Exception ex) when (IsDatabaseFailure(ex))
        {
            FileLog.Write($"[DeviceRegistry] ResolveCredential FAILED: authoritative database unavailable ({ex.GetType().Name})");
            return DeviceCredentialResolution.Unavailable;
        }
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

/// <summary>An authenticated device identity with no raw key and no stored key hash.</summary>
public sealed record DeviceCredentialIdentity(
    string DeviceId,
    string? TenantId,
    string DeviceType,
    string Status);

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

public sealed record ChildMirrorEntry(
    string DeviceId,
    string MachineName,
    string Platform,
    string DeviceType,
    string? CloudDeviceId);
