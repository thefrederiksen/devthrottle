using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.ControlApi.Drain;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.UnitTests.Drain;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// THE COMMAND LINE DOOR onto the smart shutdown (mission "Smart Director Restart", section 5.3 item 12).
///
/// One broken window must never again leave a Director impossible to empty, so the same engine File, Smart
/// Restart calls is reachable over the tunnel: start it, read where it got to, and read every record this
/// Director ever wrote.
///
/// WHAT THESE TESTS WATCH, and it is two different things:
///  - the HOST's own dispatch, on a real <see cref="ControlApiHost"/>. Put the verbs back behind a
///    stand-in, or stop asking the engine's availability question before starting, and these go red. They
///    cover every answer a Director with no Gateway can give, which is every refusal;
///  - the ONE TRANSLATION into the wire shape, over snapshots and results the REAL engine raised on the
///    real drain, and over a history the real way up computed. No test here builds a snapshot, a result or
///    a history by hand: a test that hand-builds its input never watches the caller.
///
/// WHAT THEY DO NOT COVER, stated rather than implied: a start that actually runs THROUGH the host, which
/// needs a Gateway client the host has no seam for, and the Gateway's three routes, which need a host.
/// The guard on those routes is proved in <c>SessionKeyGuardTests</c>.
///
/// In the collection every test that takes one of the Director's one-at-a-time gates shares.
/// </summary>
[Collection(DirectorGatesCollection.Name)]
public sealed class SmartRestartCommandLineTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Start = new(2026, 9, 20, 8, 0, 0, DateTimeKind.Utc);

    private DateTime _now = Start;

    // ================= the host's own dispatch =================

    private static async Task<DirectorCommandResult> OnAHostWithNoGatewayAsync(string verb, object? payload)
    {
        using var sessions = new SessionManager(new AgentOptions());
        using var dir = new TempDir();
        var host = new ControlApiHost(sessions, "1.0.0-test", () => Task.CompletedTask,
            directorId: Guid.NewGuid().ToString(), instancesDirectory: dir.Path);
        try
        {
            return await host.DispatchTunnelCommandAsync(new DirectorCommand
            {
                CommandId = "command-under-test",
                Verb = verb,
                PayloadJson = payload is string raw ? raw : JsonSerializer.Serialize(payload, Web),
            });
        }
        finally
        {
            await host.DisposeAsync();
        }
    }

    // Shows: a time the engine does not allow is refused BY NAME, naming what was asked for and what may be
    // asked for, and nothing is touched. Round the 7 to the nearest allowed value and this goes red.
    [Fact]
    public async Task Start_ATimeTheEngineDoesNotAllow_IsRefusedByNameAndNothingIsTouched()
    {
        var result = await OnAHostWithNoGatewayAsync(
            SmartRestartVerbs.Start, new SmartRestartStartOrder { Minutes = 7 });

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("5, 10, 15, 30, 60", result.Error);
        Assert.Contains("7 minutes was asked for", result.Error);
        Assert.Contains("Nothing has been touched", result.Error);
        Assert.Equal("command-under-test", result.CommandId);
    }

    // Shows: an order that cannot be read is refused as exactly that, not started on a guessed default.
    [Fact]
    public async Task Start_AnOrderThatCannotBeRead_IsRefusedAndNothingIsTouched()
    {
        var result = await OnAHostWithNoGatewayAsync(SmartRestartVerbs.Start, "{ this is not json");

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Contains("could not be read", result.Error);
        Assert.Contains("nothing has been touched", result.Error);
    }

    // Shows: the start asks the engine the same availability question the dialog asks, and passes its
    // refusal through UNCHANGED. A Director with no Gateway has nowhere to keep the record, so the command
    // line is told exactly what a person at the screen would be told - and no session is touched.
    [Fact]
    public async Task Start_ADirectorWithNoGateway_IsRefusedInTheEnginesOwnWords()
    {
        var result = await OnAHostWithNoGatewayAsync(
            SmartRestartVerbs.Start, new SmartRestartStartOrder { Minutes = 10, Reason = "update it" });

        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Contains("not connected to a Gateway", result.Error);
        Assert.Contains("keeps its record on the Gateway", result.Error);
    }

    // Shows: a Director on which no smart shutdown has been started SAYS SO, and names the command that
    // starts one. It never answers an empty run, which would read as a run that found nothing to do.
    [Fact]
    public async Task Progress_ADirectorThatHasStartedNone_SaysSoAndNamesTheCommand()
    {
        // A test process is not a Director: an earlier test in this process has run a real smart shutdown,
        // and the engine remembers the last one on purpose. Forgetting it is what makes THIS Director one
        // that has started none.
        DirectorSmartShutdown.ForgetLatestRun();

        var result = await OnAHostWithNoGatewayAsync(SmartRestartVerbs.Progress, null);

        Assert.True(result.Ok);
        var answer = JsonSerializer.Deserialize<SmartRestartProgressDto>(result.BodyJson!, Web)!;
        Assert.False(answer.Started);
        Assert.False(answer.Running);
        Assert.Empty(answer.Sessions);
        Assert.Contains("No smart shutdown has been started", answer.Detail);
        Assert.Contains("cc-devthrottle director smart-restart", answer.Detail);
    }

    // Shows: a history that could not be read is a REFUSAL carrying the reason, never an empty list - the
    // two look identical on a screen and call for opposite next steps.
    [Fact]
    public async Task History_ADirectorWithNoGateway_IsRefusedWithTheReasonAndNotAnEmptyList()
    {
        var result = await OnAHostWithNoGatewayAsync(SmartRestartVerbs.History, null);

        Assert.True(result.Ok);
        var answer = JsonSerializer.Deserialize<SmartRestartHistoryDto>(result.BodyJson!, Web)!;
        Assert.True(answer.Refused);
        Assert.Empty(answer.Entries);
        Assert.NotEmpty(answer.Message);
        Assert.Contains("Gateway", answer.Message);
    }

    // ================= the translation, over what the real engine raised =================

    /// <summary>One real smart shutdown on the drain rig, watched to the end.</summary>
    private async Task<(SmartShutdownSnapshot Mid, SmartShutdownSnapshot Final, SmartShutdownResult Result)>
        RealRunAsync(string directory)
    {
        var sessions = new FakeSessionControl { PollsBeforeReap = 1 };
        var lead = DrainTestRig.Seat("lead", "A lead");
        var under = DrainTestRig.Seat("worker", "A worker", reportsTo: "lead", order: 1);
        foreach (var seat in new[] { lead, under }) sessions.Live.Add(seat.SessionId!);
        var sink = new FakeWorkspaceSink { Captured = DrainTestRig.Document(new[] { lead, under }) };
        sessions.Handover(directory, "lead", "A lead", DrainTestRig.Block(restore: true, why: "Work is left."));

        var listening = new TaskCompletionSource();
        var engine = new DirectorSmartShutdown(
            () => new DirectorDrain(sessions, sink, "director-under-test", null,
                utcNow: () => _now,
                delay: (d, _) => { _now = _now.Add(d); return Task.CompletedTask; }),
            _ => listening.Task,
            () => new DirectorRestartEligibilityDto { Eligible = true, Reason = "the launcher would restart it" },
            "Test Director",
            directory,
            () => _now);

        var run = engine.Start(new SmartShutdownRequest(
            SmartShutdownPurpose.Close, TimeSpan.FromMinutes(10), "update to 2.9.0"));
        var seen = new List<SmartShutdownSnapshot>();
        run.Changed += s => seen.Add(s);
        listening.SetResult();
        var result = await run.Completion;
        return (seen[0], result.Final, result);
    }

    // Shows: every word on the wire is the ENGINE's word. Each row's label is the engine's own label for
    // that state and each phase label is its own label for that phase - so a terminal that prints them
    // verbatim cannot be wrong about what a state means. Word a state in the wire and this goes red.
    [Fact]
    public async Task Progress_OfARealRun_CarriesTheEnginesOwnWordForEveryRowAndThePhase()
    {
        using var dir = new TempDir();
        var (_, final, result) = await RealRunAsync(dir.Path);

        var answer = SmartRestartWire.Progress(final, result);

        Assert.Equal(SmartShutdownWords.PhaseLabel(final.Phase), answer.PhaseLabel);
        Assert.Equal(final.Phase.ToString(), answer.Phase);
        Assert.Equal(final.CountLabel, answer.CountLabel);
        Assert.Equal(final.Total, answer.Total);
        Assert.Equal(final.Gone, answer.Gone);
        Assert.NotEmpty(answer.Sessions);
        Assert.Equal(final.Sessions.Count, answer.Sessions.Count);
        for (var i = 0; i < final.Sessions.Count; i++)
        {
            var row = final.Sessions[i];
            Assert.Equal(row.SessionId, answer.Sessions[i].SessionId);
            Assert.Equal(row.Name, answer.Sessions[i].Name);
            Assert.Equal(row.State.ToString(), answer.Sessions[i].State);
            Assert.Equal(SmartShutdownWords.StateLabel(row.State), answer.Sessions[i].StateLabel);
            Assert.Equal(row.OwnerSessionId, answer.Sessions[i].OwnerSessionId);
        }
    }

    // Shows: a finished run is reported as finished, with the engine's own outcome and its own sentence -
    // which is the whole of "how it ended" that the command line prints.
    [Fact]
    public async Task Progress_OfARunThatEnded_CarriesTheOutcomeAndTheEnginesOwnSentence()
    {
        using var dir = new TempDir();
        var (_, final, result) = await RealRunAsync(dir.Path);

        var answer = SmartRestartWire.Progress(final, result);

        Assert.False(answer.Running);
        Assert.True(answer.Started);
        Assert.Equal(result.Outcome.ToString(), answer.Outcome);
        Assert.Equal(result.Detail, answer.Detail);
        Assert.Equal(result.WorkspaceId, answer.WorkspaceId);
    }

    // Shows: while the run is still going there is no outcome at all, and it says it is running. A reader
    // that must wait cannot be told a run has ended because one field happened to be empty.
    [Fact]
    public async Task Progress_OfARunStillGoing_SaysItIsRunningAndCarriesNoOutcome()
    {
        using var dir = new TempDir();
        var (mid, _, _) = await RealRunAsync(dir.Path);

        var answer = SmartRestartWire.Progress(mid, result: null);

        Assert.True(answer.Running);
        Assert.True(answer.Started);
        Assert.Null(answer.Outcome);
        Assert.NotEmpty(answer.Detail);
    }

    // Shows: a run that ended carrying NO result - which the engine's contract says cannot happen - is
    // reported as neither running nor outcome-bearing, with a sentence saying exactly that. Report it as
    // still running and this goes red.
    [Fact]
    public async Task Progress_OfARunThatEndedWithNoResult_IsNotReportedAsStillRunning()
    {
        using var dir = new TempDir();
        var (_, final, _) = await RealRunAsync(dir.Path);

        var answer = SmartRestartWire.Progress(final, result: null, endedWithoutResult: "it stopped saying nothing");

        Assert.False(answer.Running);
        Assert.True(answer.Started);
        Assert.Null(answer.Outcome);
        Assert.Equal("it stopped saying nothing", answer.Detail);
    }

    // Shows: THE HOST reads the run this process actually held, and reports it after it is over. This is
    // the whole of "how it ended" reaching the command line: the run is a real one the engine raised, the
    // host is a real host, and only the two ends are faked. Have the host read the RUNNING run alone and
    // this goes red, because by now there is none.
    [Fact]
    public async Task Progress_OnTheHost_ReportsTheRunThisProcessHeld_AfterItEnded()
    {
        using var dir = new TempDir();
        var (_, _, result) = await RealRunAsync(dir.Path);

        var answer = JsonSerializer.Deserialize<SmartRestartProgressDto>(
            (await OnAHostWithNoGatewayAsync(SmartRestartVerbs.Progress, null)).BodyJson!, Web)!;

        Assert.True(answer.Started);
        Assert.False(answer.Running);
        Assert.Equal(result.Outcome.ToString(), answer.Outcome);
        Assert.Equal(result.Detail, answer.Detail);
        Assert.NotEmpty(answer.Sessions);
    }

    // ================= the history, over what the real way up computed =================

    // Shows: every label the command line prints for a record is the WAY UP's own label, in its own order,
    // and a record that still owes seats carries the offer's own sentence saying how many.
    [Fact]
    public async Task History_CarriesEveryLabelTheWayUpComputedAndItsSeatsOwedSentence()
    {
        var rig = new WayUpTestRig();
        rig.Gateway
            .With(WayUpTestRig.Record("restart-older", Start.AddDays(-1),
                new[] { WayUpTestRig.AlreadyBack("seat-old", "An old seat") }))
            .With(WayUpTestRig.Record("restart-newest", Start,
                new[] { WayUpTestRig.Owed("seat-1", "A lead", mission: "Smart Director Restart", role: "Developer") }));

        var history = await rig.WayUp().ReadHistoryAsync(CancellationToken.None);
        var answer = SmartRestartWire.History(history);

        Assert.False(answer.Refused);
        Assert.Equal(history.Message, answer.Message);
        Assert.Equal(history.Entries.Count, answer.Entries.Count);
        for (var i = 0; i < history.Entries.Count; i++)
        {
            var entry = history.Entries[i];
            Assert.Equal(entry.WorkspaceId, answer.Entries[i].WorkspaceId);
            Assert.Equal(entry.WhenLabel, answer.Entries[i].WhenLabel);
            Assert.Equal(entry.KindLabel, answer.Entries[i].KindLabel);
            Assert.Equal(entry.ReasonLabel, answer.Entries[i].ReasonLabel);
            Assert.Equal(entry.OutcomeLabel, answer.Entries[i].OutcomeLabel);
            Assert.Equal(entry.Offer?.SeatsOwedLabel, answer.Entries[i].SeatsOwedLabel);
            Assert.Equal(entry.Seats.Select(s => s.Outcome), answer.Entries[i].Seats.Select(s => s.Outcome));
            Assert.Equal(entry.Seats.Select(s => s.Name), answer.Entries[i].Seats.Select(s => s.Name));
        }

        // The newest record is first, it still owes a seat and says so; the older one owes none and says
        // nothing at all rather than "0 sessions", which would read as a thing to act on.
        Assert.Equal("restart-newest", answer.Entries[0].WorkspaceId);
        Assert.NotNull(answer.Entries[0].SeatsOwedLabel);
        Assert.Null(answer.Entries[1].SeatsOwedLabel);
    }
}
