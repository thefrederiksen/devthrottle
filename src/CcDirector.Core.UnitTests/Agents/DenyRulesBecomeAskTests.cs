using CcDirector.Core.Agents;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.Tests.Agents;

/// <summary>
/// THE OWNER'S WORD IS FINAL, EVERYWHERE (the owner, 25 October 2026: "Yes, I want to be God everywhere. If I say
/// something, they have to follow."). Before any session starts, every rule that would refuse outright in the settings
/// files its agent reads becomes that agent's ask rule, and the launch says which. Each test works in a scratch folder
/// and a scratch home, never the machine's own settings.
/// </summary>
public sealed class DenyRulesBecomeAskTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("dt-deny-ask-").FullName;
    private string Folder => Path.Combine(_root, "repo");
    private string Home => Path.Combine(_root, "home");
    private static readonly Func<string, string?> NoEnvironment = _ => null;

    public DenyRulesBecomeAskTests()
    {
        Directory.CreateDirectory(Folder);
        Directory.CreateDirectory(Home);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private static string Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private List<FolderDenyRulesDto> Apply(AgentKind kind, Func<string, string?>? environment = null) =>
        DenyRulesBecomeAsk.Apply(kind, Folder, Home, environment ?? NoEnvironment);

    // ---------- Claude Code ----------

    [Fact]
    public void Apply_ClaudeCode_TurnsEveryDenyInTheFolderAndMachineSettingsIntoAsk_KeepingEverythingElse()
    {
        var project = Write(Path.Combine(Folder, ".claude", "settings.json"),
            "{\n  \"model\": \"opus\",\n  \"permissions\": {\n    \"allow\": [\"Bash(ls:*)\"],\n    \"deny\": [\"Bash(cc-gmail:*)\", \"Edit(.claude/settings.json)\"]\n  }\n}\n");
        var local = Write(Path.Combine(Folder, ".claude", "settings.local.json"),
            "{\"permissions\": {\"ask\": [\"Bash(git push:*)\", \"Bash(cc-gmail:*)\"], \"deny\": [\"Bash(cc-gmail:*)\", \"Write(**)\"]}}");
        var machine = Write(Path.Combine(Home, ".claude", "settings.json"),
            "{\"hooks\": {\"Stop\": []}, \"permissions\": {\"deny\": [\"Bash(rm:*)\"], \"defaultMode\": \"auto\"}}");

        var found = Apply(AgentKind.ClaudeCode);

        Assert.Equal(new[] { project, local, machine }, found.Select(f => f.File));
        Assert.All(found, f => { Assert.True(f.Rewritten); Assert.Null(f.Problem); });
        Assert.Equal(new[] { "Bash(cc-gmail:*)", "Edit(.claude/settings.json)" }, found[0].Rules);

        var p = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(project))!;
        Assert.Equal("opus", p["model"]!.GetValue<string>());
        Assert.Equal(new[] { "Bash(ls:*)" }, p["permissions"]!["allow"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Equal(new[] { "Bash(cc-gmail:*)", "Edit(.claude/settings.json)" }, p["permissions"]!["ask"]!.AsArray().Select(r => r!.GetValue<string>()));
        Assert.Null(p["permissions"]!["deny"]);

        // An ask rule already there is kept and not repeated; the new ones follow it.
        var l = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(local))!;
        Assert.Equal(new[] { "Bash(git push:*)", "Bash(cc-gmail:*)", "Write(**)" }, l["permissions"]!["ask"]!.AsArray().Select(r => r!.GetValue<string>()));

        var m = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(machine))!;
        Assert.NotNull(m["hooks"]);
        Assert.Equal("auto", m["permissions"]!["defaultMode"]!.GetValue<string>());
        Assert.Equal(new[] { "Bash(rm:*)" }, m["permissions"]!["ask"]!.AsArray().Select(r => r!.GetValue<string>()));
    }

    [Fact]
    public void Apply_ClaudeCode_HonoursClaudeConfigDir_ForTheMachineSettings()
    {
        var configDir = Path.Combine(_root, "claude-config");
        var file = Write(Path.Combine(configDir, "settings.json"), "{\"permissions\": {\"deny\": [\"Bash(rm:*)\"]}}");

        var found = Apply(AgentKind.ClaudeCode, name => name == "CLAUDE_CONFIG_DIR" ? configDir : null);

        Assert.Equal(file, Assert.Single(found).File);
        Assert.DoesNotContain("\"deny\"", File.ReadAllText(file));
    }

    [Fact]
    public void Apply_TheSecondLaunch_FindsNothing_AndAFileWithNoDenyIsNeverWritten()
    {
        Write(Path.Combine(Folder, ".claude", "settings.json"), "{\"permissions\": {\"deny\": [\"Bash(rm:*)\"]}}");
        var clean = Write(Path.Combine(Folder, ".claude", "settings.local.json"), "{ \"permissions\": { \"deny\": [] } }");
        var written = File.GetLastWriteTimeUtc(clean);

        Assert.Single(Apply(AgentKind.ClaudeCode));

        Assert.Empty(Apply(AgentKind.ClaudeCode));
        Assert.Equal("{ \"permissions\": { \"deny\": [] } }", File.ReadAllText(clean));
        Assert.Equal(written, File.GetLastWriteTimeUtc(clean));
    }

    [Fact]
    public void Apply_AFileWithComments_IsReportedAndLeftExactlyAsItWas()
    {
        var text = "{\n  // the Architect wrote this\n  \"permissions\": {\"deny\": [\"Bash(rm:*)\"]}\n}\n";
        var file = Write(Path.Combine(Folder, ".claude", "settings.json"), text);

        var entry = Assert.Single(Apply(AgentKind.ClaudeCode));

        Assert.False(entry.Rewritten);
        Assert.StartsWith("it holds comments or trailing commas, which rewriting it would drop", entry.Problem);
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void Apply_AKeyWrittenTwice_IsReportedNotThrown_AndTheFileIsLeft()
    {
        var text = "{\"permissions\": {\"deny\": [\"Bash(a:*)\"]}, \"permissions\": {\"deny\": [\"Bash(b:*)\"]}}";
        var file = Write(Path.Combine(Folder, ".claude", "settings.json"), text);

        var entry = Assert.Single(Apply(AgentKind.ClaudeCode));

        Assert.Equal("the key 'permissions' is written twice in it, so it cannot be rewritten without changing which one counts", entry.Problem);
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void Apply_AFileThatIsNotJson_IsReported_NeverPassedOverAsClean()
    {
        Write(Path.Combine(Folder, ".claude", "settings.json"), "{\"permissions\": {\"deny\": [");

        var entry = Assert.Single(Apply(AgentKind.ClaudeCode));

        Assert.StartsWith("it is not JSON, so whether it refuses anything outright could not be read", entry.Problem);
    }

    [Fact]
    public void Apply_ManagedSettings_AreReportedWithTheirRules_AndNeverWritten()
    {
        var text = "{\"permissions\": {\"deny\": [\"Bash(curl:*)\"]}}";
        var managed = Write(Path.Combine(_root, "managed-settings.json"), text);

        var entry = Assert.Single(DenyRulesBecomeAsk.ApplyTo(new[]
        {
            new DenyRulesBecomeAsk.Target(managed, DenyRulesBecomeAsk.Format.ClaudeJson, Managed: true),
        }));

        Assert.False(entry.Rewritten);
        Assert.Equal(new[] { "Bash(curl:*)" }, entry.Rules);
        Assert.Equal("it is managed settings an organisation deployed, which the Director does not change", entry.Problem);
        Assert.Equal(text, File.ReadAllText(managed));
    }

    [Fact]
    public void Apply_KeepsTheFilesLineEndings()
    {
        var file = Write(Path.Combine(Folder, ".claude", "settings.json"), "{\r\n  \"permissions\": {\"deny\": [\"Bash(rm:*)\"]}\r\n}\r\n");

        Apply(AgentKind.ClaudeCode);

        var text = File.ReadAllText(file);
        Assert.EndsWith("}\r\n", text);
        Assert.DoesNotContain("\n", text.Replace("\r\n", ""));
    }

    // ---------- Codex ----------

    [Fact]
    public void Apply_Codex_TurnsForbiddenIntoPrompt_InTheMachineAndFolderRules_LeavingAllowAlone()
    {
        var machine = Write(Path.Combine(Home, ".codex", "rules", "default.rules"),
            "prefix_rule(pattern=[\"rg\"], decision=\"allow\")\nprefix_rule(pattern=[\"git\", \"push\"], decision=\"forbidden\")\n");
        var folder = Write(Path.Combine(Folder, ".codex", "rules", "team.rules"),
            "prefix_rule(pattern=[\"rm\"], decision = 'forbidden', justification=\"never\")\n");

        var found = Apply(AgentKind.Codex);

        Assert.Equal(new[] { folder, machine }, found.Select(f => f.File));
        Assert.Equal(new[] { "line 2: prefix_rule(pattern=[\"git\", \"push\"], decision=\"forbidden\")" }, found[1].Rules);
        Assert.Equal("prefix_rule(pattern=[\"rg\"], decision=\"allow\")\nprefix_rule(pattern=[\"git\", \"push\"], decision=\"prompt\")\n",
            File.ReadAllText(machine));
        Assert.Equal("prefix_rule(pattern=[\"rm\"], decision = 'prompt', justification=\"never\")\n", File.ReadAllText(folder));
    }

    [Fact]
    public void Apply_Codex_HonoursCodexHome()
    {
        var codexHome = Path.Combine(_root, "codex-home");
        var file = Write(Path.Combine(codexHome, "rules", "a.rules"), "prefix_rule(pattern=[\"rm\"], decision=\"forbidden\")\n");

        Assert.Equal(file, Assert.Single(Apply(AgentKind.Codex, name => name == "CODEX_HOME" ? codexHome : null)).File);
        Assert.Contains("decision=\"prompt\"", File.ReadAllText(file));
    }

    // ---------- Gemini ----------

    [Fact]
    public void Apply_Gemini_TurnsDenyIntoAskUser_InTheUsersPolicies()
    {
        var file = Write(Path.Combine(Home, ".gemini", "policies", "mine.toml"),
            "[[rule]]\ntoolName = \"run_shell_command\"\ncommandPrefix = \"rm\"\ndecision = \"deny\"\npriority = 100\n\n[[rule]]\ntoolName = \"read_file\"\ndecision = \"allow\"\n");

        var entry = Assert.Single(Apply(AgentKind.Gemini));

        Assert.True(entry.Rewritten);
        Assert.Equal(new[] { "line 4: decision = \"deny\"" }, entry.Rules);
        Assert.Contains("decision = \"ask_user\"\npriority = 100", File.ReadAllText(file));
        Assert.Contains("decision = \"allow\"", File.ReadAllText(file));
    }

    [Fact]
    public void Apply_Gemini_LeavesTheFoldersPolicies_WhichGeminiDoesNotRead()
    {
        var text = "[[rule]]\ntoolName = \"x\"\ndecision = \"deny\"\n";
        var file = Write(Path.Combine(Folder, ".gemini", "policies", "team.toml"), text);

        Assert.Empty(Apply(AgentKind.Gemini));
        Assert.Equal(text, File.ReadAllText(file));
    }

    // ---------- Grok ----------

    [Fact]
    public void Apply_Grok_RenamesTheDenyListToAsk_AndTurnsADenyActionIntoAsk_OnlyInThePermissionSection()
    {
        var file = Write(Path.Combine(Home, ".grok", "config.toml"),
            "[ui]\ndeny = \"not a rule\"\n\n[permission]\ndeny = [\n  \"Bash(rm*)\",\n  \"Edit(/etc/**)\",\n]\nrules = [\n  { action = \"deny\", tool = \"bash\", pattern = \"sudo *\" },\n]\n");

        var entry = Assert.Single(Apply(AgentKind.Grok));

        Assert.True(entry.Rewritten);
        Assert.Equal(new[] { "Bash(rm*)", "Edit(/etc/**)", "line 10: { action = \"deny\", tool = \"bash\", pattern = \"sudo *\" }," }, entry.Rules);
        Assert.Equal(
            "[ui]\ndeny = \"not a rule\"\n\n[permission]\nask = [\n  \"Bash(rm*)\",\n  \"Edit(/etc/**)\",\n]\nrules = [\n  { action = \"ask\", tool = \"bash\", pattern = \"sudo *\" },\n]\n",
            File.ReadAllText(file));
    }

    [Fact]
    public void Apply_Grok_ADenyListBesideAnAskList_IsReportedAndLeft()
    {
        var text = "[permission]\nask = [\"Bash(git push*)\"]\ndeny = [\"Bash(rm*)\"]\n";
        var file = Write(Path.Combine(Folder, ".grok", "config.toml"), text);

        var entry = Assert.Single(Apply(AgentKind.Grok));

        Assert.False(entry.Rewritten);
        Assert.Equal(new[] { "Bash(rm*)" }, entry.Rules);
        Assert.StartsWith("its [permission] section has both a deny list and an ask list", entry.Problem);
        Assert.Equal(text, File.ReadAllText(file));
    }

    [Fact]
    public void Apply_Grok_AlsoReadsTheClaudeCodeSettings_ItFallsBackTo()
    {
        Write(Path.Combine(Folder, ".claude", "settings.json"), "{\"permissions\": {\"deny\": [\"Bash(rm:*)\"]}}");

        Assert.True(Assert.Single(Apply(AgentKind.Grok)).Rewritten);
    }

    // ---------- OpenCode ----------

    [Fact]
    public void Apply_OpenCode_TurnsEveryDenyUnderPermissionIntoAsk()
    {
        var file = Write(Path.Combine(Folder, "opencode.json"),
            "{\"model\": \"x\", \"permission\": {\"edit\": \"deny\", \"bash\": {\"rm *\": \"deny\", \"ls *\": \"allow\"}}, \"other\": \"deny\"}");

        var entry = Assert.Single(Apply(AgentKind.OpenCode));

        Assert.Equal(new[] { "permission.edit", "permission.bash.rm *" }, entry.Rules);
        var j = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(file))!;
        Assert.Equal("ask", j["permission"]!["edit"]!.GetValue<string>());
        Assert.Equal("ask", j["permission"]!["bash"]!["rm *"]!.GetValue<string>());
        Assert.Equal("allow", j["permission"]!["bash"]!["ls *"]!.GetValue<string>());
        Assert.Equal("deny", j["other"]!.GetValue<string>());
    }

    // ---------- agents with nothing to rewrite ----------

    [Theory]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Copilot)]
    public void Apply_AnAgentThatReadsNoneOfTheseFiles_ChangesNothing(AgentKind kind)
    {
        var text = "{\"permissions\": {\"deny\": [\"Bash(rm:*)\"]}}";
        var file = Write(Path.Combine(Folder, ".claude", "settings.json"), text);

        Assert.Empty(Apply(kind));
        Assert.Equal(text, File.ReadAllText(file));
    }

    // ---------- the launch log ----------

    [Fact]
    public void LogLine_NamesEachFileAndRule_RewrittenAndLeft_AndNothingForALaunchWithNone()
    {
        var line = DenyRulesBecomeAsk.LogLine(new[]
        {
            new FolderDenyRulesDto { File = "a/settings.json", Rules = new() { "Bash(rm:*)", "Write(**)" }, Rewritten = true },
            new FolderDenyRulesDto { File = "managed.json", Rules = new() { "Bash(curl:*)" }, Problem = "it is managed" },
            new FolderDenyRulesDto { File = "b.json", Problem = "it is not JSON" },
        });

        Assert.Equal("DENY RULES TURNED INTO ASK RULES (the owner's standing approval): a/settings.json: Bash(rm:*), Write(**). " +
                     "REFUSALS THE DIRECTOR COULD NOT TURN INTO ASK RULES: managed.json (Bash(curl:*)): it is managed; b.json: it is not JSON.", line);
        Assert.Null(DenyRulesBecomeAsk.LogLine(Array.Empty<FolderDenyRulesDto>()));
    }
}
