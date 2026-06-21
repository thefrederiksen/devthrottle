using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Pairing;

/// <summary>
/// The Gateway-side registry of enrolled devices and their unique per-device keys (issue #469).
/// This is the single issuer and record of credentials in the per-device-key trust model: each
/// machine that completes pairing gets ONE distinct, individually-revocable key, recorded here
/// alongside its name, machine, issued-at, and status.
///
/// Persisted to <c>%LOCALAPPDATA%\cc-director\config\director\devices.json</c> so the registry
/// survives a Gateway restart (a per-device key must keep working across restarts, unlike the
/// transient pairing code). The file holds the issued keys, so it is the Gateway host's secret
/// store - locked to the current user by living under the per-user config root.
///
/// Thread-safe: registration happens on request threads while the host window lists devices.
/// </summary>
public sealed class DeviceRegistry
{
    /// <summary>The status of an actively-enrolled device.</summary>
    public const string StatusActive = "active";

    private readonly string _storePath;
    private readonly object _saveLock = new();
    private readonly ConcurrentDictionary<string, DeviceRecord> _byDeviceId =
        new(StringComparer.Ordinal);

    public DeviceRegistry() : this(null) { }

    /// <param name="storePath">Override the registry file (tests pass an isolated temp path);
    /// production omits it for the shared default under the config root.</param>
    public DeviceRegistry(string? storePath)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.Config(), "director", "devices.json")
            : storePath;
        Load();
    }

    /// <summary>The on-disk registry file path.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Enroll a device: generate a unique per-device key, record the device, and return the issued
    /// key. A repeat enrollment of the SAME device id re-issues a fresh key (a re-pairing rotates
    /// the device's own key) and keeps one entry. Each call produces a distinct key.
    /// </summary>
    public DeviceRegistrationResponse Register(string deviceId, string machineName)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            throw new ArgumentException("deviceId is required", nameof(deviceId));

        var key = GenerateDeviceKey();
        var record = new DeviceRecord
        {
            DeviceId = deviceId,
            MachineName = machineName ?? "",
            DeviceKey = key,
            IssuedAtUtc = DateTime.UtcNow,
            Status = StatusActive,
        };
        _byDeviceId[deviceId] = record;
        Save();
        FileLog.Write($"[DeviceRegistry] Registered device id={deviceId}, machine={machineName}, total={_byDeviceId.Count}");
        return new DeviceRegistrationResponse
        {
            DeviceKey = key,
            DeviceId = deviceId,
            MachineName = record.MachineName,
            Status = record.Status,
            DeviceCount = _byDeviceId.Count,
        };
    }

    /// <summary>
    /// True when the supplied key matches an active device's per-device key. The lookup is over
    /// all active keys with a constant-time compare so a near-miss reveals nothing through timing.
    /// </summary>
    public bool IsValidDeviceKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        var supplied = System.Text.Encoding.ASCII.GetBytes(key);
        foreach (var record in _byDeviceId.Values)
        {
            if (!string.Equals(record.Status, StatusActive, StringComparison.Ordinal)) continue;
            var stored = System.Text.Encoding.ASCII.GetBytes(record.DeviceKey);
            if (stored.Length == supplied.Length &&
                CryptographicOperations.FixedTimeEquals(stored, supplied))
                return true;
        }
        return false;
    }

    /// <summary>The host-readable list of registered devices, newest first. Keys are never included.</summary>
    public IReadOnlyList<RegisteredDeviceDto> List()
    {
        return _byDeviceId.Values
            .OrderByDescending(r => r.IssuedAtUtc)
            .Select(r => new RegisteredDeviceDto
            {
                DeviceId = r.DeviceId,
                MachineName = r.MachineName,
                IssuedAtUtc = r.IssuedAtUtc,
                Status = r.Status,
            })
            .ToList();
    }

    /// <summary>The number of registered devices.</summary>
    public int Count => _byDeviceId.Count;

    private static string GenerateDeviceKey()
    {
        // Same 32-byte URL-safe-base64 shape the machine token uses (issue #469 Assumption 3),
        // but unique per device rather than shared.
        var bytes = new byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-')
            .Replace('/', '_')
            .TrimEnd('=');
    }

    private void Load()
    {
        if (!File.Exists(_storePath)) return;
        var json = File.ReadAllText(_storePath);
        if (string.IsNullOrWhiteSpace(json)) return;

        var records = JsonSerializer.Deserialize<List<DeviceRecord>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });
        if (records is null) return;
        foreach (var record in records)
        {
            if (string.IsNullOrEmpty(record.DeviceId)) continue;
            _byDeviceId[record.DeviceId] = record;
        }
        FileLog.Write($"[DeviceRegistry] Loaded {_byDeviceId.Count} device(s) from {_storePath}");
    }

    private void Save()
    {
        lock (_saveLock)
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            var json = JsonSerializer.Serialize(_byDeviceId.Values.ToList(), new JsonSerializerOptions
            {
                WriteIndented = true,
            });
            File.WriteAllText(_storePath, json);
        }
    }

    /// <summary>One device's full record, including the issued key (persisted, never listed).</summary>
    private sealed class DeviceRecord
    {
        public string DeviceId { get; set; } = "";
        public string MachineName { get; set; } = "";
        public string DeviceKey { get; set; } = "";
        public DateTime IssuedAtUtc { get; set; }
        public string Status { get; set; } = StatusActive;
    }
}
