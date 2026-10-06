using System.Diagnostics;
using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// The boundary of #2311's skill placement (devthrottle_internal#2311, review round 6).
///
/// THE PERSON'S THINGS - an unmarked folder or ANY link at a visible skill path - are never moved, overwritten,
/// deleted or withdrawn. The retired-copy reclaim is held to that too (SK-F16).
///
/// THE INSTALLER'S OWN THINGS - its marked folders and its staging envelopes - it may delete, but a delete never
/// follows a link inside them to its target, and an envelope it cannot delete is left for the next reconcile rather
/// than stopping this one (SK-F17). Its informational link record never stops a missing link being made (SK-F19).
/// </summary>
public sealed class SkillRound6BoundaryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skill-round6-" + Guid.NewGuid().ToString("N"));

    private string Store => Path.Combine(_root, "director", "skills", "installed");
    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string LinkRoot => Path.Combine(_root, "home", ".claude", "skills");

    private static readonly SkillSource Personal = new("gw-1", "tenant-person", null);

    public void Dispose()
    {
        if (!Directory.Exists(_root))
            return;
        foreach (var entry in Directory.GetFileSystemEntries(_root, "*", SearchOption.AllDirectories))
        {
            if (File.Exists(entry))
                File.SetAttributes(entry, FileAttributes.Normal);
            else if (!OperatingSystem.IsWindows() && new FileInfo(entry).LinkTarget is null)
                File.SetUnixFileMode(entry, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        foreach (var directory in Directory.GetDirectories(_root, "*", SearchOption.AllDirectories)
                     .OrderByDescending(d => d.Length))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
                RemoveLink(directory);
        }
        Directory.Delete(_root, recursive: true);
    }

    // ---- SK-F16: the retired-copy reclaim never takes a link ---------------------------------------------------

    [Fact]
    public void A_persons_junction_at_a_retired_installer_name_is_never_reclaimed()
    {
        // The person's own dev-throttle skill lives elsewhere and is linked in. Its target has EXACTLY the retired
        // installer's shape - no marker, one file, SKILL.md - and the reclaim has never run on this folder.
        var theirs = Path.Combine(_root, "my-skills", "dev-throttle");
        Directory.CreateDirectory(theirs);
        File.WriteAllText(Path.Combine(theirs, "SKILL.md"), "# my dev-throttle\n");
        Directory.CreateDirectory(LinkRoot);
        var link = Path.Combine(LinkRoot, "dev-throttle");
        Link(link, theirs);
        Assert.False(File.Exists(SkillDirectoryInstaller.ReclaimRecordFor(LinkRoot)));
        Holds(("dev-throttle", "fleet"));

        var placement = Install();

        Assert.True(IsLink(link), "the person's junction was moved away by the retired-copy reclaim");
        Assert.Equal("# my dev-throttle\n", File.ReadAllText(Path.Combine(link, "SKILL.md")));
        Assert.False(Directory.Exists(SkillDirectoryInstaller.SupersededRootFor(LinkRoot))
                     && Directory.GetFileSystemEntries(SkillDirectoryInstaller.SupersededRootFor(LinkRoot)).Length > 0,
            "something was moved into the superseded folder");
        Assert.Contains(placement.Problems, p => p.SkillId == "dev-throttle" && p.Fault == SkillPlacementFault.Shadowed);
    }

    // ---- SK-F19: an unreadable record never stops a missing link being made --------------------------------------

    [Fact]
    public void A_truncated_link_record_does_not_stop_a_missing_link_being_made()
    {
        Holds(("keeper", "v1"));
        Assert.True(Install().IsComplete);
        var link = Path.Combine(LinkRoot, "keeper");
        RemoveLink(link);                                                    // the agent's link has gone missing
        File.WriteAllText(SkillLinkRecord.PathFor(Shared), "[{\"link\": \"C:\\\\x");   // and the record is cut off

        var placement = Install();

        Assert.True(IsLink(link), "a missing link was not made because the informational record could not be read");
        Assert.Contains("v1", File.ReadAllText(Path.Combine(link, "SKILL.md")));
        Assert.Empty(placement.Problems);
        Assert.Contains(placement.NotesOrEmpty, n => n.Contains("could not be read"));
        Assert.Contains("keeper", File.ReadAllText(SkillLinkRecord.PathFor(Shared)));   // rewritten, readable again
    }

    // ---- SK-F17: inside our own folders, a delete never follows a link ----------------------------------------

    [Fact]
    public void A_link_inside_our_own_folder_is_removed_as_a_link_and_its_target_survives_a_refresh_and_a_withdrawal()
    {
        Holds(("keeper", "v1"), ("gone", "v1"));
        Assert.True(Install().IsComplete);
        var keeperTarget = MakeFolderWithFile("keeper-notes");
        var goneTarget = MakeFolderWithFile("gone-notes");
        Link(Path.Combine(Shared, "keeper", "notes"), keeperTarget);         // inside our marked folders
        Link(Path.Combine(Shared, "gone", "notes"), goneTarget);

        Holds(("keeper", "v2"));                                             // keeper refreshed, gone withdrawn
        var placement = Install();

        Assert.Empty(placement.Problems);
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "gone")));
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(keeperTarget, "kept.txt")));
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(goneTarget, "kept.txt")));
        Assert.Empty(Directory.GetDirectories(SkillDirectoryInstaller.StagingRootFor(Shared)!));
    }

    [Fact]
    public void DeleteOwnTree_removes_nested_links_without_entering_them()
    {
        var tree = Path.Combine(_root, "envelope");
        Directory.CreateDirectory(Path.Combine(tree, "deep", "deeper"));
        File.WriteAllText(Path.Combine(tree, "deep", "a.txt"), "a");
        var target = MakeFolderWithFile("link-target");
        Link(Path.Combine(tree, "deep", "deeper", "to-target"), target);
        if (!OperatingSystem.IsWindows())
            File.CreateSymbolicLink(Path.Combine(tree, "file-link"), Path.Combine(target, "kept.txt"));

        SkillDirectoryInstaller.DeleteOwnTree(tree);

        Assert.False(Directory.Exists(tree));
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(target, "kept.txt")));
    }

    [Fact]
    public void DeleteOwnTree_refuses_a_link_at_the_top()
    {
        var target = MakeFolderWithFile("top-target");
        var link = Path.Combine(_root, "top-link");
        Link(link, target);

        Assert.Throws<IOException>(() => SkillDirectoryInstaller.DeleteOwnTree(link));

        Assert.True(IsLink(link));
        Assert.Equal("theirs\n", File.ReadAllText(Path.Combine(target, "kept.txt")));
    }

    [Fact]
    public void An_envelope_that_cannot_be_deleted_does_not_stop_the_reconcile_and_is_cleaned_next_time()
    {
        Holds(("keeper", "v1"), ("gone", "v1"), ("later", "v1"));
        Assert.True(Install().IsComplete);
        var stuckKeeper = MakeUndeletable(Path.Combine(Shared, "keeper"));
        var stuckGone = MakeUndeletable(Path.Combine(Shared, "gone"));

        Holds(("keeper", "v2"), ("later", "v2"), ("new-one", "v1"));         // refresh, withdrawal, and a new skill
        var placement = Install();

        Assert.True(placement.IsComplete, placement.Describe());
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "keeper", "SKILL.md")));
        Assert.Contains("v2", File.ReadAllText(Path.Combine(LinkRoot, "later", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(LinkRoot, "new-one", "SKILL.md")));
        Assert.False(Directory.Exists(Path.Combine(Shared, "gone")));
        var staging = SkillDirectoryInstaller.StagingRootFor(Shared)!;
        var leftovers = Directory.GetDirectories(staging);
        Assert.Equal(2, leftovers.Length);
        Assert.All(leftovers, l => Assert.True(File.Exists(Path.Combine(l, SkillDirectoryInstaller.StagingMarkerFileName)),
            "a part-deleted envelope lost its marker first, so the next reconcile could not prove it is ours"));

        stuckKeeper();
        stuckGone();
        Assert.True(Install().IsComplete);
        Assert.Empty(Directory.GetDirectories(staging));
    }

    // ---- helpers ---------------------------------------------------------------------------------------------

    private string MakeFolderWithFile(string name)
    {
        var folder = Path.Combine(_root, "outside", name);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "kept.txt"), "theirs\n");
        return folder;
    }

    /// <summary>Put something in <paramref name="skillFolder"/> that the moved-aside folder cannot be deleted past -
    /// a read-only file on Windows, a read-only subfolder elsewhere. Returns the undo.</summary>
    private static Action MakeUndeletable(string skillFolder)
    {
        var sub = Path.Combine(skillFolder, "locked");
        Directory.CreateDirectory(sub);
        var file = Path.Combine(sub, "stuck.txt");
        File.WriteAllText(file, "stuck");
        if (OperatingSystem.IsWindows())
        {
            File.SetAttributes(file, FileAttributes.ReadOnly);
            return () =>
            {
                foreach (var f in Directory.GetFiles(Path.GetDirectoryName(Path.GetDirectoryName(skillFolder)!)!, "stuck.txt",
                             SearchOption.AllDirectories))
                    File.SetAttributes(f, FileAttributes.Normal);
            };
        }
        File.SetUnixFileMode(sub, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        return () =>
        {
            foreach (var d in Directory.GetDirectories(Path.GetDirectoryName(Path.GetDirectoryName(skillFolder)!)!, "locked",
                         SearchOption.AllDirectories))
            {
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(d, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        };
    }

    private static bool IsLink(string path) => new DirectoryInfo(path).LinkTarget is not null;

    private static void RemoveLink(string link)
    {
        if (OperatingSystem.IsWindows())
            Directory.Delete(link, recursive: false);
        else
            File.Delete(link);
    }

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
