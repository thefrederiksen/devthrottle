using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// The installer changes only what it can prove it made (devthrottle_internal#2311, review round 4).
///
/// A LINK (SK-F10): where a link points is not evidence of who made it. A link is removed only when the record kept
/// beside the shared folder says this installer made it, for this library, and it still points where the record says.
///
/// THE STAGING ROOT (SK-F11): copies are never built inside any folder agents read, and never in a folder this
/// installer did not make. Either way placement changes nothing and says why.
/// </summary>
public sealed class SkillLinkAndStagingOwnershipTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-link-own-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "director", "skills", "installed");
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");

    private static readonly SkillSource Personal = new("gw-1", "tenant-person", null);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            if (Directory.Exists(directory) && (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories))
        {
            // A dangling link is not reported by Directory.Exists; remove those too.
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
                Directory.Delete(directory, recursive: false);
        }
        Directory.Delete(_root, recursive: true);
    }

    // ---- SK-F10: a link is removed only with proof ------------------------------------------------------------

    [Fact]
    public void A_persons_dangling_link_into_the_shared_folder_survives_a_launch_and_works_again_when_its_target_returns()
    {
        Holds(("keeper", "v1"));
        Assert.True(Install().IsComplete);

        // The person's own skill, linked by hand the same way the installer links - and its folder is moved away for
        // a moment, so the link dangles.
        var handMade = Path.Combine(Shared, "hand-made");
        Directory.CreateDirectory(handMade);
        File.WriteAllText(Path.Combine(handMade, "SKILL.md"), "# hand-made\n");
        var link = Path.Combine(LinkRoot, "hand-made");
        Link(link, handMade);
        var away = Path.Combine(_root, "hand-made-away");
        Directory.Move(handMade, away);

        Install();

        Assert.True(IsLink(link), "the person's dangling link was deleted by a launch");
        Directory.Move(away, handMade);
        Assert.Equal("# hand-made\n", File.ReadAllText(Path.Combine(link, "SKILL.md")));
    }

    [Fact]
    public void Our_own_recorded_link_is_still_withdrawn_and_leaves_the_record()
    {
        Holds(("keeper", "v1"), ("gone", "v1"));
        Assert.True(Install().IsComplete);
        Assert.Contains("gone", File.ReadAllText(SkillLinkRecord.PathFor(Shared)));

        Holds(("keeper", "v1"));
        Assert.Empty(Install().Problems);

        Assert.False(IsLink(Path.Combine(LinkRoot, "gone")));
        Assert.False(Directory.Exists(Path.Combine(LinkRoot, "gone")));
        Assert.DoesNotContain(Path.Combine(LinkRoot, "gone"), File.ReadAllText(SkillLinkRecord.PathFor(Shared)).Replace(@"\\", @"\"));
        Assert.True(Directory.Exists(Path.Combine(LinkRoot, "keeper")));
    }

    [Fact]
    public void A_link_made_before_the_record_existed_is_left_alone_when_its_skill_is_withdrawn()
    {
        Holds(("keeper", "v1"), ("gone", "v1"));
        Install();
        File.Delete(SkillLinkRecord.PathFor(Shared));   // as on a machine upgraded from a release with no record

        Holds(("keeper", "v1"));
        Install();

        Assert.True(IsLink(Path.Combine(LinkRoot, "gone")), "a link with no record of who made it was removed");
        Assert.True(Directory.Exists(Path.Combine(LinkRoot, "keeper")));
    }

    [Fact]
    public void Our_link_that_the_person_pointed_somewhere_else_is_left_alone()
    {
        Holds(("keeper", "v1"), ("gone", "v1"));
        Install();
        var link = Path.Combine(LinkRoot, "gone");
        var theirs = Path.Combine(_root, "their-own-gone");
        Directory.CreateDirectory(theirs);
        Directory.Delete(link, recursive: false);
        Link(link, theirs);

        Holds(("keeper", "v1"));
        Install();

        Assert.True(IsLink(link));
    }

    // ---- SK-F11: never inside a folder agents read, never in a folder we did not make -------------------------------

    [Fact]
    public void An_agent_folder_linked_into_the_shared_folder_places_nothing_and_touches_nothing()
    {
        // ~/.claude/skills linked to ~/.agents/skills/claude-root: following the link would stage inside the shared
        // folder, where a copy moved aside is read as a skill.
        var claudeRoot = Path.Combine(Shared, "claude-root");
        Directory.CreateDirectory(claudeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(LinkRoot)!);
        Link(LinkRoot, claudeRoot);
        Holds(("keeper", "v1"));

        var placement = Install();

        Assert.All(placement.Problems, p => Assert.Equal(SkillPlacementFault.StagingFolderUnsafe, p.Fault));
        Assert.Single(placement.Problems);
        Assert.Contains("is inside a skills folder agents read", placement.Describe());
        Assert.Equal(new[] { "claude-root" }, Directory.GetDirectories(Shared).Select(d => Path.GetFileName(d)));
        Assert.Empty(Directory.GetFileSystemEntries(claudeRoot));
        Assert.False(Directory.Exists(SkillDirectoryInstaller.StagingRootFor(Shared)),
            "a staging root was created although placement was refused");
    }

    [Fact]
    public void A_folder_already_at_the_staging_name_that_the_installer_did_not_make_is_left_untouched()
    {
        var staging = SkillDirectoryInstaller.StagingRootFor(Shared)!;
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "notes.txt"), "mine\n");
        // Even a folder inside it that looks exactly like one the installer would clean up is not touched.
        var lookalike = Path.Combine(staging, "keeper.0123456789abcdef0123456789abcdef.old");
        Directory.CreateDirectory(lookalike);
        File.WriteAllText(Path.Combine(lookalike, SkillDirectoryInstaller.StagingMarkerFileName),
            "DevThrottle skill installer - a staging or moved-aside folder; safe to delete\n");
        Holds(("keeper", "v1"));

        var placement = Install();

        Assert.Equal(SkillPlacementFault.StagingFolderUnsafe, Assert.Single(placement.Problems).Fault);
        Assert.Contains("was not made by DevThrottle", placement.Describe());
        Assert.Equal("mine\n", File.ReadAllText(Path.Combine(staging, "notes.txt")));
        Assert.True(Directory.Exists(lookalike));
        Assert.False(Directory.Exists(Path.Combine(Shared, "keeper")));
    }

    [Fact]
    public void A_staging_root_the_installer_makes_carries_its_marker_and_is_used_again()
    {
        Holds(("keeper", "v1"));
        Assert.True(Install().IsComplete);
        var marker = Path.Combine(SkillDirectoryInstaller.StagingRootFor(Shared)!, SkillDirectoryInstaller.StagingMarkerFileName);
        Assert.True(File.Exists(marker));

        Holds(("keeper", "v2"));
        Assert.Empty(Install().Problems);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    private SkillPlacement Install() =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.ClaudeCode, Store, new SkillInstallPaths(Shared, LinkRoot),
            Path.Combine(_root, "reclaimed.txt"), Personal);

    private void Holds(params (string Name, string Body)[] skills)
    {
        if (Directory.Exists(Store))
            Directory.Delete(Store, recursive: true);
        Directory.CreateDirectory(Store);
        foreach (var (name, body) in skills)
        {
            SkillDirectoryInstaller.Materialize(Store, new SkillBundle(
                name, 1, "hash-" + body, "A skill.", new[] { name }, $"# {name}\n\n{body}\n",
                Array.Empty<SkillFileBytes>()), Personal);
        }
    }

    /// <summary>A directory link: a junction on Windows, a symbolic link elsewhere.</summary>
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
}
