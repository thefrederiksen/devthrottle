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
    public void Build_ASessionWhoseOwnIdArrivesPadded_IsStillFoundByAChildThatNamesItPlainly()
    {
        // The other half of the trimming rule, and the half nothing was holding: the fixtures normalised a
        // padded SUPERVISOR id, so removing the trim from the session's OWN id left everything green. The
        // parent would then be keyed under "  P  " while the child asks for "P", and the child would
        // silently become a top-level row instead of nesting.
        var parent = S("  P  ", 0);
        var child = S("kid", 1, controller: "P");

        var tree = SessionTree.Build(new[] { parent, child });

        Assert.Equal(new[] { "  P  " }, Ids(tree.Roots));
        Assert.Equal(new[] { "kid" }, Ids(SessionTree.ChildrenOf(tree, parent)));
        Assert.Equal(new[] { "kid@1" },
            SessionTree.DescendantsOf(tree, parent).Select(d => $"{d.Session.SessionId}@{d.Depth}").ToArray());
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
    public void CrewAge_AgesTheCrewFromADescendantWhenTheDescendantIsTheOlderOne()
    {
        // The fixtures only ever had a root older than its children, so dropping the descendants from the
        // ageing pass - taking the root alone - left everything green while the crew reported itself
        // younger than the session inside it.
        var root = S("R", 0, createdAt: At(18, 0, 0));
        var older = S("older", 1, controller: "R", createdAt: At(16, 0, 0));

        var sum = SessionTree.SummarizeCrew(root, new[] { older });

        Assert.Equal(At(16, 0, 0), sum.Since);
        Assert.Equal("2h 0m", SessionTree.CrewAge(sum, At(18, 0, 0)));
    }

    [Fact]
    public void CrewAge_TreatsAStampFromYearsBeforeThisOneAsReal_AndOnlyRejectsWhatCannotBeACreationStamp()
    {
        // The threshold is the year 2000, and every valid stamp in the fixtures was in 2026 - so raising
        // it to 2026 changed no answer. It would then throw away the real creation stamp of anything
        // started in an earlier year and age the crew from the wrong session entirely.
        var root = S("R", 0, createdAt: new DateTime(2026, 9, 14, 18, 0, 0, DateTimeKind.Utc));
        var longRunning = S("longRunning", 1, controller: "R",
            createdAt: new DateTime(2015, 6, 1, 9, 0, 0, DateTimeKind.Utc));

        var sum = SessionTree.SummarizeCrew(root, new[] { longRunning });

        Assert.Equal(new DateTime(2015, 6, 1, 9, 0, 0, DateTimeKind.Utc), sum.Since);
        Assert.Equal("2d 4h", SessionTree.CrewAge(sum, new DateTime(2015, 6, 3, 13, 30, 0, DateTimeKind.Utc)));

        // And the thing the threshold is actually for: a default that survived a serializer is NOT a
        // creation stamp, so it is ignored rather than ageing the crew from the beginning of time.
        var zeroStamp = new SessionDto { SessionId = "z", ActivityState = "Working" };
        var onlyRealStamps = SessionTree.SummarizeCrew(root, new[] { zeroStamp });
        Assert.Equal(new DateTime(2026, 9, 14, 18, 0, 0, DateTimeKind.Utc), onlyRealStamps.Since);
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

    [Fact]
    public void AttentionSections_TwoEqualWaits_AreOrderedByCreationTimeAndThenBySessionId()
    {
        // Both deterministic tie-breaks were decorative: no fixture held two sessions with the same
        // waiting stamp, so removing either one changed no answer - and equal-wait rows would then swap
        // places between polls, which is the exact jitter the waiting line exists to stop.
        //
        // The ids and creation times deliberately disagree with each other: "z" is the OLDEST but sorts
        // LAST by id, so a fold that had lost the creation-time tie-break cannot reach this answer by the
        // id tie-break alone. And "b" is handed in before "a" with the same creation time, so a fold that
        // had lost the id tie-break would fall back to arrival order and put "b" first.
        var wait = At(21, 0, 0);
        var b = S("b", 1, bucket: "needsYou", needsYouSince: wait, createdAt: At(16, 0, 0));
        var a = S("a", 2, bucket: "needsYou", needsYouSince: wait, createdAt: At(16, 0, 0));
        var z = S("z", 3, bucket: "needsYou", needsYouSince: wait, createdAt: At(15, 0, 0));

        var sections = SessionTree.AttentionSections(new[] { b, a, z });

        Assert.Equal(new[] { "z", "a", "b" }, Ids(sections[0].Roots));
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
    public void IsOnAnotherMachine_FallsBackToTheMachineName_WhenEitherSideDoesNotSayWhichDirectorItIsOn()
    {
        // The fallback existed and was unreachable by any fixture: every session in the suite carried a
        // Director id, so deleting the fallback and returning false left everything green - and a child on
        // another machine would then have been reported as sitting beside its parent whenever an id was
        // missing. A Director id can be absent on a roster read before the Director has registered, and the
        // machine name is the only thing left that can tell two of them apart.
        var host = S("host", 0, machineName: "SOREN_NORTH");
        var beside = S("beside", 1, controller: "host", machineName: "SOREN_NORTH");
        var away = S("away", 2, controller: "host", machineName: "SORENLAPTOP");
        var awayNamed = S("awayNamed", 3, controller: "host", directorId: "d9", machineName: "SORENLAPTOP");

        Assert.False(SessionTree.IsOnAnotherMachine(host, beside));
        Assert.True(SessionTree.IsOnAnotherMachine(host, away));
        // ONE side missing its Director id is enough to fall back: an id cannot be compared to nothing.
        Assert.True(SessionTree.IsOnAnotherMachine(host, awayNamed));
        Assert.False(SessionTree.IsOnAnotherMachine(awayNamed, away));
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
