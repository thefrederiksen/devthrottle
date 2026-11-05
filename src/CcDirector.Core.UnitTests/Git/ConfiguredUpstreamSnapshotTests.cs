using CcDirector.Core.Git;
using Xunit;

namespace CcDirector.Core.UnitTests.Git;

/// <summary>
/// The upstream question - "has this branch's remote branch been deleted?" - used to cost three git
/// processes PER BRANCH; it is now two reads per repository and a lookup. These tests fix the four
/// cases the per-branch version was careful about, against the exact text git prints, so a batch read
/// cannot quietly lose one of them. They start no process: the outputs are the ones a real git
/// produced, checked by the real-git test in CcDirector.Core.GitTests.
///
/// The direction of every failure matters. "Gone" is read as proof that a branch was merged and its
/// worktree may be DELETED, so anything the inspection cannot establish must come back as "cannot
/// tell" (<c>InspectionSucceeded: false</c>) or "no upstream configured" - never as gone.
/// </summary>
public sealed class ConfiguredUpstreamSnapshotTests
{
    // git for-each-ref --format=%(refname)%09%(upstream)%09%(upstream:track,nobracket) refs/heads/
    private const string Refs =
        "refs/heads/gone\trefs/remotes/origin/gone\tgone\n" +
        "refs/heads/keep\trefs/remotes/origin/keep\tahead 2, behind 1\n" +
        "refs/heads/main\trefs/remotes/origin/main\t\n" +
        "refs/heads/never\t\t\n" +
        "refs/heads/unmapped\t\t\n" +
        "refs/heads/release/1.2.3\trefs/remotes/origin/release/1.2.3\t\n" +
        "refs/heads/shadowed-by-a-tag\trefs/remotes/origin/shadowed-by-a-tag\tgone\n";

    // git config --get-regexp ^branch.
    private const string Config =
        "branch.main.remote origin\n" +
        "branch.main.merge refs/heads/main\n" +
        "branch.keep.remote origin\n" +
        "branch.keep.merge refs/heads/keep\n" +
        "branch.gone.remote origin\n" +
        "branch.gone.merge refs/heads/gone\n" +
        "branch.octopus.remote origin\n" +
        "branch.octopus.merge refs/heads/one\n" +
        "branch.octopus.merge refs/heads/two\n" +
        "branch.elsewhere.remote fork\n" +
        "branch.elsewhere.merge refs/heads/elsewhere\n" +
        "branch.unmapped.remote origin\n" +
        "branch.unmapped.merge refs/pull/7/head\n" +
        "branch.deleted-locally.remote origin\n" +
        "branch.deleted-locally.merge refs/heads/deleted-locally\n" +
        "branch.release/1.2.3.remote origin\n" +
        "branch.release/1.2.3.merge refs/heads/release/1.2.3\n" +
        "branch.shadowed-by-a-tag.remote origin\n" +
        "branch.shadowed-by-a-tag.merge refs/heads/shadowed-by-a-tag\n";

    private static ConfiguredUpstreamSnapshot Snapshot() => ConfiguredUpstreamSnapshot.Parse(Refs, Config);

    [Fact]
    public void Verdict_RemoteBranchDeleted_IsGone()
    {
        var v = Snapshot().Verdict("gone");
        Assert.True(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.True(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_RemoteBranchStillThere_IsNotGone()
    {
        var v = Snapshot().Verdict("keep");
        Assert.True(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_UpToDateBranch_IsNotGone()
    {
        var v = Snapshot().Verdict("main");
        Assert.True(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_NeverPushedBranch_HasNoConfiguredUpstream()
    {
        var v = Snapshot().Verdict("never");
        Assert.True(v.InspectionSucceeded);
        Assert.False(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchWithTwoConfiguredMergeValues_IsNotEligible()
    {
        // An octopus pull: one of several merge targets being gone proves nothing while another
        // survives, so the branch is ruled out of the signal rather than judged by it.
        var v = Snapshot().Verdict("octopus");
        Assert.True(v.InspectionSucceeded);
        Assert.False(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchTrackingARemoteThatCouldNotBeRefreshed_CannotBeInspected()
    {
        var v = Snapshot().Verdict("elsewhere", new[] { "fork" });
        Assert.False(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchTrackingADifferentRemoteThatWasRefreshed_IsInspected()
    {
        // The branch has no line of its own in refs/heads, so the answer is still "cannot tell" -
        // but for the ref reason, not the remote one. The remote list must not be what decides it.
        var v = Snapshot().Verdict("elsewhere", new[] { "upstream" });
        Assert.False(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
    }

    [Fact]
    public void Verdict_ConfiguredUpstreamGitCannotMapToATrackingRef_CannotBeInspected()
    {
        // A merge value outside refs/heads (a pull-request ref): git prints the branch but no
        // upstream for it. Absent is never read as gone.
        var v = Snapshot().Verdict("unmapped");
        Assert.False(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchConfiguredButWithNoRefOfItsOwn_CannotBeInspected()
    {
        var v = Snapshot().Verdict("deleted-locally");
        Assert.False(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchNameContainingDots_IsReadFromTheRightConfigKeys()
    {
        // branch.release/1.2.3.remote - the branch name itself holds dots, so the key cannot be
        // split on the first or on every dot.
        var v = Snapshot().Verdict("release/1.2.3");
        Assert.True(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.False(v.UpstreamGone);
    }

    [Fact]
    public void Verdict_BranchNotMentionedAnywhere_HasNoConfiguredUpstream()
    {
        var v = Snapshot().Verdict("no-such-branch");
        Assert.True(v.InspectionSucceeded);
        Assert.False(v.HasConfiguredUpstream);
    }

    [Fact]
    public void Verdict_RepositoryWithNoBranchConfigurationAtAll_HasNoConfiguredUpstream()
    {
        // git config --get-regexp exits 1 and prints nothing. That is an answer, not a failure.
        var v = ConfiguredUpstreamSnapshot.Parse(Refs, "").Verdict("keep");
        Assert.True(v.InspectionSucceeded);
        Assert.False(v.HasConfiguredUpstream);
    }

    [Fact]
    public void CouldNotRead_EveryVerdictFailsClosed()
    {
        var snapshot = ConfiguredUpstreamSnapshot.CouldNotRead("git for-each-ref failed: boom");
        Assert.NotNull(snapshot.Unreadable);
        foreach (var branch in new[] { "gone", "keep", "never", "octopus" })
        {
            var v = snapshot.Verdict(branch);
            Assert.False(v.InspectionSucceeded);
            Assert.False(v.UpstreamGone);
        }
    }

    [Fact]
    public void Parse_WindowsLineEndings_AreReadTheSameWay()
    {
        // GitCommandRunner re-joins captured output with the platform's line terminator, so on
        // Windows every line arrives ending "\r\n".
        var v = ConfiguredUpstreamSnapshot.Parse(Refs.Replace("\n", "\r\n"), Config.Replace("\n", "\r\n"));
        Assert.True(v.Verdict("gone").UpstreamGone);
        Assert.False(v.Verdict("keep").UpstreamGone);
        Assert.False(v.Verdict("octopus").HasConfiguredUpstream);
    }

    [Fact]
    public void Verdict_BranchSharingItsNameWithATag_IsStillJudged()
    {
        // %(refname:short) would print this branch as "heads/shadowed-by-a-tag" while git worktree
        // list reports it as "shadowed-by-a-tag", so the two would never meet and a worktree that is
        // genuinely merged would be unreapable for good. The read asks for %(refname) and strips the
        // prefix, so the names agree.
        var v = Snapshot().Verdict("shadowed-by-a-tag");
        Assert.True(v.InspectionSucceeded);
        Assert.True(v.HasConfiguredUpstream);
        Assert.True(v.UpstreamGone);
    }

    [Fact]
    public void Parse_ShortRefNames_AreStillRead()
    {
        // The prefix is stripped when it is there, so a line with no prefix is read as it stands.
        var v = ConfiguredUpstreamSnapshot.Parse("gone\trefs/remotes/origin/gone\tgone", "branch.gone.remote origin\nbranch.gone.merge refs/heads/gone");
        Assert.True(v.Verdict("gone").UpstreamGone);
    }

    [Fact]
    public void Parse_ConfigurationKeyNamingNoBranch_IsSkippedRatherThanThrowing()
    {
        // git accepts a plain "branch.merge" and "branch.remote" and prints them here. They name no
        // branch; slicing a name out of them asks for a negative length. Before this was guarded, a
        // hand-edited configuration failed a whole repository's inventory.
        var snapshot = ConfiguredUpstreamSnapshot.Parse(Refs,
            "branch.merge x\nbranch.remote y\n" + Config);

        Assert.Null(snapshot.Unreadable);
        Assert.True(snapshot.Verdict("gone").UpstreamGone);
        Assert.False(snapshot.Verdict("never").HasConfiguredUpstream);
    }
}
