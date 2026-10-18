using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.RuleHook;
using Xunit;

namespace CcDirector.RuleHook.Tests;

/// <summary>
/// The hook end to end, in process: Claude Code's real PreToolUse event on standard input (captured from
/// the installed Claude Code 2.1.302 into Fixtures), the Director's rules file (W2), the decision drop
/// (W3) and the evaluation log (W4). Every case also proves P10: the hook answers 0 whatever happens.
/// </summary>
public sealed class HookRunnerTests : IDisposable
{
    private static readonly Guid SessionId = Guid.Parse("8a1e0000-0000-4000-8000-000000000001");
    private static readonly DateTime Now = new(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-rule-hook-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string _rulesFile;
    private readonly string _decisionDir;
    private readonly Dictionary<string, string?> _env = new();
    private readonly Queue<string> _randomHex = new(["1a2b3c4d", "5e6f7a8b", "9c0d1e2f"]);
    private DateTime _now = Now.AddTicks(1234567);

    public HookRunnerTests()
    {
        _rulesFile = Path.Combine(_root, "session-rules", SessionId + ".json");
        _decisionDir = Path.Combine(_root, "session-pointers", "decisions", SessionId + ".0123456789abcdef0123456789abcdef");
        Directory.CreateDirectory(Path.GetDirectoryName(_rulesFile)!);
        Directory.CreateDirectory(_decisionDir);
        _env[HookRunner.RulesFileEnvVar] = _rulesFile;
        _env[HookRunner.DecisionDirEnvVar] = _decisionDir;
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private string EvalLog => Path.Combine(Path.GetDirectoryName(_rulesFile)!, SessionId + ".eval.log");

    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    /// <summary>The real captured event, with its command replaced.</summary>
    private static string EventFor(string command, string tool = "Bash")
    {
        var node = JsonNode.Parse(Fixture("claude-code-2.1.302-pretooluse-bash.json"))!;
        node["tool_name"] = tool;
        node["tool_input"]!["command"] = command;
        return node.ToJsonString();
    }

    private int Run(string stdin, long logLimitBytes = 1024 * 1024)
    {
        var runner = new HookRunner(name => _env.GetValueOrDefault(name), () => _now, () => _randomHex.Dequeue(), logLimitBytes);
        return runner.Run(new StringReader(stdin));
    }

    private void WriteRules(params object[] rules)
    {
        var file = new
        {
            version = 1,
            sessionId = SessionId.ToString(),
            machine = "devlinux",
            repo = "github.com/thefrederiksen/devthrottle",
            generatedUtc = "2026-10-18T11:00:00.0000000Z",
            rules,
        };
        File.WriteAllText(_rulesFile, JsonSerializer.Serialize(file));
    }

    private static object RuleJson(string id, string appliesTo, string result, int rank, params string[] match) => new
    {
        id,
        rank,
        appliesTo,
        @event = "before_command",
        tool = "shell",
        match,
        result,
        reason = "the instruction sentence for " + id,
        state = "dry_run",
    };

    private string[] LogLines() => File.ReadAllLines(EvalLog);

    private string[] Decisions() => Directory.GetFiles(_decisionDir).OrderBy(f => f, StringComparer.Ordinal).ToArray();

    // ---------- The capture itself ----------

    [Fact]
    public void Fixture_CapturedFromInstalledClaudeCode_IsAPreToolUseBashEventWithACommand()
    {
        var node = JsonNode.Parse(Fixture("claude-code-2.1.302-pretooluse-bash.json"))!;

        Assert.Equal("PreToolUse", (string?)node["hook_event_name"]);
        Assert.Equal("Bash", (string?)node["tool_name"]);
        Assert.Equal("echo RULE-PROBE", (string?)node["tool_input"]!["command"]);
    }

    // ---------- Not a Director session ----------

    [Fact]
    public void Run_NoRulesFileVariable_ReturnsZeroAndWritesNothing()
    {
        _env.Remove(HookRunner.RulesFileEnvVar);

        var exit = Run(EventFor("echo RULE-PROBE"));

        Assert.Equal(0, exit);
        Assert.False(File.Exists(EvalLog));
        Assert.Empty(Decisions());
    }

    // ---------- W4: one line per evaluation, match or not ----------

    [Fact]
    public void Run_RulesFileAbsent_LogsNoRulesFile()
    {
        var exit = Run(EventFor("echo RULE-PROBE"));

        Assert.Equal(0, exit);
        var line = Assert.Single(LogLines());
        Assert.Contains("\tno_rules_file\t", line);
        Assert.Contains("\trules=0\t", line);
        Assert.Empty(Decisions());
    }

    [Fact]
    public void Run_EmptyRulesList_LogsNoMatchAndDropsNothing()
    {
        WriteRules();

        var exit = Run(EventFor("echo RULE-PROBE"));

        Assert.Equal(0, exit);
        var line = Assert.Single(LogLines());
        Assert.StartsWith("2026-10-18T12:00:00.1234567Z\tshell\trules=0\tmatched=0\tno_match\t", line);
        Assert.EndsWith("us", line);
        Assert.Empty(Decisions());
    }

    [Fact]
    public void Run_RulesThatDoNotMatch_LogsNoMatchWithTheRuleCount()
    {
        WriteRules(RuleJson("a", "repo", "deny", 3, "rm -rf /"), RuleJson("b", "everywhere", "deny", 1, "shutdown"));

        Run(EventFor("echo RULE-PROBE"));

        var line = Assert.Single(LogLines());
        Assert.Contains("\trules=2\tmatched=0\tno_match\t", line);
        Assert.Empty(Decisions());
    }

    // ---------- W3: the decision drop ----------

    [Fact]
    public void Run_MatchingDenyRule_DropsOneDecisionInTheW3Shape()
    {
        WriteRules(RuleJson("3f2c", "repo", "deny", 3, "echo RULE-PROBE"));

        var exit = Run(EventFor("echo RULE-PROBE"));

        Assert.Equal(0, exit);
        var file = Assert.Single(Decisions());
        Assert.Equal("20261018T1200001234567Z-1a2b3c4d.json", Path.GetFileName(file));
        var d = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        Assert.Equal(1, (int)d["version"]!);
        Assert.Equal("20261018T1200001234567Z-1a2b3c4d", (string?)d["decisionId"]);
        Assert.Equal(SessionId.ToString(), (string?)d["sessionId"]);
        Assert.Equal("2026-10-18T12:00:00.1234567Z", (string?)d["occurredUtc"]);
        Assert.Equal("claude_code", (string?)d["agent"]);
        Assert.Equal("before_command", (string?)d["event"]);
        Assert.Equal("PreToolUse", (string?)d["agentEvent"]);
        Assert.Equal("shell", (string?)d["tool"]);
        Assert.Equal("Bash", (string?)d["agentTool"]);
        Assert.Equal("echo RULE-PROBE", (string?)d["subject"]);
        Assert.Equal("devlinux", (string?)d["machine"]);
        Assert.Equal("github.com/thefrederiksen/devthrottle", (string?)d["repo"]);
        Assert.Equal("would_deny", (string?)d["decision"]);
        Assert.Equal("3f2c", (string?)d["decidingRuleId"]);
        Assert.Empty(d["overriddenRuleIds"]!.AsArray());
        Assert.Equal(["3f2c"], d["matchedRuleIds"]!.AsArray().Select(n => (string?)n));
        Assert.True((long)d["evaluationMicroseconds"]! >= 0);
        // Exactly the W3 fields, no more and no fewer.
        Assert.Equal(
            ["version", "decisionId", "sessionId", "occurredUtc", "agent", "event", "agentEvent", "tool", "agentTool",
             "subject", "machine", "repo", "decision", "decidingRuleId", "overriddenRuleIds", "matchedRuleIds",
             "evaluationMicroseconds"],
            d.Select(p => p.Key));

        var line = Assert.Single(LogLines());
        Assert.Contains("\tshell\trules=1\tmatched=1\twould_deny\t", line);
    }

    [Fact]
    public void Run_RepoAllowOverEverywhereDeny_DropsWouldAllowNamingTheOverriddenRule()
    {
        WriteRules(RuleJson("global", "everywhere", "deny", 1, "git push"), RuleJson("repo", "repo", "allow", 3, "git push"));

        Run(EventFor("git push origin main"));

        var d = JsonNode.Parse(File.ReadAllText(Assert.Single(Decisions())))!;
        Assert.Equal("would_allow", (string?)d["decision"]);
        Assert.Equal("repo", (string?)d["decidingRuleId"]);
        Assert.Equal(["global"], d["overriddenRuleIds"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(["global", "repo"], d["matchedRuleIds"]!.AsArray().Select(n => (string?)n));
    }

    [Fact]
    public void Run_BackToBackMatchesInTheSameTick_DropTwoFilesNeitherOverwritten()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "RULE-PROBE"));

        Run(EventFor("echo RULE-PROBE one"));
        Run(EventFor("echo RULE-PROBE two"));

        var files = Decisions();
        Assert.Equal(2, files.Length);
        Assert.Equal(
            ["echo RULE-PROBE one", "echo RULE-PROBE two"],
            files.Select(f => (string?)JsonNode.Parse(File.ReadAllText(f))!["subject"]).OrderBy(s => s, StringComparer.Ordinal));
        Assert.Equal(2, LogLines().Length);
    }

    [Fact]
    public void Run_Match_LeavesNoTemporaryFileBehind()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "RULE-PROBE"));

        Run(EventFor("echo RULE-PROBE"));

        Assert.All(Decisions(), f => Assert.EndsWith(".json", f));
    }

    [Fact]
    public void Run_CommandLongerThan4000Characters_SubjectIsCutTo4000()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "RULE-PROBE"));

        Run(EventFor("echo RULE-PROBE " + new string('x', 5000)));

        var subject = (string?)JsonNode.Parse(File.ReadAllText(Assert.Single(Decisions())))!["subject"];
        Assert.Equal(4000, subject!.Length);
        Assert.StartsWith("echo RULE-PROBE ", subject);
    }

    [Fact]
    public void Run_CutWouldSplitASurrogatePair_CutsBeforeIt()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "RULE-PROBE"));
        // 3999 characters, then an emoji (two characters) straddling the 4000 boundary.
        var command = "RULE-PROBE" + new string('x', 3989) + "\U0001F600" + "tail";

        Run(EventFor(command));

        var subject = (string?)JsonNode.Parse(File.ReadAllText(Assert.Single(Decisions())))!["subject"];
        Assert.Equal(3999, subject!.Length);
        Assert.False(char.IsHighSurrogate(subject[^1]));
    }

    [Fact]
    public void Run_PowerShellTool_IsRecordedAsTheShellTool()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "Remove-Item"));

        Run(EventFor("Remove-Item -Recurse C:\\x", tool: "PowerShell"));

        var d = JsonNode.Parse(File.ReadAllText(Assert.Single(Decisions())))!;
        Assert.Equal("shell", (string?)d["tool"]);
        Assert.Equal("PowerShell", (string?)d["agentTool"]);
    }

    // ---------- P10: whatever goes wrong, exit 0, and the log says what ----------

    [Fact]
    public void Run_StandardInputIsNotJson_LogsAnErrorAndReturnsZero()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "x"));

        var exit = Run("this is not json");

        Assert.Equal(0, exit);
        Assert.Contains("\terror: ", Assert.Single(LogLines()));
        Assert.Empty(Decisions());
    }

    [Fact]
    public void Run_StandardInputEmpty_LogsAnErrorAndReturnsZero()
    {
        var exit = Run("");

        Assert.Equal(0, exit);
        Assert.Contains("\terror: ", Assert.Single(LogLines()));
    }

    [Fact]
    public void Run_EventHasNoCommand_LogsAnErrorNamingIt()
    {
        WriteRules();

        Run("{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{}}");

        Assert.Contains("\terror: the Bash event has no command", Assert.Single(LogLines()));
    }

    [Fact]
    public void Run_ToolIsNotAShellTool_LogsAnErrorNamingTheTool()
    {
        WriteRules();

        Run(EventFor("x", tool: "Edit"));

        Assert.Contains("\terror: tool 'Edit' is not a shell tool", Assert.Single(LogLines()));
    }

    [Fact]
    public void Run_RulesFileIsNotJson_LogsAnErrorAndReturnsZero()
    {
        File.WriteAllText(_rulesFile, "{ not json");

        var exit = Run(EventFor("echo RULE-PROBE"));

        Assert.Equal(0, exit);
        Assert.Contains("\terror: ", Assert.Single(LogLines()));
    }

    [Fact]
    public void Run_RulesFileFailsValidation_LogsTheReason()
    {
        WriteRules(RuleJson("a", "repo", "deny", 1, "x"));

        Run(EventFor("x"));

        Assert.Contains("\terror: rule a has rank 1 but appliesTo 'repo' is rank 3", Assert.Single(LogLines()));
        Assert.Empty(Decisions());
    }

    [Fact]
    public void Run_MatchButNoDecisionDirVariable_LogsAnErrorCarryingTheDecision()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "x"));
        _env.Remove(HookRunner.DecisionDirEnvVar);

        var exit = Run(EventFor("x"));

        Assert.Equal(0, exit);
        var line = Assert.Single(LogLines());
        Assert.Contains("\tmatched=1\terror: would_deny not recorded: ", line);
        Assert.Contains(HookRunner.DecisionDirEnvVar, line);
    }

    [Fact]
    public void Run_MatchButDecisionDirMissing_LogsAnErrorAndDoesNotCreateIt()
    {
        WriteRules(RuleJson("a", "everywhere", "deny", 1, "x"));
        Directory.Delete(_decisionDir);

        var exit = Run(EventFor("x"));

        Assert.Equal(0, exit);
        Assert.Contains("\terror: would_deny not recorded: ", Assert.Single(LogLines()));
        // The Director creates the drop directory at launch; the hook never invents one.
        Assert.False(Directory.Exists(_decisionDir));
    }

    // ---------- W4: the log is cut back to its last half past the limit ----------

    [Fact]
    public void Run_LogPassesItsLimit_KeepsTheNewestHalfOnWholeLines()
    {
        WriteRules();
        var old = string.Join("", Enumerable.Range(0, 200).Select(i => $"old line {i:D4}\n"));
        File.WriteAllText(EvalLog, old);
        var limit = old.Length; // the next line pushes it past the limit

        Run(EventFor("echo RULE-PROBE"), logLimitBytes: limit);

        var text = File.ReadAllText(EvalLog);
        Assert.True(text.Length <= limit / 2 + 200, $"log is {text.Length} characters after the cut");
        Assert.DoesNotContain("old line 0000", text);
        Assert.Contains("old line 0199", text);
        Assert.EndsWith("no_match\t" + text.Split('\t')[^1], text);
        Assert.All(text.TrimEnd('\n').Split('\n'), line => Assert.True(line.StartsWith("old line ") || line.StartsWith("2026-"), line));
    }

    [Fact]
    public void Run_NewLog_StartsWithTheTimeNotAByteOrderMark()
    {
        WriteRules();

        Run(EventFor("echo RULE-PROBE"));

        // A byte-order mark would make the first line's time unreadable to a reader splitting on tabs.
        Assert.Equal((byte)'2', File.ReadAllBytes(EvalLog)[0]);
    }

    [Fact]
    public void Run_LogUnderItsLimit_IsOnlyAppendedTo()
    {
        WriteRules();
        File.WriteAllText(EvalLog, "earlier\n");

        Run(EventFor("echo RULE-PROBE"));

        var lines = LogLines();
        Assert.Equal(2, lines.Length);
        Assert.Equal("earlier", lines[0]);
    }
}
