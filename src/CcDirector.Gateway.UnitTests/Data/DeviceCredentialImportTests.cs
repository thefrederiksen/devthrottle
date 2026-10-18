using System.Security.Cryptography;
using System.Text;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Pairing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Tests for the one-time <c>devices.json</c> -> <c>device_credentials</c> importer (MTR-14A), on SQLite. They
/// prove the import is LOSSLESS (every device row and every column survives, including the null variations and
/// the tenant binding), IDEMPOTENT (a second run finds the marker and does nothing - it can neither duplicate a
/// row nor pick up changes to the file), and that an absent/empty legacy registry is a valid state that still
/// marks itself done rather than being re-scanned forever.
/// </summary>
public sealed class DeviceCredentialImportTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    /// <summary>Read every device_credentials row back through the unscoped context (the table is global - no
    /// tenant query filter), newest issue first for a stable order.</summary>
    private static List<DeviceCredentialEntity> AllRows(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        return ctx.DeviceCredentials.AsNoTracking().OrderBy(d => d.DeviceId).ToList();
    }

    private static List<DeviceImportMarkerEntity> AllMarkers(GatewayDatabase db)
    {
        using var ctx = db.CreateUnscopedContext();
        return ctx.DeviceImportMarkers.AsNoTracking().ToList();
    }

    /// <summary>A realistic post-#1878 devices.json: three devices - a hosted-bound one, an unbound (local)
    /// one, and a mirrored one with a cloud id - exercising the nullable columns both null and populated.</summary>
    private const string ThreeDeviceJson = """
        [
          {
            "DeviceId": "dev-alpha",
            "MachineName": "ALPHA-PC",
            "DeviceKeyHash": "aaaa1111",
            "KeyPrefix": "AbCdEfGh",
            "KeyLast4": "WxYz",
            "IssuedAtUtc": "2026-07-20T09:00:00Z",
            "Status": "active",
            "Platform": "windows",
            "DeviceType": "workstation",
            "CloudDeviceId": null,
            "AccountSubject": "subject-1",
            "TenantId": "tenant-one"
          },
          {
            "DeviceId": "dev-beta",
            "MachineName": "BETA-PC",
            "DeviceKeyHash": "bbbb2222",
            "KeyPrefix": "11112222",
            "KeyLast4": "3333",
            "IssuedAtUtc": "2026-07-21T10:30:00Z",
            "Status": "active",
            "Platform": "unknown",
            "DeviceType": "workstation",
            "CloudDeviceId": null,
            "AccountSubject": null,
            "TenantId": null
          },
          {
            "DeviceId": "dev-gamma",
            "MachineName": "GAMMA-PHONE",
            "DeviceKeyHash": "cccc3333",
            "KeyPrefix": "ZzZzZzZz",
            "KeyLast4": "0000",
            "IssuedAtUtc": "2026-07-22T11:15:00Z",
            "Status": "active",
            "Platform": "android",
            "DeviceType": "phone",
            "CloudDeviceId": "cloud-xyz",
            "AccountSubject": "subject-2",
            "TenantId": "tenant-two"
          }
        ]
        """;

    [Fact]
    public void Import_PreservesEveryRowAndColumn()
    {
        using var db = _h.Open();
        var legacy = _h.LegacyPath("devices.json");
        File.WriteAllText(legacy, ThreeDeviceJson);

        var result = new DeviceRegistryImporter(db, legacy).Import();

        Assert.False(result.Skipped);
        Assert.Equal(3, result.ImportedCount);

        var rows = AllRows(db);
        Assert.Equal(3, rows.Count);

        // The hosted-bound device: every field, including the tenant binding, preserved.
        var alpha = rows.Single(r => r.DeviceId == "dev-alpha");
        Assert.Equal("ALPHA-PC", alpha.MachineName);
        Assert.Equal("aaaa1111", alpha.DeviceKeyHash);
        Assert.Equal("AbCdEfGh", alpha.KeyPrefix);
        Assert.Equal("WxYz", alpha.KeyLast4);
        Assert.Equal(new DateTime(2026, 7, 20, 9, 0, 0, DateTimeKind.Utc), alpha.IssuedAtUtc);
        Assert.Equal(DateTimeKind.Utc, alpha.IssuedAtUtc.Kind);
        Assert.Equal("active", alpha.Status);
        Assert.Equal("windows", alpha.Platform);
        Assert.Equal("workstation", alpha.DeviceType);
        Assert.Null(alpha.CloudDeviceId);
        Assert.Equal("subject-1", alpha.AccountSubject);
        Assert.Equal("tenant-one", alpha.TenantId);
        Assert.Null(alpha.RevokedAtUtc);
        Assert.Null(alpha.RevokedReason);

        // The unbound (local) device: no account binding survives as null, not as a default or "local".
        var beta = rows.Single(r => r.DeviceId == "dev-beta");
        Assert.Null(beta.AccountSubject);
        Assert.Null(beta.TenantId);
        Assert.Null(beta.CloudDeviceId);

        // The mirrored phone: the cloud id and its distinct tenant binding both survive.
        var gamma = rows.Single(r => r.DeviceId == "dev-gamma");
        Assert.Equal("phone", gamma.DeviceType);
        Assert.Equal("cloud-xyz", gamma.CloudDeviceId);
        Assert.Equal("subject-2", gamma.AccountSubject);
        Assert.Equal("tenant-two", gamma.TenantId);

        // The marker records the exact source and count - and it is the same transaction the rows landed in.
        var markers = AllMarkers(db);
        var marker = Assert.Single(markers);
        Assert.Equal(legacy, marker.SourcePath);
        Assert.Equal(3, marker.ImportedCount);
    }

    [Fact]
    public void Reimport_IsIdempotent_FindsMarker_AndImportsNothingNew()
    {
        using var db = _h.Open();
        var legacy = _h.LegacyPath("devices.json");
        File.WriteAllText(legacy, ThreeDeviceJson);

        var first = new DeviceRegistryImporter(db, legacy).Import();
        Assert.False(first.Skipped);
        Assert.Equal(3, first.ImportedCount);

        // The file GROWS after the first import (a device enrolled the old way in between). A second import must
        // still be a no-op: the marker gates it, so the new device is NOT picked up and no row is duplicated.
        File.WriteAllText(legacy, """
            [
              { "DeviceId": "dev-alpha", "MachineName": "ALPHA-PC", "DeviceKeyHash": "aaaa1111", "KeyPrefix": "AbCdEfGh", "KeyLast4": "WxYz", "IssuedAtUtc": "2026-07-20T09:00:00Z", "Status": "active", "Platform": "windows", "DeviceType": "workstation" },
              { "DeviceId": "dev-beta",  "MachineName": "BETA-PC",  "DeviceKeyHash": "bbbb2222", "KeyPrefix": "11112222", "KeyLast4": "3333", "IssuedAtUtc": "2026-07-21T10:30:00Z", "Status": "active", "Platform": "unknown", "DeviceType": "workstation" },
              { "DeviceId": "dev-gamma", "MachineName": "GAMMA-PHONE", "DeviceKeyHash": "cccc3333", "KeyPrefix": "ZzZzZzZz", "KeyLast4": "0000", "IssuedAtUtc": "2026-07-22T11:15:00Z", "Status": "active", "Platform": "android", "DeviceType": "phone", "CloudDeviceId": "cloud-xyz" },
              { "DeviceId": "dev-delta", "MachineName": "DELTA-PC", "DeviceKeyHash": "dddd4444", "KeyPrefix": "44445555", "KeyLast4": "6666", "IssuedAtUtc": "2026-07-22T12:00:00Z", "Status": "active", "Platform": "windows", "DeviceType": "workstation" }
            ]
            """);

        var second = new DeviceRegistryImporter(db, legacy).Import();
        Assert.True(second.Skipped);
        Assert.Equal(0, second.ImportedCount);

        // Still exactly the original three, and still exactly one marker.
        var rows = AllRows(db);
        Assert.Equal(3, rows.Count);
        Assert.DoesNotContain(rows, r => r.DeviceId == "dev-delta");
        Assert.Single(AllMarkers(db));
    }

    [Fact]
    public void AbsentFile_ImportsZero_MarksDone_AndSecondRunSkips()
    {
        var legacy = _h.LegacyPath("does-not-exist.json");
        Assert.False(File.Exists(legacy));

        using var db = _h.Open();
        var first = new DeviceRegistryImporter(db, legacy).Import();

        // An absent legacy registry (a Gateway that never enrolled a device) imports zero rows but still marks
        // itself done, so it is not re-scanned on every boot.
        Assert.False(first.Skipped);
        Assert.Equal(0, first.ImportedCount);
        Assert.Empty(AllRows(db));
        var marker = Assert.Single(AllMarkers(db));
        Assert.Equal(0, marker.ImportedCount);

        // And the marker makes the next run a no-op.
        var second = new DeviceRegistryImporter(db, legacy).Import();
        Assert.True(second.Skipped);
    }

    [Fact]
    public void Import_AnotherImporterFinishesBetweenTheCheckAndTheInsert_IsANoOp_NotAUniqueFailure()
    {
        // Main, 18 October 2026: two importers on one database both found no marker, and the second failed on the
        // marker's unique SourcePath. Put that race in order: the second importer finishes inside the first one's
        // window between its marker check and its own transaction.
        using var db = _h.Open();
        var legacy = _h.LegacyPath("devices.json");
        File.WriteAllText(legacy, ThreeDeviceJson);
        DeviceImportResult? winner = null;
        var loser = new DeviceRegistryImporter(db, legacy)
        {
            AfterMarkerCheck = () => winner = new DeviceRegistryImporter(db, legacy).Import(),
        };

        var result = loser.Import();

        Assert.NotNull(winner);
        Assert.False(winner.Value.Skipped);
        Assert.Equal(3, winner.Value.ImportedCount);
        Assert.True(result.Skipped);
        Assert.Equal(0, result.ImportedCount);
        // The file moved exactly once: the winner's rows and one marker, nothing doubled and nothing lost.
        Assert.Equal(new[] { "dev-alpha", "dev-beta", "dev-gamma" }, AllRows(db).Select(r => r.DeviceId).ToArray());
        Assert.Single(AllMarkers(db));
    }

    [Fact]
    public void Import_AnInsertThatFailsWithNoMarkerWritten_IsThrown_NotTakenForAFinishedImport()
    {
        // The race is recognised only by the marker being there afterwards. A failure for any other reason - here a
        // device row that already exists - leaves no marker, and must surface rather than read as "already done".
        using var db = _h.Open();
        var legacy = _h.LegacyPath("devices.json");
        File.WriteAllText(legacy, ThreeDeviceJson);
        var importer = new DeviceRegistryImporter(db, legacy)
        {
            AfterMarkerCheck = () =>
            {
                using var ctx = db.CreateUnscopedContext();
                ctx.DeviceCredentials.Add(new DeviceCredentialEntity
                {
                    DeviceId = "dev-alpha",
                    MachineName = "SOMEONE-ELSE",
                    DeviceKeyHash = "ffff0000",
                    IssuedAtUtc = new DateTime(2026, 10, 18, 9, 0, 0, DateTimeKind.Utc),
                    Status = "active",
                });
                ctx.SaveChanges();
            },
        };

        Assert.Throws<DbUpdateException>(() => importer.Import());
        Assert.Empty(AllMarkers(db));
    }

    [Fact]
    public void LegacyPlaintextOnlyRecord_IsHashed_Losslessly_AndPlaintextNeverStored()
    {
        // A record from BEFORE issue #1878: only the plaintext key, no hash and no masked identity. The importer
        // must hash it with the same transform DeviceRegistry uses so the device is not dropped, and the
        // plaintext must never reach the table (there is no column for it).
        const string plaintextKey = "abcdefghij0123456789KLMNOPQRSTUVWXYZ-_wxyz";
        using var db = _h.Open();
        var legacy = _h.LegacyPath("legacy-plaintext.json");
        File.WriteAllText(legacy, $$"""
            [
              { "DeviceId": "dev-legacy", "MachineName": "OLD-PC", "DeviceKey": "{{plaintextKey}}", "IssuedAtUtc": "2026-07-01T00:00:00Z", "Status": "active" }
            ]
            """);

        var result = new DeviceRegistryImporter(db, legacy).Import();
        Assert.Equal(1, result.ImportedCount);

        var row = Assert.Single(AllRows(db));
        Assert.Equal("dev-legacy", row.DeviceId);
        // Hashed with the identical SHA-256-hex transform, so the key the device still holds keeps working.
        Assert.Equal(Sha256Hex(plaintextKey), row.DeviceKeyHash);
        // The masked identity was recomputed from the plaintext while it was in hand.
        Assert.Equal(plaintextKey.Substring(0, 8), row.KeyPrefix);
        Assert.Equal(plaintextKey.Substring(plaintextKey.Length - 4), row.KeyLast4);
        // Defaults filled where the legacy record omitted them.
        Assert.Equal("unknown", row.Platform);
        Assert.Equal("workstation", row.DeviceType);
    }
}
