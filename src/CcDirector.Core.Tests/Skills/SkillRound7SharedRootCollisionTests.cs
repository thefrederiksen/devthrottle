using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// Anything at a wanted skill's name in the shared folder that is not a real directory (devthrottle_internal#2311,
/// review finding SK-F21).
///
/// A plain file, or a dangling link, at the name used to skip the ownership check - the file system does not call
/// either a directory - and go straight to the swap, whose rename collided with it and threw out of the installer:
/// every skill after it was abandoned, no link was made, and no placement was recorded. The entry is the person's: it
/// is left exactly as it is, reported as <see cref="SkillPlacementFault.Shadowed"/>, and the reconcile goes on.
/// </summary>
public sealed class SkillRound7SharedRootCollisionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-round7-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "director", "skills", "installed");
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");

    private static readonly SkillSource Personal = new("gw-1", "tenant-person", null);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        // Links first, as links: a recursive delete must never be asked to go through one.
        foreach (var entry in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(e => e.Length))
        {
            if (new FileInfo(entry).LinkTarget is null)
                continue;
            if (OperatingSystem.IsWindows() && (File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                Directory.Delete(entry, recursive: false);
            else
                File.Delete(entry);
        }
        Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_plain_file_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed()
    {
        Holds(("alpha", "v1"), ("blocked", "v1"), ("omega", "v1"));
        Directory.CreateDirectory(Shared);
        var file = Path.Combine(Shared, "blocked");
        File.WriteAllText(file, "the person's own file\n");

        var placement = Install();

        Assert.True(File.Exists(file), "the person's file at the skill's name is gone");
        Assert.Equal("the person's own file\n", File.ReadAllText(file));
        TheOthersArePlacedAndTheCollisionIsRecorded(placement);
    }

    [Fact]
    public void A_dangling_link_at_a_wanted_name_is_left_untouched_and_the_other_skills_are_placed()
    {
        // A symbolic link to nothing. On Linux and macOS any dangling link reads as "not a directory"; on Windows a
        // dangling junction or directory link still carries the directory attribute and already reached the
        // ownership check, so a FILE symbolic link is the one that shows the hole on this platform.
        Holds(("alpha", "v1"), ("blocked", "v1"), ("omega", "v1"));
        Directory.CreateDirectory(Shared);
        var link = Path.Combine(Shared, "blocked");
        var nowhere = Path.Combine(_root, "outside", "gone");
        try
        {
            File.CreateSymbolicLink(link, nowhere);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Assert.Fail("This machine refuses to create a symbolic link (on Windows that needs Developer Mode or an " +
                        $"administrator), so the dangling-link case cannot be shown here: {ex.Message}");
        }
        Assert.False(Directory.Exists(link), "the test needs an entry the file system does not call a directory");

        var placement = Install();

        Assert.Equal(nowhere, new FileInfo(link).LinkTarget);
        Assert.False(File.Exists(nowhere) || Directory.Exists(nowhere), "something was created at the link's target");
        TheOthersArePlacedAndTheCollisionIsRecorded(placement);
    }

    private void TheOthersArePlacedAndTheCollisionIsRecorded(SkillPlacement placement)
    {
        var problem = Assert.Single(placement.Problems);
        Assert.Equal("blocked", problem.SkillId);
        Assert.Equal(Shared, problem.Target);
        Assert.Equal(SkillPlacementFault.Shadowed, problem.Fault);
        Assert.Contains("blocked", placement.Describe());
        Assert.Contains(Shared, placement.Describe());
        Assert.False(placement.IsComplete);

        Assert.Equal(2, placement.Reachable);
        foreach (var name in new[] { "alpha", "omega" })
        {
            Assert.Contains("v1", File.ReadAllText(Path.Combine(Shared, name, "SKILL.md")));
            Assert.Contains("v1", File.ReadAllText(Path.Combine(LinkRoot, name, "SKILL.md")));
        }
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "blocked")), "a link was made to the person's entry");
        Assert.Empty(Directory.GetDirectories(SkillDirectoryInstaller.StagingRootFor(Shared)!));
    }

    private SkillPlacement Install() =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, "reclaimed.txt"), Personal);

    private void Holds(params (string Name, string Body)[] skills)
    {
        Directory.CreateDirectory(Store);
        foreach (var (name, body) in skills)
        {
            SkillDirectoryInstaller.Materialize(Store, new SkillBundle(
                name, 1, "hash-" + body, "A skill.", new[] { name }, $"# {name}\n\n{body}\n",
                Array.Empty<SkillFileBytes>()), Personal);
        }
    }
}
