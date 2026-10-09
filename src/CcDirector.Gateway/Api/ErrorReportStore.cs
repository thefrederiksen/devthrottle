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
    // The Error Logging mission (issue #3675). All optional: a record from an older sender has none of them.
    [JsonPropertyName("user_visible")] public bool? UserVisible { get; init; }
    [JsonPropertyName("surface")] public string? Surface { get; init; }
    [JsonPropertyName("action")] public string? Action { get; init; }
    [JsonPropertyName("correlation_id")] public string? CorrelationId { get; init; }
    [JsonPropertyName("http_status")] public int? HttpStatus { get; init; }
    [JsonPropertyName("error_code")] public string? ErrorCode { get; init; }
    [JsonPropertyName("session_id")] public string? SessionId { get; init; }
    /// <summary>Which problem this is (<see cref="ErrorFingerprint"/>). Stamped by <see cref="ErrorReportStore.Append"/>
    /// whatever the writer set, so it is only ever computed in one place.</summary>
    [JsonPropertyName("fingerprint")] public string? Fingerprint { get; init; }
    /// <summary>Which version of the fingerprint rules made <see cref="Fingerprint"/> (<see cref="ErrorFingerprint.RulesVersion"/>).</summary>
    [JsonPropertyName("fingerprint_rules")] public int? FingerprintRules { get; init; }

    /// <summary>This record with its fingerprint and the rules version computed here - the one way either is set.</summary>
    internal ErrorReportRecord Stamped() => this with
    {
        Fingerprint = ErrorFingerprint.Of(Component, Source, ExceptionType, Message, HttpStatus, ErrorCode),
        FingerprintRules = ErrorFingerprint.RulesVersion,
    };
}

/// <summary>
/// The permanent record of one problem (issue #3675): kept for good, after its full reports have aged out. The
/// owner's words: "a small per-problem summary kept for good (count, first and last seen, linked issue - no
/// message text)". So it holds no message, no stack, no account and no machine: only what identifies the problem
/// and how often it happened.
/// </summary>
internal sealed record ErrorProblemSummary
{
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    /// <summary>Which version of the fingerprint rules made the fingerprint, so a later change to the rules shows
    /// as a fork rather than as a problem that silently stopped happening.</summary>
    [JsonPropertyName("fingerprint_rules")] public int FingerprintRules { get; init; }
    [JsonPropertyName("component")] public string Component { get; init; } = "";
    /// <summary>The class that logged it - a name from our own code, never user text.</summary>
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    /// <summary>How many reports were stored.</summary>
    [JsonPropertyName("count")] public long Count { get; init; }
    /// <summary>How many times it happened: a sender folds repeats into one report and says how many.</summary>
    [JsonPropertyName("occurrences")] public long Occurrences { get; init; }
    [JsonPropertyName("first_seen_utc")] public DateTime FirstSeenUtc { get; init; }
    [JsonPropertyName("last_seen_utc")] public DateTime LastSeenUtc { get; init; }
    /// <summary>The work item filed for it ("#3675", "owner/repo#12" or a GitHub issue address), or null.</summary>
    [JsonPropertyName("linked_issue")] public string? LinkedIssue { get; init; }
}

/// <summary>One problem in the grouped read: every matching report with the same fingerprint, counted.</summary>
internal sealed record ErrorReportGroup
{
    [JsonPropertyName("fingerprint")] public string Fingerprint { get; init; } = "";
    [JsonPropertyName("component")] public string Component { get; init; } = "";
    [JsonPropertyName("source")] public string Source { get; init; } = "";
    [JsonPropertyName("exception_type")] public string? ExceptionType { get; init; }
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("occurrences")] public long Occurrences { get; init; }
    /// <summary>How many of the reports said the user saw the error.</summary>
    [JsonPropertyName("user_visible")] public int UserVisible { get; init; }
    [JsonPropertyName("first_seen_utc")] public DateTime FirstSeenUtc { get; init; }
    [JsonPropertyName("last_seen_utc")] public DateTime LastSeenUtc { get; init; }
    /// <summary>How many different machines reported it, counted by their hashed ids (issue #3646).</summary>
    [JsonPropertyName("machines")] public int Machines { get; init; }
    /// <summary>Every product version that reported it, sorted (issue #3646).</summary>
    [JsonPropertyName("versions")] public IReadOnlyList<string> Versions { get; init; } = [];
    /// <summary>The newest report's message, already scrubbed when it was stored.</summary>
    [JsonPropertyName("sample_message")] public string SampleMessage { get; init; } = "";
    [JsonPropertyName("linked_issue")] public string? LinkedIssue { get; init; }
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

/// <summary>The grouped answer: the problems with the most reports first, how many problems matched, and how many
/// reports they hold between them.</summary>
internal sealed record ErrorGroupPage(IReadOnlyList<ErrorReportGroup> Groups, int TotalGroups, int TotalReports);

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
/// RETENTION (the owner's ruling, issue #3675): "Full reports for 90 days, plus a small per-problem summary kept
/// for good (count, first and last seen, linked issue - no message text)." Day folders older than
/// <see cref="RetentionDays"/> are deleted - only folders whose name IS a date, so nothing else that ever lands
/// under the root can be swept by mistake. The summaries live in <c>&lt;root&gt;/summaries/&lt;fingerprint&gt;.json</c>,
/// a folder whose name is not a date, so the sweep never touches them.
///
/// THE SUMMARIES AND A DEPLOY. A summary is read, updated and replaced whole (written to a temporary file and
/// moved over the old one, so a reader never sees half a file). Inside one process the write lock orders every
/// update. Two containers share the files only for the seconds a deploy overlaps them, and an update from each in
/// that window can cost a summary the count of one batch. The full reports are unaffected, and the grouped read
/// counts from those.
/// </summary>
internal sealed class ErrorReportStore
{
    public const int RetentionDays = 90;
    internal const string SummaryFolder = "summaries";
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
        // The one place a fingerprint is computed (rule 7): whatever the writer set is replaced.
        var stamped = records.Select(r => r.Stamped()).ToList();
        var sb = new StringBuilder();
        foreach (var r in stamped)
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
                // A write cut off part way (a share that dropped mid-write, a container stopped) leaves a line
                // with no ending. Start on a fresh line, or the first record of this batch - often the retry of
                // the one that was cut off - would be glued onto it and both would be unreadable.
                if (stream.Length > 0 && !EndsWithNewline(file))
                    sb.Insert(0, '\n');
                var bytes = Encoding.UTF8.GetBytes(sb.ToString());
                stream.Write(bytes, 0, bytes.Length);
                stream.Flush(flushToDisk: true);
            }

            // The reports are stored. A summary that cannot be updated costs the summary one batch's count, and is
            // logged as a failure; turning it into a 503 would make the sender retry and store every report twice.
            try
            {
                UpdateSummaries(stamped);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                FileLog.Write($"[ErrorReportStore] UpdateSummaries FAILED ({ex.GetType().Name}): {ex.Message}; {stamped.Count} report(s) stored, their summary count is short");
            }

            if (now - _lastPruneUtc > TimeSpan.FromHours(1))
            {
                _lastPruneUtc = now;
                // The records are already written. Housekeeping failing must not turn a stored batch into a
                // 503, which the Director would retry and store twice.
                try
                {
                    Prune(now);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    FileLog.Write($"[ErrorReportStore] prune did not complete, will retry in an hour ({ex.GetType().Name}): {ex.Message}");
                }
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
        var matched = Scan(q);

        var byComponent = matched
            .GroupBy(r => r.Component, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal);
        var newest = matched.OrderByDescending(r => r.ReceivedUtc).Take(limit).ToList();
        return new ErrorReportPage(newest, matched.Count, byComponent);
    }

    /// <summary>
    /// The grouped read (issue #3675): every matching report, one row per fingerprint, the problem with the most
    /// reports first. The same filters as <see cref="Query"/>; the limit counts problems, not reports. Each group
    /// carries the linked issue from its permanent summary, when one was set.
    /// </summary>
    public ErrorGroupPage Group(ErrorReportQuery q)
    {
        var limit = Math.Clamp(q.Limit, 1, MaxLimit);
        var matched = Scan(q);
        var groups = matched
            .GroupBy(r => r.Fingerprint!, StringComparer.Ordinal)
            .Select(g =>
            {
                var newest = g.MaxBy(r => r.ReceivedUtc)!;
                return new ErrorReportGroup
                {
                    Fingerprint = g.Key,
                    Component = newest.Component,
                    Source = newest.Source,
                    ExceptionType = newest.ExceptionType,
                    Count = g.Count(),
                    Occurrences = g.Sum(r => (long)Math.Max(1, r.RepeatCount)),
                    UserVisible = g.Count(r => r.UserVisible == true),
                    FirstSeenUtc = g.Min(r => r.FirstSeenUtc ?? r.ReceivedUtc),
                    LastSeenUtc = g.Max(r => r.LastSeenUtc ?? r.ReceivedUtc),
                    Machines = g.Select(r => r.MachineId).Where(m => !string.IsNullOrEmpty(m)).Distinct(StringComparer.Ordinal).Count(),
                    Versions = g.Select(r => r.ProductVersion).Where(v => !string.IsNullOrEmpty(v))
                        .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList(),
                    SampleMessage = newest.Message,
                };
            })
            .OrderByDescending(g => g.Count)
            .ThenByDescending(g => g.LastSeenUtc)
            .ToList();

        var page = groups.Take(limit)
            .Select(g => g with { LinkedIssue = ReadSummary(g.Fingerprint)?.LinkedIssue })
            .ToList();
        return new ErrorGroupPage(page, groups.Count, matched.Count);
    }

    /// <summary>Every permanent summary, the most recently seen first.</summary>
    public IReadOnlyList<ErrorProblemSummary> Summaries()
    {
        var dir = Path.Combine(_root, SummaryFolder);
        if (!Directory.Exists(dir)) return [];
        var list = new List<ErrorProblemSummary>();
        foreach (var file in Directory.EnumerateFiles(dir, "*.json"))
        {
            var fingerprint = Path.GetFileNameWithoutExtension(file);
            if (!ErrorFingerprint.IsFingerprint(fingerprint)) continue;
            if (ReadSummary(fingerprint) is { } summary) list.Add(summary);
        }
        return list.OrderByDescending(s => s.LastSeenUtc).ToList();
    }

    /// <summary>
    /// Link a problem to the work item filed for it, or clear the link with null. Returns the updated summary, or
    /// null when no report with that fingerprint was ever stored - a link to a problem nobody has seen is refused,
    /// not invented. Throws <see cref="StoreBusyException"/> on a stalled share, like a write.
    /// </summary>
    public ErrorProblemSummary? SetLinkedIssue(string fingerprint, string? linkedIssue)
    {
        if (!ErrorFingerprint.IsFingerprint(fingerprint))
            throw new ArgumentException("a fingerprint is 16 lower-case hexadecimal characters", nameof(fingerprint));
        if (!Monitor.TryEnter(_writeLock, WriteLockTimeout))
            throw new StoreBusyException(
                $"the error store did not free up within {WriteLockTimeout.TotalSeconds:0} seconds; the storage share may be stalled");
        try
        {
            var existing = ReadSummary(fingerprint);
            if (existing is null) return null;
            var updated = existing with { LinkedIssue = linkedIssue };
            WriteSummary(updated);
            FileLog.Write($"[ErrorReportStore] SetLinkedIssue: fingerprint={fingerprint} linked_issue={linkedIssue ?? "(cleared)"}");
            return updated;
        }
        finally
        {
            Monitor.Exit(_writeLock);
        }
    }

    /// <summary>Fold one stored batch into the permanent summaries. Called under the write lock.</summary>
    private void UpdateSummaries(IReadOnlyList<ErrorReportRecord> stored)
    {
        foreach (var g in stored.GroupBy(r => r.Fingerprint!, StringComparer.Ordinal))
        {
            var newest = g.MaxBy(r => r.ReceivedUtc)!;
            var first = g.Min(r => r.FirstSeenUtc ?? r.ReceivedUtc);
            var last = g.Max(r => r.LastSeenUtc ?? r.ReceivedUtc);
            var count = g.Count();
            var occurrences = g.Sum(r => (long)Math.Max(1, r.RepeatCount));
            var existing = ReadSummary(g.Key);
            WriteSummary(existing is null
                ? new ErrorProblemSummary
                {
                    Fingerprint = g.Key,
                    FingerprintRules = ErrorFingerprint.RulesVersion,
                    Component = newest.Component,
                    Source = newest.Source,
                    Count = count,
                    Occurrences = occurrences,
                    FirstSeenUtc = first,
                    LastSeenUtc = last,
                }
                : existing with
                {
                    Count = existing.Count + count,
                    Occurrences = existing.Occurrences + occurrences,
                    FirstSeenUtc = first < existing.FirstSeenUtc ? first : existing.FirstSeenUtc,
                    LastSeenUtc = last > existing.LastSeenUtc ? last : existing.LastSeenUtc,
                });
        }
    }

    /// <summary>One summary, or null when none exists. A file that does not parse throws - a summary kept for good
    /// is never silently started again from nothing.</summary>
    internal ErrorProblemSummary? ReadSummary(string fingerprint)
    {
        var file = SummaryPath(fingerprint);
        if (!File.Exists(file)) return null;
        string text;
        using (var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        using (var reader = new StreamReader(stream, Encoding.UTF8))
            text = reader.ReadToEnd();
        ErrorProblemSummary? summary;
        try
        {
            summary = JsonSerializer.Deserialize<ErrorProblemSummary>(text);
        }
        catch (JsonException ex)
        {
            // Name the file: it is the one thing the person who must repair it by hand on the share needs to know.
            throw new JsonException($"the summary {file} does not parse: {ex.Message}", ex);
        }
        return summary ?? throw new JsonException($"the summary {file} is empty");
    }

    private void WriteSummary(ErrorProblemSummary summary)
    {
        var file = SummaryPath(summary.Fingerprint);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        var temp = $"{file}.{_instance}.tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(summary, LineJson), Encoding.UTF8);
        File.Move(temp, file, overwrite: true);
    }

    private string SummaryPath(string fingerprint) => Path.Combine(_root, SummaryFolder, fingerprint + ".json");

    private List<ErrorReportRecord> Scan(ErrorReportQuery q)
    {
        var matched = new List<ErrorReportRecord>();
        if (!Directory.Exists(_root)) return matched;
        for (var day = q.UntilUtc.Date; day >= q.SinceUtc.Date; day = day.AddDays(-1))
        {
            var dir = Path.Combine(_root, DayName(day));
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.jsonl"))
                foreach (var record in ReadFile(file))
                    if (Matches(record, q)) matched.Add(record);
        }
        return matched;
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
            if (record is null) continue;
            // A record stored before fingerprints existed (issue #3675) is given one as it is read, by the same code
            // that stamps every new record, so old and new reports of one problem group together.
            yield return record.Fingerprint is null ? record.Stamped() : record;
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

    private static bool EndsWithNewline(string file)
    {
        using var read = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (read.Length == 0) return true;
        read.Seek(-1, SeekOrigin.End);
        return read.ReadByte() == '\n';
    }

    private static string DayName(DateTime utc) => utc.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}
