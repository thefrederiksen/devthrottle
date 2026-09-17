using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.Tests.Git;

/// <summary>
/// What the Director makes of cc-worktrees' answers. The tool is replaced by its recorded output -
/// the exact JSON and exit codes it produced on Windows on 2026-09-17 - so these read the real
/// contract without running anything.
///
/// The one that matters most is the LAST group: every answer that is not a clean "free" comes back
/// as HELD. A slot the Director cannot prove came back must never be recorded as free, because free
/// is what hands it to the next session.
/// </summary>
public sealed class CcWorktreesPoolTests
{
    private const string GetAnswer =
        """{"repo": "C:\\Temp\\primary", "slot": "wt01", "path": "C:\\Temp\\primary.worktrees\\wt01", "lease": "36a28a14de2f4109a244821078d8f2f5", "holder": "session-abc", "base": "main", "commit": "23415e75794a698bb8e8f9626b5b5229793f18a8", "reused": false}""";

    private const string ReturnedFreeAnswer =
        """{"repo": "C:\\Temp\\primary", "slot": "wt01", "path": "C:\\Temp\\primary.worktrees\\wt01", "state": "free", "holder": null, "reason": null, "updated": "2026-09-17T20:37:07+00:00", "base": "main", "commit": "23415e75794a698bb8e8f9626b5b5229793f18a8"}""";

    private const string HeldAnswer =
        """{"error": "wt01 was not returned and is held: 1 commit is on no remote: 9176f41d2360; kept from git gc under refs/cc-worktrees/wt01/", "code": "held", "help": ["git -C C:\\Temp\\primary.worktrees\\wt01 status"], "repo": "C:\\Temp\\primary", "slot": "wt01", "path": "C:\\Temp\\primary.worktrees\\wt01", "state": "held", "holder": "session-held", "reason": "1 commit is on no remote: 9176f41d2360; kept from git gc under refs/cc-worktrees/wt01/", "updated": "2026-09-17T20:38:01+00:00"}""";

    private const string PoolFullAnswer =
        """{"error": "pool full (4 of 4): wt01 in-use by a, wt02 in-use by b, wt03 held (2 uncommitted changes), wt04 in-use by d", "code": "pool-full", "help": ["cc-worktrees list --repo C:\\Temp\\primary", "cc-worktrees return <path> --lease <lease>"]}""";

    private static readonly PooledWorktree Held =
        new(@"C:\Temp\primary", "wt01", @"C:\Temp\primary.worktrees\wt01", "b114732a620747d2bedf656c96bf139c");

    /// <summary>A pool whose tool is a recording: one answer, one exit code, and the arguments captured.</summary>
    private static CcWorktreesPool PoolAnswering(string stdout, int exitCode, List<IReadOnlyList<string>>? calls = null) =>
        new(args =>
            {
                calls?.Add(args);
                return (exitCode, stdout, "", true);
            },
            () => @"C:\fake\cc-worktrees.exe");

    [Fact]
    public void Get_ReadsTheSlotPathAndLease()
    {
        var calls = new List<IReadOnlyList<string>>();
        var pooled = PoolAnswering(GetAnswer, 0, calls).Get(@"C:\Temp\primary", "session-abc", 4);

        Assert.Equal("wt01", pooled.Slot);
        Assert.Equal(@"C:\Temp\primary.worktrees\wt01", pooled.Path);
        Assert.Equal("36a28a14de2f4109a244821078d8f2f5", pooled.Lease);
        Assert.Equal(@"C:\Temp\primary", pooled.Repo);

        var args = Assert.Single(calls);
        Assert.Equal(new[] { "get", "--repo", @"C:\Temp\primary", "--holder", "session-abc", "--pool-size", "4", "--json" }, args);
    }

    [Fact]
    public void Get_PoolFull_ThrowsTheToolsOwnWords()
    {
        var pool = PoolAnswering(PoolFullAnswer, 4);

        var refused = Assert.Throws<CcWorktreesRefusedException>(() => pool.Get(@"C:\Temp\primary", "session-abc", 4));
        Assert.Equal("pool-full", refused.Code);
        Assert.Equal(4, refused.ExitCode);
        Assert.StartsWith("pool full (4 of 4):", refused.Message);
        Assert.Contains("cc-worktrees return <path> --lease <lease>", refused.Help);
    }

    [Fact]
    public void Get_ToolNotInstalled_IsARefusalThatNamesIt()
    {
        var pool = new CcWorktreesPool(_ => (-1, "", "not found", false), () => null);

        var refused = Assert.Throws<CcWorktreesRefusedException>(() => pool.Get(@"C:\Temp\primary", "session-abc", 4));
        Assert.Equal("tool-not-runnable", refused.Code);
        Assert.Contains("cc-worktrees is not installed", refused.Message);
    }

    [Fact]
    public void Return_Free_MeansTheSlotIsBack()
    {
        var calls = new List<IReadOnlyList<string>>();
        var answer = PoolAnswering(ReturnedFreeAnswer, 0, calls).Return(Held);

        Assert.True(answer.Freed);
        Assert.Null(answer.HeldReason);
        Assert.Equal("wt01", answer.Slot);

        var args = Assert.Single(calls);
        Assert.Equal(new[] { "return", @"C:\Temp\primary.worktrees\wt01", "--lease", "b114732a620747d2bedf656c96bf139c", "--json" }, args);
    }

    [Fact]
    public void Return_Held_CarriesTheToolsReasonAndIsNotAFailure()
    {
        var answer = PoolAnswering(HeldAnswer, 3).Return(Held);

        Assert.False(answer.Freed);
        Assert.NotNull(answer.HeldReason);
        Assert.Contains("1 commit is on no remote: 9176f41d2360", answer.HeldReason);
    }

    [Fact]
    public void Return_NeverRetriesWithAStrongerFlag()
    {
        // The whole rule in one assertion: a held slot is asked for ONCE, with no destroy, no reclaim
        // and no second attempt. A retry here would be the Director deciding the tool was wrong.
        var calls = new List<IReadOnlyList<string>>();
        PoolAnswering(HeldAnswer, 3, calls).Return(Held);

        var args = Assert.Single(calls);
        Assert.Equal("return", args[0]);
        Assert.DoesNotContain("destroy", args);
        Assert.DoesNotContain("--reclaim-held", args);
        Assert.DoesNotContain("--allow-held", args);
        Assert.DoesNotContain("--yes", args);
    }

    [Theory]
    // A lease that no longer matches: the Director cannot say the slot came back.
    [InlineData("""{"error": "wt01 is no longer held under that lease; nothing was changed", "code": "lease-mismatch", "help": []}""", 1)]
    // The remote could not be reached, so the tool could not prove anything either way.
    [InlineData("""{"error": "wt01 was not returned and is held: cannot verify: fatal: unable to access origin", "code": "held", "help": []}""", 3)]
    // Output that is not JSON at all - a wrapper script printing its own error, say.
    [InlineData("cc-* tools are not fully installed - run the repair", 1)]
    // A success exit with an answer that does not say the slot is free.
    [InlineData("""{"repo": "C:\\Temp\\primary", "slot": "wt01", "path": "C:\\Temp\\primary.worktrees\\wt01", "state": "in-use"}""", 0)]
    public void Return_AnythingThatIsNotACleanFree_IsHeld(string stdout, int exitCode)
    {
        var answer = PoolAnswering(stdout, exitCode).Return(Held);

        Assert.False(answer.Freed);
        Assert.False(string.IsNullOrWhiteSpace(answer.HeldReason));
    }
}
