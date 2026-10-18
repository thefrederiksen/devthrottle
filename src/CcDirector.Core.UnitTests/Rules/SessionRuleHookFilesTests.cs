using System.Text.Json;
using CcDirector.Core.Rules;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Core.Tests.Rules;

/// <summary>
/// Session Rules part 2, phase 1: the names the Director hands the before-a-command hook (W2 and W3), the
/// folders it creates for it at launch, and the exact shape of the rules file.
/// </summary>
public sealed class SessionRuleHookFilesTests : IDisposable
{
    private readonly string _dir;

    public SessionRuleHookFilesTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "ccd-rule-hook-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void StampRuleHookEnvironment_ClaudeSession_NamesTheRulesFileAndDecisionDirectoryAndCreatesBothFolders()
    {
        // Arrange
        var id = Guid.NewGuid();
        var token = SessionHookFiles.NewDropToken();
        var rulesDir = Path.Combine(_dir, "session-rules");
        var pointerDir = Path.Combine(_dir, "session-pointers");
        var environment = new Dictionary<string, string>();

        // Act
        SessionHookFiles.StampRuleHookEnvironment(environment, id, token, rulesDir, pointerDir);

        // Assert
        Assert.Equal(Path.Combine(rulesDir, id + ".json"), environment["CC_SESSION_RULES_FILE"]);
        Assert.Equal(Path.Combine(pointerDir, "decisions", id + "." + token), environment["CC_SESSION_DECISION_DIR"]);
        Assert.True(Directory.Exists(rulesDir));
        Assert.True(Directory.Exists(environment["CC_SESSION_DECISION_DIR"]));
        Assert.False(File.Exists(environment["CC_SESSION_RULES_FILE"]));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("ABCDEF0123456789ABCDEF0123456789")]
    [InlineData("../../../../etc/passwd/aaaaaaaaaaaaaaaa")]
    public void DecisionDirectoryFor_TokenThatIsNotAMintedToken_Throws(string token)
    {
        // Act and Assert
        Assert.Throws<ArgumentException>(() => SessionHookFiles.DecisionDirectoryFor(Guid.NewGuid(), token, _dir));
    }

    [Fact]
    public void DecisionDirectoryFor_MintedToken_LivesUnderThePointerDropBoxDecisionsFolder()
    {
        // Arrange
        var id = Guid.NewGuid();
        var token = SessionHookFiles.NewDropToken();

        // Act
        var directory = SessionHookFiles.DecisionDirectoryFor(id, token, _dir);

        // Assert: parsed by the same rule as a pointer drop's name.
        Assert.Equal(Path.Combine(_dir, "decisions"), Path.GetDirectoryName(directory));
        Assert.True(SessionHookFiles.TryParseDropName(directory + SessionHookFiles.DropExtension, out var parsedId, out var parsedToken));
        Assert.Equal(id, parsedId);
        Assert.Equal(token, parsedToken);
    }

    [Fact]
    public void Render_RulesFile_HasExactlyTheW2Shape()
    {
        // Arrange
        var id = Guid.Parse("8a1e0000-0000-0000-0000-000000000001");
        var generated = new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc);

        // Act
        var text = SessionRulesFile.Render(id, "devlinux", "github.com/thefrederiksen/devthrottle", new[] { RuleSamples.DenyEcho() }, generated);

        // Assert
        using var doc = JsonDocument.Parse(text);
        var root = doc.RootElement;
        Assert.Equal(new[] { "version", "sessionId", "machine", "repo", "generatedUtc", "rules" },
            root.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(1, root.GetProperty("version").GetInt32());
        Assert.Equal("8a1e0000-0000-0000-0000-000000000001", root.GetProperty("sessionId").GetString());
        Assert.Equal("devlinux", root.GetProperty("machine").GetString());
        Assert.Equal("github.com/thefrederiksen/devthrottle", root.GetProperty("repo").GetString());
        Assert.Equal("2026-10-18T12:00:00.0000000Z", root.GetProperty("generatedUtc").GetString());
        var rule = Assert.Single(root.GetProperty("rules").EnumerateArray().ToList());
        Assert.Equal(new[] { "id", "rank", "appliesTo", "event", "tool", "match", "result", "reason", "state" },
            rule.EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal(3, rule.GetProperty("rank").GetInt32());
        Assert.Equal("repo", rule.GetProperty("appliesTo").GetString());
        Assert.Equal("before_command", rule.GetProperty("event").GetString());
        Assert.Equal("shell", rule.GetProperty("tool").GetString());
        Assert.Equal("echo RULE-PROBE", Assert.Single(rule.GetProperty("match").EnumerateArray().ToList()).GetString());
        Assert.Equal("deny", rule.GetProperty("result").GetString());
        Assert.Equal("Do not run the probe here.", rule.GetProperty("reason").GetString());
        Assert.Equal("dry_run", rule.GetProperty("state").GetString());
    }

    [Fact]
    public void WriteIfChanged_FileThatIsNotJson_IsReplaced()
    {
        // Arrange
        var id = Guid.NewGuid();
        var path = SessionHookFiles.RulesPathFor(id, _dir);
        File.WriteAllText(path, "garbage");

        // Act
        var written = SessionRulesFile.WriteIfChanged(path, id, "devlinux", "", Array.Empty<HookRuleEntry>(), new DateTime(2026, 10, 18, 12, 0, 0, DateTimeKind.Utc));

        // Assert
        Assert.True(written);
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.Empty(doc.RootElement.GetProperty("rules").EnumerateArray());
    }

    [Fact]
    public void StampRuleHookEnvironment_ClaudeSession_RecordsTheIdAndTokenItMinted()
    {
        // Arrange
        var id = Guid.NewGuid();
        var token = SessionHookFiles.NewDropToken();
        var pointerDir = Path.Combine(_dir, "session-pointers");

        // Act
        SessionHookFiles.StampRuleHookEnvironment(new Dictionary<string, string>(), id, token, Path.Combine(_dir, "rules"), pointerDir);

        // Assert: what lets the NEXT Director vouch for this directory after a restart.
        Assert.True(File.Exists(SessionHookFiles.DecisionRecordFor(id, token, pointerDir)));
    }

    [Fact]
    public void EvaluationLogPathFor_Session_IsBesideItsRulesFile()
    {
        // Arrange
        var id = Guid.NewGuid();

        // Act
        var log = SessionHookFiles.EvaluationLogPathFor(id, _dir);

        // Assert: the hook's own rule, Path.ChangeExtension(rulesFile, ".eval.log").
        Assert.Equal(Path.ChangeExtension(SessionHookFiles.RulesPathFor(id, _dir), ".eval.log"), log);
    }
}
