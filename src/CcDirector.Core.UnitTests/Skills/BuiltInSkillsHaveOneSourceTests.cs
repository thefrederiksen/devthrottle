using Xunit;

namespace CcDirector.Core.UnitTests.Skills;

/// <summary>
/// A BUILT-IN SKILL HAS ONE SOURCE, AND IT IS THE FILE THAT SHIPS.
///
/// <c>src/CcDirector.Gateway/Skills/Content/&lt;id&gt;.skill.md</c> is compiled into the Gateway binary,
/// seeded into the skill store at startup, and pulled down by every Director onto disk where agents
/// read it. That file is the master.
///
/// <c>.claude/skills/&lt;id&gt;/SKILL.md</c> is NOT a second master. It exists only so Claude Code finds
/// the skill while working inside THIS repository, and it is the shipped body plus the YAML frontmatter
/// Claude Code requires. Its body must equal the shipped body exactly.
///
/// WHY THIS GUARD EXISTS. On 2026-09-14 the two copies had drifted apart in BOTH directions and nothing
/// noticed:
///
///   - The SHIPPED fleet-comms showed five spawn examples and never mentioned <c>--controlled-by</c> or
///     <c>--standalone</c>, while the repository copy explained both. Since 2026-09-13 an undeclared
///     spawn is refused - so every agent OUTSIDE this repository was being handed examples the product
///     rejects, for a day, while the copy a developer reads was correct.
///   - The SHIPPED dev-throttle carried a browsers section the repository copy had lost.
///   - The repository move-session carried a paragraph on why a moved session stays the user's that no
///     user ever saw.
///
/// Each copy had content the other lacked. Neither was a superset. That is what two masters produce.
///
/// AND THE REPOSITORY COPY WINS WHERE IT MATTERS LEAST. Claude Code reads a project's own
/// <c>.claude/skills</c> ahead of the <c>~/.claude/skills</c> links a Director installs - so inside the
/// repository where DevThrottle is built, the hand-edited copy SHADOWS the shipped one. The single place
/// a developer would notice the shipped skill was wrong is the one place they are guaranteed not to.
///
/// SO: CHANGE THE SHIPPED FILE. Never the copy. The copy is regenerated from it, the Gateway is
/// deployed, and the new body comes back down to every machine. A built-in cannot be changed on the
/// Gateway instead - <c>SkillStore</c> holds them READ-ONLY, refuses a tenant skill under a built-in id,
/// and the seeder is the only writer of their content.
/// </summary>
public sealed class BuiltInSkillsHaveOneSourceTests
{
    [Fact]
    public void Every_repository_copy_is_the_shipped_body_and_nothing_else()
    {
        var offenders = new List<string>();

        foreach (var shipped in Directory.GetFiles(SkillContentDirectory(), "*.skill.md"))
        {
            var id = Path.GetFileName(shipped).Replace(".skill.md", string.Empty);
            var copy = Path.Combine(RepoRoot(), ".claude", "skills", id, "SKILL.md");

            // No copy is the BEST state - the skill then comes from the Gateway like it does everywhere
            // else. This guard never demands one exist; it only forbids one that disagrees.
            if (!File.Exists(copy)) continue;

            var body = StripFrontmatter(Read(copy));
            var master = Read(shipped);
            if (string.Equals(body, master, StringComparison.Ordinal)) continue;

            offenders.Add($"{id}: .claude/skills/{id}/SKILL.md does not match " +
                          $"Skills/Content/{id}.skill.md");
        }

        Assert.True(offenders.Count == 0,
            "A built-in skill has TWO different bodies in this repository, and only one of them ships:\n  " +
            string.Join("\n  ", offenders) +
            "\n\nThe file under Skills/Content is the master - it is compiled into the Gateway, served to " +
            "the fleet, and pulled down onto every machine. The .claude/skills copy exists only so Claude " +
            "Code finds the skill inside this repository, and it is that body plus frontmatter.\n\n" +
            "Edit the SHIPPED file, regenerate the copy from it, and deploy the Gateway. Editing the copy " +
            "changes what agents read in this repository and NOTHING that any user receives - which is " +
            "exactly how these drifted apart in both directions on 2026-09-14.");
    }

    [Fact]
    public void A_repository_copy_still_carries_the_frontmatter_Claude_Code_needs()
    {
        // The one legitimate difference, asserted so that "regenerate it" cannot be read as "copy the
        // shipped file verbatim" - which would leave Claude Code unable to see the skill at all.
        foreach (var shipped in Directory.GetFiles(SkillContentDirectory(), "*.skill.md"))
        {
            var id = Path.GetFileName(shipped).Replace(".skill.md", string.Empty);
            var copy = Path.Combine(RepoRoot(), ".claude", "skills", id, "SKILL.md");
            if (!File.Exists(copy)) continue;

            var text = Read(copy);
            Assert.True(text.StartsWith("---", StringComparison.Ordinal),
                $".claude/skills/{id}/SKILL.md has no YAML frontmatter, so Claude Code will not list it. " +
                "The copy is the shipped body PLUS frontmatter, not the shipped body alone.");
            Assert.Contains($"name: {id}", text, StringComparison.Ordinal);
        }
    }

    private static string Read(string path) =>
        File.ReadAllText(path).Replace("﻿", string.Empty).Replace("\r\n", "\n");

    private static string StripFrontmatter(string text)
    {
        if (!text.StartsWith("---", StringComparison.Ordinal)) return text;
        var end = text.IndexOf("---", 3, StringComparison.Ordinal);
        return end < 0 ? text : text[(end + 3)..].TrimStart('\n');
    }

    private static string SkillContentDirectory() =>
        Path.Combine(RepoRoot(), "src", "CcDirector.Gateway", "Skills", "Content");

    /// <summary>
    /// The repository root, walked up from the test binary. Fails loudly rather than passing over an
    /// empty directory: a guard whose pass condition is "found nothing to check" certifies a run that
    /// never happened.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Gateway", "Skills", "Content")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root above " + AppContext.BaseDirectory +
            ". This guard compares two files in the checkout; without it there is nothing to compare.");
    }
}
