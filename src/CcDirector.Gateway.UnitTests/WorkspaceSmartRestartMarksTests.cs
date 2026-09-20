using System.Text.Json;
using System.Text.Json.Serialization;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Workspaces;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The three marks the smart shutdown writes onto a workspace record (mission "Smart Director Restart",
/// phase 1): a seat ended at the limit, how the record came about, and that it was cancelled and when.
///
/// Every refusal and every acceptance here goes through the REAL store over an isolated on-disk SQLite
/// database - <see cref="WorkspaceStore.Save"/>, which is what the Gateway's PUT calls - and not through
/// the validator on its own, so the store's own handling of a captured record (it puts back everything the
/// capture owns before it validates) is part of what is being tested.
/// </summary>
public sealed class WorkspaceSmartRestartMarksTests : IDisposable
{
    private const string SessionId = "5ff9ab8b-07d3-4b23-953b-6c853760b56c";

    private readonly GatewayDbTestHarness _h = new();

    private static readonly DateTime Now = new(2026, 9, 19, 21, 50, 0, DateTimeKind.Utc);
    private static readonly DateTime Later = new(2026, 9, 19, 22, 1, 0, DateTimeKind.Utc);

    public void Dispose() => _h.Dispose();

    /// <summary>A captured record, stored the way a capture stores it, with the agent's conversation id on
    /// the seat - which is where a capture puts it.</summary>
    private WorkspaceStore CapturedStore()
    {
        var store = new WorkspaceStore(_h.Open());
        store.Create(Captured(), Now);
        return store;
    }

    private static WorkspaceDocument Captured()
        => new()
        {
            Id = "smart-restart-1",
            Name = "Smart restart, 19 September",
            Origin = WorkspaceOrigins.Captured,
            Machine = "SOREN_NORTH",
            DirectorId = "6d4523e2-ed03-4ae6-ac1c-71d00a37bad1",
            DirectorName = "DevThrottle_1",
            Seats =
            {
                new WorkspaceSeat
                {
                    SessionId = SessionId,
                    Name = "Linux Support - Architect",
                    Agent = "ClaudeCode",
                    RepoPath = @"D:\ReposFred\devthrottle_internal",
                    ClaudeSessionId = "c0ffee00-1111-4222-8333-444455556666",
                },
            },
        };

    // ================= the drain state =================

    [Fact]
    public void Save_ASeatEndedAtTheLimit_IsStoredWithItsConversationIdStillOnTheSeat()
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = WorkspaceShutdownKinds.SmartShutdown;
        doc.Seats[0].DrainState = WorkspaceDrainStates.EndedAtLimit;
        doc.Seats[0].ClosedAtUtc = Later;

        // The writer leaves the conversation id OFF its copy on purpose: the store puts back what the
        // capture read, so the id the way up needs is there whatever the Director sent.
        doc.Seats[0].ClaudeSessionId = null;

        store.Save(doc, Later);

        var seat = store.Get("smart-restart-1")!.Seats.Single();
        Assert.Equal("ended-at-limit", seat.DrainState);
        Assert.Equal("c0ffee00-1111-4222-8333-444455556666", seat.ClaudeSessionId);
        Assert.Equal(Later, seat.ClosedAtUtc);
    }

    [Fact]
    public void Save_ADrainStateNobodyKnows_IsStillRefusedAndTheMessageNamesTheNewOne()
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.Seats[0].DrainState = "ended-at-the-limit";

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Later));

        Assert.Contains("drainState must be one of", ex.Message);
        Assert.Contains("ended-at-limit", ex.Message);
    }

    // ================= how the record came about =================

    [Theory]
    [InlineData("smart-shutdown")]
    [InlineData("ignore-all")]
    public void Save_AKnownShutdownKind_IsStoredAndReadBack_AndTheRecordIsStillACapture(string kind)
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = kind;

        store.Save(doc, Later);

        var got = store.Get("smart-restart-1")!;
        Assert.Equal(kind, got.ShutdownKind);

        // The reason the mark is a field of its own: the store branches on this word, and a record of a
        // smart shutdown has to keep every protection a capture has.
        Assert.Equal(WorkspaceOrigins.Captured, got.Origin);
        Assert.Equal(new[] { "authored", "captured" }, WorkspaceOrigins.All);
    }

    [Fact]
    public void ShutdownKinds_AreExactlyTheTwoTheWayUpSearchesBy()
    {
        Assert.Equal(new[] { "smart-shutdown", "ignore-all" }, WorkspaceShutdownKinds.All);
    }

    [Theory]
    [InlineData("smart shutdown")]
    [InlineData("SmartShutdown")]
    [InlineData("captured")]
    [InlineData("")]
    public void Save_AShutdownKindNobodyKnows_IsRefusedNamingTheKnownOnes(string kind)
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = kind;

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Later));

        Assert.Contains("shutdownKind must be one of: smart-shutdown, ignore-all", ex.Message);
        Assert.Null(store.Get("smart-restart-1")!.ShutdownKind);
    }

    [Fact]
    public void Save_ARecordWithNoShutdownKind_IsStoredAsBefore()
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.Seats[0].DrainState = WorkspaceDrainStates.Drained;
        doc.Seats[0].HandoverPath = @"C:\handovers\arch.md";

        store.Save(doc, Later);

        var got = store.Get("smart-restart-1")!;
        Assert.Null(got.ShutdownKind);
        Assert.Null(got.CancelledAtUtc);
    }

    // ================= cancelled, and when =================

    [Fact]
    public void Save_ACancelledSmartShutdown_KeepsWhenItWasCancelled()
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = WorkspaceShutdownKinds.SmartShutdown;
        doc.CancelledAtUtc = Later;

        store.Save(doc, Later);

        Assert.Equal(Later, store.Get("smart-restart-1")!.CancelledAtUtc);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("ignore-all")]
    public void Save_CancelledOnARecordThatIsNotASmartShutdown_IsRefused(string? kind)
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = kind;
        doc.CancelledAtUtc = Later;

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Later));

        Assert.Contains("cancelledAtUtc", ex.Message);
        Assert.Contains("smart-shutdown", ex.Message);
    }

    // ================= an authored workspace says nothing about a run =================

    [Fact]
    public void Save_AnAuthoredWorkspaceClaimingAShutdown_IsRefusedNamingBothFields()
    {
        var store = new WorkspaceStore(_h.Open());
        var doc = new WorkspaceDocument
        {
            Id = "morning-fleet",
            Name = "Morning fleet",
            Origin = WorkspaceOrigins.Authored,
            ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
            CancelledAtUtc = Later,
            Seats = { new WorkspaceSeat { Name = "Architect", Agent = "ClaudeCode", RepoPath = @"D:\repo" } },
        };

        var ex = Assert.Throws<WorkspaceValidationException>(() => store.Save(doc, Now));

        Assert.Contains("An authored workspace is not the record of a run", ex.Message);
        Assert.Contains("shutdownKind", ex.Message);
        Assert.Contains("cancelledAtUtc", ex.Message);
    }

    // ================= a round trip through an older and a newer reader =================

    /// <summary>
    /// A build from BEFORE the marks, reduced to what matters for the question: it knows the id, the seats
    /// and a seat's drain state as plain text, and it carries the same "keep what I do not know" bag the
    /// real document has carried since the first version. It is a stand-in - the older type itself no
    /// longer exists in this tree - so what it shows is the mechanism the real older build relies on, not
    /// the older build.
    /// </summary>
    private sealed class DocumentAsAnOlderBuildReadsIt
    {
        public string Id { get; set; } = "";
        public List<SeatAsAnOlderBuildReadsIt> Seats { get; set; } = new();
        [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    private sealed class SeatAsAnOlderBuildReadsIt
    {
        public string? SessionId { get; set; }
        public string? DrainState { get; set; }
        [JsonExtensionData] public Dictionary<string, JsonElement>? Unknown { get; set; }
    }

    [Fact]
    public void RoundTrip_ThroughAReaderFromBeforeTheMarks_LosesNoneOfThem()
    {
        var store = CapturedStore();
        var doc = Captured();
        doc.ShutdownKind = WorkspaceShutdownKinds.SmartShutdown;
        doc.CancelledAtUtc = Later;
        doc.Seats[0].DrainState = WorkspaceDrainStates.EndedAtLimit;
        var written = JsonSerializer.Serialize(store.Save(doc, Later), WorkspaceStore.DocumentJsonOptions);

        // The older reader has no field for either document mark, so both land in its unknown bag...
        var older = JsonSerializer.Deserialize<DocumentAsAnOlderBuildReadsIt>(written, WorkspaceStore.DocumentJsonOptions)!;
        Assert.Equal("smart-shutdown", older.Unknown!["shutdownKind"].GetString());
        Assert.True(older.Unknown.ContainsKey("cancelledAtUtc"));
        // ...and the drain state is a string to it, so the new value is simply text it does not recognise.
        Assert.Equal("ended-at-limit", older.Seats.Single().DrainState);

        // ...and it writes them back out where it found them, so this build reads them again, typed.
        var rewritten = JsonSerializer.Serialize(older, WorkspaceStore.DocumentJsonOptions);
        var again = JsonSerializer.Deserialize<WorkspaceDocument>(rewritten, WorkspaceStore.DocumentJsonOptions)!;

        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, again.ShutdownKind);
        Assert.Equal(Later, again.CancelledAtUtc);
        Assert.Equal(WorkspaceDrainStates.EndedAtLimit, again.Seats.Single().DrainState);
        Assert.False(again.Unknown?.ContainsKey("shutdownKind") ?? false);

        // And what came back is still a record this store accepts.
        store.Save(again, Later);
        Assert.Equal(Later, store.Get("smart-restart-1")!.CancelledAtUtc);
    }

    [Fact]
    public void RoundTrip_ARecordWrittenBeforeTheMarksExisted_ReadsWithNoneOfThemAndIsAccepted()
    {
        var store = CapturedStore();

        // Exactly the JSON an older Director sends: no shutdownKind, no cancelledAtUtc.
        var fromAnOlderDirector = """
            {
              "schemaVersion": 1,
              "id": "smart-restart-1",
              "name": "Smart restart, 19 September",
              "origin": "captured",
              "seats": [ {
                "sessionId": "5ff9ab8b-07d3-4b23-953b-6c853760b56c",
                "name": "Linux Support - Architect", "agent": "ClaudeCode",
                "repoPath": "D:\\ReposFred\\devthrottle_internal",
                "drainState": "unreachable"
              } ],
              "ownerQuestions": [], "restoreAfterRestart": []
            }
            """;
        var doc = JsonSerializer.Deserialize<WorkspaceDocument>(fromAnOlderDirector, WorkspaceStore.DocumentJsonOptions)!;

        store.Save(doc, Later);

        var got = store.Get("smart-restart-1")!;
        Assert.Null(got.ShutdownKind);
        Assert.Null(got.CancelledAtUtc);
        Assert.Equal(WorkspaceDrainStates.Unreachable, got.Seats.Single().DrainState);
    }

    [Fact]
    public void RoundTrip_AMarkFromANewerBuildStillSurvivesBesideTheNewOnes()
    {
        var store = CapturedStore();
        var withAFieldFromTheFuture = """
            {
              "schemaVersion": 2,
              "id": "smart-restart-1",
              "name": "Smart restart, 19 September",
              "origin": "captured",
              "shutdownKind": "smart-shutdown",
              "cancelledAtUtc": "2026-09-19T22:01:00Z",
              "broughtBackAfterCancel": [ "5ff9ab8b-07d3-4b23-953b-6c853760b56c" ],
              "seats": [ {
                "sessionId": "5ff9ab8b-07d3-4b23-953b-6c853760b56c",
                "name": "Linux Support - Architect", "agent": "ClaudeCode",
                "repoPath": "D:\\ReposFred\\devthrottle_internal"
              } ],
              "ownerQuestions": [], "restoreAfterRestart": []
            }
            """;
        var doc = JsonSerializer.Deserialize<WorkspaceDocument>(withAFieldFromTheFuture, WorkspaceStore.DocumentJsonOptions)!;

        store.Save(doc, Later);

        var got = store.Get("smart-restart-1")!;
        Assert.Equal(WorkspaceShutdownKinds.SmartShutdown, got.ShutdownKind);
        Assert.Equal(Later, got.CancelledAtUtc);
        Assert.Equal(SessionId, got.Unknown!["broughtBackAfterCancel"][0].GetString());
    }
}
