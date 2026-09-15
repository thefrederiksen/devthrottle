using System.Text.Json;
using CcDirector.StateAgreementCheck;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE OWNERSHIP TREE AGREES WITH THE ANSWERS THE BROWSER IS MEASURED BY - asserted here, at commit time,
/// in a suite that actually runs.
///
/// <see cref="TreeAgreement"/> reads packages/client-core/src/sessions/tree-agreement.json - the one
/// statement of what the tree answers - runs the REAL C# fold over its sessions, and reports every
/// difference. tree.agreement.test.ts runs the REAL TypeScript fold over the same file and asserts the
/// same answers. So this test is half of a cross-language guard, and it says so out loud: passing here
/// proves the C# side, and NOTHING about the browser side.
///
/// IT LIVES HERE RATHER THAN BESIDE THE FOLD ON PURPOSE. SessionTreeTests sits in
/// CcDirector.Gateway.UnitTests, which is PARKED out of the default gate (issue #2824) and runs only
/// before a release. A guard whose whole value is catching an edit at the moment it is made must not live
/// in a suite that tells a developer nothing at commit time - the repository already wrote that rule down
/// for the skill guards, in scripts/test-local.ps1, and this is the same shape of guard. It costs
/// milliseconds: it reads one file and folds fifteen tiny rosters.
///
/// AND IT IS WATCHED FAILING, below, because a check nobody has seen go red is decoration.
/// </summary>
public sealed class TreeAgreementTests
{
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages"))) dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void TheCsharpFold_AnswersExactlyWhatTheSharedFileSays_OnEveryCase()
    {
        var result = TreeAgreement.Check(RepoRoot());

        // The scope, stated rather than assumed: a pass over zero cases is not a pass, and the reader of a
        // green run should be able to see how much was examined.
        Assert.True(result.Cases >= 15, $"The shared file holds only {result.Cases} case(s) - cases were deleted, not added.");
        Assert.True(result.Sessions >= 49, $"The shared file holds only {result.Sessions} session(s).");
        Assert.Empty(result.Findings.Select(f => f.ToString()));
    }

    [Fact]
    public void ADisagreementAboutTheTreeItself_IsReportedAndNamed()
    {
        // The Cockpit starts listing a crew's children in arrival order instead of the owner's drag order.
        var broken = Mutate("\"children\": { \"108\": [\"102\", \"101\", \"106\"] }",
            "\"children\": { \"108\": [\"106\", \"102\", \"101\"] }");

        var result = TreeAgreement.Check(broken);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("children of 108", finding.Question);
        Assert.Equal("[106, 102, 101]", finding.Expected);
        Assert.Equal("[102, 101, 106]", finding.Actual);
    }

    [Fact]
    public void ADisagreementAboutTheCrewsWords_IsReportedAndNamed()
    {
        var broken = Mutate("\"line\": \"3 under it: 1 working, 2 stopped, 0 need you\"",
            "\"line\": \"3 under it: 1 working, 2 snoozed, 0 need you\"");

        var result = TreeAgreement.Check(broken);

        var finding = Assert.Single(result.Findings);
        Assert.Contains("crew line of 108", finding.Question);
    }

    [Fact]
    public void AFixtureWhoseStampAndRawFactsDisagree_IsItselfAFinding()
    {
        // The bridge between the two languages' inputs. This session is stamped for the browser as parked
        // while its raw facts say it is working - so without this check the two folds could each pass
        // while describing two different sessions.
        var broken = Mutate(
            "{ \"sessionId\": \"112\", \"sortOrder\": 12, \"createdAt\": \"2026-09-14T16:00:00Z\", \"activityState\": \"Working\", \"effectiveColor\": \"blue\", \"triageBucket\": \"active\" }\n      ],\n      \"roots\": [\"100\", \"103\", \"108\", \"112\"],",
            "{ \"sessionId\": \"112\", \"sortOrder\": 12, \"createdAt\": \"2026-09-14T16:00:00Z\", \"activityState\": \"Working\", \"effectiveColor\": \"grey\", \"triageBucket\": \"onHold\" }\n      ],\n      \"roots\": [\"100\", \"103\", \"108\", \"112\"],");

        var result = TreeAgreement.Check(broken);

        Assert.Contains(result.Findings, f => f.Question.Contains("triage bucket of 112"));
        Assert.Contains(result.Findings, f => f.Question.Contains("effective colour of 112"));
    }

    [Fact]
    public void AnExtraCrewTheFileNeverMentions_IsReported()
    {
        // The case that a naive checker misses: it asks only about the parents the file lists, so a fold
        // that invents a crew nobody asked about passes silently. The file's list of which sessions have
        // children at all is therefore compared too.
        var broken = Mutate("\"children\": { \"108\": [\"102\", \"101\", \"106\"] }", "\"children\": {}");

        var result = TreeAgreement.Check(broken);

        Assert.Contains(result.Findings, f => f.Question.Contains("the sessions that have children at all"));
    }

    [Fact]
    public void AFileWithNoCasesInIt_ThrowsRatherThanReportingAgreement()
    {
        var empty = JsonDocument.Parse("{ \"cases\": [] }");

        var ex = Assert.Throws<InvalidOperationException>(() => TreeAgreement.Check(empty));

        Assert.Contains("ZERO cases", ex.Message);
    }

    [Fact]
    public void AFileThatCannotBeRead_ThrowsRatherThanReportingAgreement()
    {
        var wrongShape = JsonDocument.Parse("{ \"notCases\": 1 }");

        Assert.Throws<InvalidOperationException>(() => TreeAgreement.Check(wrongShape));
    }

    [Fact]
    public void AMissingFile_ThrowsRatherThanReportingAgreement()
    {
        Assert.Throws<FileNotFoundException>(() => TreeAgreement.Check(Path.Combine(Path.GetTempPath(), "no-such-repo-root")));
    }

    /// <summary>The shipped file with one exact piece of text swapped, so a fault-injection test breaks the
    /// real fixture rather than inventing a private one that agrees with itself.</summary>
    private static JsonDocument Mutate(string find, string replace)
    {
        var path = Path.Combine(RepoRoot(), TreeAgreement.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        var source = File.ReadAllText(path).Replace("\r\n", "\n");
        var occurrences = source.Split(find).Length - 1;
        Assert.True(occurrences == 1,
            $"The text to mutate appears {occurrences} times in the shared file, not once - this injection would prove nothing.");
        return JsonDocument.Parse(source.Replace(find, replace));
    }
}
