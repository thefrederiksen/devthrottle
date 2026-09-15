using CcDirector.Core.Backends;
using CcDirector.Core.Memory;
using CcDirector.Gateway.Contracts;
using CcDirector.Core.Sessions;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// THE DIRECTOR RAIL AS THE OWNERSHIP TREE (Session List Views, slice 2).
///
/// The rail is a flat list box and always has been. What changed is WHICH rows it is given: the roster
/// is projected through the ownership tree, so a crew's sessions fold under the session that started
/// them and a collapsed crew carries them as a summary line instead. These tests drive that projection,
/// <see cref="SessionRailTree.Project"/>, over real <see cref="SessionViewModel"/>s.
///
/// WHAT THEY ARE GUARDING AGAINST, and it is a specific thing that has already happened four times.
/// The same design shipped first in TypeScript and was inspected four times, and two of those
/// inspections found the SAME defect: a renderer that stopped at the first level. The fold recorded
/// Architect -> Manager -> Worker correctly; the shell drew only the root's direct children, so the
/// Manager was given no chevron, the Worker appeared nowhere at all, and the Architect's crew line said
/// "1 under it" while two sessions were under it. Every multi-level test below exists because of that,
/// and the three-level test checks the NUMBER ON THE LINE against the number of rows that appear when
/// everything is open - the two answers that were allowed to disagree.
///
/// They also guard the second finding from those inspections, which is about tests rather than code: a
/// guard that stays green when the rule it names is deleted is decoration. Every rule these tests name
/// was deleted or mutated and the test was watched go red; slice-2-report.md records which reverts were
/// run and what each failure said.
/// </summary>
public sealed class SessionRailTreeTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>A real session, with only the facts the fold reads set on it.</summary>
    private static SessionViewModel Vm(
        string name,
        ActivityState state = ActivityState.WaitingForInput,
        SessionViewModel? supervisor = null,
        DateTimeOffset? createdAt = null,
        bool snoozed = false,
        bool liveSupervisor = true)
    {
        var session = new Session(
            Guid.NewGuid(), @"C:\test\repo", @"C:\test\repo", null,
            new InertBackend(), SessionBackendType.ConPty, createdAt ?? new DateTimeOffset(Now.AddHours(-1)));
        session.IsBrandNew = false;
        session.CustomName = name;
        if (state != ActivityState.WaitingForInput) session.ApplyTerminalActivityState(state);
        if (supervisor is not null)
        {
            session.ControllerSessionId = supervisor.Session.Id;
            // TWO SEPARATE FACTS, AND THE FIXTURE MUST CARRY BOTH. The tree NESTS on
            // ControllerSessionId, which the Director knows for itself. Whether that supervisor is
            // ALIVE is a fleet-wide question only the Gateway can answer, so it arrives as its own
            // stamp (SessionDto.HasLiveSupervisor) and it is what decides the crew line's buckets: a
            // child with a live supervisor is "stopped", and the same child once its supervisor dies
            // is "need you". Stamping it here is exactly what the Gateway does on a running Director.
            session.SetGatewayResolvedRole(SessionRoles.Worker, hasLiveSupervisor: liveSupervisor);
        }
        if (snoozed) session.ApplyGatewayHold(HoldState.Held);
        return new SessionViewModel(session);
    }

    /// <summary>Stamp the drag order the rail stamps before every projection, so a crew's sessions come
    /// back in the order the list is written in here.</summary>
    private static IReadOnlyList<SessionViewModel> Roster(params SessionViewModel[] sessions)
    {
        for (var i = 0; i < sessions.Length; i++) sessions[i].Session.SortOrder = i;
        return sessions;
    }

    private static string[] Ids(params SessionViewModel[] sessions) =>
        sessions.Select(s => s.Session.Id.ToString()).ToArray();

    private static string[] NamesOf(IReadOnlyList<SessionRailRow> rows) =>
        rows.Select(r => r.Session.DisplayName).ToArray();

    // ===== A crew that is closed hides its sessions; a crew that is open shows them =====

    [Fact]
    public void CollapsedCrew_HidesItsSessions_AndStillCarriesThemOnTheCrewLine()
    {
        var architect = Vm("Architect");
        var workerOne = Vm("Worker one", supervisor: architect);
        var workerTwo = Vm("Worker two", supervisor: architect);
        var solo = Vm("Solo");

        var rows = SessionRailTree.Project(
            Roster(architect, workerOne, workerTwo, solo),
            SessionRailOrder.MyOrder,
            Array.Empty<string>(),
            Now);

        Assert.Equal(new[] { "Architect", "Solo" }, NamesOf(rows));

        var crew = rows[0];
        Assert.True(crew.HasCrew);
        Assert.False(crew.IsExpanded);
        // Collapsing hid the rows, so the row must still carry every one of them.
        Assert.Equal(2, crew.Crew.Count);
        Assert.Equal(new[] { "Worker one", "Worker two" }, crew.Crew.Select(c => c.DisplayName).ToArray());

        // A session with nobody under it is an ordinary row: no chevron, no crew line.
        Assert.False(rows[1].HasCrew);
        Assert.Empty(rows[1].Crew);
        Assert.Equal("", rows[1].CrewLine);
    }

    [Fact]
    public void ExpandedCrew_ShowsItsSessionsInDesktopOrder_IndentedUnderIt()
    {
        var architect = Vm("Architect");
        var workerOne = Vm("Worker one", supervisor: architect);
        var workerTwo = Vm("Worker two", supervisor: architect);

        // The roster is written worker two first; the drag order below is what must win.
        var roster = Roster(architect, workerTwo, workerOne);
        // Drag "Worker one" above "Worker two": the rail stamps SortOrder from the list index, and the
        // fold orders a crew by SortOrder, so the crew must follow.
        workerOne.Session.SortOrder = 1;
        workerTwo.Session.SortOrder = 2;

        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect), Now);

        Assert.Equal(new[] { "Architect", "Worker one", "Worker two" }, NamesOf(rows));
        Assert.True(rows[0].IsExpanded);
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(1, rows[1].Depth);
        Assert.Equal(1, rows[2].Depth);
        Assert.Same(architect, rows[1].Parent);
        Assert.Same(architect, rows[2].Parent);
        // An open crew still CARRIES its summary - that is what the row falls back to the moment the
        // user closes it again - and the projection is not the thing that decides to stop drawing it.
        // Project never stamps a view model (ApplyRailRow is called only from RebuildRail), so the rule
        // "an open crew drops the crew line" is a rule about the drawn row, and it is held down where it
        // can go red: SessionRailRowRenderTests.AnOpenCrewRow_DropsTheCrewLine... and
        // SessionRailWindowTests.TheCrewRowIsStampedWithWhatTheFoldSaid.
        Assert.True(rows[0].HasCrew);
        Assert.Equal("2 under it: 0 working, 2 stopped, 0 need you", rows[0].CrewLine);
    }

    // ===== THE DEFECT THE TYPESCRIPT INSPECTIONS FOUND TWICE: stopping at the first level =====

    [Fact]
    public void ThreeLevelCrew_IsFullyReachable_AndTheCrewLineCountsEveryLevel()
    {
        var architect = Vm("Architect");
        var manager = Vm("Manager", supervisor: architect);
        var worker = Vm("Worker", supervisor: manager);
        var roster = Roster(architect, manager, worker);

        // 1. The Manager is itself a crew, so it gets its own chevron - the half that was missing.
        var oneOpen = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect), Now);
        Assert.Equal(new[] { "Architect", "Manager" }, NamesOf(oneOpen));
        Assert.True(oneOpen[1].HasCrew);
        Assert.False(oneOpen[1].IsExpanded);

        // 2. Open both and the Worker is reachable, two levels in.
        var allOpen = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect, manager), Now);
        Assert.Equal(new[] { "Architect", "Manager", "Worker" }, NamesOf(allOpen));
        Assert.Equal(new[] { 0, 1, 2 }, allOpen.Select(r => r.Depth).ToArray());
        Assert.Same(manager, allOpen[2].Parent);

        // 3. THE NUMBER ON THE LINE AGAINST THE NUMBER OF ROWS. The Architect's closed crew line must
        //    count every session that appears under it when everything is open - which is every row of
        //    the fully open projection except the Architect's own. This is the exact pair of answers the
        //    TypeScript renderer was allowed to disagree about ("1 under it" over two sessions).
        var closed = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Array.Empty<string>(), Now);
        Assert.Equal("2 under it: 0 working, 2 stopped, 0 need you", closed[0].CrewLine);
        Assert.Equal(allOpen.Count - 1, closed[0].Crew.Count);
        Assert.Equal(new[] { "Manager", "Worker" }, closed[0].Crew.Select(c => c.DisplayName).ToArray());
    }

    /// <summary>The strip of small squares on a closed crew row is one square per session under it at
    /// EVERY level - collapsing a crew must hide no colour, so a grandchild has a square too.</summary>
    [Fact]
    public void CrewSquares_CoverEveryLevel_NotJustTheDirectChildren()
    {
        var architect = Vm("Architect");
        var manager = Vm("Manager", supervisor: architect);
        var workerOne = Vm("Worker one", supervisor: manager);
        var workerTwo = Vm("Worker two", supervisor: manager);

        var rows = SessionRailTree.Project(
            Roster(architect, manager, workerOne, workerTwo),
            SessionRailOrder.MyOrder,
            Array.Empty<string>(),
            Now);

        Assert.Single(rows);
        Assert.Equal(3, rows[0].Crew.Count);
        Assert.Equal(
            new[] { "Manager", "Worker one", "Worker two" },
            rows[0].Crew.Select(c => c.DisplayName).ToArray());
    }

    // ===== The crew line's exact words, and its clock =====

    [Fact]
    public void CrewLine_SaysExactlyWhatTheFoldSays()
    {
        var architect = Vm("Architect");
        var working = Vm("Worker working", ActivityState.Working, supervisor: architect);
        var stoppedOne = Vm("Worker stopped one", supervisor: architect);
        var stoppedTwo = Vm("Worker stopped two", supervisor: architect);

        var rows = SessionRailTree.Project(
            Roster(architect, working, stoppedOne, stoppedTwo),
            SessionRailOrder.MyOrder,
            Array.Empty<string>(),
            Now);

        // The need-you count is zero for every LIVE crew (issue #2826 - a session with a live supervisor
        // never goes red) and is carried anyway, never dropped when zero, because it is the one number
        // that matters the moment the supervisor dies.
        Assert.Equal("3 under it: 1 working, 2 stopped, 0 need you", rows[0].CrewLine);
    }

    [Fact]
    public void CrewAge_IsTheAgeOfTheOldestSessionUnderIt_OnTheClockItIsGiven()
    {
        var architect = Vm("Architect", createdAt: new DateTimeOffset(Now.AddMinutes(-30)));
        var old = Vm("Worker old", supervisor: architect, createdAt: new DateTimeOffset(Now.AddHours(-5).AddMinutes(-29)));
        var young = Vm("Worker young", supervisor: architect, createdAt: new DateTimeOffset(Now.AddMinutes(-2)));

        var rows = SessionRailTree.Project(
            Roster(architect, old, young), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        Assert.Equal("5h 29m", rows[0].CrewAge);
    }

    /// <summary>
    /// A CLOSED CREW MUST NOT HIDE A RED GRANDCHILD. This is the design's own condition on collapsing at
    /// all - "a collapsed crew that hides a red session is worse than a flat list" - and it is the reason
    /// the need-you count is carried on every crew line even though it reads zero all day.
    ///
    /// The count is zero while the crew is live, because a session with a live supervisor never goes red
    /// (issue #2826). Here the grandchild's supervisor has died, so the Gateway's live-supervisor stamp is
    /// gone from it and it surfaces - two levels down, inside a crew the user has closed. The number on the
    /// closed row has to say so.
    /// </summary>
    [Fact]
    public void ClosedCrew_ReportsARedGrandchild_RatherThanHidingIt()
    {
        var architect = Vm("Architect");
        var manager = Vm("Manager", supervisor: architect);
        var surfaced = Vm("Worker whose supervisor died", supervisor: manager, liveSupervisor: false);

        var rows = SessionRailTree.Project(
            Roster(architect, manager, surfaced), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        Assert.Single(rows);
        Assert.Equal("2 under it: 0 working, 1 stopped, 1 need you", rows[0].CrewLine);
    }

    // ===== A supervisor that is gone surfaces its sessions; a loop renders once =====

    [Fact]
    public void DeadSupervisor_PutsItsSessionAtTheTopLevel()
    {
        var orphan = Vm("Orphan");
        // Its supervisor is not in this rail at all - killed, or never on this Director.
        orphan.Session.ControllerSessionId = Guid.NewGuid();
        var solo = Vm("Solo");

        var rows = SessionRailTree.Project(
            Roster(orphan, solo), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        Assert.Equal(new[] { "Orphan", "Solo" }, NamesOf(rows));
        Assert.Equal(0, rows[0].Depth);
        Assert.Null(rows[0].Parent);
    }

    [Fact]
    public void OwnershipLoop_RendersEverySessionExactlyOnce()
    {
        var a = Vm("A");
        var b = Vm("B");
        a.Session.ControllerSessionId = b.Session.Id;
        b.Session.ControllerSessionId = a.Session.Id;
        var solo = Vm("Solo");

        var rows = SessionRailTree.Project(
            Roster(a, b, solo), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        // Every session renders, and none of them renders twice - a loop that put neither in the roots
        // would hide both, and one that put both in the roots AND under each other would double them.
        Assert.Equal(3, rows.Count);
        Assert.Equal(3, rows.Select(r => r.Session.Session.Id).Distinct().Count());
        Assert.Contains(rows, r => r.Session.DisplayName == "A");
        Assert.Contains(rows, r => r.Session.DisplayName == "B");
    }

    [Fact]
    public void SelfLoop_IsATopLevelRow_NotAVanishedOne()
    {
        var itself = Vm("Its own supervisor");
        itself.Session.ControllerSessionId = itself.Session.Id;

        var rows = SessionRailTree.Project(
            Roster(itself), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        Assert.Single(rows);
        Assert.Null(rows[0].Parent);
    }

    // ===== The order switch: my order, then attention, top level only =====

    [Fact]
    public void MyOrder_KeepsTheTopLevelInTheUsersDragOrder()
    {
        var snoozed = Vm("Snoozed one", snoozed: true);
        var needsYou = Vm("Needs you");
        var working = Vm("Working one", ActivityState.Working);

        var rows = SessionRailTree.Project(
            Roster(snoozed, needsYou, working), SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        Assert.Equal(new[] { "Snoozed one", "Needs you", "Working one" }, NamesOf(rows));
        // My order has no sections at all.
        Assert.All(rows, r => Assert.Equal("", r.SectionTitle));
    }

    [Fact]
    public void AttentionOrder_SectionsTheTopLevel_LongestWaitFirst()
    {
        var snoozed = Vm("Snoozed one", snoozed: true);
        var working = Vm("Working one", ActivityState.Working);
        var waitedALittle = Vm("Needs you recently");
        var waitedLonger = Vm("Needs you longest");

        // The waiting line is ordered by the Gateway's needs-you clock, earliest stamp first.
        waitedALittle.Session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", Now.AddMinutes(-2), null, false);
        waitedLonger.Session.ApplyGatewayDisplayState("red", "Needs you", "needsYou", Now.AddHours(-3), null, false);

        var rows = SessionRailTree.Project(
            Roster(snoozed, working, waitedALittle, waitedLonger),
            SessionRailOrder.Attention,
            Array.Empty<string>(),
            Now);

        Assert.Equal(
            new[] { "Needs you longest", "Needs you recently", "Working one", "Snoozed one" },
            NamesOf(rows));

        // Each section announces itself once, above its first row, and only the needs-you one is red.
        Assert.Equal("NEEDS YOU 2", rows[0].SectionTitle);
        Assert.True(rows[0].SectionIsNeedsYou);
        Assert.Equal("", rows[1].SectionTitle);
        Assert.Equal("WORKING 1", rows[2].SectionTitle);
        Assert.False(rows[2].SectionIsNeedsYou);
        Assert.Equal("SNOOZED 1", rows[3].SectionTitle);
    }

    [Fact]
    public void AttentionOrder_NeverReordersTheSessionsUnderAParent()
    {
        var architect = Vm("Architect");
        // Under a live supervisor nothing goes red, so a crew has nothing for attention to reorder -
        // and it must be left exactly in the order the user dragged it into.
        var first = Vm("Worker first", supervisor: architect);
        var second = Vm("Worker second", ActivityState.Working, supervisor: architect);
        var third = Vm("Worker third", supervisor: architect);

        var rows = SessionRailTree.Project(
            Roster(architect, first, second, third),
            SessionRailOrder.Attention,
            Ids(architect),
            Now);

        Assert.Equal(
            new[] { "Architect", "Worker first", "Worker second", "Worker third" },
            NamesOf(rows));
    }

    // ===== Dragging: among siblings, never across a parent boundary =====

    [Fact]
    public void Drag_ReordersATopLevelRowAmongTopLevelRows()
    {
        var first = Vm("First");
        var second = Vm("Second");
        var third = Vm("Third");
        var roster = Roster(first, second, third);

        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Array.Empty<string>(), Now);

        // Drop "Third" above "First": the insertion point is row 0, which is the top level's own slot.
        Assert.Equal(0, SessionRailDrag.DropTarget(rows, roster, third, 0));
        // Drop it between "First" and "Second".
        Assert.Equal(1, SessionRailDrag.DropTarget(rows, roster, third, 1));
    }

    [Fact]
    public void Drag_ReordersAChildAmongItsOwnSiblings()
    {
        var architect = Vm("Architect");
        var one = Vm("Worker one", supervisor: architect);
        var two = Vm("Worker two", supervisor: architect);
        var three = Vm("Worker three", supervisor: architect);
        var roster = Roster(architect, one, two, three);

        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect), Now);
        Assert.Equal(new[] { "Architect", "Worker one", "Worker two", "Worker three" }, NamesOf(rows));

        // Row 1 is the crew's first child slot - directly under the open crew row.
        Assert.Equal(1, SessionRailDrag.DropTarget(rows, roster, three, 1));
        // Between worker one and worker two.
        Assert.Equal(2, SessionRailDrag.DropTarget(rows, roster, three, 2));
    }

    [Fact]
    public void Drag_RefusesToLiftAChildOutOfItsCrew()
    {
        var solo = Vm("Solo");
        var architect = Vm("Architect");
        var worker = Vm("Worker", supervisor: architect);
        var tail = Vm("Tail");
        var roster = Roster(solo, architect, worker, tail);

        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect), Now);
        Assert.Equal(new[] { "Solo", "Architect", "Worker", "Tail" }, NamesOf(rows));

        // Above the very first row is the top level. A child dropped there would be leaving its crew,
        // and ownership is not arrangement - so the drag does nothing at all.
        Assert.Null(SessionRailDrag.DropTarget(rows, roster, worker, 0));
        // Between two top-level rows is the top level too, and nothing ends at that slot - the crew
        // below it is still to come - so a child cannot land there either.
        Assert.Null(SessionRailDrag.DropTarget(rows, roster, worker, 1));

        // For contrast, the slot at the BOTTOM of its own crew IS still its own crew, so that one is
        // allowed: it makes the worker the crew's last session and moves it nowhere else.
        Assert.NotNull(SessionRailDrag.DropTarget(rows, roster, worker, 3));
    }

    [Fact]
    public void Drag_RefusesToDropATopLevelRowInsideSomeoneElsesCrew()
    {
        var architect = Vm("Architect");
        var workerOne = Vm("Worker one", supervisor: architect);
        var workerTwo = Vm("Worker two", supervisor: architect);
        var solo = Vm("Solo");
        var roster = Roster(architect, workerOne, workerTwo, solo);

        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Ids(architect), Now);
        Assert.Equal(new[] { "Architect", "Worker one", "Worker two", "Solo" }, NamesOf(rows));

        // Row 1 is the crew's first child slot; row 2 is between its two sessions. Neither is the top
        // level, so a top-level row cannot land in either.
        Assert.Null(SessionRailDrag.DropTarget(rows, roster, solo, 1));
        Assert.Null(SessionRailDrag.DropTarget(rows, roster, solo, 2));
        // Row 3 is after the crew's last child and IS the top level again, so this one is allowed.
        Assert.NotNull(SessionRailDrag.DropTarget(rows, roster, solo, 3));
    }

    [Fact]
    public void Drag_WithinACollapsedRail_StillOnlyEverSeesTopLevelRows()
    {
        var architect = Vm("Architect");
        var worker = Vm("Worker", supervisor: architect);
        var solo = Vm("Solo");
        var roster = Roster(architect, worker, solo);

        // Crew closed: the worker has no row at all, so it cannot be dragged and nothing can be dropped
        // into the crew by accident.
        var rows = SessionRailTree.Project(roster, SessionRailOrder.MyOrder, Array.Empty<string>(), Now);
        Assert.Equal(new[] { "Architect", "Solo" }, NamesOf(rows));

        Assert.Null(SessionRailDrag.DropTarget(rows, roster, worker, 1));
        Assert.Equal(0, SessionRailDrag.DropTarget(rows, roster, solo, 0));
    }

    /// <summary>An inert backend: the Session needs one, these tests never run a process.</summary>
    private sealed class InertBackend : ISessionBackend
    {
        public int ProcessId => 1234;
        public string Status => "Inert";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface; nothing raises them here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
