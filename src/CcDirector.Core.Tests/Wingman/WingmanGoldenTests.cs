using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Claude;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Contracts;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

// =====================================================================================
// The #209 eval-harness skeleton: replay GOLDEN TurnPackages through the REAL contract
// and gate regressions mechanically. Goldens are review-PASSED production cases
// (package + the brief the warm brain wrote) exported by the wingman-brief-reviewer
// loop. They contain real session content, so they live OUTSIDE the repo:
//
//   %LOCALAPPDATA%\cc-director\wingman-goldens\case-<sid8>-t<N>\{package,brief,meta}.json
//   (override root with CC_WINGMAN_GOLDENS)
//
// On machines without goldens (CI, fresh clones) the suite passes vacuously and says so
// - the gate is a local regression net for contract editors, not a build requirement.
//
// What a failure means: your contract change either breaks prompt assembly on a real
// package, or rejects a brief that production validated and a reviewer passed - i.e. a
// REGRESSION against ground truth, not a style opinion. The model-quality leg of #209
// (does a NEW brief score as well as the golden?) stays in the reviewer loop; this gate
// is the mechanical half.
// =====================================================================================
public sealed class WingmanGoldenTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ITestOutputHelper _output;

    public WingmanGoldenTests(ITestOutputHelper output) => _output = output;

    private static string GoldensRoot =>
        Environment.GetEnvironmentVariable("CC_WINGMAN_GOLDENS")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "cc-director", "wingman-goldens");

    [Fact]
    public void Goldens_ReplayThroughContract_NoRegressions()
    {
        if (!Directory.Exists(GoldensRoot))
        {
            _output.WriteLine($"no goldens at {GoldensRoot} - vacuous pass (export with the wingman-brief-reviewer loop)");
            return;
        }

        var failures = new List<string>();
        var cases = Directory.GetDirectories(GoldensRoot, "case-*");
        _output.WriteLine($"replaying {cases.Length} goldens from {GoldensRoot}");

        foreach (var dir in cases)
        {
            var name = Path.GetFileName(dir);
            var package = JsonSerializer.Deserialize<TurnPackage>(
                File.ReadAllText(Path.Combine(dir, "package.json")), Json);
            var brief = JsonSerializer.Deserialize<TurnBriefDto>(
                File.ReadAllText(Path.Combine(dir, "brief.json")), Json);
            if (package is null || brief is null)
            {
                failures.Add($"{name}: package/brief failed to deserialize");
                continue;
            }

            // Gate 1: prompt assembly survives the real package and carries its sections.
            var prompt = TurnBriefContract.BuildPrompt(package);
            if (string.IsNullOrWhiteSpace(prompt) || !prompt.Contains("=== CURRENT SCREEN", StringComparison.Ordinal))
                failures.Add($"{name}: BuildPrompt lost the screen section");
            var parked = package.ParkedComposerText;
            var hasParked = !string.IsNullOrWhiteSpace(parked);
            // Full header incl. parenthetical: the RULES text also names the section.
            const string parkedSection = "=== PARKED, UNSENT USER REPLY (extracted mechanically from the composer) ===";
            if (hasParked != prompt.Contains(parkedSection, StringComparison.Ordinal))
                failures.Add($"{name}: parked section presence != ParkedComposerText (hasParked={hasParked})");

            // Gate 2: the brief production validated (and a reviewer passed) must STILL
            // validate - a contract change rejecting it is a regression against ground truth.
            var revalidated = TurnBriefContract.ParseAndValidate(ToContractJson(brief), package, "golden:replay");
            if (revalidated is null)
            {
                failures.Add($"{name}: previously-good brief now REJECTED by validation");
                continue;
            }

            // Gate 3: the parked invariant holds on ground truth end-to-end.
            if (hasParked && parked is not null)
            {
                if (revalidated.NeedsYou is null)
                    failures.Add($"{name}: parked text present but needsYou null after revalidation");
                else if (BriefBuilder.FindVerbatim(revalidated.NeedsYou.Statement, parked) is null)
                    failures.Add($"{name}: statement no longer quotes the parked reply");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} golden regression(s):\n" + string.Join("\n", failures));
    }

    /// <summary>Maps a stored TurnBriefDto back to the model-output JSON shape that
    /// ParseAndValidate consumes. Mechanical field-for-field - no interpretation.</summary>
    private static string ToContractJson(TurnBriefDto brief)
    {
        var root = new JsonObject
        {
            ["headline"] = brief.Headline,
            ["newChapter"] = brief.NewChapter,
            ["turnTitle"] = brief.TurnTitle,
            ["intent"] = brief.Intent,
            ["did"] = new JsonArray(brief.Did.Select(d => (JsonNode)d).ToArray()),
        };

        if (brief.NeedsYou is { } n)
        {
            var options = new JsonArray(n.Options.Select(o => (JsonNode)new JsonObject
            {
                ["key"] = o.Key,
                ["send"] = o.Send,
                ["note"] = o.Note,
                ["recommended"] = o.Recommended,
            }).ToArray());
            root["needsYou"] = new JsonObject
            {
                ["statement"] = n.Statement,
                ["answerVia"] = n.AnswerVia,
                ["selectionMode"] = n.SelectionMode,
                ["submit"] = n.Submit,
                ["options"] = options,
                ["evidence"] = n.Evidence,
                ["urgency"] = n.Urgency,
                ["confidence"] = n.Confidence,
                ["railLine"] = n.RailLine,
                ["ifIgnored"] = n.IfIgnored,
            };
        }
        else
        {
            root["needsYou"] = null;
        }

        root["allClear"] = brief.AllClear;
        root["suggestedAction"] = brief.SuggestedAction is { } sa
            ? new JsonObject { ["type"] = sa.Type, ["reason"] = sa.Reason }
            : null;

        return root.ToJsonString();
    }
}
