using System.Globalization;

namespace CcDirector.Gateway.Mentor;

/// <summary>
/// One session row as the reference's <c>metrics.load_sessions</c> keeps it: the non-content columns the
/// metrics read, with the three JSON array columns parsed. <see cref="OriginKind"/> and
/// <see cref="OriginSurface"/> stay null for a NULL column, as the reference keeps them - the counting
/// rules map null to <c>unknown</c> where they say so.
/// </summary>
public sealed class MetricsSession
{
    public required string Id { get; init; }
    public string? Agent { get; init; }
    public required DateTime Started { get; init; }
    public DateTime? Ended { get; init; }
    public string? Ending { get; init; }
    public string? OriginKind { get; init; }
    public string? OriginSurface { get; init; }
    public string? RepoName { get; init; }
    public long? AgentTurns { get; init; }
    public long? PeakContext { get; init; }
    public double? IdleSeconds { get; init; }
    public long? WaitingStretches { get; init; }
    public string? SummaryKind { get; init; }
    public List<object?>? LeftUnverified { get; init; }
    public List<object?>? PullRequests { get; init; }
    public List<object?>? Commits { get; init; }

    /// <summary>The reference's <c>load_sessions</c> rules over one raw row: every check it makes, stopping the
    /// run naming the row, and nothing defaulted.</summary>
    public static MetricsSession FromRow(MentorSessionRow row)
    {
        var entity = row.Entity;
        if (string.IsNullOrEmpty(entity.SessionId))
            throw new MentorDataException("Missing SessionId on a session_history row.");
        var where = "session_history row " + entity.SessionId;
        if (entity.EndingKind is not null && !MentorReaders.EndingKinds.Contains(entity.EndingKind))
            throw new MentorDataException("Unknown EndingKind '" + entity.EndingKind + "' at " + where + ".");
        var started = MentorReaders.AsUtc(entity.StartedAtUtc, where);
        DateTime? ended = entity.EndedAtUtc is null ? null : MentorReaders.AsUtc(entity.EndedAtUtc.Value, where);
        if (ended is not null && ended.Value < started)
            throw new MentorDataException("EndedAtUtc before StartedAtUtc at " + where + ".");
        if (entity.OriginKind is not null && !Metrics.OriginKinds.Contains(entity.OriginKind))
            throw new MentorDataException("Unknown OriginKind '" + entity.OriginKind + "' at " + where + "; SessionOrigin.cs knows "
                + string.Join(", ", Metrics.OriginKinds) + ".");
        if (entity.OriginSurface is not null && !Metrics.OriginSurfaces.Contains(entity.OriginSurface))
            throw new MentorDataException("Unknown OriginSurface '" + entity.OriginSurface + "' at " + where + "; SessionOrigin.cs knows "
                + string.Join(", ", Metrics.OriginSurfaces) + ".");
        return new MetricsSession
        {
            Id = entity.SessionId,
            Agent = entity.AgentKind,
            Started = started,
            Ended = ended,
            Ending = entity.EndingKind,
            OriginKind = entity.OriginKind,
            OriginSurface = entity.OriginSurface,
            RepoName = MentorReaders.RepoNameOf(entity.RepoName, where),
            AgentTurns = entity.AgentTurnCount,
            PeakContext = entity.PeakContextTokens,
            IdleSeconds = entity.CumulativeIdleSeconds,
            WaitingStretches = entity.WaitingStretchCount,
            SummaryKind = entity.SummaryKind,
            LeftUnverified = row.LeftUnverified,
            PullRequests = row.PullRequests,
            Commits = row.Commits,
        };
    }
}

/// <summary>
/// The port of the reference's <c>metrics.World</c>: one account's data, parsed once, as the metrics read
/// it - the prompt-log records (both roles, classified by origin, in timestamp order), the torn-line
/// statistics, the session rows, the state and turn events in (timestamp, sequence) order, the transcripts,
/// the extract time per source and the earliest record per source. The reference loads it from a raw
/// snapshot (<c>load_world</c>); the port is built by <see cref="MentorStore.World"/> from the Gateway's own
/// readers, and by the tests from rows they make. Content columns are never kept.
/// </summary>
public sealed class MetricsWorld
{
    public string Label { get; }
    public LocalZone Zone { get; }
    public IReadOnlyList<MentorRecord> Prompts { get; }
    public IReadOnlyList<string> Torn { get; }
    public int Recovered { get; }
    public int Lost { get; }
    public IReadOnlyList<MetricsSession> Sessions { get; }
    public IReadOnlyList<MentorEvent> Events { get; }
    public IReadOnlyList<MentorTranscript> Transcripts { get; }

    /// <summary>The session_history cutoff: the clock for sessions still open.</summary>
    public DateTime ExtractTime { get; }

    public SourceEnds SourceEnd { get; }
    private readonly Dictionary<string, DateTime?> _sourceStart;

    public MetricsWorld(string label, LocalZone zone, IReadOnlyList<MentorRecord> prompts, IReadOnlyList<string> torn, int recovered, int lost,
        IReadOnlyList<MetricsSession> sessions, IReadOnlyList<MentorEvent> events, IReadOnlyList<MentorTranscript> transcripts, SourceEnds sourceEnds)
    {
        ArgumentNullException.ThrowIfNull(zone);
        ArgumentNullException.ThrowIfNull(sourceEnds);
        Label = label;
        Zone = zone;
        // The reference sorts its prompts by timestamp and its events by (timestamp, sequence), both stably.
        Prompts = prompts.OrderBy(p => p.Ts).ToList();
        foreach (var p in Prompts)
        {
            if (p.Ts.Kind != DateTimeKind.Utc)
                throw new MentorDataException("A prompt record of kind " + p.Ts.Kind + " reached the metrics world at " + p.Pid + "; the readers answer UTC.");
            if (p.Role == "user" && p.Origin is null)
                throw new MentorDataException("The user record at " + p.Pid + " has no origin; classify the records (Origin.Classify) before building the world.");
        }
        Torn = torn;
        Recovered = recovered;
        Lost = lost;
        Sessions = sessions;
        Events = events.OrderBy(e => e.Ts).ThenBy(e => e.Seq).ToList();
        Transcripts = transcripts;
        SourceEnd = sourceEnds;
        ExtractTime = sourceEnds.SessionHistory;
        _sourceStart = new Dictionary<string, DateTime?>(StringComparer.Ordinal)
        {
            ["prompt-log"] = Prompts.Count == 0 ? null : Prompts.Min(p => p.Ts),
            ["session_history"] = Sessions.Count == 0 ? null : Sessions.Min(s => s.Started),
            ["activity_events"] = Events.Count == 0 ? null : Events.Min(e => e.Ts),
            ["dictation_transcripts"] = Transcripts.Count == 0 ? null : Transcripts.Min(t => t.Ts),
        };
    }

    /// <summary>The source's earliest record for this account, or null when it has none.</summary>
    public DateTime? SourceStart(string source)
        => _sourceStart.TryGetValue(source, out var start) ? start : throw new MentorDataException("No such source '" + source + "'.");

    public DateTime Local(DateTime utc) => Zone.ToLocal(utc);
}

/// <summary>
/// The port of the reference's <c>metrics.Week</c>: the slice of a <see cref="MetricsWorld"/> that belongs
/// to one ISO week in the account's zone - the sessions started in it, the user prompts in it, the human
/// prompts among them (the ones origin classified as the developer's own), the transcripts and the events
/// in it - and the coverage rules the baseline reads.
/// </summary>
public sealed class MetricsWeek
{
    public MetricsWorld World { get; }
    public string Label { get; }
    public BusinessHours Hours { get; }
    public DateTime Start { get; }
    public DateTime End { get; }

    /// <summary>The week's end or the extract time, whichever is earlier.</summary>
    public DateTime Horizon { get; }

    /// <summary>The eight local-midnight bounds of the week's seven days, as UTC instants.</summary>
    public IReadOnlyList<DateTime> Days { get; }

    public IReadOnlyList<MetricsSession> Sessions { get; }
    public IReadOnlyList<MentorRecord> UserPrompts { get; }
    public IReadOnlyList<MentorRecord> HumanPrompts { get; }
    public IReadOnlyList<MentorTranscript> Transcripts { get; }
    public IReadOnlyList<MentorEvent> EventsInWeek { get; }

    public MetricsWeek(MetricsWorld world, string isoWeek, BusinessHours hours)
    {
        ArgumentNullException.ThrowIfNull(world);
        World = world;
        Label = isoWeek;
        Hours = hours.Validated();
        (Start, End) = MentorReaders.WeekBounds(isoWeek, world.Zone);
        Horizon = End < world.ExtractTime ? End : world.ExtractTime;
        var (year, week) = MentorReaders.ParseIsoWeek(isoWeek);
        var monday = ISOWeek.ToDateTime(year, week, DayOfWeek.Monday);
        // The reference adds whole days to an aware local midnight, which is wall-clock arithmetic: each
        // bound is that day's local midnight whatever the offset did in between.
        Days = Enumerable.Range(0, 8).Select(i => world.Zone.ToUtc(monday.AddDays(i))).ToList();
        Sessions = world.Sessions.Where(s => Start <= s.Started && s.Started < End).ToList();
        UserPrompts = world.Prompts.Where(p => p.Role == "user" && Start <= p.Ts && p.Ts < End).ToList();
        HumanPrompts = UserPrompts.Where(p => p.Origin == "human").ToList();
        Transcripts = world.Transcripts.Where(t => Start <= t.Ts && t.Ts < End).ToList();
        EventsInWeek = world.Events.Where(e => Start <= e.Ts && e.Ts < End).ToList();
    }

    public DateTime Local(DateTime utc) => World.Local(utc);

    /// <summary>True when the source's earliest record is at or before this week's start and its extract
    /// time is at or after this week's end: the source saw the whole week.</summary>
    public bool SourceCovers(string source)
    {
        var start = World.SourceStart(source);
        var end = World.SourceEnd.For(source);
        if (start is null) return false;
        return start.Value <= Start && end >= End;
    }

    public bool CoveredBy(IEnumerable<string> sources) => sources.All(SourceCovers);

    public List<object?> SessionAgents() => Sessions.Where(s => !string.IsNullOrEmpty(s.Agent)).Select(s => s.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).Cast<object?>().ToList();

    public List<object?> PromptAgents() => UserPrompts.Where(p => !string.IsNullOrEmpty(p.Agent)).Select(p => p.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).Cast<object?>().ToList();

    public HashSet<string> PromptSessions() => UserPrompts.Select(p => p.Session).ToHashSet(StringComparer.Ordinal);

    public Dictionary<string, object?> Coverage(List<object?> agents, long covered) => new()
    {
        ["agents"] = agents,
        ["sessions_covered"] = covered,
        ["sessions_in_week"] = (long)Sessions.Count,
    };

    public Dictionary<string, object?> SessionCov(IReadOnlyList<MetricsSession> rows)
        => Coverage(rows.Where(s => !string.IsNullOrEmpty(s.Agent)).Select(s => s.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).Cast<object?>().ToList(), rows.Count);

    public Dictionary<string, object?> PromptCov(IReadOnlyList<MentorRecord> prompts)
        => Coverage(prompts.Where(p => !string.IsNullOrEmpty(p.Agent)).Select(p => p.Agent!).Distinct().OrderBy(a => a, StringComparer.Ordinal).Cast<object?>().ToList(),
            prompts.Select(p => p.Session).Distinct(StringComparer.Ordinal).Count());
}
