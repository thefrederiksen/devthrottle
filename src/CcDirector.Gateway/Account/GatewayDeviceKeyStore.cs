using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Account;

/// <summary>
/// Stores the cloud-issued per-device key this Gateway received when it registered itself as a device
/// (issue #857). The key is written under the SAME per-user config root the local pairing device key
/// (issue #469) uses, so it is locked to the current user by its location:
///     %LOCALAPPDATA%\cc-director\config\director\account-device-key.json
///
/// The record also records the install id the key was issued for. The registration coordinator reads
/// that back to recognize "this install already has a key" on a relaunch and skip a redundant
/// re-registration (the idempotency guard, issue #857) - and to discard a stale key if the cloud later
/// reports it does not know this install (a 404 heartbeat).
///
/// Security rule DT-05: the raw key lives in this file but is NEVER written to the log on any path - this
/// type logs only the install id and the file path, never the key value.
/// </summary>
public sealed class GatewayDeviceKeyStore
{
    private readonly string _storePath;
    private readonly object _gate = new();

    /// <summary>Creates the store at the default path under the config root.</summary>
    public GatewayDeviceKeyStore() : this(null) { }

    /// <param name="storePath">Override the store file (tests pass an isolated temp path); production
    /// omits it for the shared default under the config root.</param>
    public GatewayDeviceKeyStore(string? storePath)
    {
        _storePath = string.IsNullOrWhiteSpace(storePath)
            ? Path.Combine(CcStorage.Config(), "director", "account-device-key.json")
            : storePath;
    }

    /// <summary>The on-disk store file path.</summary>
    public string StorePath => _storePath;

    /// <summary>
    /// Saves the per-device key issued for <paramref name="installId"/>, overwriting any prior record
    /// (a re-registration rotates the key). The key value is never logged.
    /// </summary>
    public void Save(string installId, string deviceKey)
    {
        if (string.IsNullOrWhiteSpace(installId))
            throw new ArgumentException("Install id is required", nameof(installId));
        if (string.IsNullOrWhiteSpace(deviceKey))
            throw new ArgumentException("Device key is required", nameof(deviceKey));

        lock (_gate)
        {
            var dir = Path.GetDirectoryName(_storePath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var record = new StoredDeviceKey { InstallId = installId, DeviceKey = deviceKey, StoredAtUtc = DateTime.UtcNow };
            var json = JsonSerializer.Serialize(record, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_storePath, json);
        }
        FileLog.Write($"[GatewayDeviceKeyStore] Save: stored per-device key for install_id={installId} at {_storePath} (key value not logged)");
    }

    /// <summary>
    /// Returns the stored per-device key when one is present for <paramref name="installId"/>, otherwise
    /// null (no key, or a key stored for a different install id). The key value is never logged.
    /// </summary>
    public string? GetKeyForInstall(string installId)
    {
        if (string.IsNullOrWhiteSpace(installId))
            throw new ArgumentException("Install id is required", nameof(installId));

        var record = Load();
        if (record is null || !string.Equals(record.InstallId, installId, StringComparison.Ordinal) || string.IsNullOrWhiteSpace(record.DeviceKey))
            return null;
        return record.DeviceKey;
    }

    /// <summary>True when a per-device key is stored for <paramref name="installId"/>.</summary>
    public bool HasKeyForInstall(string installId) => GetKeyForInstall(installId) is not null;

    /// <summary>
    /// Removes the stored key (so the next registration re-issues one). Used when the cloud reports it no
    /// longer knows this install (a 404 heartbeat). A no-op when no file is present.
    /// </summary>
    public void Clear()
    {
        lock (_gate)
        {
            if (File.Exists(_storePath))
                File.Delete(_storePath);
        }
        FileLog.Write($"[GatewayDeviceKeyStore] Clear: removed stored per-device key at {_storePath}");
    }

    private StoredDeviceKey? Load()
    {
        lock (_gate)
        {
            if (!File.Exists(_storePath))
                return null;
            var json = File.ReadAllText(_storePath);
            if (string.IsNullOrWhiteSpace(json))
                return null;
            return JsonSerializer.Deserialize<StoredDeviceKey>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        }
    }

    /// <summary>The persisted record: the install id, its issued key, and when it was stored.</summary>
    private sealed class StoredDeviceKey
    {
        public string InstallId { get; set; } = "";
        public string DeviceKey { get; set; } = "";
        public DateTime StoredAtUtc { get; set; }
    }
}
