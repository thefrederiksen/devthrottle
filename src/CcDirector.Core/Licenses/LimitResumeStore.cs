using System.Text.Json;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Licenses;

/// <summary>One <c>continue</c> sent to a session that had stopped on a usage-limit reply.</summary>
public sealed record LimitResume(string RecordUuid, Guid SessionId, string SwitchId, DateTime SentAtUtc);

/// <summary>
/// The Loud When Stranded mission, step 6: every usage-limit reply a <c>continue</c> has been sent for, kept on disk
/// so ONE limit reply is answered ONCE - not again on a later switch, and not again after a Director restart.
///
/// One file per limit record (<c>&lt;record uuid&gt;.json</c>), in the license folder every Director on the machine
/// shares. The uuid is Claude Code's own record id, checked as a GUID before it is used as a file name.
/// </summary>
public sealed class LimitResumeStore
{
    /// <summary>How long a record is kept. A limit reply older than this cannot still be the last turn of a session
    /// worth resuming: the longest window, the weekly one, has reset by then.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(8);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly string _folder;
    private readonly Func<DateTime> _utcNow;

    public LimitResumeStore(string folder, Func<DateTime> utcNow)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("A folder is required", nameof(folder));
        _folder = folder;
        _utcNow = utcNow ?? throw new ArgumentNullException(nameof(utcNow));
    }

    /// <summary>True when a <c>continue</c> was already sent for the limit reply <paramref name="recordUuid"/>.</summary>
    public bool WasResumed(string recordUuid) => File.Exists(PathFor(recordUuid));

    /// <summary>Records that a <c>continue</c> was sent. Written to a temporary file and moved into place.</summary>
    public void Record(LimitResume resume)
    {
        ArgumentNullException.ThrowIfNull(resume);
        Directory.CreateDirectory(_folder);
        var path = PathFor(resume.RecordUuid);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(resume, JsonOptions));
        File.Move(temp, path, overwrite: true);
        FileLog.Write($"[LimitResumeStore] Record: record={resume.RecordUuid}, session={resume.SessionId}, switchId={resume.SwitchId}");
    }

    /// <summary>Deletes records older than <see cref="Retention"/>. Only GUID-named .json files are candidates.</summary>
    public int Prune()
    {
        if (!Directory.Exists(_folder)) return 0;
        var removed = 0;
        foreach (var file in Directory.EnumerateFiles(_folder, "*.json"))
        {
            if (!Guid.TryParse(Path.GetFileNameWithoutExtension(file), out _)) continue;
            var stored = JsonSerializer.Deserialize<LimitResume>(File.ReadAllText(file), JsonOptions);
            if (stored is not null && _utcNow() - stored.SentAtUtc <= Retention) continue;
            File.Delete(file);
            removed++;
        }
        if (removed > 0) FileLog.Write($"[LimitResumeStore] Prune: removed {removed} record(s) older than {Retention.TotalDays:0} days");
        return removed;
    }

    private string PathFor(string recordUuid)
    {
        if (!Guid.TryParse(recordUuid, out var id))
            throw new ArgumentException("a limit record id must be a GUID", nameof(recordUuid));
        return Path.Combine(_folder, id.ToString("D") + ".json");
    }
}
