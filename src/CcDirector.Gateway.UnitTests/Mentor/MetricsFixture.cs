using System.Globalization;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using Xunit;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>One prompt-log row as the reference's test fixture writes it (conftest.prompt), before it has a place in a file.</summary>
internal sealed class PromptRow
{
    public required DateTime Ts { get; init; }
    public required string Session { get; init; }
    public required string Text { get; init; }
    public required string Role { get; init; }
    public string? Modality { get; init; }
    public string? Surface { get; init; }
    public string? Context { get; init; }
    public string? Agent { get; init; }
    public string? FileDate { get; init; }
    public string SessionName { get; init; } = "alpha beta session";
}

/// <summary>
/// The port of the reference's metrics test fixtures (tools/mentor/tests/conftest.py): invented filler text,
/// prompt rows, session rows, state and turn events, transcripts, local Toronto minutes, and a synthetic
/// world that runs the metrics for one ISO week. The reference writes a tiny data root to disk and loads it
/// through metrics.load_world; the port builds the same records - a pid per file and line, the same word
/// count, the same classification through the events - and hands them to <see cref="MetricsWorld"/>, which
/// is what load_world produces either way. Every prompt text is invented filler.
/// </summary>
internal static class MetricsFixture
{
    public const string Week = "2026-W35";
    public const string DefaultExtractTime = "2026-09-01T12:00:00.000Z";

    public static LocalZone Zone => OriginFixture.Zone;

    /// <summary>'2026-08-25 10:30' in the account's zone as a UTC instant.</summary>
    public static DateTime Local(string text) => OriginFixture.Local(text);

    /// <summary>The reference's local() stamp moved by whole seconds.</summary>
    public static DateTime PlusSeconds(DateTime when, int seconds) => when.AddSeconds(seconds);

    /// <summary>Invented prompt text of exactly <paramref name="words"/> words.</summary>
    public static string Filler(int words) => OriginFixture.Words(words);

    public static DateTime ParseUtc(string text)
        => DateTime.Parse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);

    public static PromptRow Prompt(DateTime ts, string session, string? text = null, int words = 8, string role = "user", string? modality = null,
        string? surface = null, string? context = "c1", string? agent = "ClaudeCode", string? fileDate = null)
        => new()
        {
            Ts = ts, Session = session, Text = text ?? Filler(words), Role = role, Modality = modality, Surface = surface,
            Context = context, Agent = agent, FileDate = fileDate,
        };

    public static SessionHistoryEntity Session(string sessionId, DateTime started, DateTime? ended = null, string? ending = null, string agent = "ClaudeCode",
        long? agentTurns = null, long? peak = null, double? idle = null, long? stretches = null, string? summaryKind = "generated",
        string[]? leftUnverified = null, string[]? pullRequests = null, string[]? commits = null, string? originKind = null, string? originSurface = null,
        string? repoName = "owner/repo")
    {
        static string? AsJson(string[]? items) => items is null ? null : "[" + string.Join(",", items.Select(i => "\"" + i + "\"")) + "]";
        return new SessionHistoryEntity
        {
            SessionId = sessionId, SessionNumber = 1, DirectorId = "d1", RepoName = repoName, AgentKind = agent, SessionRole = "Standalone",
            StartedAtUtc = started, EndedAtUtc = ended, EndingKind = ending, LastSeenUtc = started, AgentTurnCount = agentTurns,
            PeakContextTokens = peak, CumulativeIdleSeconds = idle, WaitingStretchCount = stretches, SummaryKind = summaryKind,
            SessionName = "alpha beta session", RepoPath = "D:/gamma/delta", MachineName = "KAPPA-BOX", FirstPromptLine = "alpha beta gamma delta",
            SummaryText = "lambda sigma omega zulu", LeftUnverifiedJson = AsJson(leftUnverified), PullRequestsJson = AsJson(pullRequests),
            CommitsJson = AsJson(commits), OriginKind = originKind, OriginSurface = originSurface, TenantId = "t",
        };
    }

    private static long _seq;

    public static MentorEvent Event(DateTime ts, string session, string type, string? prev = null, string? newState = null, string? sendSource = null, string? inputOrigin = null)
    {
        var seq = Interlocked.Increment(ref _seq);
        return new MentorEvent
        {
            Ts = ts, Seq = seq, Session = session, Type = type, Prev = prev, New = newState, SendSource = sendSource, InputOrigin = inputOrigin,
            Where = "activity_events.jsonl:" + seq,
        };
    }

    public static MentorEvent Transition(DateTime ts, string session, string prev, string newState) => Event(ts, session, "activity-transition", prev, newState);

    public static MentorEvent Exited(DateTime ts, string session, string prev = "Working") => Event(ts, session, "session-exited", prev, "Exited");

    public static MentorEvent Turn(DateTime ts, string session, string? sendSource = null, string? inputOrigin = null)
        => Event(ts, session, "turn-submitted", sendSource: sendSource, inputOrigin: inputOrigin);

    public static MentorTranscript Transcript(DateTime ts, string raw, string? cleaned = null, bool cleanup = false)
        => new() { Ts = ts, Raw = raw, Cleaned = cleaned ?? raw, Cleanup = cleanup };

    public static SyntheticWorld MakeWorld(IEnumerable<PromptRow>? prompts = null, IEnumerable<SessionHistoryEntity>? sessions = null,
        IEnumerable<MentorEvent>? events = null, IEnumerable<MentorTranscript>? transcripts = null, string label = "one", string extractTime = DefaultExtractTime)
        => new(prompts?.ToList() ?? new List<PromptRow>(), sessions?.ToList() ?? new List<SessionHistoryEntity>(), events?.ToList() ?? new List<MentorEvent>(),
            transcripts?.ToList() ?? new List<MentorTranscript>(), label, ParseUtc(extractTime));

    // ------------------------------------------------------------------ reading the document

    public static Dictionary<string, object?> Obj(object? value) => Assert.IsType<Dictionary<string, object?>>(value);

    public static List<object?> Arr(object? value) => Assert.IsType<List<object?>>(value);

    public static long L(object? value) => Assert.IsType<long>(value);

    public static double D(object? value) => Assert.IsType<double>(value);

    public static string S(object? value) => Assert.IsType<string>(value);

    public static Dictionary<string, object?> Metric(Dictionary<string, object?> document, string group, string key) => Obj(Obj(document[group])[key]);

    public static object? Value(Dictionary<string, object?> document, string group, string key) => Metric(document, group, key)["value"];

    public static Dictionary<string, object?> Coverage(Dictionary<string, object?> document, string group, string key) => Obj(Metric(document, group, key)["coverage"]);

    public static Dictionary<string, object?> TopCoverage(Dictionary<string, object?> document) => Obj(document["coverage"]);

    /// <summary>Python's == on two JSON values, with the int/float distinction kept: the two serialize the same.</summary>
    public static void AssertSame(object? expected, object? actual) => Assert.Equal(ParityJson.Pretty(expected), ParityJson.Pretty(actual));

    public static List<object?> Strings(params string[] items) => items.Cast<object?>().ToList();

    public static List<object?> Longs(params long[] items) => items.Cast<object?>().ToList();

    public static List<string> Keys(object? dictionary) => Obj(dictionary).Keys.ToList();
}

/// <summary>A synthetic world for one account label: the rows, the extract time, the business hours, and the metrics run over them.</summary>
internal sealed class SyntheticWorld
{
    public List<PromptRow> Prompts { get; }
    public List<SessionHistoryEntity> Sessions { get; }
    public List<MentorEvent> Events { get; }
    public List<MentorTranscript> Transcripts { get; }
    public string Label { get; }
    public DateTime ExtractTime { get; }
    public BusinessHours Hours { get; set; } = new(8, 18);
    public List<string> Torn { get; } = new();
    public int Recovered { get; set; }
    public int Lost { get; set; }

    public SyntheticWorld(List<PromptRow> prompts, List<SessionHistoryEntity> sessions, List<MentorEvent> events, List<MentorTranscript> transcripts, string label, DateTime extractTime)
    {
        Prompts = prompts;
        Sessions = sessions;
        Events = events;
        Transcripts = transcripts;
        Label = label;
        ExtractTime = extractTime;
    }

    /// <summary>The records as the reference's data root would hold them: one daily file per UTC date (or the
    /// row's own file date), lines numbered in insertion order, the pid file:line, classified through the events.</summary>
    public List<MentorRecord> Records()
    {
        var byFile = new Dictionary<string, List<PromptRow>>(StringComparer.Ordinal);
        foreach (var row in Prompts)
        {
            var date = row.FileDate ?? row.Ts.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
            if (!byFile.TryGetValue(date, out var rows)) byFile[date] = rows = new List<PromptRow>();
            rows.Add(row);
        }
        var records = new List<MentorRecord>();
        foreach (var date in byFile.Keys.OrderBy(d => d, StringComparer.Ordinal))
        {
            var number = 0;
            foreach (var row in byFile[date])
            {
                number++;
                records.Add(new MentorRecord
                {
                    Ts = row.Ts, Session = row.Session, Context = row.Context, Role = row.Role, Modality = row.Modality, Surface = row.Surface,
                    Words = Origin.CountWords(row.Text), Text = row.Text, Agent = row.Agent, Name = row.SessionName,
                    Pid = "conversation-" + date + ".jsonl:" + number.ToString(CultureInfo.InvariantCulture),
                });
            }
        }
        records = records.OrderBy(r => r.Ts).ToList();
        Origin.Classify(records, Events);
        return records;
    }

    /// <summary>The manifest's extract times as the reference derives them: the database sources at the extract
    /// time, the prompt log at the end of the last complete UTC day, which is the extract day's midnight.</summary>
    public SourceEnds SourceEnds()
    {
        var promptLog = DateTime.SpecifyKind(ExtractTime.Date, DateTimeKind.Utc);
        return new SourceEnds(promptLog, ExtractTime, ExtractTime, ExtractTime);
    }

    public MetricsWorld Build()
    {
        var sessions = Sessions.Select(e => MetricsSession.FromRow(MentorSessionRow.Parse(e))).ToList();
        return new MetricsWorld(Label, MetricsFixture.Zone, Records(), Torn, Recovered, Lost, sessions, Events, Transcripts, SourceEnds());
    }

    public Dictionary<string, object?> Run(string week = MetricsFixture.Week)
        => Metrics.BuildMetrics(Build(), week, "2026-09-01T12:00:00Z", Hours);
}
