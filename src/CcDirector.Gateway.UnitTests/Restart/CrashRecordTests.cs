using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi.SmartRestart;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Workspaces;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CcDirector.Gateway.UnitTests.Restart;

/// <summary>
/// Issue #4120: a Linux Director was killed by the out-of-memory killer with 17 sessions, and the next Director found
/// all 17 in its crash journal - and offered nothing, because the way up reads only the records a Director writes on
/// its way DOWN, and a killed Director has no way down. These tests drive the crash journal from the file the dead
/// Director wrote, through the Gateway's crash-record route and the real workspace store, to the way up's offer and its
/// "Reopen its saved conversation". In-memory throughout: a test server, a test database, no port and no waiting.
/// </summary>
public sealed class CrashRecordTests : IAsyncLifetime
{
    private const string LiveDirector = "director-after-the-crash";
    private const string DeadDirector = "4f2a9c1e-0000-0000-0000-000000000000";
    private const int DeadPid = 2_000_000_001;
    private static readonly DateTimeOffset DeadStartedAt = new(WayUpTestRig.Now.AddHours(-30));

    /// <summary>
    /// The moment the dead Director's journal in <paramref name="dir"/> was last written - the moment it crashed.
    /// The way up is asked at that moment rather than at the machine's "now", so whether the record is recent
    /// enough to offer is decided by the record, not by how long the run took.
    /// </summary>
    private static DateTime LastWrittenUtc(string dir) =>
        DirectorCrashJournal.ListPendingRecoveries(dir).Single().LastUpdatedUtc.UtcDateTime;
    private const string Lead = "11111111-1111-1111-1111-111111111111";
    private const string Worker = "22222222-2222-2222-2222-222222222222";

    private readonly GatewayDbTestHarness _h = new();
    private WorkspaceStore _store = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;
    private bool _credentialIsTheLiveDirectors = true;
    private string? _deviceType;

    public async Task InitializeAsync()
    {
        _store = new WorkspaceStore(_h.Open());
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Services.AddLogging();
        _app = builder.Build();
        // Stands in for the authentication gate: what kind of device the credential belongs to.
        _app.Use(async (ctx, next) =>
        {
            if (_deviceType is not null) ctx.Items[AuthMiddleware.DeviceTypeItemKey] = _deviceType;
            await next();
        });
        WorkspaceEndpoints.Map(_app, _store,
            connectedFleet: _ => (Streaming.FleetObservation.Unknown, Array.Empty<SessionDto>()),
            lookupDirector: id => id == LiveDirector
                ? new DirectorDto { DirectorId = LiveDirector, DisplayName = WayUpTestRig.ThisDirector, MachineName = WayUpTestRig.ThisMachine, Version = "2.31.0" }
                : null,
            sendRestore: (_, _, _) => Task.FromResult<DirectorCommandResult?>(null),
            callerIsDirector: (_, id) => _credentialIsTheLiveDirectors && id == LiveDirector,
            passDevReports: (_, _, _) => throw new InvalidOperationException("no dev reports in these tests"));
        await _app.StartAsync();
        _http = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    private static CrashJournalDto Journal() => new()
    {
        DirectorId = DeadDirector,
        Pid = DeadPid,
        MachineName = WayUpTestRig.ThisMachine,
        StartedAtUtc = DeadStartedAt,
        LastUpdatedUtc = new DateTimeOffset(WayUpTestRig.Now.AddHours(-10)),
        Sessions =
        {
            new CrashJournalSessionDto { SessionId = Lead, Name = "Mission - Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead", CreatedAtUtc = new DateTimeOffset(WayUpTestRig.Now.AddHours(-20)) },
            new CrashJournalSessionDto { SessionId = Worker, Name = "Mission - Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker", ControllerSessionId = Lead, CreatedAtUtc = new DateTimeOffset(WayUpTestRig.Now.AddHours(-19)) },
        },
    };

    private Task<HttpResponseMessage> PostAsync(CrashJournalDto journal, string? id = null) =>
        _http.PostAsJsonAsync("/gateway/workspaces/crash-records", new WorkspaceCrashRecordRequest
        {
            Id = id ?? WorkspaceCrashRecords.IdFor(journal),
            DirectorId = LiveDirector,
            Journal = journal,
        });

    // ================= the Gateway's route, on the real store =================

    [Fact]
    public async Task PostCrashRecord_FromTheNextDirector_KeepsEverySessionAsASeatThatDiedWithIt()
    {
        // Act
        var res = await PostAsync(Journal());

        // Assert
        Assert.Equal(HttpStatusCode.Created, res.StatusCode);
        var doc = _store.Get(WorkspaceCrashRecords.IdFor(Journal()))!;
        Assert.Equal(WorkspaceShutdownKinds.Crash, doc.ShutdownKind);
        Assert.Equal(WorkspaceOrigins.Captured, doc.Origin);
        // Stamped with the LIVE Director's name and machine, from the Gateway's registration - what the way up finds by.
        Assert.Equal(WayUpTestRig.ThisDirector, doc.DirectorName);
        Assert.Equal(WayUpTestRig.ThisMachine, doc.Machine);
        Assert.Equal(2, doc.Seats.Count);
        Assert.All(doc.Seats, s =>
        {
            Assert.Equal(WorkspaceDrainStates.DiedWithDirector, s.DrainState);
            Assert.Equal(WorkspaceRestoreDecisions.Undecided, s.Restore!.Decision);
        });
        Assert.Equal("conv-worker", doc.Seats.Single(s => s.SessionId == Worker).ClaudeSessionId);
        Assert.Equal(Lead, doc.Seats.Single(s => s.SessionId == Worker).ReportsTo);
    }

    [Fact]
    public async Task PostCrashRecord_SameJournalAgain_IsAlreadyRecordedAndWritesNothing()
    {
        // Arrange
        Assert.Equal(HttpStatusCode.Created, (await PostAsync(Journal())).StatusCode);

        // Act - the next start-up sends every pending journal again.
        var again = await PostAsync(Journal());

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        var body = JsonDocument.Parse(await again.Content.ReadAsStringAsync()).RootElement;
        Assert.True(body.GetProperty("alreadyRecorded").GetBoolean());
        Assert.Single(_store.List());
    }

    [Fact]
    public async Task PostCrashRecord_FromAPhone_IsRefused()
    {
        _deviceType = "phone";

        var res = await PostAsync(Journal());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task PostCrashRecord_CredentialIsNotThatDirectors_IsRefused()
    {
        _credentialIsTheLiveDirectors = false;

        var res = await PostAsync(Journal());

        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Empty(_store.List());
    }

    [Fact]
    public async Task PostCrashRecord_IdNotDerivedFromTheDeadProcess_IsRefused()
    {
        var res = await PostAsync(Journal(), id: "crash-made-up-1");

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(_store.List());
    }

    // ================= from the file on disk to the offer =================

    [Fact]
    public async Task CrashJournalOnDisk_NextStartUp_IsOfferedBackWithItsSavedConversations()
    {
        // Arrange - a Director that died: its journal is on disk naming a process that is not running.
        var dir = Path.Combine(Path.GetTempPath(), "cc-crash-record-" + Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new DirectorCrashJournal(DeadDirector, DeadPid, WayUpTestRig.ThisMachine, "someone", DeadStartedAt, dir);
            journal.Update(new[]
            {
                new DirectorCrashJournalSession { SessionId = Lead, Name = "Mission - Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead" },
                new DirectorCrashJournalSession { SessionId = Worker, Name = "Mission - Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker", ControllerSessionId = Lead },
            });
            Assert.Single(DirectorCrashJournal.DetectAndClaim(Environment.ProcessId, dir));
            var rig = new WayUpTestRig();

            // Act - what the start-up does: send the pending journals, then look for the offer.
            var sent = await new WayUpCrashRecorder(rig.Gateway).RecordAsync(DirectorCrashJournal.ListPendingRecoveries(dir), CancellationToken.None);
            var offer = await rig.WayUp(nowUtc: LastWrittenUtc(dir)).FindOfferAsync(CancellationToken.None);

            // Assert
            Assert.Equal(1, sent.Recorded);
            Assert.Equal(WayUpOfferState.Offered, offer.State);
            var record = offer.Record!;
            Assert.Equal(WayUpWords.HeadlineFor(WorkspaceShutdownKinds.Crash), record.Headline);
            Assert.Contains("stopped without shutting down", record.WhenLabel);
            Assert.Equal(2, record.SeatsEndedWithoutHandover);
            var rows = record.Rows.Where(r => r.Kind == WayUpRowKind.EndedWithoutHandover).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, r => Assert.True(r.Reopen!.CanReopen));
            // Nothing is brought back by itself: every seat is its own reopen, unticked.
            Assert.DoesNotContain(record.Rows, r => r.Kind == WayUpRowKind.BringBack);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReopenFromACrashRecord_StartsTheSavedConversationAndTheRecordIsNotOfferedAgain()
    {
        // Arrange
        var rig = new WayUpTestRig();
        await new WayUpCrashRecorder(rig.Gateway).RecordAsync(new[] { JournalOnDisk() }, CancellationToken.None);
        var wayUp = rig.WayUp();
        var offer = await wayUp.FindOfferAsync(CancellationToken.None);

        // Act
        var reopened = await wayUp.ReopenAsync(new WayUpReopenRequest(offer.Record!.WorkspaceId, Worker), CancellationToken.None);

        // Assert - the same conversation, told it stopped with the Director rather than that it was shut down.
        Assert.True(reopened.Started, reopened.Message);
        var started = Assert.Single(rig.Gateway.Started);
        Assert.Equal("conv-worker", started.ResumeSessionId);
        Assert.Contains("stopped without shutting down", started.PrePrompt);

        // The next start-up sends the journal again: already recorded, and the used record is not offered again.
        var again = await new WayUpCrashRecorder(rig.Gateway).RecordAsync(new[] { JournalOnDisk() }, CancellationToken.None);
        Assert.Equal(1, again.AlreadyRecorded);
        Assert.Equal(WayUpOfferState.NothingWaiting, (await rig.WayUp().FindOfferAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task CrashRecorder_GatewayRefuses_DoesNotThrowAndASmartShutdownIsStillOffered()
    {
        // Arrange - a Gateway too old for the route, holding an ordinary smart-shutdown record.
        var rig = new WayUpTestRig();
        rig.Gateway.RefuseCrashRecords = "404 - this Gateway has no crash-record route";
        rig.Gateway.With(WayUpTestRig.Record("restart-20260920-1236-devthrottle-1", WayUpTestRig.Now, new[]
        {
            new WorkspaceSeat
            {
                SessionId = Lead, Name = "Lead", Agent = "ClaudeCode", RepoPath = "/repos/app", DrainState = WorkspaceDrainStates.Drained,
                HandoverPath = "/handovers/lead.md", Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Restore },
            },
        }));

        // Act
        var sent = await new WayUpCrashRecorder(rig.Gateway).RecordAsync(new[] { JournalOnDisk() }, CancellationToken.None);
        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        // Assert
        Assert.Single(sent.Failures);
        Assert.Equal(WayUpOfferState.Offered, offer.State);
        Assert.Equal("restart-20260920-1236-devthrottle-1", offer.Record!.WorkspaceId);
    }

    [Fact]
    public async Task CrashRecorder_JournalWithNoSession_SendsNothing()
    {
        var rig = new WayUpTestRig();
        var empty = JournalOnDisk();
        empty.Sessions.Clear();

        var sent = await new WayUpCrashRecorder(rig.Gateway).RecordAsync(new[] { empty }, CancellationToken.None);

        Assert.Empty(rig.Gateway.CrashJournalsSent);
        Assert.Equal(0, sent.Recorded);
    }

    [Fact]
    public void IdFor_SameDeadProcess_SameId_AndAValidWorkspaceId()
    {
        var id = WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid, DeadStartedAt);

        Assert.Equal(id, WorkspaceCrashRecords.IdFor(DeadDirector.ToUpperInvariant(), DeadPid, DeadStartedAt));
        Assert.NotEqual(id, WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid + 1, DeadStartedAt));
        // Review finding 2: the same Director id and a recycled process id is a different process.
        Assert.NotEqual(id, WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid, DeadStartedAt.AddHours(5)));
        WorkspaceValidation.ValidateId(id);
    }

    // Review finding 2: an instance keeps its Director id across restarts and process ids are reused, so a second crash
    // that drew a past crash's process id must be recorded and offered, not answered "already recorded".
    [Fact]
    public async Task SecondCrashWithARecycledProcessId_IsRecordedAndOffered()
    {
        // Arrange - the first crash is recorded and its one seat reopened, so its record is used.
        var rig = new WayUpTestRig();
        var recorder = new WayUpCrashRecorder(rig.Gateway);
        var first = JournalOnDisk();
        await recorder.RecordAsync(new[] { first }, CancellationToken.None);
        var firstOffer = await rig.WayUp().FindOfferAsync(CancellationToken.None);
        foreach (var row in firstOffer.Record!.Rows) await rig.WayUp().ReopenAsync(new WayUpReopenRequest(firstOffer.Record.WorkspaceId, row.RowId), CancellationToken.None);
        var second = JournalOnDisk(startedAt: first.StartedAtUtc.AddHours(5), lastUpdated: first.LastUpdatedUtc.AddHours(6));
        second.Sessions.RemoveAt(0);

        // Act - the same instance crashes again, its process having drawn the same process id.
        var sent = await recorder.RecordAsync(new[] { second }, CancellationToken.None);
        var offer = await rig.WayUp(nowUtc: WayUpTestRig.Now.AddHours(6)).FindOfferAsync(CancellationToken.None);

        // Assert
        Assert.Equal(1, sent.Recorded);
        Assert.Equal(WayUpOfferState.Offered, offer.State);
        Assert.Equal(WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid, second.StartedAtUtc), offer.Record!.WorkspaceId);
    }

    // Review rounds 1 and 2: the journal stays listed in the Interrupted sessions list - the door away from the machine -
    // and a seat reopened from the crash record is taken out of it, so that list stops offering a session already back.
    [Fact]
    public async Task ReopenFromACrashRecord_TakesTheSeatOutOfTheCrashJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-crash-record-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Arrange - recorded, and still listed by the Interrupted sessions list.
            var journal = new DirectorCrashJournal(DeadDirector, DeadPid, WayUpTestRig.ThisMachine, "someone", DeadStartedAt, dir);
            journal.Update(new[]
            {
                new DirectorCrashJournalSession { SessionId = Lead, Name = "Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead" },
                new DirectorCrashJournalSession { SessionId = Worker, Name = "Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker" },
            });
            Assert.Single(DirectorCrashJournal.DetectAndClaim(Environment.ProcessId, dir));
            var rig = new WayUpTestRig();
            await new WayUpCrashRecorder(rig.Gateway).RecordAsync(DirectorCrashJournal.ListPendingRecoveries(dir), CancellationToken.None);
            Assert.Equal(2, DirectorCrashJournal.ListPendingRecoveries(dir).Single().Sessions.Count);
            var crashedAt = LastWrittenUtc(dir);
            var offer = await rig.WayUp(nowUtc: crashedAt).FindOfferAsync(CancellationToken.None);

            // Act - reopen the worker from the record, and hand the engine's report to the journal, as the Director does.
            var reopened = await rig.WayUp(nowUtc: crashedAt).ReopenAsync(new WayUpReopenRequest(offer.Record!.WorkspaceId, Worker), CancellationToken.None);
            var (recordId, seatId) = Assert.Single(rig.CrashSeatsReopened);
            var removed = CrashJournalRows.RemoveReopened(recordId, seatId, dir);

            // Assert - the Interrupted sessions list now offers only the lead.
            Assert.True(reopened.Started, reopened.Message);
            Assert.Equal(offer.Record.WorkspaceId, recordId);
            Assert.True(removed);
            var left = Assert.Single(DirectorCrashJournal.ListPendingRecoveries(dir)).Sessions;
            Assert.Equal(Lead, Assert.Single(left).SessionId);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task ReopenFromASmartShutdownRecord_DoesNotTouchACrashJournal()
    {
        var rig = new WayUpTestRig();
        rig.Gateway.With(WayUpTestRig.Record("restart-20260920-1236-devthrottle-1", WayUpTestRig.Now, new[]
        {
            new WorkspaceSeat
            {
                SessionId = Lead, Name = "Lead", Agent = "ClaudeCode", RepoPath = "/repos/app", ClaudeSessionId = "conv-lead",
                DrainState = WorkspaceDrainStates.EndedAtLimit, Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
            },
        }));

        var reopened = await rig.WayUp().ReopenAsync(new WayUpReopenRequest("restart-20260920-1236-devthrottle-1", Lead), CancellationToken.None);

        Assert.True(reopened.Started, reopened.Message);
        Assert.Empty(rig.CrashSeatsReopened);
    }

    // Review finding 3: a factory seat's agent finds its saved conversation by the folder it runs in, so the reopen of a
    // crashed seat must start it in the same kit folder, on the same mission.
    [Fact]
    public async Task ReopenOfACrashedFactorySeat_StartsInItsOwnKitFolderOnItsMission()
    {
        // Arrange
        var rig = new WayUpTestRig();
        var missionId = Guid.NewGuid();
        var journal = JournalOnDisk();
        var seat = journal.Sessions.Single(s => s.SessionId == Worker);
        seat.Factory = "acme";
        seat.FactorySeat = "writer";
        seat.FactoryKitDirectory = "/kits/acme/writer-22222222";
        seat.MissionId = missionId.ToString();
        seat.MissionName = "Mission";
        await new WayUpCrashRecorder(rig.Gateway).RecordAsync(new[] { journal }, CancellationToken.None);
        var offer = await rig.WayUp().FindOfferAsync(CancellationToken.None);

        // Act
        var reopened = await rig.WayUp().ReopenAsync(new WayUpReopenRequest(offer.Record!.WorkspaceId, Worker), CancellationToken.None);

        // Assert
        Assert.True(reopened.Started, reopened.Message);
        var started = Assert.Single(rig.Gateway.Started);
        Assert.Equal("/kits/acme/writer-22222222", started.FactoryKitDirectory);
        Assert.Equal(missionId, started.MissionId);
        Assert.Equal("conv-worker", started.ResumeSessionId);
    }

    private static DirectorCrashJournalData JournalOnDisk(DateTimeOffset? startedAt = null, DateTimeOffset? lastUpdated = null) => new()
    {
        DirectorId = DeadDirector,
        Pid = DeadPid,
        MachineName = WayUpTestRig.ThisMachine,
        StartedAtUtc = startedAt ?? DeadStartedAt,
        LastUpdatedUtc = lastUpdated ?? new DateTimeOffset(WayUpTestRig.Now.AddHours(-1)),
        Sessions =
        {
            new DirectorCrashJournalSession { SessionId = Lead, Name = "Mission - Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead" },
            new DirectorCrashJournalSession { SessionId = Worker, Name = "Mission - Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker", ControllerSessionId = Lead },
        },
    };
}
