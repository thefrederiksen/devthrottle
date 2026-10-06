using CcDirector.Core.Agents;
using CcDirector.Core.Skills;
using Xunit;

namespace CcDirector.Core.Tests.Skills;

/// <summary>
/// Two Directors reconciling ONE shared skill folder at the same instant (devthrottle_internal#2311, review
/// finding SK-F1). Deciding who owns a folder and then deleting or replacing it are two steps; without one lock
/// around both, a team Director withdrawing a name can read its own stamp, the personal Director can take the
/// name over and re-stamp it in between, and the team Director then deletes the person's folder.
///
/// Two installers on two threads, released together by a barrier, many rounds. The lock is a named
/// operating-system mutex, so two threads contend for it exactly as two processes do.
/// </summary>
public sealed class SkillFolderConcurrencyTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "skill-folder-race-" + Guid.NewGuid().ToString("N"));

    private static readonly SkillSource Personal = new("gw-1", "tenant-person", null);
    private static readonly SkillSource Team = new("gw-1", "team-a", "team-a");

    private string Shared => Path.Combine(_root, "home", ".agents", "skills");
    private string StoreOf(SkillSource source) => Path.Combine(_root, "director-" + source.TenantId, "skills", "installed");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void A_withdrawal_racing_a_takeover_never_deletes_or_overwrites_the_other_sources_folder()
    {
        const int rounds = 150;
        var failures = new List<string>();

        for (var round = 0; round < rounds; round++)
        {
            if (Directory.Exists(Shared))
                Directory.Delete(Shared, recursive: true);

            // The team installed "both" first, so the folder carries the team's stamp.
            Holds(Team, "both");
            Install(Team);

            // Now, at the same instant: the person's Director takes "both" over, and the team's Director - whose
            // Gateway has withdrawn it - removes what it believes is still its own.
            Holds(Personal, "both", "mine");
            Holds(Team);
            var errors = new Exception?[2];
            using var barrier = new Barrier(2);
            var personal = new Thread(() => errors[0] = Run(barrier, Personal));
            var team = new Thread(() => errors[1] = Run(barrier, Team));
            personal.Start();
            team.Start();
            personal.Join();
            team.Join();

            foreach (var error in errors.Where(e => e is not null))
                failures.Add($"round {round}: an installer threw {error!.GetType().Name}: {error.Message}");

            // The person's library installed "both" and "mine"; the team's withdrawal may only ever remove a
            // folder stamped as the team's. Whatever the order, both must be the person's afterwards.
            foreach (var name in new[] { "both", "mine" })
            {
                var marker = Path.Combine(Shared, name, SkillDirectoryInstaller.MarkerFileName);
                if (!File.Exists(marker))
                    failures.Add($"round {round}: '{name}' is gone - the team's withdrawal deleted the person's folder");
                else if (!File.ReadAllText(marker).Contains("tenant=tenant-person"))
                    failures.Add($"round {round}: '{name}' is not stamped as the person's: {File.ReadAllText(marker).Replace('\n', ' ')}");
                else if (!File.ReadAllText(Path.Combine(Shared, name, "SKILL.md")).Contains("from the personal account"))
                    failures.Add($"round {round}: '{name}' holds content that is not the person's");
            }
        }

        Assert.True(failures.Count == 0,
            $"{failures.Count} failure(s) in {rounds} rounds; first: {string.Join(" | ", failures.Take(5))}");
    }

    [Fact]
    public void The_lock_has_one_name_per_folder_whatever_the_spelling_and_a_different_one_for_another_folder()
    {
        var folder = Path.Combine(_root, "home", ".agents", "skills");
        Assert.Equal(SharedSkillFolderLock.NameFor(folder), SharedSkillFolderLock.NameFor(folder + Path.DirectorySeparatorChar));
        if (OperatingSystem.IsWindows())
            Assert.Equal(SharedSkillFolderLock.NameFor(folder), SharedSkillFolderLock.NameFor(folder.ToUpperInvariant()));
        Assert.NotEqual(SharedSkillFolderLock.NameFor(folder), SharedSkillFolderLock.NameFor(Path.Combine(_root, "home", ".claude", "skills")));
    }

    [Fact]
    public void A_Director_that_cannot_have_the_folder_in_time_changes_nothing_and_says_so()
    {
        Holds(Personal, "mine");
        Install(Personal);
        Holds(Personal, "other");   // "mine" withdrawn, "other" new - but the folder is held by somebody else

        var held = SharedSkillFolderLock.TryAcquire(new[] { Shared }, TimeSpan.FromSeconds(1));
        Assert.NotNull(held);
        SkillPlacement? placement = null;
        var other = new Thread(() => placement = SkillDirectoryInstaller.InstallFor(
            AgentKind.Codex, StoreOf(Personal), new SkillInstallPaths(Shared, null),
            Path.Combine(_root, "reclaimed.txt"), Personal, folderLockWait: TimeSpan.FromMilliseconds(200)));
        other.Start();
        other.Join();

        Assert.True(Directory.Exists(Path.Combine(Shared, "mine")), "a Director without the lock removed a folder");
        Assert.False(Directory.Exists(Path.Combine(Shared, "other")), "a Director without the lock installed a folder");
        Assert.Equal(0, placement!.Reachable);
        Assert.Equal(SkillPlacementFault.FolderBusy, Assert.Single(placement.Problems).Fault);
        Assert.Contains("another Director was placing skills", placement.Describe());

        // Once the folder is free, the next placement does the work.
        held.Dispose();
        var after = Install(Personal);
        Assert.True(after.IsComplete);
        Assert.False(Directory.Exists(Path.Combine(Shared, "mine")));
    }

    private Exception? Run(Barrier barrier, SkillSource source)
    {
        try
        {
            barrier.SignalAndWait();
            Install(source);
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
    }

    /// <summary>The shared folder only: Codex reads it natively, so no links - the race is in the copies.</summary>
    private SkillPlacement Install(SkillSource source) =>
        SkillDirectoryInstaller.InstallFor(
            AgentKind.Codex, StoreOf(source), new SkillInstallPaths(Shared, null),
            Path.Combine(_root, "reclaimed.txt"), source);

    private void Holds(SkillSource source, params string[] names)
    {
        var store = StoreOf(source);
        if (Directory.Exists(store))
            Directory.Delete(store, recursive: true);
        Directory.CreateDirectory(store);
        foreach (var name in names)
        {
            SkillDirectoryInstaller.Materialize(store, new SkillBundle(
                name, 1, "hash-1", "A skill.", new[] { name }, $"# {name}\n\nfrom {source.Describe()}\n",
                Array.Empty<SkillFileBytes>()));
        }
    }
}
