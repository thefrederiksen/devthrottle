using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Prompts;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// One session row as the reference's <c>packet.load_sessions</c> keeps it: the columns the session tools
/// show, names included. <see cref="Started"/> and <see cref="Ended"/> are UTC.
/// </summary>
public sealed class MentorSession
{
    public required string Id { get; init; }
    public string? Name { get; init; }
    public string? Agent { get; init; }
    public required DateTime Started { get; init; }
    public DateTime? Ended { get; init; }
    public string? Ending { get; init; }
    public long? AgentTurns { get; init; }
    public long? PeakContext { get; init; }
    public string? RepoName { get; init; }
    public required string OriginKind { get; init; }
}

/// <summary>
/// The ports of the reference's readers - <c>packet.load_sessions</c>, <c>packet.load_records</c>,
/// <c>metrics.load_events</c>, <c>packet.classify_records</c> - and the small rules beside them
/// (<c>repo_name_of</c>, <c>json_array_column</c>, <c>stamp_utc</c>, the ISO-week arithmetic). The
/// reference reads JSON files; the port reads the Gateway's own entities and its prompt log, and every rule
/// that decides what a row means is the reference's, sentence for sentence. No fallbacks: a malformed row
/// stops the run naming it.
/// </summary>
public static class MentorReaders
{
    public static readonly HashSet<string> EndingKinds = new(StringComparer.Ordinal) { "closed", "finished", "director-stopped", "interrupted" };
    public static readonly HashSet<string> ActivityStates = new(StringComparer.Ordinal) { "Starting", "Idle", "Working", "WaitingForInput", "WaitingForPerm", "Exited" };
    public static readonly HashSet<string> StateEventTypes = new(StringComparer.Ordinal) { "activity-transition", "session-exited" };
    public const string TurnEventType = "turn-submitted";

    /// <summary>What a session row's OriginKind may say (SessionOrigin.cs: recorded at birth, never guessed;
    /// a NULL column is a Director that predates the field and is read as unknown).</summary>
    public static readonly string[] SessionOriginKinds = { "human", "agent", "schedule", "unknown" };

    // ------------------------------------------------------------------ time

    /// <summary>A stored timestamp as a UTC instant. The Gateway's columns are UTC by convention; a value of
    /// unspecified kind (the SQLite provider) is that convention, a local one is a defect and stops the run.</summary>
    public static DateTime AsUtc(DateTime value, string where)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
            _ => throw new MentorDataException("Timestamp of local kind at " + where + "; the Gateway stores UTC."),
        };
    }

    /// <summary>The reference's <c>stamp_utc</c>: seconds precision, Z suffix.</summary>
    public static string StampUtc(DateTime utc)
        => new DateTime(utc.Ticks - utc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc).ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    private static readonly Regex IsoWeekRe = new(@"^([0-9]{4})-W([0-9]{2})$", RegexOptions.CultureInvariant);

    public static (int Year, int Week) ParseIsoWeek(string isoWeek)
    {
        var match = isoWeek is null ? null : IsoWeekRe.Match(isoWeek);
        if (match is null || !match.Success)
            throw new MentorDataException("ISO week must look like 2026-W35, got '" + (isoWeek ?? "None") + "'.");
        return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture));
    }

    /// <summary>'2026-W35' in a zone: (Monday 00:00 local, next Monday 00:00 local) as UTC instants.</summary>
    public static (DateTime Start, DateTime End) WeekBounds(string isoWeek, LocalZone zone)
    {
        var (year, week) = ParseIsoWeek(isoWeek);
        var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
        return (zone.ToUtc(monday), zone.ToUtc(monday.AddDays(7)));
    }

    public static string IsoWeekLabel(DateTime date)
        => ISOWeek.GetYear(date).ToString(CultureInfo.InvariantCulture) + "-W" + ISOWeek.GetWeekOfYear(date).ToString("00", CultureInfo.InvariantCulture);

    /// <summary>The n ISO weeks before <paramref name="isoWeek"/>, oldest first.</summary>
    public static List<string> PriorWeeks(string isoWeek, int n = 4)
    {
        var (year, week) = ParseIsoWeek(isoWeek);
        var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
        var labels = new List<string>();
        for (var back = n; back > 0; back--) labels.Add(IsoWeekLabel(monday.AddDays(-7 * back)));
        return labels;
    }

    // ------------------------------------------------------------------ columns

    private static readonly Regex RepoNameDriveRe = new("^[A-Za-z]:", RegexOptions.CultureInvariant);

    /// <summary>
    /// The session row's RepoName as a NAME: null for NULL or empty, else the owner/repo slug. Anything that
    /// could be a path - a backslash, a colon, a drive letter, a leading slash, a '..' segment, or more than
    /// one slash - stops the run naming the row rather than writing a path into an answer.
    /// </summary>
    public static string? RepoNameOf(string? value, string where)
    {
        if (string.IsNullOrEmpty(value)) return null;
        string? reason = null;
        if (value.Contains('\\')) reason = "a backslash";
        else if (value.Contains(':')) reason = "a colon";
        else if (RepoNameDriveRe.IsMatch(value)) reason = "a drive letter";
        else if (value.StartsWith('/')) reason = "a leading slash";
        else if (value.Split('/').Contains("..")) reason = "a '..' segment";
        else if (value.Count(c => c == '/') > 1) reason = "more than one slash";
        if (reason is not null)
            throw new MentorDataException("RepoName at " + where + " carries " + reason + ", so it is a path, not a name; the row "
                + "is refused rather than written into an answer (BRIEF V6).");
        return value;
    }

    /// <summary>A JSON text column that must be null or a JSON array; answers the items (string, long,
    /// double, bool or null each) or null for a NULL column. Not an array: the run stops naming the place.</summary>
    public static List<object?>? JsonArrayColumn(string? value, string where)
    {
        if (value is null) return null;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(value);
        }
        catch (JsonException)
        {
            throw new MentorDataException("JSON column is not JSON at " + where + ".");
        }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                throw new MentorDataException("JSON column is not an array at " + where + ".");
            var items = new List<object?>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                items.Add(element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    JsonValueKind.Null => null,
                    _ => (object?)element.Clone(),
                });
            }
            return items;
        }
    }

    // ------------------------------------------------------------------ the readers

    /// <summary>
    /// The port of <c>packet.load_sessions</c>: SessionId to row with the columns the tools show. The
    /// reference keeps the raw RepoName here and refuses a path-shaped one only in its metrics reader; the
    /// port applies <see cref="RepoNameOf"/> at the one read, as the mandate asks, so no path reaches an
    /// answer whichever reader served it. Rows keep the order they were given in.
    /// </summary>
    public static Dictionary<string, MentorSession> LoadSessions(IEnumerable<SessionHistoryEntity> rows)
    {
        var sessions = new Dictionary<string, MentorSession>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            if (string.IsNullOrEmpty(row.SessionId))
                throw new MentorDataException("Missing SessionId on a session_history row.");
            var where = "session_history row " + row.SessionId;
            if (row.EndingKind is not null && !EndingKinds.Contains(row.EndingKind))
                throw new MentorDataException("Unknown EndingKind '" + row.EndingKind + "' at " + where + ".");
            var originKind = row.OriginKind ?? "unknown";
            if (!SessionOriginKinds.Contains(originKind))
                throw new MentorDataException("Unknown OriginKind '" + originKind + "' at " + where + "; expected one of "
                    + string.Join(", ", SessionOriginKinds) + " or NULL.");
            sessions[row.SessionId] = new MentorSession
            {
                Id = row.SessionId,
                Name = row.SessionName,
                Agent = row.AgentKind,
                Started = AsUtc(row.StartedAtUtc, where),
                Ended = row.EndedAtUtc is null ? null : AsUtc(row.EndedAtUtc.Value, where),
                Ending = row.EndingKind,
                AgentTurns = row.AgentTurnCount,
                PeakContext = row.PeakContextTokens,
                RepoName = RepoNameOf(row.RepoName, where),
                OriginKind = originKind,
            };
        }
        return sessions;
    }

    /// <summary>
    /// The port of <c>packet.load_records</c> over what <see cref="GatewayPromptLog.ReadDetailed"/> answered:
    /// every prompt-log record (both roles), sorted by timestamp (a stable sort, so records that share a
    /// timestamp keep their file order), pid = file:line. Torn lines were already recovered by the reader.
    /// </summary>
    public static List<MentorRecord> LoadRecords(PromptLogReadResult read)
    {
        var records = new List<MentorRecord>(read.Records.Count);
        foreach (var line in read.Records)
        {
            var where = line.FileName + ":" + line.LineNumber.ToString(CultureInfo.InvariantCulture);
            var row = line.Record;
            if (row.Role != "user" && row.Role != "assistant")
                throw new MentorDataException("Unknown role '" + row.Role + "' at " + where + ".");
            if (string.IsNullOrEmpty(row.SessionId))
                throw new MentorDataException("Missing sessionId at " + where + ".");
            records.Add(new MentorRecord
            {
                Ts = AsUtc(row.TsUtc, where),
                Session = row.SessionId,
                Context = row.ContextId,
                Role = row.Role,
                Modality = row.Modality,
                Surface = row.Surface,
                Words = row.WordCount,
                Text = row.Text,
                Agent = row.Agent,
                Name = row.SessionName,
                Pid = where,
            });
        }
        return records.OrderBy(r => r.Ts).ToList();
    }

    /// <summary>
    /// The port of <c>metrics.load_events</c>: the state transitions and the turn-submitted events, sorted by
    /// (OccurredUtc, DirectorSequence). Any other event type is not the mentor's and is passed over.
    /// </summary>
    public static List<MentorEvent> LoadEvents(IEnumerable<ActivityEventEntity> rows)
    {
        var events = new List<MentorEvent>();
        foreach (var row in rows)
        {
            var kind = row.EventType;
            if (!StateEventTypes.Contains(kind) && kind != TurnEventType) continue;
            var where = "activity_events row " + row.EventId.ToString("D");
            if (string.IsNullOrEmpty(row.SessionId))
                throw new MentorDataException("Missing SessionId at " + where + ".");
            var newState = row.NewState;
            var prevState = row.PreviousState;
            if (kind == "session-exited") newState = "Exited";
            if (StateEventTypes.Contains(kind))
            {
                foreach (var state in new[] { prevState, newState })
                    if (state is not null && !ActivityStates.Contains(state))
                        throw new MentorDataException("Unknown activity state '" + state + "' at " + where + ".");
                if (newState is null)
                    throw new MentorDataException("Transition without NewState at " + where + ".");
            }
            events.Add(new MentorEvent
            {
                Ts = AsUtc(row.OccurredUtc, where),
                Seq = row.DirectorSequence,
                Session = row.SessionId,
                Type = kind,
                Prev = prevState,
                New = newState,
                SendSource = row.SendSource,
                InputOrigin = row.InputOrigin,
                Where = where,
            });
        }
        return events.OrderBy(e => e.Ts).ThenBy(e => e.Seq).ToList();
    }

    /// <summary>Give every user record its origin (<see cref="Origin.Classify"/>, the same function the
    /// metrics run), so the readers and the tools see the class the metrics counted.</summary>
    public static Origin.Counts ClassifyRecords(IReadOnlyList<MentorRecord> records, IEnumerable<MentorEvent> events)
        => Origin.Classify(records, events);
}

/// <summary>
/// The port of <c>packet.WeekData</c>: one account's sessions and prompt-log records inside one ISO week.
/// </summary>
public sealed class WeekData
{
    public string Label { get; }
    public LocalZone Zone { get; }
    public string Week { get; }
    public DateTime Start { get; }
    public DateTime End { get; }

    /// <summary>The rows started in the week, keyed by session id.</summary>
    public Dictionary<string, MentorSession> Sessions { get; }

    /// <summary>Every record in the week, in the readers' order.</summary>
    public List<MentorRecord> Records { get; }

    public Dictionary<string, List<MentorRecord>> BySession { get; }
    public Dictionary<string, MentorRecord> ByPid { get; }

    public WeekData(string label, LocalZone zone, string isoWeek, Dictionary<string, MentorSession> sessions, IReadOnlyList<MentorRecord> records)
    {
        Label = label;
        Zone = zone;
        Week = isoWeek;
        (Start, End) = MentorReaders.WeekBounds(isoWeek, zone);
        Sessions = new Dictionary<string, MentorSession>(StringComparer.Ordinal);
        foreach (var (sid, s) in sessions)
            if (Start <= s.Started && s.Started < End) Sessions[sid] = s;
        Records = records.Where(r => Start <= r.Ts && r.Ts < End).ToList();
        BySession = new Dictionary<string, List<MentorRecord>>(StringComparer.Ordinal);
        foreach (var record in Records)
        {
            if (!BySession.TryGetValue(record.Session, out var list)) BySession[record.Session] = list = new List<MentorRecord>();
            list.Add(record);
        }
        ByPid = new Dictionary<string, MentorRecord>(StringComparer.Ordinal);
        foreach (var record in Records) ByPid[record.Pid] = record;
    }

    public string Stamp(DateTime utc) => Zone.Stamp(utc);

    /// <summary>The session's records in the week, or none.</summary>
    public List<MentorRecord> RecordsOf(string sessionId) => BySession.TryGetValue(sessionId, out var list) ? list : new List<MentorRecord>();

    public List<MentorRecord> UserTurns(string sessionId) => RecordsOf(sessionId).Where(r => r.Role == "user").ToList();

    /// <summary>SessionName from session_history, else the prompt log's sessionName, else unnamed.</summary>
    public string SessionName(string sessionId)
    {
        if (Sessions.TryGetValue(sessionId, out var row) && !string.IsNullOrEmpty(row.Name)) return row.Name;
        foreach (var record in RecordsOf(sessionId))
            if (!string.IsNullOrEmpty(record.Name)) return record.Name;
        return "(unnamed session)";
    }
}
