using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The workflow that sets the two Factory Boxes settings on production (<c>.github/workflows/set-gateway-boxes-settings.yml</c>,
/// issue #4258). It restarts production and it carries account ids in a PUBLIC repository, so this guard keeps the
/// properties that matter from the default gate: it serialises with the deploy and the rollback, it checks both values
/// before it logs in to Azure, it writes those two settings and no other, and the tenant ids never pass through a
/// <c>${{ }}</c> expression - GitHub prints a step's script with every expression already filled in, so an expression
/// is a public copy of the value in the run log, whatever the script itself prints.
///
/// What the validation accepts and refuses is proved by running the step's script, which a gate suite may not do (no
/// real process); see the pull request that added the workflow. This guard checks the refusal is still there to run.
/// </summary>
public sealed class BoxesSettingsWorkflowContractTests
{
    private const string CheckStepName = "- name: Check both values";
    private const string LoginStepName = "- name: Azure login (OIDC, no stored secret)";
    private const string WriteStepName = "- name: Set CC_BOXES_DESKTOP_ORIGIN and DEVTHROTTLE_BOXES_TENANT_IDS";
    private const string WaitStepName = "- name: Wait for production to come back";

    // BoxDesktopOrigin.EnvVar. Not read from source because BoxDesktopOrigin.cs reaches main with the relay leg,
    // after this workflow; the tenant list's name IS read from BoxesSwitch below.
    private const string DesktopOriginSetting = "CC_BOXES_DESKTOP_ORIGIN";

    [Fact]
    public void BoxesSettingsWorkflow_SerialisesWithTheDeploy_AndRunsOnlyByHand()
    {
        var workflow = ReadWorkflow();

        Assert.Matches(new Regex(@"^on:\r?\n  workflow_dispatch:\r?\n", RegexOptions.Multiline), workflow);
        Assert.DoesNotMatch(new Regex(@"^  (push|pull_request|schedule|workflow_run):", RegexOptions.Multiline), workflow);
        Assert.Contains("concurrency:\n  group: hosted-gateway-production\n  cancel-in-progress: false", Normalise(workflow), StringComparison.Ordinal);
        Assert.Contains("    environment: hosted-gateway-production", workflow, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxesSettingsWorkflow_ChecksBothValues_BeforeItLogsIn_ThenWritesAndWaits()
    {
        var workflow = ReadWorkflow();

        var checkAt = workflow.IndexOf(CheckStepName, StringComparison.Ordinal);
        var loginAt = workflow.IndexOf(LoginStepName, StringComparison.Ordinal);
        var writeAt = workflow.IndexOf(WriteStepName, StringComparison.Ordinal);
        var waitAt = workflow.IndexOf(WaitStepName, StringComparison.Ordinal);
        Assert.True(checkAt > 0 && loginAt > 0 && writeAt > 0 && waitAt > 0, "A step was renamed; this guard can no longer tell the order.");
        Assert.True(checkAt < loginAt && loginAt < writeAt && writeAt < waitAt,
            "The steps must run check, login, write, wait - a value is refused before production can be touched.");

        var check = Step(workflow, checkAt);
        // The check gates the run: an `if:` or `continue-on-error:` would keep every string below and stop it gating.
        var stepKeys = Regex.Matches(check, @"^        ([a-z-]+):", RegexOptions.Multiline).Select(m => m.Groups[1].Value).ToList();
        Assert.Equal(new[] { "id", "run" }, stepKeys);
        Assert.Contains("\"gateway.devthrottle.com\"", check, StringComparison.Ordinal);
        Assert.Contains("tr '[:upper:]' '[:lower:]'", check, StringComparison.Ordinal);
        Assert.Contains("[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", check, StringComparison.Ordinal);
        Assert.Contains("exit 1", check, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxesSettingsWorkflow_WritesOnlyTheTwoBoxesSettings()
    {
        var workflow = ReadWorkflow();
        var tenantSetting = BoxesSwitchVariable();
        var write = Step(workflow, workflow.IndexOf(WriteStepName, StringComparison.Ordinal));

        var setNames = Regex.Matches(write, @"SETTINGS\+=\(""([A-Z_]+)=").Select(m => m.Groups[1].Value).ToHashSet();
        var deleteNames = Regex.Matches(write, @"DELETES\+=\(""([A-Z_]+)""\)").Select(m => m.Groups[1].Value).ToHashSet();
        var expected = new HashSet<string> { DesktopOriginSetting, tenantSetting };
        Assert.Equal(expected, setNames);
        Assert.Equal(expected, deleteNames);

        // Exactly one set and one delete command, each fed only from those lists.
        Assert.Single(Regex.Matches(workflow, @"az webapp config appsettings set "));
        Assert.Single(Regex.Matches(workflow, @"az webapp config appsettings delete "));
        Assert.Contains("--settings \"${SETTINGS[@]}\"", write, StringComparison.Ordinal);
        Assert.Contains("--setting-names \"${DELETES[@]}\"", write, StringComparison.Ordinal);
    }

    [Fact]
    public void BoxesSettingsWorkflow_NeverPutsTheTenantIdsInAnExpression_AndMasksThemFirst()
    {
        var workflow = ReadWorkflow();

        // Any expression naming the input - inputs.tenant_ids, github.event.inputs.tenant_ids, inputs['tenant_ids'] -
        // is filled in and printed in the public log.
        foreach (Match expression in Regex.Matches(workflow, @"\$\{\{[^}]*\}\}"))
            Assert.DoesNotContain("tenant_ids", expression.Value, StringComparison.Ordinal);
        Assert.DoesNotMatch(new Regex(@"set -[a-z]*x|set -o xtrace"), workflow);

        // Inside the check, the ids are read from the event file and masked before the step prints anything else.
        var check = Step(workflow, workflow.IndexOf(CheckStepName, StringComparison.Ordinal));
        var readAt = check.IndexOf("jq -r '.inputs.tenant_ids", StringComparison.Ordinal);
        var maskAt = check.IndexOf("echo \"::add-mask::$RAW_IDS\"", StringComparison.Ordinal);
        var firstOtherEcho = Regex.Matches(check, @"echo ""(?!::add-mask::)").Select(m => m.Index).DefaultIfEmpty(int.MaxValue).Min();
        Assert.True(readAt > 0, "The check no longer reads the tenant ids from the event file.");
        Assert.True(maskAt > readAt, "The tenant ids are not masked after they are read.");
        Assert.True(maskAt < firstOtherEcho, "The check prints something before the tenant ids are masked.");
        Assert.Contains("echo \"::add-mask::$entry\"", check, StringComparison.Ordinal);
    }

    private static string ReadWorkflow() =>
        File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "set-gateway-boxes-settings.yml"));

    private static string Normalise(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>The step that starts at <paramref name="start"/>, up to the next step.</summary>
    private static string Step(string workflow, int start)
    {
        Assert.True(start > 0, "Could not find the step.");
        var end = workflow.IndexOf("\n      - name:", start + 1, StringComparison.Ordinal);
        return end < 0 ? workflow[start..] : workflow[start..end];
    }

    /// <summary>BoxesSwitch.EnvironmentVariable, read from the Gateway's own source so a rename fails here.</summary>
    private static string BoxesSwitchVariable()
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Boxes", "BoxesSwitch.cs"));
        var m = Regex.Match(text, @"const string EnvironmentVariable = ""([^""]+)""");
        Assert.True(m.Success, "Could not find BoxesSwitch.EnvironmentVariable.");
        return m.Groups[1].Value;
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir, ".github", "workflows", "set-gateway-boxes-settings.yml")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
