using System.Globalization;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// BRINGING THE SESSIONS BACK, AND THE SEED FILE THAT SAYS LESS (mission "Smart Director Restart", section
/// 5.3 item 14 and ruling 10.6).
///
/// The way up re-implements no part of a restore. What it owns is the ORDER - which seats, and a seed file
/// each - so that is what is pinned here, against a restore that records what it was handed.
///
/// The seed file's own test asserts the WHOLE file against the four parts it is allowed to hold. It is
/// written that way on purpose: a test that only checked some banned phrase was absent would pass for a
/// file holding a different rule of conduct nobody thought to ban.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public sealed class DirectorWayUpBringBackTests : IDisposable
{
    private static readonly DateTime Shutdown = new(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc);

    private readonly string _folder = Path.Combine(
        Path.GetTempPath(), "way-up-tests-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>Create the drain folder the handovers and the seed files share.</summary>
    public DirectorWayUpBringBackTests() => Directory.CreateDirectory(_folder);

    /// <summary>Take the folder away again.</summary>
    public void Dispose()
    {
        if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true);
    }

    private string Handover(string seat) => Path.Combine(_folder, $"{seat} - handover.md");

    private WorkspaceSeat Owed(string id, string name, string? reportsTo = null, int sortOrder = 0)
        => WayUpTestRig.Owed(id, name, Handover(id), reportsTo, sortOrder: sortOrder);

    /// <summary>
    /// THE ORDER NAMES EVERY SEAT OF EVERY TICKED ROW, and the seats under it, and it is handed to the
    /// restore - which orders leads first and resolves each owner itself.
    /// </summary>
    [Fact]
    public async Task Bring_back_names_every_seat_under_every_ticked_row()
    {
        var rig = Rig(
            Owed("lead-a", "Lead A", sortOrder: 0),
            Owed("tech-a", "Tech A", reportsTo: "lead-a", sortOrder: 1),
            Owed("worker-a", "Worker A", reportsTo: "tech-a", sortOrder: 2),
            Owed("lead-b", "Lead B", sortOrder: 3));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a", "lead-b" }), CancellationToken.None);

        var order = Assert.Single(rig.Restore.Orders);
        Assert.Equal("restart-1", order.WorkspaceId);
        Assert.Equal(new[] { "lead-a", "tech-a", "worker-a", "lead-b" }, order.Seats?.ToArray());
        Assert.Null(order.RequestedBySessionId);
        Assert.True(result.Started);
        Assert.Equal(4, result.Seats.Count);
        Assert.Equal("4 sessions came back.", result.Message);

        // THE BRING BACK ASKS NOTHING ABOUT WHAT IS RUNNING EITHER. It re-implements no part of a restore,
        // and the restore refuses a seat that is still alive by its own rule, on its own roster read. The
        // seam CAN ask - the reopen needs it - so only this count keeps a second such question out of here.
        Assert.Equal(0, rig.Gateway.RosterAsked);
    }

    /// <summary>A row left unticked is left alone: its seats are not named, so the restore never touches them.</summary>
    [Fact]
    public async Task A_row_left_unticked_is_not_brought_back()
    {
        var rig = Rig(
            Owed("lead-a", "Lead A", sortOrder: 0),
            Owed("lead-b", "Lead B", sortOrder: 1));

        await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-b" }), CancellationToken.None);

        var order = Assert.Single(rig.Restore.Orders);
        Assert.Equal(new[] { "lead-b" }, order.Seats?.ToArray());
    }

    /// <summary>
    /// THE SEED FILE, WHOLE. Four parts and nothing else: you are a restored session, read this document,
    /// what changed while you were gone, verify the state of your work before acting.
    ///
    /// NO RULES OF CONDUCT. On 19 September 2026 a seed line of exactly that kind ("no commits unless the
    /// owner asks") overrode a commit the session's own handover had planned. The handover is the session's
    /// own plan and a line added underneath it outranks nothing.
    /// </summary>
    [Fact]
    public async Task The_seed_file_holds_exactly_the_four_parts_and_nothing_else()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));

        await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        var order = Assert.Single(rig.Restore.Orders);
        var seeds = (IReadOnlyDictionary<string, string>)(order.Seeds ?? new Dictionary<string, string>());
        var seed = Assert.Contains("lead-a", seeds);
        Assert.Equal(Path.Combine(_folder, "lead-a - Lead A - what changed.md"), seed);

        var newLine = Environment.NewLine;
        var when = Shutdown.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);
        var expected =
            "You are a restored session." + newLine +
            newLine +
            $"Read {Handover("lead-a")} - it is your own handover, written before you were shut down." + newLine +
            newLine +
            $"What changed while you were gone: the Director DevThrottle_1 was shut down on {when} " +
            "for this reason: update to 2.9.0, and it has restarted." + newLine +
            newLine +
            "Verify the state of your work before you act on anything." + newLine;

        Assert.Equal(expected, await File.ReadAllTextAsync(seed));
    }

    /// <summary>The same four parts when the owner gave no reason: the third part still says what changed,
    /// and still says no more than that.</summary>
    [Fact]
    public async Task The_seed_file_with_no_reason_holds_the_same_four_parts()
    {
        var rig = RigWithReason(null, Owed("lead-a", "Lead A"));

        await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        var seeds = (IReadOnlyDictionary<string, string>)(Assert.Single(rig.Restore.Orders).Seeds ?? new Dictionary<string, string>());
        var seed = Assert.Contains("lead-a", seeds);
        var newLine = Environment.NewLine;
        var when = Shutdown.ToLocalTime().ToString("d MMMM yyyy 'at' HH:mm", CultureInfo.InvariantCulture);
        var expected =
            "You are a restored session." + newLine +
            newLine +
            $"Read {Handover("lead-a")} - it is your own handover, written before you were shut down." + newLine +
            newLine +
            $"What changed while you were gone: the Director DevThrottle_1 was shut down on {when} " +
            "with no reason given, and it has restarted." + newLine +
            newLine +
            "Verify the state of your work before you act on anything." + newLine;

        Assert.Equal(expected, await File.ReadAllTextAsync(seed));
    }

    /// <summary>The seed file goes beside the handover it points at, in the drain folder the record already
    /// names, so a restored session can read both.</summary>
    [Fact]
    public async Task The_seed_file_is_written_beside_the_handover()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));

        await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        var seeds = (IReadOnlyDictionary<string, string>)(Assert.Single(rig.Restore.Orders).Seeds ?? new Dictionary<string, string>());
        var seed = Assert.Contains("lead-a", seeds);
        Assert.Equal(_folder, Path.GetDirectoryName(seed));
        Assert.True(File.Exists(seed));
    }

    /// <summary>
    /// A seat with no handover gets NO seed file - a seed naming a document that does not exist is worse
    /// than none - and it is still named in the order, so the restore refuses it by itself with its own
    /// reason and the seats around it still come back.
    /// </summary>
    [Fact]
    public async Task A_seat_with_no_handover_gets_no_seed_file_and_is_still_named()
    {
        var withoutHandover = WayUpTestRig.Owed("worker", "A worker", handover: "");
        withoutHandover.HandoverPath = null;
        var rig = Rig(Owed("lead-a", "Lead A"), withoutHandover);
        rig.Restore.FailureBySeat["worker"] = "it has no handover and no seed file";

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a", "worker" }), CancellationToken.None);

        var order = Assert.Single(rig.Restore.Orders);
        Assert.Equal(new[] { "lead-a", "worker" }, order.Seats?.ToArray());
        Assert.Equal(new[] { "lead-a" }, order.Seeds?.Keys.ToArray());
        Assert.True(result.Started);
        Assert.Contains(result.Seats, s => s.SessionId == "worker" && s.RestoredSessionId is null);
        Assert.Contains("no handover and no seed file", result.Seats.Single(s => s.SessionId == "worker").Outcome);
    }

    /// <summary>A row the record does not hold is refused BY NAME and nothing is started. A row quietly
    /// dropped is a session left dead with nobody noticing.</summary>
    [Fact]
    public async Task A_row_that_is_not_in_the_record_is_refused_by_name()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a", "a-row-that-does-not-exist" }),
            CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("a-row-that-does-not-exist", result.Refusal);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>A seat that ended without a handover is never brought back this way, and the refusal says
    /// what to do instead.</summary>
    [Fact]
    public async Task A_row_that_ended_without_a_handover_is_refused_with_what_to_do_instead()
    {
        var rig = Rig(Owed("lead-a", "Lead A"), WayUpTestRig.Ended("stuck", "A busy worker"));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "stuck" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("Reopen its saved conversation instead", result.Refusal);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>Nothing ticked is nothing to do, and it says so rather than handing the restore an empty
    /// order, which would bring back every owed seat.</summary>
    [Fact]
    public async Task Nothing_ticked_is_refused_and_never_sent_as_an_empty_order()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", Array.Empty<string>()), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("no row was ticked", result.Refusal);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>When the restore refuses - another restore is running, a seat is still running - the reason
    /// is handed straight back, and nothing claims to have started.</summary>
    [Fact]
    public async Task A_restore_that_refuses_is_reported_with_its_own_reason()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));
        rig.Restore.Refusal = "a restore is already running on this Director";

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Equal("a restore is already running on this Director", result.Refusal);
        Assert.Empty(result.Seats);
    }

    /// <summary>A record that has been deleted since it was offered is said plainly, not treated as an
    /// empty one.</summary>
    [Fact]
    public async Task A_record_that_is_no_longer_there_is_refused_with_its_name()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));
        rig.Gateway.ListedButGone.Add("restart-1");

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("restart-1", result.Refusal);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>A Gateway that cannot be reached refuses the bring back with the reason, and starts nothing.</summary>
    [Fact]
    public async Task The_gateway_unreachable_refuses_the_bring_back_with_the_reason()
    {
        var rig = Rig(Owed("lead-a", "Lead A"));
        rig.Gateway.Unreachable = "No connection could be made to the Gateway";

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Contains("No connection could be made to the Gateway", result.Refusal);
        Assert.Empty(rig.Restore.Orders);
    }

    /// <summary>Each seat says what became of it in plain words, and a seat that did not come back says why.</summary>
    [Fact]
    public async Task A_seat_that_does_not_come_back_says_why_beside_it()
    {
        var rig = Rig(Owed("lead-a", "Lead A", sortOrder: 0), Owed("lead-b", "Lead B", sortOrder: 1));
        rig.Restore.FailureBySeat["lead-b"] = "its owner is not running on any Director this Gateway can reach now";

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-1", new[] { "lead-a", "lead-b" }), CancellationToken.None);

        Assert.True(result.Started);
        Assert.Equal("1 came back; 1 could not. Each one says why beside it.", result.Message);
        Assert.Equal("Came back as back-lea.", result.Seats.Single(s => s.SessionId == "lead-a").Outcome);
        Assert.Contains("Did not come back:", result.Seats.Single(s => s.SessionId == "lead-b").Outcome);
    }

    private static WayUpTestRig Rig(params WorkspaceSeat[] seats) => RigWithReason("update to 2.9.0", seats);

    private static WayUpTestRig RigWithReason(string? reason, params WorkspaceSeat[] seats)
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-1", Shutdown, seats, reason));
        return rig;
    }

    /// <summary>
    /// A RECORD OF ANOTHER DIRECTOR ON THIS MACHINE IS NOT BROUGHT BACK (review finding 4). The two read
    /// paths narrow candidates to this machine and this Director's display name; the bring back took
    /// whatever workspace id it was handed, so a second caller - the phase 4 command line, or a window
    /// defect - could have brought another Director's sessions up onto this one. The Gateway's restore route
    /// refuses a record from another MACHINE and not one from another Director here.
    /// </summary>
    [Fact]
    public async Task Bringing_back_another_directors_record_is_refused_by_name_and_the_restore_is_never_called()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-theirs", Shutdown,
            new[] { WayUpTestRig.Owed("seat-1", "Their lead") },
            directorName: WayUpTestRig.OtherDirector));

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-theirs", new[] { "seat-1" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Empty(rig.Restore.Orders);
        Assert.Contains($"belongs to Director '{WayUpTestRig.OtherDirector}'", result.Refusal);
    }

    /// <summary>A record captured on ANOTHER MACHINE is refused by the same one rule, naming the machine.</summary>
    [Fact]
    public async Task Bringing_back_a_record_from_another_machine_is_refused_by_name()
    {
        var rig = new WayUpTestRig();
        var theirs = WayUpTestRig.Record("restart-elsewhere", Shutdown, new[] { WayUpTestRig.Owed("seat-1", "A lead") });
        theirs.Machine = "SOME_OTHER_MACHINE";
        rig.Gateway.With(theirs);

        var result = await rig.WayUp().BringBackAsync(
            new WayUpBringBackRequest("restart-elsewhere", new[] { "seat-1" }), CancellationToken.None);

        Assert.False(result.Started);
        Assert.Empty(rig.Restore.Orders);
        Assert.Contains("captured on machine 'SOME_OTHER_MACHINE'", result.Refusal);
    }
}
