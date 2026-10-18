using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Rules;
using CcDirector.Core.Sessions;
using CcDirector.RuleHook;
using Xunit;

namespace CcDirector.RuleHook.Tests;

/// <summary>
/// THE TWO SIDES OF W2 AND W4, READ AGAINST EACH OTHER. The Director writes the per-session rules file
/// (<see cref="SessionRulesFile.Render"/>) and computes where the hook's evaluation log lives
/// (<see cref="SessionHookFiles.EvaluationLogPathFor"/>); cc-rule-hook reads that file
/// (<see cref="RulesFile"/>) and writes that log (<see cref="EvaluationLog.PathFor"/>). Each side has its own
/// tests against the contract as written; nothing else would notice them drifting apart. A renamed field
/// would not fail loudly either - the hook ignores a name it does not know and reads a default instead, so a
/// rule's appliesTo or match would quietly go missing - and a moved log would make every negative control
/// "proven" by a no_match line that is being written somewhere nobody reads.
/// </summary>
public sealed class DirectorAndHookContractTests : IDisposable
{
    private static readonly Guid SessionId = Guid.Parse("8a1e0000-0000-4000-8000-0000000000c2");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-rule-contract-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Every field different from every other and from its default, so a field read from the
    /// wrong name, or not read at all, cannot pass by coincidence.</summary>
    private static readonly HookRuleEntry[] DirectorRules =
    [
        new()
        {
            Id = "rule-repo-3f2c", Rank = 3, AppliesTo = "repo", Event = "before_command", Tool = "shell",
            Match = ["echo RULE-PROBE", "git push -f"], Result = "deny", Reason = "the instruction sentence, verbatim", State = "dry_run",
        },
        new()
        {
            Id = "rule-everywhere-9a01", Rank = 1, AppliesTo = "everywhere", Event = "before_command", Tool = "shell",
            Match = ["echo RULE-PROBE"], Result = "allow", Reason = "a broader allow", State = "live",
        },
    ];

    private static string Rendered() => SessionRulesFile.Render(
        SessionId, "devlinux", "github.com/thefrederiksen/devthrottle", DirectorRules,
        new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));

    [Fact]
    public void RulesFile_RenderedByTheDirector_IsReadByTheHookFieldForField()
    {
        var read = JsonSerializer.Deserialize(Rendered(), RuleHookJson.Default.RulesFile)!;

        Assert.Equal(SessionRulesFile.Version, read.Version);
        Assert.Equal(SessionId.ToString(), read.SessionId);
        Assert.Equal("devlinux", read.Machine);
        Assert.Equal("github.com/thefrederiksen/devthrottle", read.Repo);
        Assert.Equal(DirectorRules.Length, read.Rules!.Count);
        for (var i = 0; i < DirectorRules.Length; i++)
        {
            var written = DirectorRules[i];
            var hook = read.Rules[i];
            Assert.Equal(written.Id, hook.Id);
            Assert.Equal(written.Rank, hook.Rank);
            Assert.Equal(written.AppliesTo, hook.AppliesTo);
            Assert.Equal(written.Event, hook.Event);
            Assert.Equal(written.Tool, hook.Tool);
            Assert.Equal(written.Match, hook.Match);
            Assert.Equal(written.Result, hook.Result);
            Assert.Equal(written.Reason, hook.Reason);
            Assert.Equal(written.State, hook.State);
        }
        // And the hook accepts it: the Director's ranks agree with the hook's own P3 table.
        Assert.Equal(DirectorRules.Length, RuleResolver.Validate(read).Count);
    }

    /// <summary>
    /// The field-for-field test above checks the fields the hook reads today. This checks the NAMES both
    /// ways, so a field added on one side only shows up here: every name the Director writes is one the
    /// hook reads, except the one it writes for people (generatedUtc), and every name the hook reads is
    /// one the Director writes.
    /// </summary>
    [Fact]
    public void RulesFile_FieldNames_AreTheSameOnBothSides()
    {
        var file = JsonNode.Parse(Rendered())!.AsObject();
        var writtenTop = file.Select(p => p.Key).Where(k => k != "generatedUtc").OrderBy(k => k, StringComparer.Ordinal);
        var writtenRule = file["rules"]![0]!.AsObject().Select(p => p.Key).OrderBy(k => k, StringComparer.Ordinal);

        var readTop = RuleHookJson.Default.RulesFile.Properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal);
        var readRule = RuleHookJson.Default.GetTypeInfo(typeof(HookRule))!.Properties.Select(p => p.Name).OrderBy(k => k, StringComparer.Ordinal);

        Assert.Equal(readTop, writtenTop);
        Assert.Equal(readRule, writtenRule);
        Assert.Contains("generatedUtc", file.Select(p => p.Key));
    }

    /// <summary>The whole path, on disk, where the Director puts things: the Director's file at the
    /// Director's path, the Director's variable names, and the hook's log found where the Director looks.</summary>
    [Fact]
    public void Hook_RunOnTheDirectorsFileAndVariables_DecidesWithItsRulesAndLogsWhereTheDirectorReads()
    {
        var rulesDirectory = Path.Combine(_root, "session-rules");
        var rulesPath = SessionHookFiles.RulesPathFor(SessionId, rulesDirectory);
        var decisions = Path.Combine(_root, "decisions");
        Directory.CreateDirectory(decisions);
        SessionRulesFile.WriteIfChanged(rulesPath, SessionId, "devlinux", "github.com/thefrederiksen/devthrottle",
            DirectorRules, new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));
        var environment = new Dictionary<string, string?>
        {
            [SessionHookFiles.RulesFileEnvVar] = rulesPath,
            [SessionHookFiles.DecisionDirEnvVar] = decisions,
        };
        const string evt = "{\"hook_event_name\":\"PreToolUse\",\"tool_name\":\"Bash\",\"tool_input\":{\"command\":\"echo RULE-PROBE\"},\"tool_use_id\":\"toolu_contract\"}";

        new HookRunner(environment.GetValueOrDefault, () => new DateTime(2026, 10, 18, 12, 0, 1, DateTimeKind.Utc), () => "0c0ffee0")
            .Run(new StringReader(evt));

        var log = SessionHookFiles.EvaluationLogPathFor(SessionId, rulesDirectory);
        var line = Assert.Single(File.ReadAllLines(log));
        Assert.Contains("\trules=2\tmatched=2\twould_deny\t", line);
        var decision = JsonNode.Parse(File.ReadAllText(Assert.Single(Directory.GetFiles(decisions))))!;
        Assert.Equal("rule-repo-3f2c", (string?)decision["decidingRuleId"]);
        Assert.Equal(["rule-everywhere-9a01"], decision["overriddenRuleIds"]!.AsArray().Select(n => (string?)n));
        Assert.Equal(SessionId.ToString(), (string?)decision["sessionId"]);
    }

    [Fact]
    public void EvaluationLogPath_ComputedByBothSides_IsTheSameFile()
    {
        var directory = Path.Combine(_root, "session-rules");

        Assert.Equal(
            SessionHookFiles.EvaluationLogPathFor(SessionId, directory),
            EvaluationLog.PathFor(SessionHookFiles.RulesPathFor(SessionId, directory)));
    }

    [Fact]
    public void EnvironmentVariableNames_StampedByTheDirector_AreTheOnesTheHookReads()
    {
        Assert.Equal(SessionHookFiles.RulesFileEnvVar, HookRunner.RulesFileEnvVar);
        Assert.Equal(SessionHookFiles.DecisionDirEnvVar, HookRunner.DecisionDirEnvVar);
    }
}
