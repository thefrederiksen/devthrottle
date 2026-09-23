using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Api;

/// <summary>
/// One stored error report (issue #3311): a Director error, a launcher error, or an installer failure.
/// Written by <see cref="ErrorReportStore"/> as one line of JSON; the null fields are left out.
/// </summary>
internal sealed record ErrorReportRecord
{
    [JsonPropertyName("received_utc")] public DateTime ReceivedUtc { get; init; }
    [JsonPropertyName("component")] public string Component { get; init; } = "";
    /// <summary>The account the reporting device belongs to; empty for an installer report, which is sent
    /// before the machine has any account.</summary>
    [JsonPropertyName("account")] public string Account { get; init; } = "";
    /// <summary>A one-way hash of the credential that sent it (a device), or the install id (an installer).</summary>
    [JsonPropertyName("device")] public string Device { get; init; } = "";
    [JsonPropertyName("machine_id")] public string MachineId { get; init; } = "";
    [JsonPropertyName("product_version")] public string ProductVersion { get; init; } = "";
    [JsonPropertyName("os")] public string Os { get; init; } = "";
    [JsonPropertyName("os_version")] public string OsVersion { get; init; } = "";
    [JsonPropertyName("arch")] public string Arch { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("kind")] public string Kind { get; init; } = "";
    [JsonPropertyName("message")] public string Message { get; init; } = "";
    [JsonPropertyName("exception_type")] public string? ExceptionType { get; init; }
    [JsonPropertyName("stack")] public string? Stack { get; init; }
    [JsonPropertyName("repeat_count")] public int RepeatCount { get; init; } = 1;
    [JsonPropertyName("first_seen_utc")] public DateTime? FirstSeenUtc { get; init; }
    [JsonPropertyName("last_seen_utc")] public DateTime? LastSeenUtc { get; init; }
    // Installer reports only.
    [JsonPropertyName("installer")] public string? Installer { get; init; }
    [JsonPropertyName("step")] public string? Step { get; init; }
    [JsonPropertyName("diagnostics")] public string? Diagnostics { get; init; }
}

/// <summary>The store could not take a write in time - the storage share is stalled or busy. The route answers
/// 503 and the Director retries later.</summary>
internal sealed class StoreBusyException(string message) : Exception(message);

/// <summary>What to read back. Every filter is optional; an empty one matches everything.</summary>
internal sealed record ErrorReportQuery(
    DateTime SinceUtc,
    DateTime UntilUtc,
    string? Account = null,
    string? MachineId = null,
    string? ProductVersion = null,
    string? Component = null,
    int Limit = 100);

/// <summary>The answer: the newest matching records, and how many matched in all.</summary>
internal sealed record ErrorReportPage(IReadOnlyList<ErrorReportRecord> Records, int TotalMatched, IReadOnlyDictionary<string, int> ByComponent);

/// <summary>
/// The durable record of error reports (issue #3311), kept as files on the Gateway's DURABLE storage root -
/// on hosted, the Azure Files share mounted at <c>/home/gateway/cc-director</c>, which a deploy does not
/// touch. No database table: a schema change is the owner's decision, and files answer every question the
/// owner asked (by account, machine, version and time) at this volume.
///
/// WHY NOT THE GATEWAY LOG. The first slice (#3314) wrote one FileLog line per installer report and called
/// that the durable record. On hosted it is not: <c>GatewayEntryPoint</c> puts the process log on the
/// container's TEMPORARY disk, so every such line is gone on the next deploy.
///
/// LAYOUT. <c>&lt;root&gt;/error-reports/&lt;yyyy-MM-dd&gt;/&lt;instance&gt;.jsonl</c>, one JSON record per line. The
/// instance part is unique to this process, because during a deploy two containers run against one share
/// mounted without byte-range locks, and two writers on one file clobber each other mid-record. A reader
/// reads every file in a day folder. A line that does not parse (a write cut short) is skipped.
///
/// RETENTION. Day folders older than <see cref="RetentionDays"/> are deleted - only folders whose name IS a
/// date, so nothing else that ever lands under the root can be swept by mistake.
/// </summary>
internal sealed class ErrorReportStore
{
    public const int RetentionDays = 30;
    public const int MaxLimit = 500;

    private static readonly JsonSerializerOptions LineJson = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _root;
    private readonly string _instance;
    private readonly Func<DateTime> _clock;
    private readonly object _writeLock = new();
    private DateTime _lastPruneUtc = DateTime.MinValue;

    public ErrorReportStore(string root, Func<DateTime>? clock = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        _root = root;
        _instance = Guid.NewGuid().ToString("N")[..12];
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public string Root => _root;

    /// <summary>How long a write waits for the one before it. Past this the share is taken to be stalled.</summary>
    internal static readonly TimeSpan WriteLockTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Append records. Throws when the storage cannot be written - the route answers 503 and the caller
    /// retries - rather than accepting a report and losing it.
    ///
    /// A STALLED SHARE MUST NOT STARVE THE GATEWAY. The write is synchronous against the Azure Files share,
    /// which has stalled before (the reason the process log moved off it). And the moment it stalls is the
    /// moment every Director starts logging connection errors and posting them here. So a write waits at most
    /// <see cref="WriteLockTimeout"/> for the one ahead of it and then gives up with
    /// <see cref="StoreBusyException"/>: at most ONE request thread is ever stuck on the share, and every other
    /// report is refused at once and retried by its Director later.
    /// </summary>
    public void Append(IReadOnlyCollection<ErrorReportRecord> records)
    {
        if (records.Count == 0) return;
        // The folder is named from the records' own stamp, not a second clock read, so a batch received just
        // before midnight is filed under the day a query for that day will open.
        var day = records.Max(r => r.ReceivedUtc);
        var now = _clock();
        var sb = new StringBuilder();
        foreach (var r in records)
            sb.Append(JsonSerializer.Serialize(r, LineJson)).Append('\n');

        if (!Monitor.TryEnter(_writeLock, WriteLockTimeout))
            throw new StoreBusyException(
                $"the error store did not free up within {WriteLockTimeout.TotalSeconds:0} seconds; the storage share may be stalled");
        try
        {
            var dir = Path.Combine(_root, DayName(day));
            Directory.CreateDirectory(dir);
            var file = Path.Combine(dir, _instance + ".jsonl");
            using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            {
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            if (now - _lastPruneUtc > TimeSpan.FromHours(1))
            {
                _lastPruneUtc = now;
                Prune(now);
            }
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }
    }

    /// <summary>Test seam: hold the write lock, as a write stuck on a stalled share would.</summary>
    internal IDisposable HoldWriteLockForTests()
    {
        Monitor.Enter(_writeLock);
        return new Releaser(_writeLock);
    }

    private sealed class Releaser(object gate) : IDisposable
    {
        public void Dispose() => Monitor.Exit(gate);
    }

    /// <summary>Newest first. Reads only the day folders the time range touches.</summary>
    public ErrorReportPage Query(ErrorReportQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        var matched = new List<ErrorReportRecord>();
        if (Directory.Exists(_root))
        {
            for (var day = q.UntilUtc.Date; day >= q.SinceUtc.Date; day = day.AddDays(-1))
            {
                var dir = Path.Combine(_root, DayName(day));
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                    foreach (var record in ReadFile(file))
                        if (Matches(record, q)) matched.Add(record);
            }
        }

        var byComponent = matched
            .GroupBy(r => r.Component, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var newest = matched.OrderByDescending(r => r.ReceivedUtc).Take(limit).ToList();
        return new ErrorReportPage(newest, matched.Count, byComponent);
    }

    private static bool Matches(ErrorReportRecord r, ErrorReportQuery q)
    {
        if (r.ReceivedUtc < q.SinceUtc || r.ReceivedUtc > q.UntilUtc) return false;
        if (!string.IsNullOrEmpty(q.Account) && !string.Equals(r.Account, q.Account, StringComparison.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrEmpty(q.MachineId) && !string.Equals(r.MachineId, q.MachineId, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(q.Component) && !string.Equals(r.Component, q.Component, StringComparison.Ordinal)) return false;
        if (!string.IsNullOrEmpty(q.ProductVersion) && !r.ProductVersion.StartsWith(q.ProductVersion, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    private static IEnumerable<ErrorReportRecord> ReadFile(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0) continue;
            ErrorReportRecord? record;
            try
            {
                record = JsonSerializer.Deserialize<ErrorReportRecord>(line);
            }
            catch (JsonException)
            {
                // A line cut short by a write in progress, or by a container stopped mid-write.
                continue;
            }
            if (record is not null) yield return record;
        }
    }

    /// <summary>Delete day folders older than the retention period. Only a folder whose whole name parses as
    /// a date is a candidate; anything else under the root is left alone.</summary>
    internal int Prune(DateTime nowUtc)
    {
        if (!Directory.Exists(_root)) return 0;
        var cutoff = nowUtc.Date.AddDays(-RetentionDays);
        var deleted = 0;
        foreach (var dir in Directory.EnumerateDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (!DateTime.TryParseExact(name, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)) continue;
            if (day >= cutoff) continue;
            try
            {
                Directory.Delete(dir, recursive: true);
                deleted++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The other container may be reading it during a deploy. Housekeeping must not cost a
                // report its place in the store, so this folder is simply tried again on the next prune.
                FileLog.Write($"[ErrorReportStore] prune of {name} did not complete, will retry ({ex.GetType().Name}): {ex.Message}");
            }
        }
        if (deleted > 0) FileLog.Write($"[ErrorReportStore] pruned {deleted} day folder(s) older than {RetentionDays} days");
        return deleted;
    }

    private static string DayName(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
