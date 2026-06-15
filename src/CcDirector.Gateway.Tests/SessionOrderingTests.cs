using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="SessionOrdering"/> - the shared client-side policy the Cockpit's
/// session rail uses for desktop-stable ordering (tree view) and needs-you-first triage.
/// </summary>
public sealed class SessionOrderingTests
{
    private static SessionDto S(string id, int sortOrder = 0, string color = "blue",
        bool onHold = false, DateTime createdAt = default, string briefingState = "None") => new()
    {
        SessionId = id,
        SortOrder = sortOrder,
        StatusColor = color,
        OnHold = onHold,
        BriefingState = briefingState,
        CreatedAt = createdAt == default ? new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) : createdAt,
    };

    [Fact]
    public void InDesktopOrder_SortsBySortOrder_Ascending()
    {
        var sessions = new[] { S("c", 2), S("a", 0), S("b", 1) };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "a", "b", "c" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_EqualSortOrder_FallsBackToCreatedAt()
    {
        // Every session reports SortOrder 0 (e.g. a Director predating the field): the
        // CreatedAt tie-break must give a deterministic, stable order.
        var t = new DateTime(2026, 5, 31, 12, 0, 0, DateTimeKind.Utc);
        var sessions = new[]
        {
            S("late",  0, createdAt: t.AddMinutes(10)),
            S("early", 0, createdAt: t),
            S("mid",   0, createdAt: t.AddMinutes(5)),
        };

        var ordered = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "early", "mid", "late" }, ordered.Select(s => s.SessionId));
    }

    [Fact]
    public void InDesktopOrder_DoesNotMutateInput()
    {
        var sessions = new[] { S("c", 2), S("a", 0) };

        _ = SessionOrdering.InDesktopOrder(sessions);

        Assert.Equal(new[] { "c", "a" }, sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void Classify_Red_NotHeld_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red")));
    }

    [Fact]
    public void Classify_NonRed_NotHeld_IsActive()
    {
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "blue")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverRed()
    {
        // A parked session sinks to the bottom even when it would otherwise be "needs you".
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", onHold: true)));
    }

    // ----- effective color while the wingman is reading (issue #196) -----

    [Fact]
    public void EffectiveColor_RedWhileBriefing_IsYellow()
    {
        // The Director stamps raw red at turn-end (it no longer knows about briefing,
        // #187); the Gateway stamps BriefingState=Briefing. The ONE presented color
        // must be yellow - never a red dot next to a "wingman reading..." chip.
        Assert.Equal("yellow", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void EffectiveColor_RedAfterBriefLands_IsRed()
    {
        Assert.Equal("red", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void EffectiveColor_BlueWhileBriefing_StaysBlue()
    {
        // A NEW turn already running: raw activity wins, the stale in-flight brief
        // must not paint a working session yellow.
        Assert.Equal("blue", SessionOrdering.EffectiveColor(S("x", color: "blue", briefingState: "Briefing")));
    }

    [Fact]
    public void IsBriefing_OnlyWhenBriefingAndRed()
    {
        Assert.True(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "blue", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "Briefed")));
        Assert.False(SessionOrdering.IsBriefing(S("x", color: "red", briefingState: "None")));
    }

    // ----- effective color while a user-requested deep dive runs (issue #217) -----

    [Fact]
    public void IsExplaining_AnyRawColor_TrueWhileExplaining()
    {
        // Explain is USER-initiated: the orange must show no matter what the session is
        // doing underneath (the original red gate suppressed it on working sessions and
        // left the rail contradicting the brief pane).
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "blue", briefingState: "Explaining")));
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "red", briefingState: "Explaining")));
        Assert.True(SessionOrdering.IsExplaining(S("x", color: "green", briefingState: "Explaining")));
        Assert.False(SessionOrdering.IsExplaining(S("x", color: "red", briefingState: "Briefing")));
        Assert.False(SessionOrdering.IsExplaining(S("x", color: "blue", briefingState: "None")));
    }

    [Fact]
    public void EffectiveColor_WhileExplaining_IsOrange_EvenWhenWorking()
    {
        Assert.Equal("orange", SessionOrdering.EffectiveColor(S("x", color: "blue", briefingState: "Explaining")));
        Assert.Equal("orange", SessionOrdering.EffectiveColor(S("x", color: "red", briefingState: "Explaining")));
    }

    [Fact]
    public void Classify_RedWhileExplaining_IsActive_NotNeedsYou()
    {
        // Same #196 rule as briefing: while the deep dive runs the session must not sit
        // in NEEDS YOU - red may only return after the report lands.
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Explaining")));
    }

    [Fact]
    public void Classify_RedWhileBriefing_IsActive_NotNeedsYou()
    {
        // The triage regression in issue #196: a mid-brief session must NOT enter the
        // NEEDS YOU bucket (and then flop back out when the brief lands or refutes).
        Assert.Equal(SessionOrdering.TriageBucket.Active,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing")));
    }

    [Fact]
    public void Classify_RedAfterBriefLands_IsNeedsYou()
    {
        Assert.Equal(SessionOrdering.TriageBucket.NeedsYou,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefed")));
    }

    [Fact]
    public void Classify_OnHold_TakesPrecedenceOverBriefing()
    {
        Assert.Equal(SessionOrdering.TriageBucket.OnHold,
            SessionOrdering.Classify(S("x", color: "red", briefingState: "Briefing", onHold: true)));
    }

    [Fact]
    public void InBucket_FiltersToBucket_AndKeepsDesktopOrder()
    {
        var sessions = new[]
        {
            S("active1", sortOrder: 1, color: "blue"),
            S("needs1",  sortOrder: 3, color: "red"),
            S("held1",   sortOrder: 0, color: "red", onHold: true),
            S("needs0",  sortOrder: 2, color: "red"),
        };

        var needs = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.NeedsYou);
        var active = SessionOrdering.InBucket(sessions, SessionOrdering.TriageBucket.OnHold);

        // Only the two non-held red sessions, in SortOrder order (2 before 3).
        Assert.Equal(new[] { "needs0", "needs1" }, needs.Select(s => s.SessionId));
        // The held-red session lands in OnHold, not NeedsYou.
        Assert.Equal(new[] { "held1" }, active.Select(s => s.SessionId));
    }

    // ===== by-repo grouping (issue #219) =====

    /// <summary>Builds a session with the fields the repo grouping reads. RemoteRepo wins over
    /// RepoPath when present; MachineName/DirectorId are carried to prove they do NOT affect the
    /// group key.</summary>
    private static SessionDto R(string id, string repoPath = "", string remoteRepo = "",
        int sortOrder = 0, string machine = "", string directorId = "") => new()
    {
        SessionId = id,
        RepoPath = repoPath,
        RemoteRepo = remoteRepo,
        SortOrder = sortOrder,
        MachineName = machine,
        DirectorId = directorId,
        StatusColor = "blue",
        CreatedAt = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    [Fact]
    public void RepoName_PrefersNormalizedRemote_LeafCaseInsensitiveDotGitStripped()
    {
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", remoteRepo: "thefrederiksen/cc-director.git")));
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", remoteRepo: "  thefrederiksen/cc-director  ")));
    }

    [Fact]
    public void RepoName_FallsBackToRepoPathLeaf_WhenNoRemote()
    {
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: @"D:\ReposFred\cc-director")));
        Assert.Equal("cc-director", SessionOrdering.RepoName(R("x", repoPath: "/home/user/src/cc-director/")));
    }

    [Fact]
    public void RepoName_NoRemoteNoPath_IsNull()
    {
        Assert.Null(SessionOrdering.RepoName(R("x")));
    }

    [Fact]
    public void InRepoGroups_HeadersAreAlphabetical_CaseInsensitive()
    {
        var sessions = new[]
        {
            R("z", repoPath: @"D:\zebra"),
            R("a", repoPath: @"D:\Apple"),
            R("b", repoPath: @"D:\banana"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        // "Apple" sorts before "banana" before "zebra" ignoring case.
        Assert.Equal(new[] { "Apple", "banana", "zebra" }, groups.Select(g => g.Name));
    }

    [Fact]
    public void InRepoGroups_NoRepoGroup_IsPlacedLast()
    {
        var sessions = new[]
        {
            R("none", repoPath: ""),
            R("named", repoPath: @"D:\alpha"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        Assert.Equal(2, groups.Count);
        Assert.Equal("alpha", groups[0].Name);
        Assert.False(groups[0].IsNoRepo);
        Assert.Equal(SessionOrdering.NoRepoGroup, groups[^1].Name);
        Assert.True(groups[^1].IsNoRepo);
    }

    [Fact]
    public void InRepoGroups_WithinGroup_UsesDesktopOrder()
    {
        // Two sessions in the same repo; lower SortOrder must render first regardless of input order.
        var sessions = new[]
        {
            R("second", repoPath: @"D:\repo", sortOrder: 2),
            R("first",  repoPath: @"D:\repo", sortOrder: 1),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal(new[] { "first", "second" }, repo.Sessions.Select(s => s.SessionId));
    }

    [Fact]
    public void InRepoGroups_SameRepoAcrossMachines_CoalescesIntoOneGroup()
    {
        // Same repo (same RemoteRepo) on two different machines / Directors must land under ONE header.
        var sessions = new[]
        {
            R("onA", remoteRepo: "thefrederiksen/cc-director.git", machine: "MACHINE_A", directorId: "dirA"),
            R("onB", remoteRepo: "thefrederiksen/cc-director",     machine: "MACHINE_B", directorId: "dirB"),
        };

        var groups = SessionOrdering.InRepoGroups(sessions);

        var repo = Assert.Single(groups);
        Assert.Equal("cc-director", repo.Name);
        Assert.Equal(new[] { "onA", "onB" }, repo.Sessions.Select(s => s.SessionId).OrderBy(x => x));
    }

    [Fact]
    public void InRepoGroups_DoesNotMutateInput()
    {
        var sessions = new[]
        {
            R("b", repoPath: @"D:\repo", sortOrder: 2),
            R("a", repoPath: @"D:\repo", sortOrder: 1),
        };

        _ = SessionOrdering.InRepoGroups(sessions);

        // Original array order preserved (grouping snapshots, never sorts in place).
        Assert.Equal(new[] { "b", "a" }, sessions.Select(s => s.SessionId));
    }
}
