using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Reports;
using Xunit;
using static CcDirector.Gateway.Tests.Fleet.FleetManagerWalkthroughFoldTests;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// May a session be closed from the walkthrough? (the Fleet Manager mission, step 7.) Close is allowed only on positive
/// evidence that nothing would be lost; every other case is a refusal that says why, including "cannot tell".
/// </summary>
public sealed class FleetManagerCloseRuleTests
{
    private const string Sid = "81000000-0000-4000-8000-000000000001";

    private static FleetCloseVerdict Decide(SessionDto? session, IReadOnlyList<StoredRepoState> repos, bool live = true,
        TurnVerdictDto? verdict = null, string? fleetManager = Marked)
        => FleetManagerCloseRule.Decide(session, live, fleetManager, verdict, repos, TimeZoneInfo.Utc, Now);

    private static StoredRepoState WithWorktree(string path, string? branch, bool dirty = false, bool? merged = true, int ahead = 0)
    {
        var repo = Repo();
        repo.Worktrees.Add(new RepoStateWorktreeDto { Path = path, Branch = branch, IsDirty = dirty, BranchMergedIntoDefault = merged });
        if (branch is not null)
            repo.Branches.Add(new RepoStateBranchDto { Name = branch, MergedIntoDefault = merged, CommitsAheadOfDefault = ahead, CheckedOut = true });
        return repo;
    }

    [Fact]
    public void Decide_CleanSessionOnAMergedBranchInspectedAfterItsLastActivity_IsAllowed()
    {
        var verdict = Decide(Session(Sid, "Release session"), new[] { Repo() });

        Assert.True(verdict.Allowed);
        Assert.Null(verdict.Refusal);
        Assert.Equal("Its branch main is fully in origin/main and its working copy was clean when it was inspected at 14:25, "
                     + "after its last activity. Closing stops Release session; it cannot be undone.", verdict.Evidence);
    }

    [Fact]
    public void Decide_AWorktreeInsideTheRepository_IsJudgedByTheWorktree_TheLongestMatch()
    {
        var repo = WithWorktree("/work/widgets/.worktrees/fix-sort", "fix-sort", merged: false, ahead: 2);
        var inWorktree = Session(Sid, "Worktree session", repo: "/work/widgets/.worktrees/fix-sort/");

        var verdict = Decide(inWorktree, new[] { repo });

        Assert.False(verdict.Allowed);
        Assert.Equal("Close is not offered: branch fix-sort has 2 commits that are not in origin/main.", verdict.Refusal);
    }

    [Fact]
    public void Decide_AMergedWorktreeWithASubfolderSession_IsAllowed()
    {
        var repo = WithWorktree("/work/widgets-fix", "fix-sort");

        var verdict = Decide(Session(Sid, "Sub", repo: "/work/widgets-fix/src/app"), new[] { repo });

        Assert.True(verdict.Allowed);
        Assert.StartsWith("Its branch fix-sort is fully in origin/main", verdict.Evidence);
    }

    [Fact]
    public void Decide_WindowsPaths_MatchWhateverTheCaseAndSeparator()
    {
        var repo = Repo();
        var windows = new StoredRepoState
        {
            DirectorId = repo.DirectorId, Name = repo.Name, Path = @"C:\Repos\Widgets", DefaultBranch = repo.DefaultBranch,
            CurrentBranch = repo.CurrentBranch, CollectedAtUtc = repo.CollectedAtUtc, Branches = repo.Branches,
        };

        var verdict = Decide(Session(Sid, "Windows session", repo: "c:/repos/widgets/"), new[] { windows });

        Assert.True(verdict.Allowed);
    }

    public static TheoryData<string, SessionDto?, StoredRepoState[], bool, string> Refusals()
    {
        var unmergedOne = Repo(merged: false, ahead: 1);
        var otherDirector = Repo();
        var foreign = new StoredRepoState
        {
            DirectorId = "director-b", Name = otherDirector.Name, Path = otherDirector.Path,
            DefaultBranch = otherDirector.DefaultBranch, CurrentBranch = otherDirector.CurrentBranch,
            CollectedAtUtc = otherDirector.CollectedAtUtc, Branches = otherDirector.Branches,
        };
        var noDefault = Repo();
        var unknownDefault = new StoredRepoState
        {
            DirectorId = noDefault.DirectorId, Name = "widgets", Path = noDefault.Path, DefaultBranch = null,
            CurrentBranch = "main", CollectedAtUtc = noDefault.CollectedAtUtc, Branches = noDefault.Branches,
        };
        return new TheoryData<string, SessionDto?, StoredRepoState[], bool, string>
        {
            { "not running", Session(Sid, "S"), new[] { Repo() }, false,
                "Close is not offered: this session is not running, so there is nothing to close." },
            { "no row", null, new[] { Repo() }, true,
                "Close is not offered: this session is not running, so there is nothing to close." },
            { "uncommitted", Session(Sid, "S", uncommitted: 1), new[] { Repo() }, true,
                "Close is not offered: this session has 1 uncommitted file." },
            { "uncommitted unknown", Session(Sid, "S", uncommitted: null), new[] { Repo() }, true,
                "Close is not offered: this session's computer has not said whether its working copy has uncommitted changes, "
                + "so the Gateway cannot tell whether work would be lost." },
            { "no folder", Session(Sid, "S", repo: "  "), new[] { Repo() }, true,
                "Close is not offered: this session has not said which folder it works in, so the Gateway cannot tell whether "
                + "its work is pushed and merged." },
            { "no report", Session(Sid, "S"), Array.Empty<StoredRepoState>(), true,
                "Close is not offered: the Gateway holds no report of this session's repository from its computer, so it cannot "
                + "tell whether its work is pushed and merged." },
            { "another computer's report", Session(Sid, "S"), new[] { foreign }, true,
                "Close is not offered: the Gateway holds no report of this session's repository from its computer, so it cannot "
                + "tell whether its work is pushed and merged." },
            { "a folder beside the repository", Session(Sid, "S", repo: "/work/widgets-old"), new[] { Repo() }, true,
                "Close is not offered: the Gateway holds no report of this session's repository from its computer, so it cannot "
                + "tell whether its work is pushed and merged." },
            { "report older than the work", Session(Sid, "S", lastActivity: Now.AddMinutes(-2)), new[] { Repo() }, true,
                "Close is not offered: the repository was last inspected at 14:25, before this session's last activity at 14:28, "
                + "so the Gateway cannot tell whether that work is pushed and merged." },
            { "dirty when inspected", Session(Sid, "S"), new[] { Repo(dirty: true) }, true,
                "Close is not offered: its working copy had uncommitted changes when it was inspected at 14:25." },
            { "detached", Session(Sid, "S"), new[] { Repo(current: null) }, true,
                "Close is not offered: its working copy is not on a named branch, so the Gateway cannot tell whether its work is merged." },
            { "default unknown", Session(Sid, "S"), new[] { unknownDefault }, true,
                "Close is not offered: the default branch of widgets could not be determined, so the Gateway cannot tell whether "
                + "branch main is merged." },
            { "merge unknown", Session(Sid, "S"), new[] { Repo(merged: null) }, true,
                "Close is not offered: the Gateway could not tell whether branch main is contained in origin/main." },
            { "one commit not in", Session(Sid, "S"), new[] { unmergedOne }, true,
                "Close is not offered: branch main has 1 commit that is not in origin/main." },
            { "not in, count unknown", Session(Sid, "S"), new[] { Repo(merged: false, ahead: 0) }, true,
                "Close is not offered: branch main has work that is not in origin/main." },
            { "the Fleet Manager", Session(Marked, "Fleet Manager", controller: null), new[] { Repo() }, true,
                "Close is not offered: this is the Fleet Manager itself. Restart or move it in Settings." },
        };
    }

    [Theory]
    [MemberData(nameof(Refusals))]
    public void Decide_EveryCaseWithoutProof_IsRefusedAndSaysWhy(string name, SessionDto? session, StoredRepoState[] repos,
        bool live, string expected)
    {
        var verdict = Decide(session, repos, live);

        Assert.False(verdict.Allowed, name);
        Assert.Equal(expected, verdict.Refusal);
        Assert.Null(verdict.Evidence);
    }

    [Fact]
    public void Decide_NoKnownLastActivity_IsRefused()
    {
        var session = Session(Sid, "S");
        session.LastActivityAt = null;

        var verdict = Decide(session, new[] { Repo() });

        Assert.Equal("Close is not offered: the Gateway does not know when this session last worked, so it cannot tell whether "
                     + "the repository report from 14:25 is still true.", verdict.Refusal);
    }

    [Fact]
    public void Decide_AStopTheWingmanReadAfterTheReport_IsLaterWork_AndRefuses()
    {
        var verdict = Menu();
        verdict.TurnEndObservedAtUtc = Now.AddMinutes(-1);

        var decision = Decide(Session(Sid, "S", lastActivity: Now.AddHours(-1)), new[] { Repo() }, verdict: verdict);

        Assert.False(decision.Allowed);
        Assert.StartsWith("Close is not offered: the repository was last inspected at 14:25, before this session's last activity at 14:29",
            decision.Refusal);
    }
}
