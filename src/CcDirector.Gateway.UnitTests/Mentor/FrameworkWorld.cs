using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using CcDirector.Gateway.Prompts;
using static CcDirector.Gateway.Tests.Mentor.MetricsFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's <c>tests/framework_world.py</c> (over <c>test_mentor_tools.py</c>'s rows): the
/// synthetic world the report-framework tests share. Account "one" in 2026-W35: a session started before the week,
/// a session with two stamped prompts in one minute, a ledger-origin prompt, an envelope, an agent-tool framing, an
/// unresolved record, an assistant reply and a two-word prompt; an open session started by another session with no
/// repository name and a prompt carrying a smart quote and an accented letter; a session with no session_history
/// row; a row-only session with no human prompt; two sessions sharing a name - with one more prompt so the shared
/// name has TWO sessions with a prompt at one minute (12:00) and one session alone at another (13:00) - and WITHOUT
/// the id8 twin (session I08), which a topic line cannot name twice. Eleven human prompts, so the level words are
/// judged. Account "two" is a second tenant beside it whose text must never reach account one's answers. Every
/// prompt is invented filler.
///
/// The reference writes a data root and starts a run; the port restores the same rows INTO the Gateway's own stores -
/// the daily files under a temporary prompt-log root (account one in the local root, account two in its minted
/// partition), the session rows and the one turn event through a SQLite Gateway database, the synthetic corpus as
/// the turn-log root - and builds the store over them, exactly as the service will. Built once for the class; each
/// test takes its own run folder and surface (<see cref="NewRun"/>), as the reference's per-test fixture does.
/// </summary>
public sealed class FrameworkWorld : IDisposable
{
    public const string HumanCountValue = "11";
    public const long HumanCount = 11;
    public const string Week = MetricsFixture.Week;

    public const string A01 = "a0100000-0000-4000-8000-000000000a01";
    public const string B02 = "b0200000-0000-4000-8000-000000000b02";
    public const string C03 = "c0300000-0000-4000-8000-000000000c03";
    public const string D00 = "d0000000-0000-4000-8000-000000000d00";
    public const string E04 = "e0400000-0000-4000-8000-000000000e04";
    public const string F05 = "f0500000-0000-4000-8000-000000000f05";
    public const string G06 = "a0600000-0000-4000-8000-000000000a06";
    public const string H07 = "b0700000-0000-4000-8000-000000000b07";
    public const string I08 = "b0700000-0000-4000-8000-000000000b08";
    public const string Old = "00000000-0000-4000-8000-000000000old";
    public const string T01 = "e0100000-0000-4000-8000-000000000e01";

    public const string Alpha = "alpha session name";
    public const string Bravo = "bravo session name";
    public const string Early = "early session name";
    public const string Charlie = "charlie session name";
    public const string Shared = "shared session name";
    public const string Hotel = "hotel session name";

    public static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal)
    {
        [D00] = Early, [A01] = Alpha, [B02] = Bravo, [C03] = Charlie, [E04] = "echo session name", [F05] = Shared,
        [G06] = Shared, [H07] = Hotel, [I08] = "india session name", [Old] = "old session name",
    };

    public const string AssistantToken = "QUOKKAFILL";
    public const string EnvelopeToken = "ENVELOPEZULU";
    public const string AgentToolToken = "TASKNOTEYANKEE";
    public const string UnresolvedToken = "UNRESOLVEDSIGMA";
    public const string OtherAccountToken = "ZZTWO";
    public const string OtherTenantRow = "OTHERTENANTROW";

    public const string StampedText = "alpha beta gamma delta stamped one";
    public const string SameMinuteText = "second prompt in the same minute echo foxtrot";
    public const string LedgerText = "ledger origin prompt kappa lambda";
    public const string TwoWordText = "zulu yankee";
    public const string EarlyText = "early session prompt quebec romeo";
    public const string NoRowText = "no row session prompt yankee juliet";
    public const string UnicodeText = "caf\u00e9 \u201cquoted\u201d tango";
    public const string SharedTextF = "shared name prompt one mike november";
    public const string SharedTextG = "shared name prompt two oscar papa";
    public const string PrefixTextH = "prefix twin prompt sierra tango";
    public const string PrefixTextI = "prefix twin prompt uniform victor";
    public const string ExtraSharedText = "shared name prompt extra golf hotel";

    private const string MachineDir = "KAPPA-BOX-b0082663";

    /// <summary>Account one is the local tenant (the prompt-log root itself); account two is a minted tenant beside it.</summary>
    public static readonly TenantId One = TenantId.Local;
    public static readonly TenantId Two = new("3c1f1f15-9f84-4230-8000-000000000002");

    public string Root { get; }
    public GatewayDatabase Db { get; }
    public MentorStore Store { get; }
    private int _runs;

    public FrameworkWorld()
    {
        Root = Path.Combine(Path.GetTempPath(), "mentor-framework-" + Guid.NewGuid().ToString("N"));
        var promptLogRoot = Path.Combine(Root, "prompt-log");
        Directory.CreateDirectory(promptLogRoot);
        var promptLog = new GatewayPromptLog(promptLogRoot);
        Directory.CreateDirectory(promptLog.DirectoryFor(Two));

        var (sessions, prompts, events) = BuildRows();
        sessions = sessions.Where(row => row.SessionId != I08).ToList();
        prompts = prompts.Where(record => record.SessionId != I08).ToList();
        prompts.Add(Rec(G06, "2026-08-28 12:00", ExtraSharedText, modality: "typed", surface: "desktop"));
        WritePrompts(promptLog, One, prompts);
        var (otherSessions, otherPrompts) = OtherAccountRows();
        WritePrompts(promptLog, Two, otherPrompts);

        Db = new GatewayDatabase(new SingleTenantContext(), Path.Combine(Root, "gateway.db"));
        using (var ctx = Db.CreateContext(One))
        {
            foreach (var row in sessions) { row.TenantId = One.Value; ctx.SessionHistory.Add(row); }
            foreach (var row in events) { row.TenantId = One.Value; ctx.ActivityEvents.Add(row); }
            ctx.SaveChanges();
        }
        using (var ctx = Db.CreateContext(Two))
        {
            foreach (var row in otherSessions) { row.TenantId = Two.Value; ctx.SessionHistory.Add(row); }
            ctx.SaveChanges();
        }
        var corpus = BuildCorpus(Path.Combine(Root, "corpus"));
        var extract = ParseUtc(DefaultExtractTime);
        var sourceEnds = new SourceEnds(DateTime.SpecifyKind(extract.Date, DateTimeKind.Utc), extract, extract, extract);
        Store = new MentorStore(One, promptLog, () => Db.CreateContext(One), corpus, Zone, new BusinessHours(8, 18), sourceEnds, Week, "one");
    }

    /// <summary>A fresh run folder with its own tool log and surface over the shared store - the reference's
    /// per-test <c>start_run</c>. The folder is empty apart from what the surface and the assembler write.</summary>
    public Run NewRun()
    {
        var number = Interlocked.Increment(ref _runs);
        var runDir = Path.Combine(Root, "run-" + number.ToString(CultureInfo.InvariantCulture));
        Directory.CreateDirectory(runDir);
        return new Run(runDir, Store, new ToolSurface(Store, new ToolLog(Path.Combine(runDir, ToolLog.FileName))));
    }

    public sealed class Run
    {
        public string RunDir { get; }
        public MentorStore Store { get; }
        public ToolSurface Surface { get; }

        public Run(string runDir, MentorStore store, ToolSurface surface)
        {
            RunDir = runDir;
            Store = store;
            Surface = surface;
        }
    }

    public void Dispose()
    {
        Db.Dispose();
        try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { /* per-run temp */ }
    }

    // ------------------------------------------------------------------ the rows

    public static PromptRecord Rec(string sid, string stamp, string text, string role = "user", string? modality = null, string? surface = null)
        => new()
        {
            TsUtc = Local(stamp), Machine = "KAPPA-BOX", SessionId = sid, ContextId = "c1", SessionName = Names[sid], RepoPath = "D:/gamma/delta",
            Agent = "ClaudeCode", Role = role, Modality = modality, Surface = surface, TimestampFromAgent = true,
            CharCount = text.EnumerateRunes().Count(), WordCount = PyTextForTests.CountWords(text), Text = text,
        };

    public static SessionHistoryEntity Row(string sid, string started, DateTime? ended = null, string? ending = null, string agent = "ClaudeCode",
        long? agentTurns = null, long? peak = null, string? originKind = null, string? repoName = "owner/repo", string[]? commits = null,
        string[]? pullRequests = null, string[]? leftUnverified = null)
    {
        var entry = Session(sid, Local(started), ended, ending, agent, agentTurns, peak, originKind: originKind, repoName: repoName,
            commits: commits, pullRequests: pullRequests, leftUnverified: leftUnverified);
        entry.SessionName = Names[sid];
        return entry;
    }

    /// <summary>(sessions, prompts, events) for account one.</summary>
    public static (List<SessionHistoryEntity> Sessions, List<PromptRecord> Prompts, List<ActivityEventEntity> Events) BuildRows()
    {
        var sessions = new List<SessionHistoryEntity>
        {
            Row(Old, "2026-07-01 09:00", ended: Local("2026-07-01 10:00"), ending: "finished", originKind: "human"),
            Row(D00, "2026-08-20 09:00", ended: Local("2026-08-27 10:00"), ending: "finished", agentTurns: 3, originKind: "human", repoName: "repo-early"),
            Row(A01, "2026-08-24 09:00", ended: Local("2026-08-24 12:00"), ending: "finished", agentTurns: 9, peak: 123456, originKind: "human",
                repoName: "repo-alpha", commits: new[] { "c1" }, pullRequests: new[] { "pr1" }, leftUnverified: new[] { "u1", "u2" }),
            Row(B02, "2026-08-25 10:00", ended: null, ending: null, agent: "RawCli", agentTurns: 2, originKind: "agent", repoName: null),
            Row(E04, "2026-08-28 08:00", ended: Local("2026-08-28 09:00"), ending: "finished", agentTurns: 1, originKind: "agent", repoName: "repo-echo"),
            Row(F05, "2026-08-28 11:00", ended: Local("2026-08-28 12:30"), ending: "finished", originKind: "human"),
            Row(G06, "2026-08-28 12:00", ended: Local("2026-08-28 13:30"), ending: "finished", originKind: "human"),
            Row(H07, "2026-08-29 09:00", ended: Local("2026-08-29 09:30"), ending: "finished", originKind: "human"),
            Row(I08, "2026-08-29 10:00", ended: Local("2026-08-29 10:30"), ending: "finished", originKind: "human"),
        };
        sessions[2].TurnCount = 4;
        sessions[2].WhatWasBuiltJson = "[\"built one\"]";
        sessions[2].BranchesJson = "[\"branch-one\"]";
        var prompts = new List<PromptRecord>
        {
            Rec(Old, "2026-07-01 09:05", "old prompt whiskey xray", modality: "typed", surface: "desktop"),
            Rec(D00, "2026-08-27 09:30", EarlyText, modality: "typed", surface: "desktop"),
            Rec(A01, "2026-08-24 09:05", StampedText, modality: "typed", surface: "desktop"),
            Rec(A01, "2026-08-24 09:05", SameMinuteText, modality: "typed", surface: "desktop"),
            Rec(A01, "2026-08-24 09:06", AssistantToken + " reply text omega", role: "assistant"),
            Rec(A01, "2026-08-24 09:15", "Message [message from zulu session (KAPPA-BOX), id m1] " + EnvelopeToken + " body"),
            Rec(A01, "2026-08-24 09:20", "<task-notification>" + AgentToolToken + "</task-notification>"),
            Rec(A01, "2026-08-24 09:25", UnresolvedToken + " sigma omega"),
            Rec(A01, "2026-08-24 09:30", TwoWordText, modality: "typed", surface: "desktop"),
            Rec(B02, "2026-08-25 10:05", UnicodeText, modality: "voice", surface: "cockpit"),
            Rec(C03, "2026-08-26 11:00", NoRowText, modality: "typed", surface: "desktop"),
            Rec(F05, "2026-08-28 12:00", SharedTextF, modality: "typed", surface: "desktop"),
            Rec(G06, "2026-08-28 13:00", SharedTextG, modality: "typed", surface: "desktop"),
            Rec(H07, "2026-08-29 09:05", PrefixTextH, modality: "typed", surface: "desktop"),
            Rec(I08, "2026-08-29 10:05", PrefixTextI, modality: "typed", surface: "desktop"),
        };
        var ledger = Rec(A01, "2026-08-24 09:10", LedgerText);
        prompts.Add(ledger);
        var events = new List<ActivityEventEntity> { TurnEvent(ledger.TsUtc, A01, sendSource: "UserInput", inputOrigin: "voice/phone") };
        return (sessions, prompts, events);
    }

    private static long _eventSeq;

    /// <summary>The reference's <c>conftest.turn</c> as an activity_events row: a turn-submitted event with its send source and input origin.</summary>
    public static ActivityEventEntity TurnEvent(DateTime ts, string sessionId, string? sendSource = null, string? inputOrigin = null)
    {
        var seq = Interlocked.Increment(ref _eventSeq);
        return new ActivityEventEntity
        {
            EventId = Guid.NewGuid(), DirectorSequence = seq, OccurredUtc = ts, RecordedUtc = ts, DirectorId = "d1", SessionId = sessionId,
            Machine = "KAPPA-BOX", AgentKind = "ClaudeCode", ContextId = "c1", EventType = "turn-submitted", Cause = "quiet-threshold",
            InputOrigin = inputOrigin, SendSource = sendSource, DetectorMode = "byte", DetectorVersion = "shadow-v1",
        };
    }

    public static (List<SessionHistoryEntity> Sessions, List<PromptRecord> Prompts) OtherAccountRows()
    {
        var entry = Session(T01, Local("2026-08-25 10:00"), ended: Local("2026-08-25 11:00"), ending: "finished", originKind: "human",
            repoName: "repo-two", commits: new[] { OtherAccountToken + " commit" });
        entry.SessionName = OtherAccountToken + " session";
        var record = new PromptRecord
        {
            TsUtc = Local("2026-08-25 10:05"), Machine = "KAPPA-BOX", SessionId = T01, ContextId = "c1", SessionName = OtherAccountToken + " session",
            RepoPath = "D:/gamma/delta", Agent = "ClaudeCode", Role = "user", Modality = "typed", Surface = "desktop", TimestampFromAgent = true,
            CharCount = (OtherAccountToken + " alpha beta p0").Length, WordCount = 4, Text = OtherAccountToken + " alpha beta p0",
        };
        return (new List<SessionHistoryEntity> { entry }, new List<PromptRecord> { record });
    }

    /// <summary>The records into the tenant's daily files, one per UTC date, compact JSON as the Gateway writes them.</summary>
    private static void WritePrompts(GatewayPromptLog promptLog, TenantId tenant, IEnumerable<PromptRecord> prompts)
    {
        var byFile = new Dictionary<DateTime, List<string>>();
        foreach (var record in prompts)
        {
            var day = record.TsUtc.Date;
            if (!byFile.TryGetValue(day, out var lines)) byFile[day] = lines = new List<string>();
            lines.Add(JsonSerializer.Serialize(record));
        }
        foreach (var (day, lines) in byFile)
            File.WriteAllLines(promptLog.FileFor(tenant, DateTime.SpecifyKind(day, DateTimeKind.Utc)), lines, new UTF8Encoding(false));
    }

    // ------------------------------------------------------------------ the synthetic corpus

    private static Dictionary<string, object?> CorpusRecord(string sid, string name, string capturedUtc, string stateAfter, List<object?> rows, List<object?> messages)
        => new()
        {
            ["schema_version"] = 1L,
            ["record_id"] = "rec-" + capturedUtc,
            ["captured_at_utc"] = capturedUtc,
            ["at_a_glance"] = new Dictionary<string, object?>
            {
                ["session_id"] = sid, ["session_name"] = name, ["computer"] = "KAPPA-BOX", ["agent"] = "RawCli",
                ["repository"] = "D:\\gamma\\delta", ["director_id"] = "d1", ["account"] = "tenant-one",
            },
            ["moment"] = new Dictionary<string, object?> { ["activity_state_before"] = "Working", ["activity_state_after"] = stateAfter },
            ["session"] = new Dictionary<string, object?> { ["SessionId"] = sid, ["Name"] = name },
            ["terminal"] = new Dictionary<string, object?> { ["rows"] = rows, ["row_count"] = (long)rows.Count },
            ["conversation"] = new Dictionary<string, object?> { ["messages"] = messages },
            ["observed"] = new Dictionary<string, object?>(),
            ["verdict"] = null,
            ["gaps"] = new List<object?>(),
        };

    private static Dictionary<string, object?> Message(string role, string stampUtc, string text)
        => new()
        {
            ["Role"] = role, ["Timestamp"] = stampUtc,
            ["Parts"] = new List<object?> { new Dictionary<string, object?> { ["Kind"] = "Text", ["Text"] = text, ["ToolName"] = null, ["ToolId"] = null } },
        };

    private static void WriteBundle(string path, IEnumerable<Dictionary<string, object?>> records)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var file = File.Create(path);
        using var gzip = new GZipStream(file, CompressionLevel.Fastest);
        using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        foreach (var record in records) writer.Write(ParityJson.Compact(record) + "\n");
    }

    /// <summary>UTC day 2026-08-25: two records for B02 (10:07 and 10:50 local) under account one's tenant folder, and one
    /// record for another tenant beside it at 14:06Z - NEARER the moment the reference's tests ask about, so a read
    /// that ignored the tenant folder would answer it.</summary>
    public static string BuildCorpus(string root)
    {
        var rows = new List<object?> { "" };
        for (var i = 1; i <= 45; i++) rows.Add("row " + i);
        rows.Add(""); rows.Add("");
        var messages = new List<object?>();
        for (var i = 0; i < 10; i++)
            messages.Add(Message("User", "2026-08-25T14:0" + i + ":00+00:00", "message m" + i + " " + (i == 4 ? new string('x', 500) : "")));
        var near = CorpusRecord(B02, Names[B02], "2026-08-25T14:07:00.1234567Z", "WaitingForInput", rows, messages);
        var far = CorpusRecord(B02, Names[B02], "2026-08-25T14:50:00.0000000Z", "Working", rows.Take(3).ToList(), messages.Take(2).ToList());
        WriteBundle(Path.Combine(root, "2026-08-25", One.Value + "-3c1f1f15", MachineDir, "w1.jsonl.gz"), new[] { near, far });
        var other = CorpusRecord(B02, OtherTenantRow, "2026-08-25T14:06:00.0000000Z", "Working", new List<object?> { OtherTenantRow },
            new List<object?> { Message("User", "2026-08-25T14:06:00+00:00", OtherTenantRow) });
        WriteBundle(Path.Combine(root, "2026-08-25", Two.Value + "-9f840230", MachineDir, "w2.jsonl.gz"), new[] { other });
        return root;
    }

    // ------------------------------------------------------------------ the slots

    public static string Cite(string name, string at, string? fragment = null)
    {
        var text = name + ", " + at;
        if (fragment is not null) text += " (\"" + fragment + "\")";
        return text;
    }

    private static Dictionary<string, object?> Recommendation(string title, string saw, string cost, string tryText)
        => new() { ["title"] = title, ["saw"] = saw, ["cost"] = cost, ["try"] = tryText };

    /// <summary>A slots.json that every validator accepts on this world (the reference's <c>good_slots</c>), a fresh object each call.</summary>
    public static Dictionary<string, object?> GoodSlots()
    {
        var observation = Cite(Alpha, "2026-08-24 09:10", "ledger origin prompt kappa lambda") + " named the target; "
            + Cite(Hotel, "2026-08-29 09:05", "prefix twin prompt sierra tango") + " did not.";
        const string step = "Name the file or the number you mean.";
        var levels = new[] { "sometimes", "mostly", "rarely", "sometimes", "mostly", "rarely" };
        var prompting = new Dictionary<string, object?>();
        var keys = new[] { "specific_target", "check_agent_can_run", "one_task_per_prompt", "corrections_carry_reason", "not_re_explaining", "session_hygiene" };
        for (var i = 0; i < keys.Length; i++)
            prompting[keys[i]] = new Dictionary<string, object?> { ["level"] = levels[i], ["observation"] = observation, ["step"] = step };
        return new Dictionary<string, object?>
        {
            ["recommendations"] = new List<object?>
            {
                Recommendation("Send one ask at a time",
                    "Two asks landed in one minute: " + Cite(Alpha, "2026-08-24 09:05", "alpha beta gamma delta stamped one") + " and "
                        + Cite(Alpha, "2026-08-24 09:05", "second prompt in the same minute") + ".",
                    "The second ask waits behind the first, as at " + Cite(Alpha, "2026-08-24 09:05") + ". You read both answers at once.",
                    "Send one prompt and wait for its answer before the next."),
                Recommendation("Close a session that has finished",
                    Cite(Early, "2026-08-27 09:30", "early session prompt quebec romeo") + " came a week after that session started, and "
                        + Cite(Charlie, "2026-08-26 11:00") + " ran with no session row.",
                    "An old session carries context you no longer need, as " + Cite(Early, "2026-08-27 09:30") + " shows.",
                    "Start a fresh session for a new task."),
                Recommendation("Name the shared sessions apart",
                    "Two sessions carry one name: " + Cite(Shared, "2026-08-28 13:00", "shared name prompt two oscar papa") + " and "
                        + Cite(Hotel, "2026-08-29 09:05", "prefix twin prompt sierra tango") + ".",
                    "A shared name hides which session did the work, as at " + Cite(Shared, "2026-08-28 13:00") + ".",
                    "Give each session its own name when you start it."),
            },
            ["went_well"] = new Dictionary<string, object?>
            {
                ["text"] = "You kept a prompt short when short was enough: " + Cite(Alpha, "2026-08-24 09:30", "zulu yankee") + ".",
            },
            ["prompting"] = prompting,
        };
    }

    public static Dictionary<string, object?> Rec(Dictionary<string, object?> slots, int position) => (Dictionary<string, object?>)((List<object?>)slots["recommendations"]!)[position]!;

    public static Dictionary<string, object?> WentWell(Dictionary<string, object?> slots) => (Dictionary<string, object?>)slots["went_well"]!;

    public static Dictionary<string, object?> Prompting(Dictionary<string, object?> slots, string key) => (Dictionary<string, object?>)((Dictionary<string, object?>)slots["prompting"]!)[key]!;

    /// <summary>slots.json as the reference's <c>write_slots</c> writes it: JSON, indent 2, ASCII.</summary>
    public static void WriteSlots(string runDir, object? slots)
        => File.WriteAllText(Path.Combine(runDir, Slots.SlotsFile), ParityJson.PrettyOrdered(slots), new UTF8Encoding(false));

    /// <summary>Fetch every citation and fragment of the slots through the surface first, as a writer would (the reference's
    /// <c>fetch_all</c>): a unique name is cited by name; a shared one by each id8.</summary>
    public static void FetchAll(ToolSurface surface, object? slots)
    {
        var index = surface.SessionIndex();
        var ids = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var row in index)
        {
            var name = (string)row["name"]!;
            if (!ids.TryGetValue(name, out var list)) ids[name] = list = new List<string>();
            list.Add((string)row["id8"]!);
        }
        foreach (var (_, text) in LogCheck.WrittenFields(slots))
        {
            foreach (System.Text.RegularExpressions.Match match in ReportCheck.MinuteRe.Matches(text))
            {
                var at = match.Value;
                var before = text.Substring(0, match.Index - 2);
                var name = ids.Keys.Where(n => before.EndsWith(n, StringComparison.Ordinal)).OrderByDescending(n => n.Length).First();
                foreach (var reference in ids[name].Count == 1 ? new[] { name } : ids[name].ToArray())
                {
                    try
                    {
                        surface.Cite(reference, at);
                    }
                    catch (ToolError)
                    {
                        continue;
                    }
                    var tail = text.Substring(match.Index + match.Length);
                    var fragment = ReportCheck.FragmentRe.Match(tail);
                    if (fragment.Success) surface.VerifyQuote(reference, at, fragment.Groups["fragment"].Value);
                }
            }
        }
    }
}
