using System.Text.Json.Nodes;
using CcDirector.Core.Agents;
using CcDirector.Core.Claude;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Sessions;

/// <summary>
/// RULES AND SKILLS ON THE LAUNCH LINE (Factory Definition mission, phase 5, design section 5; phase 0 proof (a) and
/// finding 3, proof (c)). A seat with rules gets its OWN settings file in its kit - the shared file's hooks, from the
/// same builder, plus its rules - passed as its one <c>--settings</c>; the shared file is never written with them. A seat
/// with factory skills gets <c>--plugin-dir</c>; a rules-only kit has no plugin. An agent that cannot take what the kit
/// carries is refused with the shared table's reason. Every file is in a scratch folder; no agent is started.
/// </summary>
[Trait("Category", "FactoryDefinition")]
public sealed class FactoryKitLaunchTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-kit-line-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string HooksDirectory => Path.Combine(_scratch, "claude-hooks");

    private const string RuledRules =
        "{\n  \"permissions\": {\n    \"deny\": [\n      \"Read(./kit-denied.txt)\",\n      \"Bash(git push:*)\"\n    ],\n    \"ask\": [\n      \"Bash(git tag:*)\"\n    ]\n  }\n}\n";

    private const string NoRules = "{\n  \"permissions\": {\n    \"deny\": [],\n    \"ask\": []\n  }\n}\n";

    /// <summary>A kit folder as FactoryRunKit writes it, with the given rules.json, kit.json and optional plugin.</summary>
    private string Kit(string rules, string kitJson = "{\"kind\": \"seat\", \"secrets\": []}\n", bool plugin = false)
    {
        var dir = Path.Combine(_scratch, "factory-run", Guid.NewGuid().ToString());
        Directory.CreateDirectory(Path.Combine(dir, FactoryRunKit.WorkFolderName));
        File.WriteAllText(Path.Combine(dir, FactoryRunKit.RulesFileName), rules);
        File.WriteAllText(Path.Combine(dir, FactoryRunKit.KitFileName), kitJson);
        if (plugin)
        {
            Directory.CreateDirectory(Path.Combine(dir, FactoryRunKit.PluginFolderName, ".claude-plugin"));
            File.WriteAllText(Path.Combine(dir, FactoryRunKit.PluginFolderName, ".claude-plugin", "plugin.json"), "{\"name\": \"factory-kit\"}\n");
            Directory.CreateDirectory(Path.Combine(dir, FactoryRunKit.PluginFolderName, "skills", "send-mail"));
            File.WriteAllText(Path.Combine(dir, FactoryRunKit.PluginFolderName, "skills", "send-mail", "SKILL.md"), "# Send mail\n");
        }
        return dir;
    }

    private static int Count(string haystack, string needle)
    {
        var count = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = haystack.IndexOf(needle, i + 1, StringComparison.Ordinal))
            count++;
        return count;
    }

    [Fact]
    public void PlanFor_ClaudeCodeSeatWithRules_WritesItsOwnSettingsWithTheSharedHooksAndItsRulesAndLeavesTheSharedFileUntouched()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        var sharedBefore = File.ReadAllBytes(shared);
        var kit = Kit(RuledRules);

        var plan = FactoryKitLaunch.PlanFor(AgentKind.ClaudeCode, kit, FactoryKitLaunch.Read(kit), HooksDirectory, forWindows: true);

        Assert.Equal(Path.Combine(kit, ClaudeHookInstaller.SessionSettingsFileName), plan.SettingsPath);
        var own = JsonNode.Parse(File.ReadAllText(plan.SettingsPath!))!.AsObject();
        // The rules, exactly as rules.json carries them.
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(RuledRules)!["permissions"], own["permissions"]));
        // Everything else is the shared file's content, from the same builder: the same hooks, the same script.
        own.Remove("permissions");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(File.ReadAllText(shared)), own));
        // The shared file: not a byte changed, and no rule in it.
        Assert.Equal(sharedBefore, File.ReadAllBytes(shared));
        Assert.DoesNotContain("permissions", File.ReadAllText(shared));
        Assert.DoesNotContain("git push", File.ReadAllText(shared));
    }

    [Fact]
    public void ClaudeArguments_ASeatWithRules_HasOneSettingsNamingTheKitsFile()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        var kit = Kit(RuledRules);
        var plan = FactoryKitLaunch.PlanFor(AgentKind.ClaudeCode, kit, FactoryKitLaunch.Read(kit), HooksDirectory, forWindows: true);

        var line = FactoryKitLaunch.ClaudeArguments("--dangerously-skip-permissions --session-id abc", shared, plan);

        Assert.Equal($"--dangerously-skip-permissions --session-id abc --settings \"{Path.Combine(kit, "settings.json")}\"", line);
        Assert.Equal(1, Count(line, "--settings"));
        Assert.DoesNotContain(shared, line);
    }

    [Fact]
    public void ClaudeArguments_ASeatWithNoRules_IsTodaysLineAndWritesNoSettingsFile()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        var kit = Kit(NoRules);
        var contents = FactoryKitLaunch.Read(kit);

        var plan = FactoryKitLaunch.PlanFor(AgentKind.ClaudeCode, kit, contents, HooksDirectory, forWindows: true);
        var line = FactoryKitLaunch.ClaudeArguments("--dangerously-skip-permissions --session-id abc", shared, plan);

        Assert.False(contents.HasRules);
        Assert.Equal(FactoryKitLaunch.ClaudeArguments("--dangerously-skip-permissions --session-id abc", shared, null), line);
        Assert.Equal($"--dangerously-skip-permissions --session-id abc --settings \"{shared}\"", line);
        Assert.False(File.Exists(Path.Combine(kit, ClaudeHookInstaller.SessionSettingsFileName)));
    }

    [Fact]
    public void ClaudeArguments_UNDEFINED_FACTORY_NoKit_IsTodaysLineByteForByte()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        const string args = "--permission-mode auto --session-id 0f0e";

        // What SessionManager appended before phase 5: the shared file, once, quoted.
        Assert.Equal($"{args} --settings \"{shared}\"".Trim(), FactoryKitLaunch.ClaudeArguments(args, shared, null));
        // And when the hook files could not be written, no --settings at all, as before.
        Assert.Equal(args, FactoryKitLaunch.ClaudeArguments(args, null, null));
    }

    [Fact]
    public void ClaudeArguments_ASeatKitWithFactorySkills_AddsPluginDirForTheKitsPluginFolder()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        var kit = Kit(NoRules, plugin: true);

        var plan = FactoryKitLaunch.PlanFor(AgentKind.ClaudeCode, kit, FactoryKitLaunch.Read(kit), HooksDirectory, forWindows: true);
        var line = FactoryKitLaunch.ClaudeArguments("--session-id abc", shared, plan);

        Assert.Equal($"--session-id abc --settings \"{shared}\" --plugin-dir \"{Path.Combine(kit, "claude-plugin")}\"", line);
    }

    [Fact]
    public void ClaudeArguments_ARulesOnlyKit_HasNoPluginDir()
    {
        var shared = ClaudeHookInstaller.EnsureInstalled(HooksDirectory, forWindows: true)!;
        // A child's kit: rules, no secrets, and never a plugin (the Gateway composes none for it).
        var kit = Kit(RuledRules, kitJson: "{\"kind\": \"rules-only\"}\n");

        var plan = FactoryKitLaunch.PlanFor(AgentKind.ClaudeCode, kit, FactoryKitLaunch.Read(kit), HooksDirectory, forWindows: true);
        var line = FactoryKitLaunch.ClaudeArguments("--session-id abc", shared, plan);

        Assert.Null(plan.PluginDirectory);
        Assert.DoesNotContain("--plugin-dir", line);
        Assert.Contains($"--settings \"{Path.Combine(kit, "settings.json")}\"", line);
    }

    [Fact]
    public void Read_ASeatKit_ReadsTheSecretNamesAndARulesOnlyKitHasNone()
    {
        var seat = FactoryKitLaunch.Read(Kit(NoRules, kitJson: "{\"kind\": \"seat\", \"secrets\": [\"smtp-password\", \"crm-token\"]}\n"));
        var child = FactoryKitLaunch.Read(Kit(RuledRules, kitJson: "{\"kind\": \"rules-only\"}\n"));

        Assert.Equal(new[] { "smtp-password", "crm-token" }, seat.Secrets);
        Assert.Empty(child.Secrets);
        Assert.True(child.HasRules);
    }

    [Fact]
    public void Read_RulesThatAreNotAListOfRules_IsRefusedInWords()
    {
        var kit = Kit("{\"permissions\": {\"deny\": \"Bash(git push:*)\"}}\n");

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryKitLaunch.Read(kit));

        Assert.Equal("the kit's rules.json has a 'deny' that is not a list of rules, so its rules cannot be read", ex.Message);
    }

    [Fact]
    public void Read_RulesWithAnAllowList_IsRefusedInWordsAndNeverPassedThrough()
    {
        var kit = Kit("{\"permissions\": {\"deny\": [], \"ask\": [], \"allow\": [\"Bash(git push:*)\"]}}\n");

        var ex = Assert.Throws<InvalidOperationException>(() => FactoryKitLaunch.Read(kit));

        Assert.Equal("the kit's rules.json has the key 'allow', and a kit carries only 'deny' and 'ask' rules, so its rules cannot be read", ex.Message);
    }

    [Fact]
    public void WhyTheAgentCannotTakeIt_ACodexKitWithRules_IsTheRulesTablesReason()
    {
        var kit = FactoryKitLaunch.Read(Kit(RuledRules));

        Assert.Equal("No session was started: Codex enforces a rule given at launch only while its hook runs; when the hook " +
                     "is missing, fails or times out the command runs anyway, so a Codex seat cannot carry rules.",
            FactoryKitLaunch.WhyTheAgentCannotTakeIt(AgentKind.Codex, kit));
    }

    [Fact]
    public void WhyTheAgentCannotTakeIt_ACodexKitWithFactorySkills_IsTheSkillTablesReason()
    {
        var kit = FactoryKitLaunch.Read(Kit(NoRules, plugin: true));

        Assert.Equal("No session was started: Codex reads skills only from the start folder and its parents, the user's " +
                     "home folder and an administrator folder; none of them is for one session alone, so a Codex seat " +
                     "cannot be given a factory skill.",
            FactoryKitLaunch.WhyTheAgentCannotTakeIt(AgentKind.Codex, kit));
    }

    [Fact]
    public void WhyTheAgentCannotTakeIt_ClaudeCodeWithBoth_OrAnyAgentWithAnEmptyKit_IsNotRefused()
    {
        Assert.Null(FactoryKitLaunch.WhyTheAgentCannotTakeIt(AgentKind.ClaudeCode, FactoryKitLaunch.Read(Kit(RuledRules, plugin: true))));
        Assert.Null(FactoryKitLaunch.WhyTheAgentCannotTakeIt(AgentKind.Codex, FactoryKitLaunch.Read(Kit(NoRules))));
    }

    [Fact]
    public void PlanFor_AnyAgentButClaudeCode_AddsNothingAndWritesNothing()
    {
        var kit = Kit(NoRules);

        var plan = FactoryKitLaunch.PlanFor(AgentKind.Codex, kit, FactoryKitLaunch.Read(kit), HooksDirectory, forWindows: true);

        Assert.Equal(new FactoryKitLaunchPlan(null, null), plan);
        Assert.False(File.Exists(Path.Combine(kit, ClaudeHookInstaller.SessionSettingsFileName)));
    }

    [Fact]
    public void WhyTheSecretsRefuse_EveryAnswer_SaysItInWords()
    {
        var secrets = new[] { "smtp-password", "crm-token" };
        FactoryMachineAnswer Answer(params FactorySecretAnswer[] answers) => new() { Machine = "M1", Secrets = answers.ToList() };

        Assert.Null(FactoryKitLaunch.WhyTheSecretsRefuse("outreach", "M1", secrets, Answer(
            new() { Name = "smtp-password", Held = true, AllowedForRun = true },
            new() { Name = "crm-token", Held = true, AllowedForRun = true })));
        Assert.Equal("The seat outreach needs the secret 'crm-token', which M1 does not hold, so no session was started. " +
                     "Add it there with cc-secrets.",
            FactoryKitLaunch.WhyTheSecretsRefuse("outreach", "M1", secrets, Answer(
                new() { Name = "smtp-password", Held = true, AllowedForRun = true },
                new() { Name = "crm-token", Held = false })));
    }
}
