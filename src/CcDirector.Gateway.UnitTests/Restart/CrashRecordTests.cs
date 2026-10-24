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
            Id = id ?? WorkspaceCrashRecords.IdFor(journal.DirectorId, journal.Pid),
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
        var doc = _store.Get(WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid))!;
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
            var journal = new DirectorCrashJournal(DeadDirector, DeadPid, WayUpTestRig.ThisMachine, "someone", DateTimeOffset.UtcNow, dir);
            journal.Update(new[]
            {
                new DirectorCrashJournalSession { SessionId = Lead, Name = "Mission - Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead" },
                new DirectorCrashJournalSession { SessionId = Worker, Name = "Mission - Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker", ControllerSessionId = Lead },
            });
            Assert.Single(DirectorCrashJournal.DetectAndClaim(Environment.ProcessId, dir));
            var rig = new WayUpTestRig();

            // Act - what the start-up does: send the pending journals, then look for the offer.
            var sent = await new WayUpCrashRecorder(rig.Gateway).RecordAsync(DirectorCrashJournal.ListPendingRecoveries(dir), CancellationToken.None);
            var offer = await rig.WayUp(nowUtc: DateTime.UtcNow).FindOfferAsync(CancellationToken.None);

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
        var id = WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid);

        Assert.Equal(id, WorkspaceCrashRecords.IdFor(DeadDirector.ToUpperInvariant(), DeadPid));
        Assert.NotEqual(id, WorkspaceCrashRecords.IdFor(DeadDirector, DeadPid + 1));
        WorkspaceValidation.ValidateId(id);
    }

    private static DirectorCrashJournalData JournalOnDisk() => new()
    {
        DirectorId = DeadDirector,
        Pid = DeadPid,
        MachineName = WayUpTestRig.ThisMachine,
        LastUpdatedUtc = new DateTimeOffset(WayUpTestRig.Now.AddHours(-1)),
        Sessions =
        {
            new DirectorCrashJournalSession { SessionId = Lead, Name = "Mission - Lead", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-lead" },
            new DirectorCrashJournalSession { SessionId = Worker, Name = "Mission - Worker", RepoPath = "/repos/app", Agent = "ClaudeCode", ClaudeSessionId = "conv-worker", ControllerSessionId = Lead },
        },
    };
}
