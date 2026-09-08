using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Prompts;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Mentor;

/// <summary>An account's business hours, whole hours 0 to 24, start before end (the config's value).</summary>
public sealed record BusinessHours(int Start, int End)
{
    public BusinessHours Validated()
    {
        if (Start < 0 || Start > 24 || End < 0 || End > 24)
            throw new MentorDataException("business hours must be whole hours from 0 to 24, got start " + Start + ", end " + End + ".");
        if (Start >= End)
            throw new MentorDataException("business hours need start < end, got start " + Start + ", end " + End + ".");
        return this;
    }
}

/// <summary>
/// The extract end per source (the reference's <c>metrics.source_extract_times</c>): the instant up to
/// which each source is known to be complete. The parity run feeds the manifest's values; the weekly service
/// feeds the run start. All UTC.
/// </summary>
public sealed record SourceEnds(DateTime PromptLog, DateTime SessionHistory, DateTime ActivityEvents, DateTime DictationTranscripts)
{
    public DateTime For(string source) => source switch
    {
        "prompt-log" => PromptLog,
        "session_history" => SessionHistory,
        "activity_events" => ActivityEvents,
        "dictation_transcripts" => DictationTranscripts,
        _ => throw new MentorDataException("No extract time for source '" + source + "'."),
    };
}

/// <summary>One raw session_history row with its five JSON array columns parsed (null is no items).</summary>
public sealed class MentorSessionRow
{
    public required SessionHistoryEntity Entity { get; init; }
    public List<object?>? WhatWasBuilt { get; init; }
    public List<object?>? LeftUnverified { get; init; }
    public List<object?>? Branches { get; init; }
    public List<object?>? PullRequests { get; init; }
    public List<object?>? Commits { get; init; }
}

/// <summary>One dictation transcript as <c>metrics.load_transcripts</c> keeps it.</summary>
public sealed class MentorTranscript
{
    public required DateTime Ts { get; init; }
    public required string Raw { get; init; }
    public required string Cleaned { get; init; }
    public required bool Cleanup { get; init; }
}

/// <summary>One turn-log corpus record: when it was captured, where it was read, and its JSON.</summary>
public sealed class TurnLogRecord
{
    public required DateTime Captured { get; init; }
    public required string Where { get; init; }
    public required JsonElement Root { get; init; }
}

/// <summary>
/// The port of the reference's <c>mentor_tools/store.py</c> TenantStore: ONE tenant's data, read from ONE
/// source, bound at construction. The reference reads a raw snapshot of the Gateway's stores; this class
/// reads the Gateway's stores in place - the prompt log through <see cref="GatewayPromptLog"/>, the three
/// tables through a <see cref="GatewayDbContext"/> the caller already scoped to the tenant, the turn log in
/// its bundle layout - and nothing above it knows which side of the seam it is on. No method takes a tenant,
/// so a store cannot be asked about another account.
///
/// The reads, all cached after the first call exactly as the reference caches them:
/// - <see cref="PromptLog"/>: every daily file of the tenant's partition, torn lines recovered.
/// - <see cref="Sessions"/> / <see cref="Records"/>: the packet's readers with origin.classify through the
///   events - these keep the session and prompt-log NAMES the metrics deliberately drop.
/// - <see cref="WeekDataFor"/>: a WeekData over those, the object the prompts file renders from.
/// - <see cref="SessionRow"/>: the raw row for the outcome columns neither reader keeps.
/// - <see cref="TurnLogRecords"/>: the corpus records for that session on one LOCAL day; the corpus is laid
///   out by UTC day, so one local day spans two folders; both are read and the records are kept by their
///   captured_at_utc inside the local day's bounds.
///
/// Errors: a tenant that is not minted, a missing partition or corpus root, an unreadable bundle or record
/// raise <see cref="MentorDataException"/> naming the thing and what to do. Nothing is skipped.
/// </summary>
public sealed class MentorStore
{
    public const string BundleSuffix = ".jsonl.gz";
    private static readonly Regex ConversationFileRe = new(@"^conversation-(\d{8})\.jsonl$", RegexOptions.CultureInvariant);

    private readonly GatewayPromptLog _promptLog;
    private readonly Func<GatewayDbContext> _openContext;
    private readonly object _gate = new();

    public TenantId Tenant { get; }
    public string Label { get; }
    public string WeekLabel { get; }
    public LocalZone Zone { get; }
    public BusinessHours Hours { get; }
    public SourceEnds SourceEnds { get; }
    public string TurnLogRoot { get; }

    private PromptLogReadResult? _promptLogRead;
    private Dictionary<string, MentorSession>? _sessions;
    private List<MentorRecord>? _records;
    private List<MentorEvent>? _events;
    private List<MentorTranscript>? _transcripts;
    private Dictionary<string, MentorSessionRow>? _rows;
    private readonly Dictionary<string, WeekData> _weekData = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Session, string Day), List<TurnLogRecord>> _turnLog = new();

    public MentorStore(TenantId tenant, GatewayPromptLog promptLog, Func<GatewayDbContext> openContext, string turnLogRoot,
        LocalZone zone, BusinessHours hours, SourceEnds sourceEnds, string weekLabel, string label)
    {
        ArgumentNullException.ThrowIfNull(promptLog);
        ArgumentNullException.ThrowIfNull(openContext);
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(sourceEnds);
        if (!tenant.IsValid)
            throw new MentorDataException("The mentor store needs a valid tenant; an unresolved tenant is refused, never defaulted.");
        MentorReaders.ParseIsoWeek(weekLabel);
        if (string.IsNullOrWhiteSpace(label))
            throw new MentorDataException("The mentor store needs the account label the reference prints.");
        Tenant = tenant;
        _promptLog = promptLog;
        _openContext = openContext;
        Zone = zone;
        Hours = hours.Validated();
        SourceEnds = sourceEnds;
        WeekLabel = weekLabel;
        Label = label;
        // DirectoryFor refuses a tenant that is not a minted account, naming it; a minted tenant with no
        // partition has no prompt history to read, which the reference also refuses.
        var partition = promptLog.DirectoryFor(tenant);
        if (!Directory.Exists(partition))
            throw new MentorDataException("prompt-log partition not found for tenant " + tenant.ToLogString() + ": "
                + Path.GetFileName(partition) + ". The account has pushed no prompts to this Gateway.");
        if (string.IsNullOrWhiteSpace(turnLogRoot) || !Directory.Exists(turnLogRoot))
            throw new MentorDataException("turn-log corpus root not found: " + turnLogRoot
                + ". Point the store at the Gateway's turn-log directory (CcStorage.TurnLog()) or the pulled corpus.");
        TurnLogRoot = turnLogRoot;
    }

    // ---------------------------------------------------------------- the prompt log

    /// <summary>Every daily file of the tenant's partition, torn lines recovered, read once.</summary>
    public PromptLogReadResult PromptLog()
    {
        lock (_gate)
        {
            if (_promptLogRead is not null) return _promptLogRead;
            FileLog.Write($"[MentorStore] PromptLog: reading the partition of tenant={Tenant.ToLogString()}");
            var partition = _promptLog.DirectoryFor(Tenant);
            var days = new List<DateTime>();
            foreach (var path in Directory.GetFiles(partition, "conversation-*.jsonl"))
            {
                var match = ConversationFileRe.Match(Path.GetFileName(path));
                if (!match.Success) continue;
                days.Add(DateTime.ParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal));
            }
            if (days.Count == 0)
                throw new MentorDataException("No conversation-YYYYMMDD.jsonl files in the prompt-log partition of tenant "
                    + Tenant.ToLogString() + ". The account has pushed no prompts to this Gateway.");
            days.Sort();
            _promptLogRead = _promptLog.ReadDetailed(Tenant, days[0], days[^1]);
            FileLog.Write($"[MentorStore] PromptLog: tenant={Tenant.ToLogString()} files={days.Count} records={_promptLogRead.Records.Count} torn={_promptLogRead.TornLines.Count} recovered={_promptLogRead.Recovered} lost={_promptLogRead.Lost}");
            return _promptLogRead;
        }
    }

    /// <summary>The torn lines as the reference names them: conversation-YYYYMMDD.jsonl:line.</summary>
    public List<string> TornLines() => PromptLog().TornLines.Select(t => t.FileName + ":" + t.LineNumber.ToString(CultureInfo.InvariantCulture)).ToList();

    // ---------------------------------------------------------------- the tables

    /// <summary>Every session row of the tenant, through the packet's reader, read once.</summary>
    public Dictionary<string, MentorSession> Sessions()
    {
        lock (_gate)
        {
            if (_sessions is not null) return _sessions;
            _sessions = MentorReaders.LoadSessions(RawRows().Values.Select(r => r.Entity));
            FileLog.Write($"[MentorStore] Sessions: tenant={Tenant.ToLogString()} rows={_sessions.Count}");
            return _sessions;
        }
    }

    /// <summary>Every prompt-log record of the tenant, classified through the events, read once.</summary>
    public List<MentorRecord> Records()
    {
        lock (_gate)
        {
            if (_records is not null) return _records;
            var records = MentorReaders.LoadRecords(PromptLog());
            var counts = MentorReaders.ClassifyRecords(records, Events());
            _records = records;
            FileLog.Write($"[MentorStore] Records: tenant={Tenant.ToLogString()} records={records.Count} {Origin.SummaryLine(Label, counts)}");
            return _records;
        }
    }

    /// <summary>The tenant's state and turn events, through the metrics reader, read once.</summary>
    public List<MentorEvent> Events()
    {
        lock (_gate)
        {
            if (_events is not null) return _events;
            FileLog.Write($"[MentorStore] Events: reading activity_events of tenant={Tenant.ToLogString()}");
            List<ActivityEventEntity> rows;
            using (var ctx = _openContext())
                rows = ctx.ActivityEvents.AsNoTracking()
                    .OrderBy(e => e.OccurredUtc).ThenBy(e => e.DirectorSequence).ThenBy(e => e.EventId)
                    .ToList();
            _events = MentorReaders.LoadEvents(rows);
            FileLog.Write($"[MentorStore] Events: tenant={Tenant.ToLogString()} rows={rows.Count} kept={_events.Count}");
            return _events;
        }
    }

    /// <summary>The tenant's dictation transcripts as metrics.load_transcripts keeps them, read once.</summary>
    public List<MentorTranscript> Transcripts()
    {
        lock (_gate)
        {
            if (_transcripts is not null) return _transcripts;
            FileLog.Write($"[MentorStore] Transcripts: reading dictation_transcripts of tenant={Tenant.ToLogString()}");
            List<DictationTranscriptEntity> rows;
            using (var ctx = _openContext())
                rows = ctx.DictationTranscripts.AsNoTracking().OrderBy(t => t.TimestampUtc).ThenBy(t => t.Id).ToList();
            var transcripts = new List<MentorTranscript>(rows.Count);
            foreach (var row in rows)
            {
                var where = "dictation_transcripts row " + row.Id;
                if (row.RawText is null) throw new MentorDataException("RawText is not a string at " + where + ".");
                transcripts.Add(new MentorTranscript
                {
                    Ts = MentorReaders.AsUtc(row.TimestampUtc, where),
                    Raw = row.RawText,
                    Cleaned = row.CleanedText ?? row.RawText,
                    Cleanup = row.CleanupApplied,
                });
            }
            _transcripts = transcripts;
            FileLog.Write($"[MentorStore] Transcripts: tenant={Tenant.ToLogString()} rows={rows.Count}");
            return _transcripts;
        }
    }

    public WeekData WeekDataFor(string isoWeek)
    {
        lock (_gate)
        {
            if (_weekData.TryGetValue(isoWeek, out var week)) return week;
            week = new WeekData(Label, Zone, isoWeek, Sessions(), Records());
            _weekData[isoWeek] = week;
            return week;
        }
    }

    private Dictionary<string, MentorSessionRow> RawRows()
    {
        if (_rows is not null) return _rows;
        FileLog.Write($"[MentorStore] RawRows: reading session_history of tenant={Tenant.ToLogString()}");
        List<SessionHistoryEntity> rows;
        using (var ctx = _openContext())
            rows = ctx.SessionHistory.AsNoTracking().OrderBy(s => s.StartedAtUtc).ThenBy(s => s.SessionId).ToList();
        var parsed = new Dictionary<string, MentorSessionRow>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.SessionId))
                throw new MentorDataException("Missing SessionId on a session_history row.");
            var where = "session_history row " + row.SessionId;
            parsed[row.SessionId] = new MentorSessionRow
            {
                Entity = row,
                WhatWasBuilt = MentorReaders.JsonArrayColumn(row.WhatWasBuiltJson, where + " WhatWasBuiltJson"),
                LeftUnverified = MentorReaders.JsonArrayColumn(row.LeftUnverifiedJson, where + " LeftUnverifiedJson"),
                Branches = MentorReaders.JsonArrayColumn(row.BranchesJson, where + " BranchesJson"),
                PullRequests = MentorReaders.JsonArrayColumn(row.PullRequestsJson, where + " PullRequestsJson"),
                Commits = MentorReaders.JsonArrayColumn(row.CommitsJson, where + " CommitsJson"),
            };
        }
        _rows = parsed;
        return _rows;
    }

    /// <summary>The raw session_history row (the JSON array columns parsed), or null without a row.</summary>
    public MentorSessionRow? SessionRow(string sessionId)
    {
        lock (_gate)
            return RawRows().TryGetValue(sessionId, out var row) ? row : null;
    }

    // ---------------------------------------------------------------- the turn-log corpus

    /// <summary>'YYYY-MM-DD' in the account's zone: (start, end) as UTC instants, the end one local day on.</summary>
    public (DateTime Start, DateTime End) LocalDayBounds(string day)
    {
        if (day is null || !DateTime.TryParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            throw new MentorDataException("day must be YYYY-MM-DD, got '" + (day ?? "None") + "'.");
        return (Zone.ToUtc(date), Zone.ToUtc(date.AddDays(1)));
    }

    /// <summary>Every bundle for this tenant under one UTC day folder, in the reference's walk order; an absent
    /// day folder is no bundles.</summary>
    public List<string> AccountBundles(string utcDay)
    {
        var dayDir = Path.Combine(TurnLogRoot, utcDay);
        if (!Directory.Exists(dayDir)) return new List<string>();
        var bundles = new List<string>();
        // sorted(Path.iterdir()) on Windows compares case-folded names; the oracle is produced there.
        foreach (var accountDir in Directory.GetDirectories(dayDir).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
        {
            if (!Path.GetFileName(accountDir).StartsWith(Tenant.Value, StringComparison.Ordinal)) continue;
            foreach (var machineDir in Directory.GetDirectories(accountDir).OrderBy(d => Path.GetFileName(d), StringComparer.OrdinalIgnoreCase))
            {
                foreach (var path in Directory.GetFiles(machineDir).OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase))
                    if (path.EndsWith(BundleSuffix, StringComparison.Ordinal)) bundles.Add(path);
            }
        }
        return bundles;
    }

    /// <summary>captured_at_utc as a UTC instant; the corpus writes seven fractional digits, which the reference
    /// cuts to six before parsing, so the port accepts one to seven and keeps the reference's precision.</summary>
    public static DateTime ParseCaptured(JsonElement record, string where)
    {
        if (!record.TryGetProperty("captured_at_utc", out var element) || element.ValueKind != JsonValueKind.String)
            throw new MentorDataException("captured_at_utc is not a Z-suffixed UTC string at " + where + ": "
                + (element.ValueKind == JsonValueKind.Undefined ? "None" : element.GetRawText()));
        var value = element.GetString()!;
        if (!value.EndsWith('Z'))
            throw new MentorDataException("captured_at_utc is not a Z-suffixed UTC string at " + where + ": " + PyText.Repr(value));
        var text = value.Substring(0, value.Length - 1);
        var dot = text.IndexOf('.');
        if (dot >= 0)
        {
            var fraction = text.Substring(dot + 1);
            if (fraction.Length > 6) fraction = fraction.Substring(0, 6);
            text = text.Substring(0, dot) + "." + fraction;
        }
        var formats = new[] { "yyyy-MM-dd'T'HH:mm:ss", "yyyy-MM-dd'T'HH:mm:ss.f", "yyyy-MM-dd'T'HH:mm:ss.ff", "yyyy-MM-dd'T'HH:mm:ss.fff",
            "yyyy-MM-dd'T'HH:mm:ss.ffff", "yyyy-MM-dd'T'HH:mm:ss.fffff", "yyyy-MM-dd'T'HH:mm:ss.ffffff" };
        if (!DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            throw new MentorDataException("captured_at_utc is unparseable at " + where + ": " + PyText.Repr(value));
        return parsed;
    }

    /// <summary>Every record of one bundle, in file order, as (line number, JSON). A bundle is concatenated gzip
    /// members, one per record; an unreadable bundle or record stops the run naming it.</summary>
    public static List<(int Number, JsonElement Root)> ReadBundle(string path)
    {
        var records = new List<(int, JsonElement)>();
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var reader = new StreamReader(gzip, new UTF8Encoding(false));
            var number = 0;
            while (reader.ReadLine() is { } raw)
            {
                number++;
                var line = PyText.Strip(raw);
                if (line.Length == 0) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    records.Add((number, document.RootElement.Clone()));
                }
                catch (JsonException error)
                {
                    throw new MentorDataException("unreadable record at " + path + " line " + number + ": " + error.Message + ". Re-pull the bundle.");
                }
            }
        }
        catch (IOException error)
        {
            throw new MentorDataException("unreadable bundle " + path + ": " + error.Message + ". Re-pull the bundle.");
        }
        catch (InvalidDataException error)
        {
            throw new MentorDataException("unreadable bundle " + path + ": " + error.Message + ". Re-pull the bundle.");
        }
        return records;
    }

    /// <summary>The corpus records for this account and that session on one LOCAL day, oldest first. Reads
    /// the UTC day folders the local day touches; keeps a record by its captured_at_utc.</summary>
    public List<TurnLogRecord> TurnLogRecords(string sessionId, string day)
    {
        lock (_gate)
        {
            var key = (sessionId, day);
            if (_turnLog.TryGetValue(key, out var cached)) return cached;
            var (start, end) = LocalDayBounds(day);
            var utcDays = new List<string>();
            var walk = start.Date;
            var last = end.AddTicks(-TimeSpan.TicksPerMillisecond / 1000).Date;   // one microsecond before the end, as the reference
            while (walk <= last)
            {
                utcDays.Add(walk.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                walk = walk.AddDays(1);
            }
            var found = new List<TurnLogRecord>();
            foreach (var utcDay in utcDays)
            {
                foreach (var path in AccountBundles(utcDay))
                {
                    foreach (var (number, root) in ReadBundle(path))
                    {
                        string? recordSession = null;
                        if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("at_a_glance", out var glance)
                            && glance.ValueKind == JsonValueKind.Object && glance.TryGetProperty("session_id", out var sid)
                            && sid.ValueKind == JsonValueKind.String)
                            recordSession = sid.GetString();
                        if (recordSession != sessionId) continue;
                        var where = path + " line " + number.ToString(CultureInfo.InvariantCulture);
                        var captured = ParseCaptured(root, where);
                        if (start <= captured && captured < end)
                            found.Add(new TurnLogRecord { Captured = captured, Where = where, Root = root });
                    }
                }
            }
            found = found.OrderBy(r => r.Captured).ToList();
            _turnLog[key] = found;
            FileLog.Write($"[MentorStore] TurnLogRecords: tenant={Tenant.ToLogString()} session={PromptsFile.Id8(sessionId)} day={day} records={found.Count}");
            return found;
        }
    }
}
