using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// A visible skill folder is never without its marker (devthrottle_internal#2311, review finding SK-F5). The shared
/// folders are reconciled by every Director on the computer, and a folder at a skill's name with no marker - or with
/// no source stamp - is read by every one of them as the owner's own skill and frozen. So a reconciliation killed at
/// ANY step must leave the name either holding a complete, stamped copy or empty, and the next reconciliation must
/// put the skill back. Each test kills the reconciliation by throwing from inside it at one named step.
///
/// Also here: the cleanup of what a killed reconciliation leaves deletes only folders it can prove it made (SK-F8),
/// and copies are built on the volume a linked skills folder really lives on (SK-F9).
/// </summary>
public sealed class SkillSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-swap-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "director", "skills", "installed");
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");
    private string Staging => SkillDirectoryInstaller.StagingRootFor(Shared)!;

    private static readonly SkillSource Personal = new("gw-1", "tenant-person", null);
    private static readonly SkillSource TeamA = new("gw-1", "team-a", "team-a");

    public void Dispose()
    {
        SkillDirectoryInstaller.SwapStepForTests.Value = null;
        if (!Directory.Exists(_root))
            return;
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    // ---- SK-F5: never without its marker ---------------------------------------------------------------------

    [Theory]
    [InlineData("copying")]       // part-way through copying the files
    [InlineData("copied")]        // every file copied, the source stamp not yet written
    [InlineData("staged")]        // the new copy complete and stamped, not yet in place
    [InlineData("moved-aside")]   // the old copy renamed away, the new one not yet renamed in
    [InlineData("swapped")]       // the new copy in place, the old one not yet deleted
    public void A_reconcile_killed_at_any_step_of_replacing_a_skill_is_repaired_by_the_next_one(string step)
    {
        foreach (var source in new[] { Personal, TeamA })
        {
            Reset();
            Holds(source, ("keeper", "v1"));
            Assert.True(Install(source).IsComplete);
            Holds(source, ("keeper", "v2"));

            var reached = KillAt(step, () => Install(source));

            Assert.True(reached, $"the reconcile never reached the step '{step}'");
            AssertNeverWithoutItsMarker(Path.Combine(Shared, "keeper"));

            var after = Install(source);
            Assert.Empty(after.Problems);
            Assert.DoesNotContain(after.Problems, p => p.Fault == SkillPlacementFault.Shadowed);
            Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
            AssertNeverWithoutItsMarker(Path.Combine(Shared, "keeper"));
            Assert.Empty(Directory.GetDirectories(Staging));
        }
    }

    [Theory]
    [InlineData("copying")]
    [InlineData("copied")]
    [InlineData("staged")]
    [InlineData("swapped")]
    public void A_reconcile_killed_while_placing_a_new_skill_is_repaired_by_the_next_one(string step)
    {
        Holds(TeamA, ("new-one", "v1"));

        Assert.True(KillAt(step, () => Install(TeamA)), $"the reconcile never reached the step '{step}'");
        AssertNeverWithoutItsMarker(Path.Combine(Shared, "new-one"));

        Assert.Empty(Install(TeamA).Problems);
        Assert.Contains("v1", File.ReadAllText(Path.Combine(LinkRoot, "new-one", "SKILL.md")));
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public void A_reconcile_killed_while_withdrawing_a_skill_leaves_no_half_deleted_folder_and_the_next_one_finishes()
    {
        Holds(TeamA, ("keeper", "v1"), ("gone", "v1"));
        Assert.True(Install(TeamA).IsComplete);
        Holds(TeamA, ("keeper", "v1"));

        Assert.True(KillAt("withdrawn-aside", () => Install(TeamA)), "the withdrawal never reached its rename");
        Assert.False(Directory.Exists(Path.Combine(Shared, "gone")));

        Assert.Empty(Install(TeamA).Problems);
        Assert.False(Directory.Exists(Path.Combine(Shared, "gone")));
        Assert.False(File.Exists(Path.Combine(LinkRoot, "gone", "SKILL.md")));   // its link is left in place and reads as nothing (SK-F12)
        Assert.Empty(Directory.GetDirectories(Staging));
    }

    [Fact]
    public void Nothing_is_ever_built_inside_a_skills_folder()
    {
        Holds(Personal, ("keeper", "v1"));

        var inside = new List<string>();
        SkillDirectoryInstaller.SwapStepForTests.Value = (_, _) =>
        {
            if (Directory.Exists(Shared))
                inside.AddRange(Directory.GetDirectories(Shared).Select(d => Path.GetFileName(d)));
        };
        try
        {
            Install(Personal);
        }
        finally
        {
            SkillDirectoryInstaller.SwapStepForTests.Value = null;
        }

        Assert.All(inside, name => Assert.Equal("keeper", name));
    }

    // ---- SK-F8: cleanup deletes only what it can prove it made -----------------------------------------------

    [Theory]
    [InlineData("backup.0123456789abcdef0123456789abcdef.old")]          // the review's example: the right SHAPE
    [InlineData("keeper.0123456789abcdef0123456789abcdef.staging")]
    [InlineData("notes.0123456789abcdef0123456789abcdef.withdrawn")]
    [InlineData("somebody-elses-folder")]
    public void A_folder_in_the_staging_folder_without_this_installers_marker_is_never_deleted(string name)
    {
        Directory.CreateDirectory(Staging);
        var foreign = Path.Combine(Staging, name);
        Directory.CreateDirectory(Path.Combine(foreign, "work"));
        File.WriteAllText(Path.Combine(foreign, "work", "precious.txt"), "somebody's work\n");
        // A marker file of the right NAME whose content is not ours proves nothing either.
        File.WriteAllText(Path.Combine(foreign, SkillDirectoryInstaller.StagingMarkerFileName), "not the installer's\n");
        Holds(Personal, ("keeper", "v1"));

        Install(Personal);

        Assert.Equal("somebody's work\n", File.ReadAllText(Path.Combine(foreign, "work", "precious.txt")));
    }

    [Fact]
    public void Every_folder_the_installer_makes_in_the_staging_folder_carries_its_marker_before_anything_else()
    {
        Holds(Personal, ("keeper", "v1"), ("gone", "v1"));
        Install(Personal);
        Holds(Personal, ("keeper", "v2"));

        var seen = new List<string>();
        SkillDirectoryInstaller.SwapStepForTests.Value = (_, _) =>
        {
            foreach (var folder in Directory.GetDirectories(Staging))
            {
                Assert.True(File.Exists(Path.Combine(folder, SkillDirectoryInstaller.StagingMarkerFileName)),
                    $"'{folder}' is in the staging folder without the installer's marker");
                seen.Add(Path.GetFileName(folder));
            }
        };
        try
        {
            Install(Personal);
        }
        finally
        {
            SkillDirectoryInstaller.SwapStepForTests.Value = null;
        }

        Assert.Contains(seen, n => n.EndsWith(".staging", StringComparison.Ordinal));
        Assert.Contains(seen, n => n.EndsWith(".old", StringComparison.Ordinal));
        Assert.Contains(seen, n => n.EndsWith(".withdrawn", StringComparison.Ordinal));
    }

    // ---- SK-F9: build on the volume the skills folder really lives on --------------------------------------------

    [Fact]
    public void A_skills_folder_that_is_a_link_has_its_copies_built_beside_the_folder_it_points_at()
    {
        var real = Path.Combine(_root, "elsewhere", "agent-skills");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(Path.GetDirectoryName(Shared)!);
        Link(Shared, real);

        Assert.Equal(Path.Combine(_root, "elsewhere", "agent-skills.devthrottle-staging"), SkillDirectoryInstaller.StagingRootFor(Shared));

        Holds(TeamA, ("keeper", "v1"), ("gone", "v1"));
        Assert.True(Install(TeamA).IsComplete);
        Holds(TeamA, ("keeper", "v2"));
        Assert.Empty(Install(TeamA).Problems);

        Assert.Contains("v2", File.ReadAllText(Path.Combine(real, "keeper", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(real, "gone")));
        Assert.True(Directory.Exists(Path.Combine(_root, "elsewhere", "agent-skills.devthrottle-staging")));
        Assert.False(Directory.Exists(Path.Combine(_root, "home", ".agents", "skills.devthrottle-staging")),
            "a staging folder was made beside the link's own spelling - on another volume, every move would fail");
    }

    [Fact]
    public void A_skills_folder_linked_to_nowhere_changes_nothing_and_says_why()
    {
        var real = Path.Combine(_root, "elsewhere", "agent-skills");
        Directory.CreateDirectory(real);
        Directory.CreateDirectory(Path.GetDirectoryName(Shared)!);
        Link(Shared, real);
        Directory.Delete(real);   // the link now points at nothing

        Assert.Null(SkillDirectoryInstaller.StagingRootFor(Shared));

        Holds(Personal, ("keeper", "v1"));
        var placement = Install(Personal);

        var problem = Assert.Single(placement.Problems);
        Assert.Equal(SkillPlacementFault.FolderLinkUnresolved, problem.Fault);
        Assert.Contains("is a link whose target cannot be found", placement.Describe());
        Assert.False(Directory.Exists(LinkRoot), "the agent's own folder was touched although nothing could be placed");
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    /// <summary>Run <paramref name="reconcile"/> and kill it when it reaches <paramref name="step"/>, checking at
    /// EVERY step it passes on the way that the skill's visible folder is complete or absent. True when the step
    /// was reached.</summary>
    private static bool KillAt(string step, Action reconcile)
    {
        var reached = false;
        SkillDirectoryInstaller.SwapStepForTests.Value = (at, folder) =>
        {
            AssertNeverWithoutItsMarker(folder);
            if (at != step)
                return;
            reached = true;
            throw new SimulatedKill(step);
        };
        try
        {
            Assert.Throws<SimulatedKill>(reconcile);
        }
        finally
        {
            SkillDirectoryInstaller.SwapStepForTests.Value = null;
        }
        return reached;
    }

    /// <summary>The folder at a skill's name is either absent or a complete copy with its marker and its source
    /// stamp. Anything else is read by every Director as the owner's own skill.</summary>
    private static void AssertNeverWithoutItsMarker(string folder)
    {
        if (!Directory.Exists(folder))
            return;
        var marker = Path.Combine(folder, SkillDirectoryInstaller.MarkerFileName);
        Assert.True(File.Exists(marker), $"'{folder}' is visible without its marker");
        Assert.NotNull(SkillSource.ReadStamp(File.ReadAllLines(marker)));
        Assert.True(File.Exists(Path.Combine(folder, "SKILL.md")), $"'{folder}' is visible without its SKILL.md");
        Assert.False(File.Exists(Path.Combine(folder, SkillDirectoryInstaller.StagingMarkerFileName)),
            $"'{folder}' carries the staging marker into the skills folder");
    }

    /// <summary>A directory link: a junction on Windows (what a person makes without administrator rights), a
    /// symbolic link elsewhere.</summary>
    private static void Link(string link, string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        })!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
    }

    private void Reset()
    {
        Dispose();
        Directory.CreateDirectory(_root);
    }

    private SkillPlacement Install(SkillSource source) =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, "reclaimed.txt"), source);

    private void Holds(SkillSource source, params (string Name, string Body)[] skills)
    {
        if (Directory.Exists(Store))
            Directory.Delete(Store, recursive: true);
        Directory.CreateDirectory(Store);
        foreach (var (name, body) in skills)
        {
            SkillDirectoryInstaller.Materialize(Store, new SkillBundle(
                name, 1, "hash-" + body, "A skill.", new[] { name }, $"# {name}\n\n{body}\n",
                new[] { new SkillFileBytes("references/more.md", "# More\n"u8.ToArray(), false) }), source);
        }
    }

    private sealed class SimulatedKill(string step) : Exception($"killed at '{step}'");
}
