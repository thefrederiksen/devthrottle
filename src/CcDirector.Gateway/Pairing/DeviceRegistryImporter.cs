using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// The one-time, idempotent migration of the legacy <c>devices.json</c> registry into the durable
/// <c>device_credentials</c> table (MTR-14A). It reads the JSON file the <see cref="DeviceRegistry"/> writes,
/// inserts one row per device, and records a <see cref="DeviceImportMarkerEntity"/> - all inside a SINGLE
/// database transaction, so either the whole import commits or nothing does. The marker is what makes a second
/// run a no-op: the JSON file is never re-imported, so a restart, redeploy, or retry can neither duplicate a
/// row nor resurrect a device that was revoked after the import.
///
/// SCOPE (MTR-14A). This is purely additive. It does NOT change how <see cref="DeviceRegistry"/> reads or
/// writes at runtime - that cutover is MTR-14B - and it is deliberately NOT wired into Gateway startup here;
/// MTR-14B decides when it runs, after the runtime is ready to read from the table. In MTR-14A it exists as a
/// tested component: it populates the new schema from the old file, losslessly and once.
///
/// It writes through <see cref="GatewayDatabase.CreateUnscopedContext"/> because <c>device_credentials</c> is a
/// GLOBAL table (no tenant query filter) that spans every tenant's devices, exactly like the <c>tenants</c>
/// mapping the <see cref="Tenancy.TenantRegistry"/> owns. Each imported row carries its OWN tenant binding, so a
/// key still only ever resolves to its own tenant.
/// </summary>
public sealed class DeviceRegistryImporter
{
    /// <summary>How many leading characters of a key make up its non-secret masked prefix (issue #1899),
    /// mirroring <see cref="DeviceRegistry"/>. Used only to recompute a mask for a legacy plaintext-only
    /// record that predates #1899; a normal post-#1878 file already carries the mask and this is not read.</summary>
    private const int KeyPrefixLength = 8;

    /// <summary>How many trailing characters of a key make up its non-secret masked suffix (issue #1899).</summary>
    private const int KeyLast4Length = 4;

    private readonly GatewayDatabase _db;
    private readonly string _storePath;

    /// <param name="db">The Gateway EF database. The importer reads and writes the global
    /// <c>device_credentials</c> and <c>device_import_markers</c> tables through its unscoped context.</param>
    /// <param name="storePath">Override the legacy registry file (tests pass an isolated temp path); production
    /// omits it for the same shared default the <see cref="DeviceRegistry"/> uses.</param>
    public DeviceRegistryImporter(GatewayDatabase db, string? storePath = null)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? DeviceRegistry.StorePathUnder(CcStorage.Root())
            : storePath;
    }

    /// <summary>The devices.json path this importer migrates from - the marker's identity.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Run the one-time import. Returns a result describing what happened: whether it was skipped because a
    /// marker already recorded a prior import, and how many rows were written. Idempotent - a second call with
    /// the same source path finds the marker and does nothing.
    ///
    /// Transactional: the device rows and the marker are inserted in one transaction, so a marker exists only
    /// when every row committed. There is no partial import to recover from.
    /// </summary>
    public DeviceImportResult Import()
    {
        using var ctx = _db.CreateUnscopedContext();

        // Already imported? The marker's presence is the whole idempotency signal - if it is there, the file was
        // migrated in full (the marker and the rows commit together), so there is nothing to do.
        if (ctx.DeviceImportMarkers.AsNoTracking().Any(m => m.SourcePath == _storePath))
        {
            FileLog.Write($"[DeviceRegistryImporter] Import: already done for {_storePath} (marker present) -> no-op");
            return new DeviceImportResult(Skipped: true, ImportedCount: 0);
        }

        var parsed = ReadLegacyFile();

        using var tx = ctx.Database.BeginTransaction();

        foreach (var record in parsed)
            ctx.DeviceCredentials.Add(ToEntity(record));

        ctx.DeviceImportMarkers.Add(new DeviceImportMarkerEntity
        {
            SourcePath = _storePath,
            ImportedAtUtc = DateTime.UtcNow,
            ImportedCount = parsed.Count,
        });

        ctx.SaveChanges();
        tx.Commit();

        FileLog.Write($"[DeviceRegistryImporter] Import: migrated {parsed.Count} device(s) from {_storePath} into device_credentials (marker written)");
        return new DeviceImportResult(Skipped: false, ImportedCount: parsed.Count);
    }

    /// <summary>
    /// Read and parse the legacy devices.json into records. An absent or empty file is a valid, ordinary state
    /// (a Gateway that never enrolled a device): it yields zero records, and the import still runs and is still
    /// marked done so the empty legacy registry is not re-scanned forever.
    /// </summary>
    private List<ParsedDeviceRecord> ReadLegacyFile()
    {
        if (!File.Exists(_storePath))
        {
            FileLog.Write($"[DeviceRegistryImporter] ReadLegacyFile: no file at {_storePath} - importing zero devices");
            return new List<ParsedDeviceRecord>();
        }

        var json = File.ReadAllText(_storePath);
        if (string.IsNullOrWhiteSpace(json))
            return new List<ParsedDeviceRecord>();

        var records = JsonSerializer.Deserialize<List<ParsedDeviceRecord>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (records is null)
            return new List<ParsedDeviceRecord>();

        // Skip any record with no device id (a corrupt/blank entry) exactly as DeviceRegistry.Load does - it is
        // not a device and has no natural key. And de-duplicate by device id keeping the LAST occurrence, which
        // mirrors Load's `_byDeviceId[id] = record` last-writer-wins: a hand-edited file with a duplicate id
        // must import as the registry itself would read it, not fail on a primary-key violation.
        var byId = new Dictionary<string, ParsedDeviceRecord>(StringComparer.Ordinal);
        foreach (var r in records)
        {
            if (string.IsNullOrEmpty(r.DeviceId)) continue;
            byId[r.DeviceId] = r;
        }
        return byId.Values.ToList();
    }

    /// <summary>
    /// Map one parsed legacy record to a <see cref="DeviceCredentialEntity"/>, preserving every field. A normal
    /// post-#1878 file already carries the key HASH and the masked identity, which are copied verbatim. A legacy
    /// record that still holds only the plaintext key (written before #1878, and normally already migrated in
    /// place by <see cref="DeviceRegistry.Load"/> before this importer ever sees the file) is hashed here with
    /// the SAME transform so no device is dropped - the plaintext is used only to derive the hash and mask and
    /// is never persisted to the table.
    /// </summary>
    private static DeviceCredentialEntity ToEntity(ParsedDeviceRecord r)
    {
        var hash = r.DeviceKeyHash ?? "";
        var prefix = r.KeyPrefix ?? "";
        var last4 = r.KeyLast4 ?? "";

        if (string.IsNullOrEmpty(hash) && !string.IsNullOrEmpty(r.DeviceKey))
        {
            hash = HashKey(r.DeviceKey);
            if (string.IsNullOrEmpty(prefix)) prefix = MaskPrefix(r.DeviceKey);
            if (string.IsNullOrEmpty(last4)) last4 = MaskLast4(r.DeviceKey);
        }

        return new DeviceCredentialEntity
        {
            DeviceId = r.DeviceId,
            MachineName = r.MachineName ?? "",
            DeviceKeyHash = hash,
            KeyPrefix = prefix,
            KeyLast4 = last4,
            IssuedAtUtc = r.IssuedAtUtc,
            Status = string.IsNullOrEmpty(r.Status) ? DeviceRegistry.StatusActive : r.Status,
            Platform = string.IsNullOrEmpty(r.Platform) ? DeviceRegistry.UnknownPlatform : r.Platform,
            DeviceType = string.IsNullOrEmpty(r.DeviceType) ? DeviceRegistry.DefaultDeviceType : r.DeviceType,
            CloudDeviceId = r.CloudDeviceId,
            AccountSubject = r.AccountSubject,
            TenantId = r.TenantId,
            // The legacy JSON registry has no revocation columns (a revoked device was removed outright); every
            // imported row is therefore un-revoked. The MTR-15 tombstone lifecycle populates these later.
            RevokedAtUtc = null,
            RevokedReason = null,
        };
    }

    /// <summary>The stored form of a device key: the lower-case hexadecimal SHA-256 (issue #1878), the identical
    /// transform <see cref="DeviceRegistry"/> uses. Duplicated here rather than shared so MTR-14A does not touch
    /// DeviceRegistry; MTR-14B consolidates when it moves the runtime onto this table.</summary>
    private static string HashKey(string key) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();

    private static string MaskPrefix(string key) =>
        key.Length <= KeyPrefixLength ? key : key.Substring(0, KeyPrefixLength);

    private static string MaskLast4(string key) =>
        key.Length < KeyLast4Length ? "" : key.Substring(key.Length - KeyLast4Length);

    /// <summary>The subset of a legacy devices.json record this import reads. Mirrors the private
    /// <c>DeviceRegistry.DeviceRecord</c> JSON shape, including the pre-#1878 plaintext <see cref="DeviceKey"/>
    /// so a not-yet-migrated file still imports losslessly.</summary>
    private sealed class ParsedDeviceRecord
    {
        public string DeviceId { get; set; } = "";
        public string? MachineName { get; set; }
        public string? DeviceKey { get; set; }
        public string? DeviceKeyHash { get; set; }
        public string? KeyPrefix { get; set; }
        public string? KeyLast4 { get; set; }
        public DateTime IssuedAtUtc { get; set; }
        public string? Status { get; set; }
        public string? Platform { get; set; }
        public string? DeviceType { get; set; }
        public string? CloudDeviceId { get; set; }
        public string? AccountSubject { get; set; }
        public string? TenantId { get; set; }
    }
}

/// <summary>The outcome of a <see cref="DeviceRegistryImporter.Import"/> run.</summary>
/// <param name="Skipped">True when a marker already recorded a prior import, so this run did nothing.</param>
/// <param name="ImportedCount">The number of device rows written (0 when skipped or when the source was empty).</param>
public readonly record struct DeviceImportResult(bool Skipped, int ImportedCount);
