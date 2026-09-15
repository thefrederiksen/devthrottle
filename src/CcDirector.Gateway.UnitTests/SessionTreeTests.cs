using System;
using System.Linq;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The ownership tree fold, case for case against the TypeScript it is ported from
/// (packages/client-core/src/sessions/tree.test.ts). Every case there has a case here, named after what
/// it proves, so a reader can lay the two files side by side.
///
/// THE FIXTURE IS THE RULE FACTORY CREW OF 2026-09-14, in miniature: an Architect (108) with a working
/// Worker and two stopped ones, and a standalone session beside it. Same ids, same sort orders, same
/// timestamps as the TypeScript, so a number that differs between the two files is a real disagreement
/// and not an artefact of two different made-up rosters.
///
/// WHERE THE TWO LANGUAGES TAKE DIFFERENT INPUT. The TypeScript reads the Gateway's STAMPED
/// triageBucket; this fold calls <see cref="SessionOrdering.Classify"/>, which folds the same answer from
/// the raw facts the Gateway stamps it from. So a fixture here sets the RAW facts that produce the
/// TypeScript fixture's stamped bucket, and the bucket each one is meant to land in is named in the
/// helper below. That the two really do line up is not asserted by eye here - it is asserted
/// mechanically, per session, by the shared fixture file and CcDirector.StateAgreementCheck.TreeAgreement.
/// </summary>
public sealed class SessionTreeTests
{
    /// <summary>The TypeScript fixture's default createdAt, so both files age their crews from the same
    /// instant.</summary>
    private static readonly DateTime DefaultCreatedAt = new(2026, 9, 14, 16, 0, 0, DateTimeKind.Utc);

    private static DateTime At(int hour, int minute, int second) =>
        new(2026, 9, 14, hour, minute, second, DateTimeKind.Utc);

    /// <summary>
    /// A session carrying the RAW facts that fold to the wanted triage bucket:
    /// <c>"active"</c> - working, blue;
    /// <c>"needsYou"</c> - stopped at its prompt with nobody driving it, red;
    /// <c>"supervised"</c> - stopped with a LIVE supervisor, so it recedes to slate and sinks into the
    /// parked bucket (the TypeScript fixture's "supporting"/onHold children);
    /// <c>"snoozed"</c> - the owner parked it himself, grey.
    /// </summary>
    private static SessionDto S(
        string id,
        int sortOrder = 0,
        string? controller = null,
        string bucket = "active",
        DateTime? createdAt = null,
        DateTime? needsYouSince = null,
        string directorId = "",
        string machineName = "") => new()
    {
        SessionId = id,
        SortOrder = sortOrder,
        ControllerSessionId = controller,
        CreatedAt = createdAt ?? DefaultCreatedAt,
        NeedsYouSince = needsYouSince,
        DirectorId = directorId,
        MachineName = machineName,
        ActivityState = bucket == "active" ? "Working" : "WaitingForInput",
        OnHold = bucket == "snoozed",
        HasLiveSupervisor = bucket == "supervised",
    };

    // The crew, exactly as the TypeScript fixture builds it.
    private static SessionDto Architect() =>
        S("108", 2, bucket: "needsYou", needsYouSince: At(21, 39, 20), createdAt: At(16, 12, 42));

    private static SessionDto W106() => S("106", 5, controller: "108", createdAt: At(17, 0, 8));

    private static SessionDto W102() =>
        S("102", 3, controller: "108", bucket: "supervised", createdAt: At(16, 22, 1));

    private static SessionDto W101() =>
        S("101", 4, controller: "108", bucket: "supervised", createdAt: At(16, 39, 20));

    private static SessionDto S103() => S("103", 1, bucket: "needsYou", needsYouSince: At(21, 41, 13));

    private static SessionDto S100() => S("100", 0, bucket: "snoozed");

    private static SessionDto S112() => S("112", 12);

    private static string[] Ids(IEnumerable<SessionDto> sessions) => sessions.Select(s => s.SessionId).ToArray();

    // ===== Build: nesting, order, a dead supervisor, a session that names itself =====

    [Fact]
    public void Build_NestsUnderTheSupervisorNamedByControllerSessionId_ChildrenInDesktopOrder()
    {
        var architect = Architect();
        var s112 = S112();
        var tree = SessionTree.Build(new[] { S100(), S103(), architect, W106(), W102(), W101(), s112 });

        Assert.Equal(new[] { "100", "103", "108", "112" }, Ids(tree.Roots));
        // Desktop order is the owner's drag order: 102 (3), 101 (4), 106 (5) - NOT the order they arrived.
        Assert.Equal(new[] { "102", "101", "106" }, Ids(SessionTree.ChildrenOf(tree, architect)));
        Assert.Empty(SessionTree.ChildrenOf(tree, s112));
    }

    [Fact]
    public void Build_KeepsRootsInTheOrderGiven_SoTheCallerOwnsMyOrderVersusAttention()
    {
        var tree = SessionTree.Build(new[] { S112(), Architect(), S100(), W106() });

        Assert.Equal(new[] { "112", "108", "100" }, Ids(tree.Roots));
    }

    [Fact]
    public void Build_AChildWhoseSupervisorIsAbsent_IsATopLevelRow_SoADeadSupervisorSurfacesItsSessions()
    {
        var tree = SessionTree.Build(new[] { S100(), W106(), W102() });

        Assert.Equal(new[] { "100", "106", "102" }, Ids(tree.Roots));
        Assert.Empty(tree.ChildrenBySessionId);
    }

    [Fact]
    public void Build_NeverNestsASessionUnderItself()
    {
        var loop = S("9", controller: "9");

        var tree = SessionTree.Build(new[] { loop });

        Assert.Equal(new[] { "9" }, Ids(tree.Roots));
        Assert.Empty(tree.ChildrenBySessionId);
    }

    [Fact]
    public void Build_ASupervisorIdThatIsBlankOrOnlySpaces_MeansTheSessionAnswersToNobody()
    {
        // The wire can carry "" or "   " for a session nobody is driving. Neither may be treated as the
        // id of a session, and neither may make the row vanish looking for a parent that cannot exist.
        var empty = S("e", 0, controller: "");
        var spaces = S("s", 1, controller: "   ");

        var tree = SessionTree.Build(new[] { empty, spaces });

        Assert.Equal(new[] { "e", "s" }, Ids(tree.Roots));
        Assert.Empty(tree.ChildrenBySessionId);
    }

    [Fact]
    public void Build_ASupervisorIdWithSpacesAroundIt_StillNamesItsSupervisor()
    {
        var architect = Architect();
        var child = S("c", 1, controller: "  108  ");

        var tree = SessionTree.Build(new[] { architect, child });

        Assert.Equal(new[] { "108" }, Ids(tree.Roots));
        Assert.Equal(new[] { "c" }, Ids(SessionTree.ChildrenOf(tree, architect)));
    }

    [Fact]
    public void AnEmptyList_ProducesAnEmptyTree_AnEmptyCrewAndNoSections()
    {
        var tree = SessionTree.Build(Array.Empty<SessionDto>());

        Assert.Empty(tree.Roots);
        Assert.Empty(tree.ChildrenBySessionId);
        Assert.Empty(SessionTree.AttentionSections(Array.Empty<SessionDto>()));

        var sum = SessionTree.SummarizeCrew(S112(), Array.Empty<SessionDto>());
        Assert.Equal(0, sum.Count);
        Assert.Equal("0 under it: 0 working, 0 stopped, 0 need you", SessionTree.CrewSummaryLine(sum));
    }

    [Fact]
    public void Build_ChildrenNeverReSort_SoAttentionIsATopLevelAnswerOnly()
    {
        // A child that needs you and a child that is working, dragged into an order that puts the working
        // one first. Attention would hoist the red one; under a parent nothing may.
        var architect = Architect();
        var working = S("w", 1, controller: "108");
        var red = S("r", 2, controller: "108", bucket: "needsYou", needsYouSince: At(20, 0, 0));

        var tree = SessionTree.Build(new[] { architect, red, working });

        Assert.Equal(new[] { "w", "r" }, Ids(SessionTree.ChildrenOf(tree, architect)));
    }

    // ===== The crew summary =====

    [Fact]
    public void SummarizeCrew_CountsTheChildrenByTriageBucket_AndCarriesAZeroNeedsYouCount()
    {
        var architect = Architect();

        var sum = SessionTree.SummarizeCrew(architect, new[] { W102(), W101(), W106() });

        Assert.Equal(3, sum.Count);
        Assert.Equal(0, sum.NeedsYou);
        Assert.Equal(1, sum.Working);
        Assert.Equal(2, sum.Stopped);
        Assert.Equal(At(16, 12, 42), sum.Since);
        Assert.Equal("3 under it: 1 working, 2 stopped, 0 need you", SessionTree.CrewSummaryLine(sum));
    }

    [Fact]
    public void SummarizeCrew_CountsASurfacedRedChild_SoACollapsedCrewCannotHideIt()
    {
        // The dead-supervisor case: the child still names 108 as its supervisor, but nothing is driving it
        // any more, so it has gone red on its own (issue #2826).
        var red = S("7", controller: "108", bucket: "needsYou", needsYouSince: At(21, 0, 0));

        var sum = SessionTree.SummarizeCrew(Architect(), new[] { red, W106() });

        Assert.Equal(1, sum.NeedsYou);
        Assert.Equal("2 under it: 1 working, 0 stopped, 1 need you", SessionTree.CrewSummaryLine(sum));
    }

    [Fact]
    public void CrewAge_AgesTheCrewFromItsOldestSession_RootIncluded()
    {
        // 108 was created at 16:12:42 and 106 at 17:00:08, so the crew is as old as the Architect.
        var sum = SessionTree.SummarizeCrew(Architect(), new[] { W106() });

        Assert.Equal("5h 29m", SessionTree.CrewAge(sum, At(21, 42, 0)));
    }

    [Fact]
    public void CrewAge_GivesNoAge_WhenNothingCarriesACreationStamp()
    {
        var rootWithoutStamp = new SessionDto { SessionId = "x", ActivityState = "Working" };
        var childWithoutStamp = new SessionDto { SessionId = "y", ActivityState = "Working" };

        var sum = SessionTree.SummarizeCrew(rootWithoutStamp, new[] { childWithoutStamp });

        Assert.Null(sum.Since);
        Assert.Equal("", SessionTree.CrewAge(sum, At(21, 42, 0)));
    }

    [Theory]
    // The ladder crewAge climbs, from waiting.ts durationFromMs: minutes, then hours and minutes, then
    // days and hours. A sub-minute crew reads "0m" - the "just now" wording belongs to a different label.
    [InlineData(0, "0m")]
    [InlineData(59, "0m")]
    [InlineData(12 * 60, "12m")]
    [InlineData(64 * 60, "1h 4m")]
    [InlineData((2 * 24 * 60 + 3 * 60) * 60, "2d 3h")]
    public void DurationFrom_ClimbsTheSameLadderAsTheClients(int seconds, string expected)
    {
        Assert.Equal(expected, SessionTree.DurationFrom(TimeSpan.FromSeconds(seconds)));
    }

    // ===== The attention order =====

    [Fact]
    public void AttentionSections_PutTheLongestWaitOnTop_ThenWorking_ThenSnoozed_AndDropEmptySections()
    {
        var sections = SessionTree.AttentionSections(new[] { S100(), S103(), Architect(), S112() });

        Assert.Equal(new[] { "Needs you", "Working", "Snoozed" }, sections.Select(s => s.Title).ToArray());
        // 108 has waited since 21:39:20 and 103 since 21:41:13 - 108 has waited longer, so 108 is first.
        Assert.Equal(new[] { "108", "103" }, Ids(sections[0].Roots));
        Assert.Equal(new[] { "112" }, Ids(sections[1].Roots));
        Assert.Equal(new[] { "100" }, Ids(sections[2].Roots));
    }

    [Fact]
    public void AttentionSections_OmitASectionWithNothingInIt()
    {
        var sections = SessionTree.AttentionSections(new[] { S112() });

        Assert.Equal(new[] { "Working" }, sections.Select(s => s.Title).ToArray());
    }

    [Fact]
    public void AttentionSections_ASessionWithNoWaitingStampSortsToTheBottomOfTheLine()
    {
        // We cannot place it in the queue, so it must never jump ahead of a session with a real wait.
        var unstamped = S("u", 0, bucket: "needsYou");
        var waiting = S("w", 9, bucket: "needsYou", needsYouSince: At(21, 0, 0));

        var sections = SessionTree.AttentionSections(new[] { unstamped, waiting });

        Assert.Equal(new[] { "w", "u" }, Ids(sections[0].Roots));
    }

    // ===== The three cases the pull request 2852 inspections forced =====

    [Fact]
    public void DescendantsOf_KeepsASecondOwnershipLevelReachable_WithItsDirectParentAndDepth()
    {
        var architect = Architect();
        var manager = S("M", 6, controller: "108", createdAt: At(17, 30, 0));
        var deepWorker = S("D", 7, controller: "M", bucket: "supervised", createdAt: At(17, 40, 0));

        var tree = SessionTree.Build(new[] { architect, manager, deepWorker, W106() });

        Assert.Equal(new[] { "108" }, Ids(tree.Roots));
        Assert.Equal(new[] { "106", "M" }, Ids(SessionTree.ChildrenOf(tree, architect)));
        Assert.Equal(new[] { "D" }, Ids(SessionTree.ChildrenOf(tree, manager)));

        var descendants = SessionTree.DescendantsOf(tree, architect);
        Assert.Equal(
            new[] { "106@1", "M@1", "D@2" },
            descendants.Select(d => $"{d.Session.SessionId}@{d.Depth}").ToArray());
        // Each descendant names the session it is DIRECTLY under, not the root.
        Assert.Equal(new[] { "108", "108", "M" }, descendants.Select(d => d.Parent.SessionId).ToArray());

        var sum = SessionTree.SummarizeCrew(architect, descendants.Select(d => d.Session));
        Assert.Equal("3 under it: 2 working, 1 stopped, 0 need you", SessionTree.CrewSummaryLine(sum));
    }

    [Fact]
    public void SummarizeCrew_CountsAGrandchild_SoACollapsedCrewCannotHideTheLevelBelowIt()
    {
        // The counting half of the case above, asserted on its own: the multi-level assertion there runs
        // first, so it would swallow this symptom if the two shared a test.
        var architect = Architect();
        var manager = S("M", 6, controller: "108");
        var grandchild = S("D", 7, controller: "M", bucket: "supervised");

        var tree = SessionTree.Build(new[] { architect, manager, grandchild });
        var sum = SessionTree.SummarizeCrew(architect, SessionTree.DescendantsOf(tree, architect).Select(d => d.Session));

        Assert.Equal(2, sum.Count);
        Assert.Equal(1, sum.Stopped);
        Assert.Equal("2 under it: 1 working, 1 stopped, 0 need you", SessionTree.CrewSummaryLine(sum));
    }

    [Fact]
    public void Build_NestsAChildOnAnotherDirectorUnderItsParent_AndTheRowCanSayItIsElsewhere()
    {
        var local = S("108", 2, bucket: "needsYou", needsYouSince: At(21, 39, 20), createdAt: At(16, 12, 42),
            directorId: "d1", machineName: "SOREN_NORTH");
        var remote = S("R", 0, controller: "108", directorId: "d2", machineName: "SORENLAPTOP");

        var tree = SessionTree.Build(new[] { local, remote });

        Assert.Equal(new[] { "108" }, Ids(tree.Roots));
        Assert.Equal(new[] { "R" }, Ids(SessionTree.ChildrenOf(tree, local)));
        Assert.True(SessionTree.IsOnAnotherMachine(local, remote));

        var alongside = S("106", 5, controller: "108", directorId: "d1", machineName: "SOREN_NORTH");
        Assert.False(SessionTree.IsOnAnotherMachine(local, alongside));
    }

    [Fact]
    public void Build_RendersEveryMemberOfAnOwnershipLoopExactlyOnce_AsRoots()
    {
        var a = S("a", 0, controller: "b");
        var b = S("b", 1, controller: "a");

        var tree = SessionTree.Build(new[] { S112(), a, b });

        Assert.Equal(new[] { "112", "a", "b" }, Ids(tree.Roots));
        Assert.Empty(SessionTree.DescendantsOf(tree, a));
        Assert.Empty(SessionTree.DescendantsOf(tree, b));
    }

    [Fact]
    public void Build_PromotesALoopsWholeSubtreeWithIt_SoNothingUnderALoopIsLost()
    {
        var a = S("a", 0, controller: "b");
        var b = S("b", 1, controller: "a");
        var under = S("u", 2, controller: "b");

        var tree = SessionTree.Build(new[] { a, b, under });

        Assert.Equal(new[] { "a", "b" }, Ids(tree.Roots));
        Assert.Equal(new[] { "u" }, Ids(SessionTree.ChildrenOf(tree, b)));
    }

    [Fact]
    public void Build_EverySessionRendersExactlyOnce_AcrossTheWholeFixture()
    {
        // The invariant every rule above serves, asserted once over a roster that carries all of them at
        // the same time: a normal crew, a two-level crew, a dead supervisor's orphan, a self-loop, and a
        // two-session loop with a tail.
        var architect = Architect();
        var manager = S("M", 6, controller: "108");
        var orphan = S("orphan", 8, controller: "gone");
        var selfLoop = S("self", 9, controller: "self");
        var a = S("a", 10, controller: "b");
        var b = S("b", 11, controller: "a");
        var tail = S("tail", 13, controller: "b");
        var all = new[] { architect, W106(), manager, orphan, selfLoop, a, b, tail, S112() };

        var tree = SessionTree.Build(all);

        var rendered = tree.Roots
            .SelectMany(r => SessionTree.DescendantsOf(tree, r).Select(d => d.Session).Prepend(r))
            .Select(s => s.SessionId)
            .ToList();

        Assert.Equal(all.Length, rendered.Count);
        Assert.Equal(all.Select(s => s.SessionId).OrderBy(x => x, StringComparer.Ordinal),
            rendered.OrderBy(x => x, StringComparer.Ordinal));
    }
}
