using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// An architecture fitness function for the two things the release workflow got wrong, both of which
/// produced something that LOOKED right to whoever read it.
///
/// 1. It published a generated list of internal pull-request titles instead of the release notes we
///    wrote (issue #1106). v1.8.7 shipped that way: four pull-request titles and a compare link.
///    Every correct-looking release page before it was a PERSON pasting
///    docs/public/release-notes/&lt;tag&gt;.md over the generated list afterwards, so the page was right
///    by accident. A page of pull-request titles looks like release notes, so nobody checks it.
///
/// 2. It published the release BEFORE attaching its assets (issue #1079), so every release had a
///    window in which /releases/latest named a version whose release-manifest.json did not exist and
///    every updater that checked inside it failed. Measured on v1.8.8: published 10:48:48Z, assets
///    attached 10:54:11Z; a launcher checked at 10:54:05Z, six seconds early, and failed.
///
/// This is a C# test on purpose. CI runs `dotnet test` and nothing else, so a guard written in any
/// other language would never run - and a guard nobody runs is not a guard. It reads the workflow as
/// text, the same way MobileViewportContractTests pins the mobile shells.
///
/// Scope, stated rather than implied: this pins the MECHANISM in the workflow file. It cannot prove
/// what a future GitHub Actions run does. It exists to stop the specific regressions that already
/// shipped - somebody turning generated notes back on, or setting the release straight to published
/// "to save a step".
/// </summary>
public sealed class ReleaseWorkflowContractTests
{
    private const string WorkflowPath = ".github/workflows/release.yml";
    private const string ScriptPath = "scripts/new-release.ps1";
    private const string NotesDir = "docs/public/release-notes";

    private static string Workflow() => File.ReadAllText(Path.Combine(GetRepoRoot(), WorkflowPath));

    [Fact]
    public void Workflow_PublishesOurWrittenNotes_NotAGeneratedListOfPullRequestTitles()
    {
        var yml = Workflow();

        // generate_release_notes is the exact switch that produced the v1.8.7 page. Even set to
        // false it should not be here: its presence is an invitation to flip it back.
        Assert.False(yml.Contains("generate_release_notes", StringComparison.Ordinal),
            $"{WorkflowPath} mentions generate_release_notes again. That switch publishes a list of internal "
            + "pull-request titles, which is what v1.8.7 shipped to strangers. The release body must come from "
            + $"{NotesDir}/<tag>.md and nothing else.");

        Assert.True(Regex.IsMatch(yml, @"body_path:\s*\$\{\{\s*steps\.notes\.outputs\.path\s*\}\}"),
            $"{WorkflowPath} no longer sets body_path from the resolved notes step. Without it the release page "
            + "carries whatever the action decides to invent.");

        Assert.True(yml.Contains($"NOTES=\"{NotesDir}/${{GITHUB_REF_NAME}}.md\"", StringComparison.Ordinal),
            $"{WorkflowPath} no longer resolves the notes file as {NotesDir}/<tag>.md. That path IS the contract - "
            + "the release manager writes that file and the workflow publishes it verbatim.");
    }

    [Fact]
    public void Workflow_StopsTheReleaseWhenTheWrittenNotesAreMissingOrEmpty()
    {
        var yml = Workflow();
        var step = StepBody(yml, "Resolve the written release notes");
        Assert.False(step is null, $"{WorkflowPath} no longer has a 'Resolve the written release notes' step. "
            + "If it was renamed, update this test - do not delete the guard.");

        // Absent file: must exit non-zero, not warn and carry on.
        Assert.True(Regex.IsMatch(step!, @"if\s+\[\s+!\s+-f\s+""\$NOTES""\s+\]"),
            "The notes step no longer tests that the file EXISTS.");
        Assert.True(Regex.IsMatch(step!, @"Release notes missing[\s\S]*?exit 1"),
            "A missing notes file no longer STOPS the release. An empty page that stops the release is safer than "
            + "a confident wrong one that ships - that is the whole point of this guard.");

        // Present but empty is the same defect wearing a different hat, so the floor is on content.
        Assert.True(Regex.IsMatch(step!, @"Release notes empty[\s\S]*?exit 1"),
            "A notes file that exists but says nothing no longer stops the release. A placeholder file would "
            + "satisfy an existence check and publish a blank release page.");

        // No fallback. The one rule that matters: never generate a competing version nobody wrote.
        Assert.False(Regex.IsMatch(step!, @"\|\||else[\s\S]{0,200}(generate|gh api.*commits|git log)"),
            "The notes step appears to fall back to generating notes. It must not: a plausible wrong page is "
            + "worse than no page, because it does not get read.");
    }

    [Fact]
    public void Workflow_AttachesEveryAssetToADraft_ThenPublishesInASeparateStep()
    {
        var yml = Workflow();

        var draftStep = StepBody(yml, "Create draft release with all assets");
        Assert.False(draftStep is null, $"{WorkflowPath} no longer has a 'Create draft release with all assets' step. "
            + "The release must be assembled as a draft; a draft is not 'latest', so nothing can see it half-built.");

        Assert.True(Regex.IsMatch(draftStep!, @"draft:\s*true"),
            "The release is created with draft: false again. That is the #1079 defect exactly: publishing makes a "
            + "release 'latest' instantly while its assets attach minutes later, and every updater that checks "
            + "inside that window fails. Measured window on v1.8.8: 5m23s.");

        Assert.True(Regex.IsMatch(draftStep!, @"files:\s*release-files/\*"),
            "The draft step no longer attaches the assets, so publishing could not be the completing step.");

        var publishStep = StepBody(yml, "Verify the manifest is attached, then publish");
        Assert.False(publishStep is null, $"{WorkflowPath} no longer has the publish step. Creating a draft and never "
            + "publishing it would ship nothing at all.");

        Assert.True(publishStep!.Contains("release-manifest.json", StringComparison.Ordinal),
            "The publish step no longer checks that release-manifest.json is attached. Every updater resolves the "
            + "manifest first, so a release published without it is one nobody can install - and it takes the "
            + "'latest' slot from the previous working release.");
        Assert.True(Regex.IsMatch(publishStep!, @"Manifest not attached[\s\S]*?exit 1"),
            "A draft with no manifest is no longer refused; it would be published anyway.");
        Assert.True(Regex.IsMatch(publishStep!, @"--method PATCH[^\n]*releases/\$RELEASE_ID[^\n]*draft=false"),
            "The publish step no longer takes the release out of draft, so the draft would never ship.");

        // By ID, not by tag. GitHub associates a draft with its tag only on PUBLISH, so the by-tag
        // endpoint cannot see this release yet - a tag lookup here asks a question about a different
        // release (or none) and would read as an answer about this one.
        Assert.True(publishStep!.Contains("steps.draft.outputs.id", StringComparison.Ordinal),
            "The publish step no longer takes the release id from the draft step.");
        Assert.True(Regex.IsMatch(publishStep!, @"if \[ -z ""\$RELEASE_ID"" \][\s\S]*?exit 1"),
            "An empty release id no longer stops the step. It would then check the assets of nothing and "
            + "publish nothing, while the run went green.");

        // Order is the property that matters: assemble, then publish. Not the other way round.
        Assert.True(yml.IndexOf("Create draft release with all assets", StringComparison.Ordinal)
                    < yml.IndexOf("Verify the manifest is attached, then publish", StringComparison.Ordinal),
            "The publish step runs BEFORE the assets are attached. That ordering is the defect.");
    }

    [Fact]
    public void NewReleaseScript_RefusesToTagWhenTheWrittenNotesAreMissing()
    {
        var script = File.ReadAllText(Path.Combine(GetRepoRoot(), ScriptPath));

        Assert.True(script.Contains(@"docs\public\release-notes\$tagName.md", StringComparison.Ordinal),
            $"{ScriptPath} no longer looks for the written release notes. The workflow's copy of this check can "
            + "only fail AFTER the tag is pushed and the whole build has run, and a pushed tag cannot be "
            + "un-pushed. Checking here costs nothing.");

        // The guard must TERMINATE, by whatever route. The script may exit inline or call its Fail
        // helper; both end the run, and pinning one spelling would fail an honest refactor while
        // proving nothing extra. What must not happen is warning and carrying on, because that
        // pushes the tag.
        Assert.True(Regex.IsMatch(script, @"if \(-not \(Test-Path \$notesPath\)\)[\s\S]*?(exit 1|Fail )"),
            $"{ScriptPath} no longer EXITS when the notes file is absent. Warning and continuing would push the tag.");

        Assert.True(Regex.IsMatch(script, @"\$notesChars -lt 200[\s\S]*?(exit 1|Fail )"),
            $"{ScriptPath} no longer rejects a placeholder notes file. The workflow applies a 200-character floor; "
            + "these two must agree, or the script waves through exactly what the workflow will reject.");

        // ...and if it terminates via the helper, the helper has to actually terminate. Without this
        // the assertions above could be satisfied by a Fail that printed a message and returned,
        // which is precisely the "warned and carried on" failure they exist to prevent.
        if (Regex.IsMatch(script, @"function Fail"))
        {
            var fail = Regex.Match(script, @"function Fail[\s\S]*?\n\}");
            Assert.True(fail.Success && fail.Value.Contains("exit 1", StringComparison.Ordinal),
                $"{ScriptPath} defines a Fail helper that does not exit. Every guard that calls it would print its "
                + "message and then carry on to push the tag - the exact defect these guards exist to stop.");
        }

        // The tag step checks the notes IN THE CANDIDATE, and it has to do so BEFORE the tag is created, or it
        // is decoration.
        var guardAt = script.IndexOf("ls-tree --name-only $candidate -- $notesRel", StringComparison.Ordinal);
        var tagAt = script.IndexOf("git -C $repoRoot tag $tagName", StringComparison.Ordinal);
        Assert.True(guardAt > 0 && tagAt > 0 && guardAt < tagAt,
            $"{ScriptPath} checks the release notes AFTER creating the tag. The point of the local guard is to fail "
            + "while nothing has happened yet.");
    }

    /// <summary>
    /// The frozen candidate (Release Process Fix mission, 10 October 2026). The release workflow runs no
    /// tests and a pushed tag cannot be un-pushed, so the script may tag only a commit the release gate
    /// passed on. It used to merge the bump and tag in one run with no gate between them. Now step 1
    /// freezes the candidate and never tags; step 2 (-Tag) tags only after assert-gated.ps1 accepts it.
    /// </summary>
    [Fact]
    public void NewReleaseScript_TagsOnlyACandidateTheReleaseGateAccepted()
    {
        var script = File.ReadAllText(Path.Combine(GetRepoRoot(), ScriptPath));

        var tagCalls = Regex.Matches(script, @"git -C \$repoRoot tag \$tagName");
        Assert.True(tagCalls.Count == 1,
            $"{ScriptPath} creates a tag in {tagCalls.Count} places. Exactly one place may tag: the -Tag step, "
            + "after the gate is proven.");

        var tagAt = tagCalls[0].Index;
        var assertAt = script.IndexOf("& $assertGated $candidate", StringComparison.Ordinal);
        Assert.True(assertAt > 0 && assertAt < tagAt,
            $"{ScriptPath} tags without first running assert-gated.ps1 on the candidate. A tag on a commit the "
            + "release gate never passed ships whatever the default gate does not run.");

        var refusal = Regex.Match(script, @"& \$assertGated \$candidate\s*\r?\n\s*if \(\$LASTEXITCODE -ne 0\) \{\s*\r?\n\s*Fail ");
        Assert.True(refusal.Success,
            $"{ScriptPath} runs assert-gated.ps1 but does not stop when it refuses.");

        var freezeAt = script.IndexOf("STEP 1: FREEZE THE CANDIDATE ====", StringComparison.Ordinal);
        Assert.True(freezeAt > tagAt,
            $"{ScriptPath}: the candidate step must come after the tag step's exit, so step 1 can never reach "
            + "the tag.");
    }

    /// <summary>
    /// The floors in the two places must be the same number. Two guards for one rule drift apart by
    /// default: the script waves a file through, the workflow rejects it, and the tag is already pushed.
    /// </summary>
    [Fact]
    public void TheNotesContentFloor_IsTheSameNumberInBothGuards()
    {
        var yml = Workflow();
        var script = File.ReadAllText(Path.Combine(GetRepoRoot(), ScriptPath));

        var inWorkflow = Regex.Match(yml, @"CHARS""?\s*-lt\s+(\d+)");
        Assert.True(inWorkflow.Success, $"Could not find the content floor in {WorkflowPath}.");
        var inScript = Regex.Match(script, @"\$notesChars -lt (\d+)");
        Assert.True(inScript.Success, $"Could not find the content floor in {ScriptPath}.");

        Assert.Equal(inWorkflow.Groups[1].Value, inScript.Groups[1].Value);
    }

    /// <summary>
    /// Returns the text of a workflow step from its `- name:` line to the next step at the same
    /// indentation, so an assertion about one step cannot be satisfied by text in another.
    /// </summary>
    private static string? StepBody(string yml, string stepName)
    {
        var marker = $"- name: {stepName}";
        var start = yml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) return null;

        var lineStart = yml.LastIndexOf('\n', start) + 1;
        var indent = start - lineStart;
        var rest = yml[(start + marker.Length)..];

        // The next line that begins a step at the SAME indentation ends this one.
        var next = Regex.Match(rest, $@"\n {{{indent}}}- name: ");
        return next.Success ? rest[..next.Index] : rest;
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
