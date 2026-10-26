using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// A deploy whose new image never comes up rolls production back to last-known-good by itself, and the run
/// still fails (issue #4514). On 2026-10-26 production was down for thirty minutes, most of it waiting for a
/// person to dispatch the rollback by hand; and the outgoing record that deploy kept named the broken image,
/// with commit "unknown", so a rollback to "whatever was live before" would have restored a broken image.
///
/// The rollback is <c>scripts/rollback-to-last-known-good.sh</c>, called from the failure branch of the deploy
/// step in <c>.github/workflows/deploy-hosted-gateway.yml</c>; its behaviour - which image it pins, in every
/// shape including the 2026-10-26 one - is proved by <c>scripts/rollback-to-last-known-good.test.sh</c>
/// against a fake az, a fake gh and a fake /healthz.
///
/// This guard keeps the wiring honest from the default gate, without starting any process: the failure
/// branch calls the script and still exits red; nothing hands it the outgoing record; it resolves only
/// last-known-good, through the resolver it shares with the manual rollback workflow; it pins once; and
/// nothing dispatches the rollback workflow, which would deadlock on the concurrency group the deploy holds.
/// Each rule has a mutation below that must make it fail, so a check that quietly stops checking is caught.
/// </summary>
public sealed class ProductionRollsBackToLastKnownGoodTests
{
    private const string DeployStepName = "- name: Deploy the new image in place and measure the outage";
    private const string ImagePin = "az webapp config container set";
    private const string FailureBranchOpen = "if [ \"$streak\" -lt 3 ]; then";
    private const string RollbackCall = "bash scripts/rollback-to-last-known-good.sh";
    private const string ScriptPath = "scripts/rollback-to-last-known-good.sh";
    private const string ResolverPath = "scripts/resolve-gateway-image.sh";
    private const string OldDecision = "does not roll back by itself";
    private const string NewRuling = "THIS REVERSES AN EARLIER DECISION, ON THE OWNER'S RULING (issue #4514";

    [Fact]
    public void DeployWorkflow_RollsBackToLastKnownGood_WhenTheNewImageNeverComesUp()
    {
        var (workflow, script, rollbackWorkflow) = ReadSources();

        var violations = Violations(workflow, script, rollbackWorkflow);

        Assert.True(violations.Count == 0, string.Join("\n", violations));
    }

    [Fact]
    public void RollbackProof_IsCommittedBesideTheScripts()
    {
        var root = RepoRoot();
        Assert.True(File.Exists(Path.Combine(root, ResolverPath)), "The shared image resolver is missing.");
        Assert.True(File.Exists(Path.Combine(root, "scripts", "rollback-to-last-known-good.test.sh")),
            "The fake-backed proof of the automatic rollback is missing.");
        var ci = File.ReadAllText(Path.Combine(root, ".github", "workflows", "ci.yml"));
        Assert.Contains("run: bash scripts/rollback-to-last-known-good.test.sh", ci, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Mutations() => new()
    {
        { "the failure branch no longer calls the rollback", "no longer runs bash scripts/rollback-to-last-known-good.sh" },
        { "the failure branch ends green after the rollback", "does not end with exit 1" },
        { "the failure branch hands the outgoing image to the rollback", "hands the outgoing record to the rollback" },
        { "the failure branch dispatches the rollback workflow", "The deploy step dispatches a workflow" },
        { "the script dispatches the rollback workflow", "The rollback script dispatches a workflow" },
        { "the script resolves the outgoing commit instead of last-known-good", "The rollback script reads an outgoing value" },
        { "the script resolves another tag instead of last-known-good", "resolves [live]" },
        { "the script tries a second image", "pins 2 times" },
        { "the manual rollback workflow keeps its own copy of the resolution", "resolves images itself again" },
        { "the old does-not-roll-back decision is back", "still records the old decision" },
        { "the issue permission is gone", "no longer grants issues: write" },
    };

    [Theory]
    [MemberData(nameof(Mutations))]
    public void Violations_CatchEachMutation(string mutation, string expectedViolation)
    {
        // Arrange
        var (workflow, script, rollbackWorkflow) = ReadSources();
        Assert.Empty(Violations(workflow, script, rollbackWorkflow));

        // Act
        switch (mutation)
        {
            case "the failure branch no longer calls the rollback":
                workflow = ReplaceOnce(workflow, "              " + RollbackCall + " || rc=$?\n", "              true || rc=$?\n");
                break;
            case "the failure branch ends green after the rollback":
                workflow = ReplaceOnce(workflow, "the run is red on purpose.\"\n            exit 1\n", "the run is red on purpose.\"\n            exit \"$rc\"\n");
                break;
            case "the failure branch hands the outgoing image to the rollback":
                workflow = ReplaceOnce(workflow, "FAILED_DIGEST=\"${{ steps.digest.outputs.digest }}\"",
                    "FAILED_DIGEST=\"${{ steps.digest.outputs.digest }}\" OUTGOING_IMAGE=\"${{ steps.outgoing_image.outputs.image }}\"");
                break;
            case "the failure branch dispatches the rollback workflow":
                workflow = ReplaceOnce(workflow, "            rc=0\n",
                    "            rc=0\n            gh workflow run rollback-hosted-gateway.yml --ref main -f commit=last-known-good\n");
                break;
            case "the script dispatches the rollback workflow":
                script = ReplaceOnce(script, "# 3. PIN, ONCE.\n", "# 3. PIN, ONCE.\ngh api -X POST repos/x/y/actions/workflows/rollback-hosted-gateway.yml/dispatches\n");
                break;
            case "the script resolves the outgoing commit instead of last-known-good":
                script = ReplaceOnce(script, "bash \"$RESOLVER\" last-known-good", "bash \"$RESOLVER\" \"$OUTGOING_COMMIT\"");
                break;
            case "the script resolves another tag instead of last-known-good":
                script = ReplaceOnce(script, "bash \"$RESOLVER\" last-known-good", "bash \"$RESOLVER\" live");
                break;
            case "the script tries a second image":
                script = ReplaceOnce(script, "# 4. WAIT, ONCE",
                    "az webapp config container set --resource-group \"$AZURE_RG\" --name \"$APP\" --docker-custom-image-name \"$OTHER\"\n# 4. WAIT, ONCE");
                break;
            case "the manual rollback workflow keeps its own copy of the resolution":
                rollbackWorkflow = ReplaceOnce(rollbackWorkflow, "          bash scripts/resolve-gateway-image.sh",
                    "          DIGEST=$(az acr repository show --name \"$ACR\" --image \"$REF_ARG\" --query digest --output tsv)\n          bash scripts/resolve-gateway-image.sh");
                break;
            case "the old does-not-roll-back decision is back":
                workflow = ReplaceOnce(workflow, NewRuling, "It " + OldDecision + ": whether the old image is better is a human decision (issue #4514");
                break;
            case "the issue permission is gone":
                workflow = ReplaceOnce(workflow, "  issues: write ", "  # issues: write ");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation), mutation, "unknown mutation");
        }
        var violations = Violations(workflow, script, rollbackWorkflow);

        // Assert
        Assert.True(violations.Any(x => x.Contains(expectedViolation, StringComparison.Ordinal)),
            $"The guard did not notice: {mutation}. It reported: [{string.Join(" | ", violations)}]");
    }

    /// <summary>Every way the deploy can stop rolling back to last-known-good, as readable sentences.</summary>
    private static List<string> Violations(string workflow, string script, string rollbackWorkflow)
    {
        var v = new List<string>();

        if (workflow.Contains(OldDecision, StringComparison.Ordinal))
            v.Add($"The deploy workflow still records the old decision ('{OldDecision}').");
        if (!workflow.Contains(NewRuling, StringComparison.Ordinal))
            v.Add("The comment recording the owner's ruling for automatic rollback is gone.");
        if (!Regex.IsMatch(workflow, @"^  issues: write\b", RegexOptions.Multiline))
            v.Add("The deploy workflow no longer grants issues: write, so the rollback cannot raise its alert.");

        var stepAt = workflow.IndexOf(DeployStepName, StringComparison.Ordinal);
        if (stepAt < 0)
        {
            v.Add("The deploy step was renamed; this guard can no longer find the failure branch.");
            return v;
        }
        var stepEnd = workflow.IndexOf("\n      - name:", stepAt + DeployStepName.Length, StringComparison.Ordinal);
        var step = workflow[stepAt..(stepEnd < 0 ? workflow.Length : stepEnd)];
        var commands = string.Join("\n", Commands(step));
        if (Dispatches(commands))
            v.Add("The deploy step dispatches a workflow; inside the held concurrency group that deadlocks.");

        // The failure branch of the wait AFTER the image pin (the earlier one belongs to the Teams switches).
        var pinAt = Regex.Match(step, @"^[ \t]*" + Regex.Escape(ImagePin), RegexOptions.Multiline);
        var branchAt = pinAt.Success ? step.IndexOf(FailureBranchOpen, pinAt.Index, StringComparison.Ordinal) : -1;
        if (branchAt < 0)
        {
            v.Add("The failure branch after the image pin is gone; this guard cannot check the rollback.");
            return v;
        }
        var branchEnd = step.IndexOf("\n          fi\n", branchAt, StringComparison.Ordinal);
        var branchLines = Commands(step[branchAt..(branchEnd < 0 ? step.Length : branchEnd)]);
        var callAt = branchLines.FindIndex(l => l.Contains(RollbackCall, StringComparison.Ordinal));
        if (callAt < 0)
            v.Add("The failure branch no longer runs " + RollbackCall + ".");
        if (branchLines.Count == 0 || branchLines[^1] != "exit 1" || branchLines.FindLastIndex(l => l == "exit 1") < callAt)
            v.Add("The failure branch does not end with exit 1 after the rollback, so an automatic recovery could read as a green deploy.");
        var branch = string.Join("\n", branchLines);
        if (Regex.IsMatch(branch, @"steps\.outgoing|\$\{?OUTGOING|outgoing_image", RegexOptions.IgnoreCase))
            v.Add("The failure branch hands the outgoing record to the rollback; on 2026-10-26 that record was the broken image.");
        if (!branch.Contains("FAILED_DIGEST=\"${{ steps.digest.outputs.digest }}\"", StringComparison.Ordinal))
            v.Add("The failure branch no longer tells the rollback which image just failed.");

        var scriptCommands = Commands(script);
        var scriptText = string.Join("\n", scriptCommands);
        if (Dispatches(scriptText))
            v.Add("The rollback script dispatches a workflow; inside the held concurrency group that deadlocks.");
        if (Regex.IsMatch(scriptText, @"\$\{?OUTGOING", RegexOptions.IgnoreCase))
            v.Add("The rollback script reads an outgoing value; its only target is last-known-good.");
        if (!scriptCommands.Any(l => l.Contains("RESOLVER=\"$HERE/resolve-gateway-image.sh\"", StringComparison.Ordinal)))
            v.Add("The rollback script no longer uses the shared resolver.");
        var resolves = Regex.Matches(scriptText, @"bash ""\$RESOLVER"" ([^\s);]+)").Select(m => m.Groups[1].Value).ToList();
        if (resolves.Count != 1 || resolves[0] != "last-known-good")
            v.Add($"The rollback script must resolve exactly one target, last-known-good; it resolves [{string.Join(", ", resolves)}].");
        var pins = scriptCommands.Count(l => Regex.IsMatch(l, @"^(?:if !\s*)?" + Regex.Escape(ImagePin)));
        if (pins != 1)
            v.Add($"The rollback script pins {pins} times; it must pin once and never try further targets.");

        var manual = string.Join("\n", Commands(rollbackWorkflow));
        if (!manual.Contains("bash scripts/resolve-gateway-image.sh \"$COMMIT_INPUT\"", StringComparison.Ordinal))
            v.Add("The manual rollback workflow no longer uses the shared resolver.");
        if (manual.Contains("az acr repository show", StringComparison.Ordinal))
            v.Add("The manual rollback workflow resolves images itself again: two copies of the resolution will drift.");

        return v;
    }

    private static bool Dispatches(string commands) =>
        Regex.IsMatch(commands, @"workflow\s+run\b|/dispatches\b|createWorkflowDispatch", RegexOptions.IgnoreCase);

    /// <summary>The trimmed lines that are not blank and not comments, so a comment quoting a command cannot satisfy or trip a rule.</summary>
    private static List<string> Commands(string text) =>
        text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).ToList();

    private static string ReplaceOnce(string text, string find, string replace)
    {
        var at = text.IndexOf(find, StringComparison.Ordinal);
        Assert.True(at >= 0, $"The mutation cannot find its anchor '{find}'; update the mutation with the code.");
        return text[..at] + replace + text[(at + find.Length)..];
    }

    private static (string Workflow, string Script, string RollbackWorkflow) ReadSources()
    {
        var root = RepoRoot();
        string Read(params string[] parts) => File.ReadAllText(Path.Combine([root, .. parts])).Replace("\r\n", "\n");
        return (Read(".github", "workflows", "deploy-hosted-gateway.yml"),
                Read(ScriptPath),
                Read(".github", "workflows", "rollback-hosted-gateway.yml"));
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir, ".github", "workflows", "deploy-hosted-gateway.yml")))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
