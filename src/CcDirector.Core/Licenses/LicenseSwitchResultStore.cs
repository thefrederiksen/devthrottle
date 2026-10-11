using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 3.6: every finished <see cref="LicenseSwitchResult"/>, kept by SwitchId for
/// <see cref="LicenseContract.SwitchResultRetention"/>, in memory and on disk in the key store's folder, so a
/// repeated SwitchId is answered from here - even after a Director restart - and no switch ever runs twice.
///
/// One file per SwitchId (<c>&lt;id&gt;.json</c>). The id is checked against
/// <see cref="LicenseContract.SwitchIdShape"/> before it reaches this class, so it is safe as a file name.
/// </summary>
public sealed class LicenseSwitchResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _folder;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, LicenseSwitchResult> _memory = new(StringComparer.Ordinal);

    public LicenseSwitchResultStore(string folder, Func<DateTime> utcNow)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("A folder is required", nameof(folder));
        _folder = folder;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>The stored result for <paramref name="switchId"/> when one finished within the retention, else null.</summary>
    public LicenseSwitchResult? Find(string switchId)
    {
        RequireShape(switchId);
        if (_memory.TryGetValue(switchId, out var cached))
            return IsCurrent(cached) ? cached : null;

        var path = PathFor(switchId);
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<LicenseSwitchResult>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"the stored switch result {switchId} is empty");
        if (!IsCurrent(stored)) return null;
        _memory[switchId] = stored;
        return stored;
    }

    /// <summary>Stores a finished result. Written to a temporary file and moved into place, so a crash never leaves
    /// half a result that a restarted Director would then fail to read.</summary>
    public void Save(LicenseSwitchResult result)
    {
        RequireShape(result.SwitchId);
        Directory.CreateDirectory(_folder);
        var path = PathFor(result.SwitchId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(temp, path, overwrite: true);
        _memory[result.SwitchId] = result;
        FileLog.Write($"[LicenseSwitchResultStore] Save: switchId={result.SwitchId}, outcome={result.Outcome}");
    }

    /// <summary>Deletes stored results older than the retention. Only files this store names - a SwitchId-shaped
    /// name with the .json extension - are candidates; anything else in the folder is left alone.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_folder)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!LicenseContract.SwitchIdShape.IsMatch(id)) continue;
            var stored = JsonSerializer.Deserialize<LicenseSwitchResult>(File.ReadAllText(file), JsonOptions);
            if (stored is not null && IsCurrent(stored)) continue;
            File.Delete(file);
            _memory.TryRemove(id, out _);
            removed++;
        }
        if (removed > 0) FileLog.Write($"[LicenseSwitchResultStore] Prune: removed {removed} result(s) older than {LicenseContract.SwitchResultRetention.TotalHours:0} hours");
        return removed;
    }

    private bool IsCurrent(LicenseSwitchResult result) => _utcNow() - result.FinishedAtUtc <= LicenseContract.SwitchResultRetention;

    private string PathFor(string switchId) => Path.Combine(_folder, switchId + ".json");

    private static void RequireShape(string switchId)
    {
        if (switchId is null || !LicenseContract.SwitchIdShape.IsMatch(switchId))
            throw new ArgumentException("a SwitchId must be 32 lower-case hexadecimal characters", nameof(switchId));
    }
}
