using System.Diagnostics;
using System.Text.Json;
using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The reaper, against real git repositories, ACTUALLY TRYING to take a cc-worktrees pool slot.
///
/// Every worktree here is arranged to be as plainly safe to reap as the reaper's own rule allows -
/// clean, merged, and past the activity cooling-off - so the ONLY thing between it and a delete is
/// the pool guard. The control test proves that: the identical worktree, with no pool, is removed.
///
/// Both of the pool's markers are exercised separately, because they protect different machines: the
/// LAYOUT protects a slot on a machine whose records were lost, and the RECORDS protect a slot whose
/// path does not look like one.
/// </summary>
public sealed class PooledWorktreeReaperTests : IDisposable
{
    private readonly string _root;
    private readonly string _origin;
    private readonly string _primary;
    private readonly string _home;
    private readonly WorktreeLeftoverStore _leftovers;

    public PooledWorktreeReaperTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "ccd-pool-reaper-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
        _home = Path.Combine(_root, "cc-worktrees-home");
        _leftovers = new WorktreeLeftoverStore(Path.Combine(_root, "leftovers"));
        _origin = Path.Combine(_root, "origin.git");
        _primary = Path.Combine(_root, "primary");

        RunGit(_root, "-c", "init.defaultBranch=main", "init", "--bare", _origin);
        RunGit(_root, "-c", "init.defaultBranch=main", "clone", _origin, _primary);
        RunGit(_primary, "config", "user.email", "test@cc-director.local");
        RunGit(_primary, "config", "user.name", "CC Director Test");
        RunGit(_primary, "config", "commit.gpgsign", "false");

        File.WriteAllText(Path.Combine(_primary, "README.md"), "initial\n");
        RunGit(_primary, "add", "-A");
        RunGit(_primary, "commit", "-m", "initial commit");
        RunGit(_primary, "branch", "-M", "main");
        RunGit(_primary, "push", "-u", "origin", "main");
    }

    public void Dispose()
    {
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try { Directory.Delete(_root, recursive: true); return; } catch { Thread.Sleep(150); }
        }
    }

    /// <summary>An hour into the future, so a freshly made worktree is past the activity cooling-off.</summary>
    private static readonly Func<DateTime> Later = () => DateTime.UtcNow.AddHours(1);

    /// <summary>A roster that positively reports no live sessions, so the reap may proceed.</summary>
    private static readonly Func<CancellationToken, Task<IReadOnlyList<LiveSessionRef>>> NoSessions =
        _ => Task.FromResult<IReadOnlyList<LiveSessionRef>>(Array.Empty<LiveSessionRef>());

    /// <summary>
    /// A worktree on a detached HEAD at the default branch tip - clean, contained in origin/main, and
    /// therefore exactly what the reaper's rule calls safe to remove. This is the shape a pool slot
    /// sits in between holders.
    /// </summary>
    private string AddDetachedWorktreeAt(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        RunGit(_primary, "worktree", "add", "--detach", path, "main");
        return path;
    }

    private WorktreeReaperService Reaper(CcWorktreesPoolSlots slots) =>
        new(leftovers: _leftovers, utcNow: Later, poolSlots: slots,
            inventory: new WorktreeInventoryService(poolSlots: slots));

    // ---- the control ---------------------------------------------------------------------------

    [Fact]
    public async Task AnIdenticalWorktreeThatIsNotAPoolSlot_IsRemoved()
    {
        // Without this the test below proves only that this reaper removes nothing.
        var plain = AddDetachedWorktreeAt(Path.Combine(_root, "wt-plain"));

        var result = await Reaper(new CcWorktreesPoolSlots(Path.Combine(_root, "no-pool-here"))).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success, result.Error);
        Assert.Equal(1, result.RemovedCount);
        Assert.False(Directory.Exists(plain));
    }

    // ---- the layout marker ---------------------------------------------------------------------

    [Fact]
    public async Task APoolSlotRecognisedByItsLayout_IsLeftAlone_EvenWithNoRecordsOnThisMachine()
    {
        var slot = AddDetachedWorktreeAt(Path.Combine(_root, "primary.worktrees", "wt01"));

        // No cc-worktrees state at all: the layout is the only thing saying this is a slot, and it is
        // the only thing there is to say it on a machine whose records were lost.
        var result = await Reaper(new CcWorktreesPoolSlots(Path.Combine(_root, "no-pool-here"))).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(slot), "a cc-worktrees pool slot must never be removed by the Director's reaper");
    }

    [Fact]
    public async Task TheReaperRefusesAPoolSlotEvenWhenTheInventoryOffersItAsSafe()
    {
        // THE SECOND, INDEPENDENT CHECK. In the test above the inventory keeps the slot out of the safe
        // set, so the reaper's own guard is never reached and nothing about it is proven. Here the
        // inventory is given a detector that knows of no pools, so the slot arrives in SafeToReap and
        // the reaper is one step from deleting it - which is the only way to show that the check at the
        // destructive step itself does the work. The decision and the act are not the same moment, and
        // this is the moment that cannot be undone.
        //
        // The slot's path deliberately has no pool SHAPE, so only the records can recognise it and the
        // blind detector really is blind.
        var slot = AddDetachedWorktreeAt(Path.Combine(_root, "wt-recorded"));
        WriteRecords(_primary, ("wt01", slot));
        var blind = new CcWorktreesPoolSlots(Path.Combine(_root, "no-pool-here"));
        var seeing = new CcWorktreesPoolSlots(_home);

        var offered = await new WorktreeInventoryService(poolSlots: blind).GetInventoryAsync(_primary);
        Assert.Contains(offered.SafeToReap, w => WorktreeReaperService.NormalizePath(w.Path)
            == WorktreeReaperService.NormalizePath(slot));

        var result = await new WorktreeReaperService(
            inventory: new WorktreeInventoryService(poolSlots: blind),
            leftovers: _leftovers, utcNow: Later, poolSlots: seeing).ReapAsync(_primary, NoSessions);

        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(slot), "the reaper's own guard must refuse a pool slot the inventory offered");
        Assert.Contains(WorktreeReaperService.NormalizePath(slot), result.Skipped);
    }

    [Fact]
    public async Task APoolSlot_IsNotEvenOfferedAsSafeToReap()
    {
        var slot = AddDetachedWorktreeAt(Path.Combine(_root, "primary.worktrees", "wt01"));

        var inventory = await new WorktreeInventoryService(poolSlots: new CcWorktreesPoolSlots(Path.Combine(_root, "no-pool-here")))
            .GetInventoryAsync(_primary);

        Assert.True(inventory.Success, inventory.Error);
        Assert.Equal(0, inventory.SafeToReapCount);
        var row = inventory.Worktrees.Single(w => WorktreeReaperService.NormalizePath(w.Path)
            == WorktreeReaperService.NormalizePath(slot));
        Assert.Equal(WorktreeSafetyReason.CcWorktreesPoolSlot, row.Reason);
    }

    // ---- the records marker --------------------------------------------------------------------

    [Fact]
    public async Task APoolSlotTheRecordsName_IsLeftAlone_EvenWhenItsPathHasNoPoolShape()
    {
        var slot = AddDetachedWorktreeAt(Path.Combine(_root, "wt-recorded"));
        WriteRecords(_primary, ("wt01", slot));

        var result = await Reaper(new CcWorktreesPoolSlots(_home)).ReapAsync(_primary, NoSessions);

        Assert.True(result.Success, result.Error);
        Assert.Equal(0, result.RemovedCount);
        Assert.True(Directory.Exists(slot));
    }

    // ---- cannot tell is not no -----------------------------------------------------------------

    [Fact]
    public async Task RecordsThatCannotBeRead_AbortTheReap_RatherThanRemovingAnything()
    {
        var plain = AddDetachedWorktreeAt(Path.Combine(_root, "wt-plain"));
        Directory.CreateDirectory(_home);
        File.WriteAllText(Path.Combine(_home, "registry.json"), "{ this is not json");

        var result = await Reaper(new CcWorktreesPoolSlots(_home)).ReapAsync(_primary, NoSessions);

        Assert.False(result.Success);
        Assert.Contains("cc-worktrees", result.Error);
        Assert.True(Directory.Exists(plain), "nothing is removed while which directories belong to a pool is unknown");
    }

    private void WriteRecords(string repo, params (string Slot, string Path)[] slots)
    {
        Directory.CreateDirectory(Path.Combine(_home, "pools"));
        File.WriteAllText(Path.Combine(_home, "registry.json"),
            JsonSerializer.Serialize(new { version = 1, repos = new[] { repo } }));
        var entries = slots.ToDictionary(s => s.Slot, s => new { path = s.Path, state = "free" });
        File.WriteAllText(Path.Combine(_home, "pools", "primary-abcdef123456.json"),
            JsonSerializer.Serialize(new { version = 4, repo, slots = entries }));
    }

    private static void RunGit(string workingDirectory, params string[] args)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var proc = Process.Start(psi)!;
        proc.StandardOutput.ReadToEnd();
        var error = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed in {workingDirectory}: {error}");
    }
}
