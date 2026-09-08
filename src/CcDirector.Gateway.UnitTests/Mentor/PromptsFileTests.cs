using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Mentor;
using Xunit;
using static CcDirector.Gateway.Tests.Mentor.OriginFixture;

namespace CcDirector.Gateway.Tests.Mentor;

/// <summary>
/// The port of the reference's test_prompts_file.py on the same synthetic world: four sessions with human
/// prompts in 2026-W35 - one started before the week, one with a stamped prompt, a ledger-origin prompt, an
/// envelope, an agent-tool framing, an unresolved record and an assistant reply, one open session started by
/// another session with no repository name and a prompt carrying a smart quote and an accented letter, and
/// one session with no session_history row. The reference renders through metrics.json; the port renders
/// through the readers and the same WeekData, and the expected lines are the reference test's.
/// </summary>
[Collection(OriginCollection.Name)]
public sealed class PromptsFileTests
{
    private const string Week = "2026-W35";
    private const string AssistantToken = "QUOKKAFILL";
    private const string EnvelopeToken = "ENVELOPEZULU";
    private const string AgentToolToken = "TASKNOTEYANKEE";
    private const string UnresolvedToken = "UNRESOLVEDSIGMA";
    private const string StampedText = "alpha beta gamma delta stamped one";
    private const string LedgerText = "ledger origin prompt kappa lambda";
    private const string EarlyText = "early session prompt quebec romeo";
    private const string NoRowText = "no row session prompt yankee juliet";
    private static readonly string UnicodeText = "caf\u00e9 \u201cquoted\u201d tango";
    private const string UnicodeAscii = "cafe \"quoted\" tango";

    private static readonly Dictionary<string, string> Names = new()
    {
        ["d00"] = "early session name", ["a01"] = "alpha session name", ["b02"] = "bravo session name", ["c03"] = "charlie session name",
    };

    private static MentorRecord Rec(string sid, string stamp, string text, string role = "user", string? modality = null, string? surface = null)
    {
        var record = Prompt(Local(stamp), sid, text: text, role: role, modality: modality, surface: surface);
        return new MentorRecord
        {
            Ts = record.Ts, Session = record.Session, Context = record.Context, Role = record.Role, Modality = record.Modality,
            Surface = record.Surface, Words = record.Words, Text = record.Text, Agent = record.Agent, Name = Names[sid], Pid = record.Pid,
        };
    }

    private static SessionHistoryEntity Row(string sid, string started, string? ended, string? ending, long? agentTurns, string originKind, string? repoName, string agent = "ClaudeCode")
        => new()
        {
            SessionId = sid, SessionName = Names[sid], AgentKind = agent, StartedAtUtc = Local(started),
            EndedAtUtc = ended is null ? null : Local(ended), EndingKind = ending, AgentTurnCount = agentTurns,
            OriginKind = originKind, RepoName = repoName, TenantId = "t",
        };

    private static (string Text, WeekData Week, List<MentorRecord> Human) Build()
    {
        var sessions = MentorReaders.LoadSessions(new[]
        {
            Row("d00", "2026-08-20 09:00", "2026-08-27 10:00", "finished", 3, "human", "repo-early"),
            Row("a01", "2026-08-24 09:00", "2026-08-24 12:00", "finished", 9, "human", "repo-alpha"),
            Row("b02", "2026-08-25 10:00", null, null, 2, "agent", null, agent: "Codex"),
        });
        var ledger = Rec("a01", "2026-08-24 09:10", LedgerText);
        var records = new List<MentorRecord>
        {
            Rec("d00", "2026-08-27 09:30", EarlyText, modality: "typed", surface: "desktop"),
            Rec("a01", "2026-08-24 09:05", StampedText, modality: "typed", surface: "desktop"),
            Rec("a01", "2026-08-24 09:06", AssistantToken + " reply text omega", role: "assistant"),
            Rec("a01", "2026-08-24 09:15", "Message [message from zulu session (KAPPA-BOX), id m1] " + EnvelopeToken + " body"),
            Rec("a01", "2026-08-24 09:20", "<task-notification>" + AgentToolToken + "</task-notification>"),
            Rec("a01", "2026-08-24 09:25", UnresolvedToken + " sigma omega"),
            Rec("b02", "2026-08-25 10:05", UnicodeText, modality: "voice", surface: "cockpit"),
            Rec("c03", "2026-08-26 11:00", NoRowText, modality: "typed", surface: "desktop"),
            ledger,
        };
        records = records.OrderBy(r => r.Ts).ToList();
        var events = new[] { Turn(ledger.Ts, "a01", sendSource: "UserInput", inputOrigin: "voice/phone") };
        MentorReaders.ClassifyRecords(records, events);
        var week = new WeekData("one", Zone, Week, sessions, records);
        var human = PromptsFile.HumanPrompts(week);
        var (text, sessionCount, replaced) = PromptsFile.RenderFile(week, human, sessions);
        Assert.Equal(4, sessionCount);
        Assert.Equal(0, replaced);
        return (text, week, human);
    }

    private static List<string> SessionHeaders(string text) => text.Split('\n').Where(l => l.StartsWith("#### Session ")).ToList();

    private static string SessionBlock(string text, string sid)
    {
        var start = text.IndexOf("#### Session " + sid + " - ", StringComparison.Ordinal);
        var end = text.IndexOf("\n#### Session ", start + 1, StringComparison.Ordinal);
        if (end < 0) end = text.IndexOf("\nEnd of prompts.", start, StringComparison.Ordinal);
        return text.Substring(start, end - start);
    }

    [Fact]
    public void Count_and_only_human_prompts_appear()
    {
        var (text, _, human) = Build();
        Assert.Equal(5, human.Count);
        Assert.Equal(5, text.Split("\n[20").Length - 1);                       // one stamp line per prompt
        Assert.EndsWith("End of prompts. 5 human prompts.", text.TrimEnd('\n'));
        Assert.Contains("- human prompts: 5; words: ", text);
        foreach (var body in new[] { StampedText, LedgerText, EarlyText, NoRowText, UnicodeAscii })
            Assert.Contains("\n    " + body + "\n", text);
        foreach (var token in new[] { EnvelopeToken, AgentToolToken, UnresolvedToken, AssistantToken })
            Assert.DoesNotContain(token, text);
        Assert.Contains("[2026-08-24 09:10 local, voice/phone, 5 words]\n    " + LedgerText, text);
        Assert.Contains("[2026-08-24 09:05 local, typed/desktop, 6 words]\n    " + StampedText, text);
        Assert.DoesNotContain("ASSISTANT", text);
        Assert.True(text.All(c => c < 128));
        Assert.DoesNotContain("caf?", text);
    }

    [Fact]
    public void Session_order_and_header_lines()
    {
        var (text, _, _) = Build();
        Assert.Equal(new[]
        {
            "#### Session d00 - " + Names["d00"],
            "#### Session a01 - " + Names["a01"],
            "#### Session b02 - " + Names["b02"],
            "#### Session c03 - " + Names["c03"],
        }, SessionHeaders(text));
        var early = SessionBlock(text, "d00");
        Assert.Contains("\nrepo: repo-early\n", early);
        Assert.Contains("\nstart: 2026-08-20 09:00; end: 2026-08-27 10:00\n", early);
        Assert.Contains("\nstarted by: human\n", early);
        var alpha = SessionBlock(text, "a01");
        Assert.Contains("\nrepo: repo-alpha\nagent: ClaudeCode\nstart: 2026-08-24 09:00; end: 2026-08-24 12:00\nstarted by: human\n", alpha);
        Assert.True(alpha.IndexOf(StampedText, StringComparison.Ordinal) < alpha.IndexOf(LedgerText, StringComparison.Ordinal));
        var bravo = SessionBlock(text, "b02");
        Assert.Contains("\nrepo: no repository name\nagent: Codex\nstart: 2026-08-25 10:00; end: open at extract time\nstarted by: agent\n", bravo);
        var charlie = SessionBlock(text, "c03");
        Assert.Contains("\nrepo: no session row\n", charlie);
        Assert.Contains("\nstart: no session row (first prompt 2026-08-26 11:00); end: no session row\n", charlie);
        Assert.Contains("\nstarted by: no session row\n", charlie);
    }

    [Fact]
    public void Session_index_lists_every_session_with_its_id8_repo_count_and_origin()
    {
        var (text, _, _) = Build();
        var index = text.Substring(text.IndexOf("## Session index", StringComparison.Ordinal), text.IndexOf("#### Session ", StringComparison.Ordinal) - text.IndexOf("## Session index", StringComparison.Ordinal));
        var rows = index.Split('\n').Where(l => l.StartsWith("- ")).ToList();
        Assert.Equal(new[]
        {
            "- d00 | repo-early | 1 | human | " + Names["d00"],
            "- a01 | repo-alpha | 2 | human | " + Names["a01"],
            "- b02 | no repository name | 1 | agent | " + Names["b02"],
            "- c03 | no session row | 1 | no session row | " + Names["c03"],
        }, rows);
        Assert.Contains("first eight characters of the session id | repository name | human prompts | started by | session name", index);
    }

    [Fact]
    public void First_block_and_closing_line()
    {
        var (text, _, _) = Build();
        Assert.StartsWith("# Your prompts - one - " + Week + "\n\n- account: one\n", text);
        var first = text.Substring(0, text.IndexOf("## Session index", StringComparison.Ordinal));
        Assert.Contains("- week: " + Week + ", Monday 2026-08-24 to Sunday 2026-08-30, local time\n", first);
        Assert.Contains("- time zone: America/Toronto\n", first);
        Assert.Contains("- human prompts: 5; words: 25; sessions they fall in: 4\n", first);
        Assert.Contains("- " + PromptsFile.OwnPromptsSentence + "\n", first);
        foreach (var word in new[] { "agent", "framework", "unresolved" })
            Assert.DoesNotContain(word, first);
        Assert.EndsWith("\nEnd of prompts. 5 human prompts.", text.TrimEnd('\n'));
        Assert.DoesNotContain("every human prompt", text);
    }

    [Fact]
    public void Week_bounds_and_prior_weeks_follow_the_iso_calendar_in_the_zone()
    {
        var (start, end) = MentorReaders.WeekBounds("2026-W36", Zone);
        Assert.Equal(Local("2026-08-31 00:00"), start);
        Assert.Equal(Local("2026-09-07 00:00"), end);
        Assert.Equal(new[] { "2026-W32", "2026-W33", "2026-W34", "2026-W35" }, MentorReaders.PriorWeeks("2026-W36"));
        Assert.Equal(new[] { "2025-W52", "2026-W01" }, MentorReaders.PriorWeeks("2026-W02", 2));
        Assert.Throws<MentorDataException>(() => MentorReaders.ParseIsoWeek("2026-36"));
        Assert.Equal("2026-08-24T13:05:00Z", MentorReaders.StampUtc(Local("2026-08-24 09:05").AddMilliseconds(345)));
    }

    [Fact]
    public void A_repo_name_that_is_a_path_is_refused_naming_the_reason()
    {
        Assert.Null(MentorReaders.RepoNameOf(null, "w"));
        Assert.Null(MentorReaders.RepoNameOf("", "w"));
        Assert.Equal("owner/repo", MentorReaders.RepoNameOf("owner/repo", "w"));
        foreach (var (value, reason) in new[] { ("D:\\x", "a backslash"), ("a:b", "a colon"), ("/x/y", "a leading slash"), ("a/../b", "a '..' segment"), ("a/b/c", "more than one slash") })
        {
            var error = Assert.Throws<MentorDataException>(() => MentorReaders.RepoNameOf(value, "w"));
            Assert.Contains(reason, error.Message);
        }
    }

    [Fact]
    public void A_json_array_column_is_null_a_list_or_a_refusal()
    {
        Assert.Null(MentorReaders.JsonArrayColumn(null, "w"));
        Assert.Equal(new object?[] { "a", 2L, true, null }, MentorReaders.JsonArrayColumn("[\"a\", 2, true, null]", "w"));
        Assert.Contains("not an array", Assert.Throws<MentorDataException>(() => MentorReaders.JsonArrayColumn("{}", "w")).Message);
        Assert.Contains("not JSON", Assert.Throws<MentorDataException>(() => MentorReaders.JsonArrayColumn("[", "w")).Message);
    }
}
