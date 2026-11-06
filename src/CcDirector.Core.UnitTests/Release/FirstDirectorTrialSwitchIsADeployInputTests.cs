using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// The first-Director 7-day Pro trial (Website Black mission; <c>src/CcDirector.Gateway/Tenancy/FirstDirectorTrial.cs</c>)
/// is turned on in ONE place: the <c>trial_at_first_director</c> input of
/// <c>.github/workflows/deploy-hosted-gateway.yml</c>, which writes the Azure application setting
/// <c>CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR</c>. This guard keeps that from the default gate, without starting any process:
///   1. the input is a choice of unchanged, on and off, and defaults to unchanged;
///   2. the refusal step turns on into 1, off into 0, unchanged into NOTHING, and refuses any other value - before the
///      Azure login, so a refused value has built and changed nothing;
///   3. the setting is written only when that answer is not empty, so a run without the input writes no such
///      setting, and nothing else in the workflow writes it;
///   4. the image carries no line for it at all, so the setting is never stated in two places.
/// The run of the refusal step's own script against every value is in the release's proof
/// (devthrottle docs/missions/website-black-trial-grant/proof.md).
/// </summary>
public sealed class FirstDirectorTrialSwitchIsADeployInputTests
{
    private const string Setting = "CC_GATEWAY_TRIAL_AT_FIRST_DIRECTOR";
    private const string RefusalStepName = "- name: Refuse a Teams switch value that is not on, off or unchanged";
    private const string LoginStepName = "- name: Azure login (OIDC, no stored secret)";

    [Fact]
    public void TheInput_IsAChoiceOfUnchangedOnOff_DefaultingToUnchanged()
    {
        var workflow = Workflow();
        var m = Regex.Match(workflow, @"^      trial_at_first_director:\n(?<body>(?:        .*\n)+)", RegexOptions.Multiline);
        Assert.True(m.Success, "The deploy workflow no longer declares the trial_at_first_director input.");
        var body = m.Groups["body"].Value;
        Assert.Contains("        type: choice\n", body, StringComparison.Ordinal);
        Assert.Contains("        options: [unchanged, 'on', 'off']\n", body, StringComparison.Ordinal);
        Assert.Contains("        default: unchanged\n", body, StringComparison.Ordinal);
    }

    [Fact]
    public void TheRefusalStep_MapsOnTo1_OffTo0_UnchangedToNothing_AndRefusesAnythingElse_BeforeTheLogin()
    {
        var workflow = Workflow();
        var stepAt = workflow.IndexOf(RefusalStepName, StringComparison.Ordinal);
        var loginAt = workflow.IndexOf(LoginStepName, StringComparison.Ordinal);
        Assert.True(stepAt > 0 && loginAt > 0, "The refusal or the login step was renamed; this guard can no longer place them.");
        Assert.True(stepAt < loginAt, "The switch values are checked AFTER the Azure login.");
        var step = workflow[stepAt..workflow.IndexOf("\n      - name:", stepAt + RefusalStepName.Length, StringComparison.Ordinal)];

        Assert.Contains("TRIAL_INPUT: ${{ inputs.trial_at_first_director }}", step, StringComparison.Ordinal);
        Assert.Contains("trial_want=$(to_value \"${TRIAL_INPUT:-unchanged}\")", step, StringComparison.Ordinal);
        Assert.Contains("echo \"trial_want=$trial_want\"", step, StringComparison.Ordinal);
        Assert.Contains("on) echo 1 ;;", step, StringComparison.Ordinal);
        Assert.Contains("off) echo 0 ;;", step, StringComparison.Ordinal);
        Assert.Contains("unchanged) echo '' ;;", step, StringComparison.Ordinal);
        Assert.Contains("*) echo \"REFUSED:", step, StringComparison.Ordinal);
        Assert.Contains("return 1 ;;", step, StringComparison.Ordinal);
        Assert.Contains("set -euo pipefail", step, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSetting_IsWrittenOnlyWhenARunAsksForIt_AndNowhereElse()
    {
        var workflow = Workflow();

        // Decided only from a non-empty requested value.
        Assert.Contains("if [ -n \"$TRIAL_WANT\" ] && [ \"$TRIAL_WANT\" != \"$trial_old\" ]; then trial_write=\"$TRIAL_WANT\"; fi",
            workflow, StringComparison.Ordinal);
        Assert.Contains("trial_write=\"\"\n", workflow, StringComparison.Ordinal);

        // Written only when that decision is not empty: the ONE place the workflow names "<setting>=".
        Assert.Contains($"if [ -n \"$TRIAL_WRITE\" ]; then SETTINGS+=(\"{Setting}=$TRIAL_WRITE\"); fi", workflow, StringComparison.Ordinal);
        Assert.Single(Regex.Matches(workflow, Regex.Escape(Setting + "=")));
    }

    [Fact]
    public void TheImage_CarriesNoLineForTheSetting()
    {
        var dockerfile = File.ReadAllText(Path.Combine(RepoRoot(), "Dockerfile"));
        Assert.DoesNotContain(Setting, dockerfile, StringComparison.Ordinal);
    }

    private static string Workflow() =>
        File.ReadAllText(Path.Combine(RepoRoot(), ".github", "workflows", "deploy-hosted-gateway.yml")).Replace("\r\n", "\n");

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir, ".github", "workflows", "deploy-hosted-gateway.yml")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
