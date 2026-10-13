using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// License Switching contract 8.4: every finished <see cref="LicenseSignInResult"/>, kept by SignInId for
/// <see cref="LicenseContract.SignInResultRetention"/>, in memory and on disk beside the switch results, so a repeated
/// SignInId is answered from here - even after a Director restart - and no sign-in ever runs twice for one request.
///
/// One file per SignInId (<c>&lt;id&gt;.json</c>). The id is checked against
/// <see cref="LicenseContract.SignInIdShape"/> before it reaches this class, so it is safe as a file name.
/// </summary>
public sealed class LicenseSignInResultStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _folder;
    private readonly Func<DateTime> _utcNow;
    private readonly ConcurrentDictionary<string, LicenseSignInResult> _memory = new(StringComparer.Ordinal);

    public LicenseSignInResultStore(string folder, Func<DateTime> utcNow)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("A folder is required", nameof(folder));
        _folder = folder;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>The stored result for <paramref name="signInId"/> when one finished within the retention, else null.</summary>
    public LicenseSignInResult? Find(string signInId)
    {
        RequireShape(signInId);
        if (_memory.TryGetValue(signInId, out var cached))
            return IsCurrent(cached) ? cached : null;

        var path = PathFor(signInId);
        if (!File.Exists(path)) return null;
        var stored = JsonSerializer.Deserialize<LicenseSignInResult>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidOperationException($"the stored sign-in result {signInId} is empty");
        if (!IsCurrent(stored)) return null;
        _memory[signInId] = stored;
        return stored;
    }

    /// <summary>Stores a finished result. Written to a temporary file and moved into place, so a crash never leaves
    /// half a result that a restarted Director would then fail to read.</summary>
    public void Save(LicenseSignInResult result)
    {
        RequireShape(result.SignInId);
        Directory.CreateDirectory(_folder);
        var path = PathFor(result.SignInId);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(result, JsonOptions));
        File.Move(temp, path, overwrite: true);
        _memory[result.SignInId] = result;
        FileLog.Write($"[LicenseSignInResultStore] Save: signInId={result.SignInId}, outcome={result.Outcome}");
    }

    /// <summary>Deletes stored results older than the retention. Only files this store names - a SignInId-shaped
    /// name with the .json extension - are candidates; anything else in the folder is left alone.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_folder)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            var id = Path.GetFileNameWithoutExtension(file);
            if (!LicenseContract.SignInIdShape.IsMatch(id)) continue;
            var stored = JsonSerializer.Deserialize<LicenseSignInResult>(File.ReadAllText(file), JsonOptions);
            if (stored is not null && IsCurrent(stored)) continue;
            File.Delete(file);
            _memory.TryRemove(id, out _);
            removed++;
        }
        if (removed > 0) FileLog.Write($"[LicenseSignInResultStore] Prune: removed {removed} result(s) older than {LicenseContract.SignInResultRetention.TotalHours:0} hours");
        return removed;
    }

    private bool IsCurrent(LicenseSignInResult result) => _utcNow() - result.FinishedAtUtc <= LicenseContract.SignInResultRetention;

    private string PathFor(string signInId) => Path.Combine(_folder, signInId + ".json");

    private static void RequireShape(string signInId)
    {
        if (signInId is null || !LicenseContract.SignInIdShape.IsMatch(signInId))
            throw new ArgumentException("a SignInId must be 32 lower-case hexadecimal characters", nameof(signInId));
    }
}
