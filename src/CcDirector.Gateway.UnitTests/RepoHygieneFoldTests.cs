using System;
using System.Collections.Generic;
using System.Linq;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Reports;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The hygiene fold (issues #2118 + #2119): stored repo-state snapshots into the morning report's
/// stale-worktree and unmerged-branch rows.
///
/// Every rule here is conservative in one direction, because a wrong answer in this email costs the owner
/// work rather than time. The tests are written as the harms they prevent: never call unmerged work safe to
/// remove, never call an unknown state a finished one, never suggest deleting a dirty or occupied worktree,
/// and never report an age that was not measured.
/// </summary>
public sealed class RepoHygieneFoldTests
{
    private static readonly DateTime Now = new(2026, 7, 24, 12, 0, 0, DateTimeKind.Utc);

    private static StoredRepoState Repo(
        string name = "devthrottle",
        string? defaultBranch = "origin/main",
        string? currentBranch = "main",
        IEnumerable<RepoStateWorktreeDto>? worktrees = null,
        IEnumerable<RepoStateBranchDto>? branches = null) => new()
    {
        DirectorId = "dir-1",
        MachineName = "SOREN",
        Name = name,
        Path = "D:/ReposFred/" + name,
        DefaultBranch = defaultBranch,
        CurrentBranch = currentBranch,
        CollectedAtUtc = Now,
        ReceivedAtUtc = Now,
        Worktrees = worktrees?.ToList() ?? new List<RepoStateWorktreeDto>(),
        Branches = branches?.ToList() ?? new List<RepoStateBranchDto>(),
    };

    private static RepoStateWorktreeDto Worktree(
        string path, double ageDays, bool? merged = true, bool dirty = false, bool live = false) => new()
    {
        Path = path,
        Branch = "wt/" + path.Split('/').Last(),
        TipCommitUtc = Now.AddDays(-ageDays),
        LastActivityUtc = Now.AddDays(-ageDays),
        IsDirty = dirty,
        BranchMergedIntoDefault = merged,
        HasLiveSession = live,
    };

    private static RepoStateBranchDto Branch(string name, double ageDays, bool? merged = false, int commits = 3) => new()
    {
        Name = name,
        TipCommitUtc = Now.AddDays(-ageDays),
        CommitsAheadOfDefault = commits,
        MergedIntoDefault = merged,
        CheckedOut = false,
    };

    private static List<T> Of<T>(IEnumerable<MorningAttentionItemDto> items) => items.OfType<T>().Cast<T>().ToList();

    // ---- nothing invented ------------------------------------------------------------------------------

    [Fact]
    public void No_repositories_yields_no_rows_at_all()
    {
        Assert.Empty(RepoHygieneFold.Items(Array.Empty<StoredRepoState>(), Now));
    }

    [Fact]
    public void A_tidy_repository_yields_no_rows()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/fresh", ageDays: 1) },
                 branches: new[] { Branch("feature", ageDays: 0.1) }),
        }, Now);

        Assert.Empty(items);
    }

    // ---- safe to remove is the narrow claim ------------------------------------------------------------

    [Fact]
    public void Old_clean_unoccupied_and_MERGED_is_the_only_thing_called_safe_to_remove()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/done-work", ageDays: 30, merged: true) }),
        }, Now);

        var item = Assert.Single(Of<StaleWorktreesAttentionDto>(items));
        Assert.True(item.SafeToRemove);
        Assert.Equal(1, item.Count);
        Assert.Equal(new[] { "done-work" }, item.Worktrees);
        Assert.Equal(30, item.OldestAgeDays);
        Assert.Equal("devthrottle", item.Repo);
    }

    [Fact]
    public void An_old_UNMERGED_worktree_is_listed_but_NEVER_marked_safe()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/unfinished", ageDays: 20, merged: false) }),
        }, Now);

        var item = Assert.Single(Of<StaleWorktreesAttentionDto>(items));
        Assert.False(item.SafeToRemove);
        Assert.Equal(new[] { "unfinished" }, item.Worktrees);
    }

    [Fact]
    public void An_UNKNOWN_merged_state_is_never_treated_as_finished()
    {
        // Null means the default branch was undetectable or the inspection failed. Saying "this is old" is
        // honest; saying "this is finished" is not.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(defaultBranch: null, worktrees: new[] { Worktree("D:/wt/unknown", ageDays: 40, merged: null) }),
        }, Now);

        var item = Assert.Single(Of<StaleWorktreesAttentionDto>(items));
        Assert.False(item.SafeToRemove);
    }

    [Fact]
    public void Safe_and_not_safe_worktrees_become_SEPARATE_rows_never_one_blended_count()
    {
        // "Six worktrees, four of them safe to remove" is a sentence a person acts on wrongly.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[]
            {
                Worktree("D:/wt/a", ageDays: 10, merged: true),
                Worktree("D:/wt/b", ageDays: 12, merged: true),
                Worktree("D:/wt/c", ageDays: 40, merged: false),
            }),
        }, Now);

        var rows = Of<StaleWorktreesAttentionDto>(items);
        Assert.Equal(2, rows.Count);

        var safe = Assert.Single(rows, r => r.SafeToRemove);
        Assert.Equal(2, safe.Count);
        Assert.Equal(12, safe.OldestAgeDays);

        var notSafe = Assert.Single(rows, r => !r.SafeToRemove);
        Assert.Equal(new[] { "c" }, notSafe.Worktrees);
        Assert.Equal(40, notSafe.OldestAgeDays);
    }

    // ---- the exclusions --------------------------------------------------------------------------------

    [Fact]
    public void A_DIRTY_worktree_is_never_reported_however_old_it_is()
    {
        // Uncommitted work is the one thing that exists nowhere else.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/dirty", ageDays: 90, merged: true, dirty: true) }),
        }, Now);

        Assert.Empty(Of<StaleWorktreesAttentionDto>(items));
    }

    [Fact]
    public void An_OCCUPIED_worktree_is_never_reported()
    {
        // A live session working in it is not stale work - it is someone's desk.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/busy", ageDays: 90, merged: true, live: true) }),
        }, Now);

        Assert.Empty(Of<StaleWorktreesAttentionDto>(items));
    }

    [Fact]
    public void A_worktree_younger_than_the_staleness_bar_is_not_reported()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/wt/recent", ageDays: RepoHygieneFold.StaleWorktreeDays - 0.5, merged: true) }),
        }, Now);

        Assert.Empty(Of<StaleWorktreesAttentionDto>(items));
    }

    [Fact]
    public void A_worktree_with_NO_observed_timestamp_is_skipped_rather_than_aged()
    {
        // An unknown age is not a small age and it is not a large one either.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[]
            {
                new RepoStateWorktreeDto { Path = "D:/wt/timeless", BranchMergedIntoDefault = true },
            }),
        }, Now);

        Assert.Empty(Of<StaleWorktreesAttentionDto>(items));
    }

    [Fact]
    public void The_row_carries_BASE_NAMES_never_the_owners_directory_layout()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(worktrees: new[] { Worktree("D:/Personal/Private/Client-Work/secret-client", ageDays: 30, merged: true) }),
        }, Now);

        var item = Assert.Single(Of<StaleWorktreesAttentionDto>(items));
        Assert.Equal(new[] { "secret-client" }, item.Worktrees);
        Assert.DoesNotContain("Personal", string.Join("|", item.Worktrees), StringComparison.Ordinal);
    }

    // ---- unmerged branches -----------------------------------------------------------------------------

    [Fact]
    public void Unmerged_branches_are_reported_oldest_first_with_their_commit_counts()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(branches: new[]
            {
                Branch("feature/newer", ageDays: 3, commits: 1),
                Branch("feature/oldest", ageDays: 40, commits: 12),
                Branch("feature/middle", ageDays: 9, commits: 4),
            }),
        }, Now);

        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Equal(new[] { "feature/oldest", "feature/middle", "feature/newer" },
            item.Branches.Select(b => b.Name));
        Assert.Equal(12, item.Branches[0].Commits);
        Assert.Equal(40, item.Branches[0].AgeDays);
    }

    [Fact]
    public void A_MERGED_branch_and_an_UNKNOWN_one_are_both_left_out_of_the_NAMED_branches()
    {
        // Only a definite false is an unmerged branch. A null is "not determined", and NAMING it would
        // tell the owner they have unfinished work on a branch nobody inspected. It is counted instead.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(branches: new[]
            {
                Branch("done", ageDays: 30, merged: true),
                Branch("undetermined", ageDays: 30, merged: null),
            }),
        }, Now);

        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Empty(item.Branches);
        Assert.Equal(1, item.Undetermined);
    }

    [Fact]
    public void What_could_not_be_worked_out_is_COUNTED_beside_what_could()
    {
        // The defect: a repository with far more branches than the email showed, and nothing on the page
        // admitting the rest existed.
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(defaultBranch: "origin/main", currentBranch: "working-on-this", branches: new[]
            {
                Branch("proven-a", ageDays: 30),
                Branch("proven-b", ageDays: 10),
                Branch("unknown-a", ageDays: 30, merged: null),
                Branch("unknown-b", ageDays: 0.1, merged: null),
                new RepoStateBranchDto { Name = "timeless", MergedIntoDefault = false, CommitsAheadOfDefault = 2 },
                // Known quantities - none of these is an unknown:
                Branch("done", ageDays: 30, merged: true),
                Branch("today", ageDays: 0.5),
                Branch("main", ageDays: 30, merged: null),
                Branch("working-on-this", ageDays: 30, merged: null),
            }),
        }, Now);

        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Equal(new[] { "proven-a", "proven-b" }, item.Branches.Select(b => b.Name));
        Assert.Equal(3, item.Undetermined);
    }

    [Fact]
    public void A_repository_where_everything_was_worked_out_carries_no_undetermined_key_on_the_wire()
    {
        var items = RepoHygieneFold.Items(new[] { Repo(branches: new[] { Branch("proven", ageDays: 30) }) }, Now);

        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Equal(0, item.Undetermined);
        var json = System.Text.Json.JsonSerializer.Serialize<MorningAttentionItemDto>(item,
            new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("undetermined", json);
        Assert.Contains("\"branches\"", json);
    }

    [Fact]
    public void The_default_branch_and_the_checked_out_branch_are_excluded()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(defaultBranch: "origin/main", currentBranch: "working-on-this", branches: new[]
            {
                Branch("main", ageDays: 30),
                Branch("working-on-this", ageDays: 30),
                Branch("genuinely-stale", ageDays: 30),
            }),
        }, Now);

        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Equal(new[] { "genuinely-stale" }, item.Branches.Select(b => b.Name));
    }

    [Fact]
    public void A_branch_touched_within_the_last_day_is_work_in_progress_not_a_recommendation()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(branches: new[] { Branch("today", ageDays: 0.5) }),
        }, Now);

        Assert.Empty(Of<UnmergedBranchesAttentionDto>(items));
    }

    [Fact]
    public void A_branch_with_no_tip_date_is_skipped_rather_than_aged()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo(branches: new[]
            {
                new RepoStateBranchDto { Name = "timeless", MergedIntoDefault = false, CommitsAheadOfDefault = 2 },
            }),
        }, Now);

        // Never aged and never named - but counted, not dropped.
        var item = Assert.Single(Of<UnmergedBranchesAttentionDto>(items));
        Assert.Empty(item.Branches);
        Assert.Equal(1, item.Undetermined);
    }

    // ---- several repositories --------------------------------------------------------------------------

    [Fact]
    public void Each_repository_gets_its_own_rows()
    {
        var items = RepoHygieneFold.Items(new[]
        {
            Repo("alpha", worktrees: new[] { Worktree("D:/wt/a", ageDays: 30, merged: true) }),
            Repo("beta", branches: new[] { Branch("beta-branch", ageDays: 30) }),
        }, Now);

        Assert.Equal("alpha", Assert.Single(Of<StaleWorktreesAttentionDto>(items)).Repo);
        Assert.Equal("beta", Assert.Single(Of<UnmergedBranchesAttentionDto>(items)).Repo);
    }
}
