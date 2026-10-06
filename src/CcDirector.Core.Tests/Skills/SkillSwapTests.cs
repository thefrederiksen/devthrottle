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
/// </summary>
public sealed class SkillSwapTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-swap-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "director", "skills", "installed");
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");

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
            Assert.Empty(Directory.GetDirectories(SkillDirectoryInstaller.StagingRootFor(Shared)));
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
        Assert.Empty(Directory.GetDirectories(SkillDirectoryInstaller.StagingRootFor(Shared)));
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
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "gone")));
        Assert.Empty(Directory.GetDirectories(SkillDirectoryInstaller.StagingRootFor(Shared)));
    }

    [Fact]
    public void Nothing_is_ever_built_inside_a_skills_folder_and_a_staging_folder_name_this_code_did_not_make_is_kept()
    {
        var foreign = Path.Combine(SkillDirectoryInstaller.StagingRootFor(Shared), "somebody-elses-folder");
        Directory.CreateDirectory(foreign);
        Holds(Personal, ("keeper", "v1"));

        var inside = new List<string>();
        SkillDirectoryInstaller.SwapStepForTests.Value = (_, _) =>
            inside.AddRange(Directory.GetDirectories(Shared).Select(Path.GetFileName)!);
        try
        {
            Install(Personal);
        }
        finally
        {
            SkillDirectoryInstaller.SwapStepForTests.Value = null;
        }

        Assert.All(inside, name => Assert.Equal("keeper", name));
        Assert.True(Directory.Exists(foreign));
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
