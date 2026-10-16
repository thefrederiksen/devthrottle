using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeDisplay;

/// <summary>
/// Review finding 1 on pull request #4086: stamping a roster costs ONE receipt read for all its API-mode rows, never
/// one per row, and that read (<see cref="ApiModeVerdictReceipts"/>) answers every session it was asked for, grouped,
/// oldest prompt first, in the asking account only. The receipts go in through the REAL ingest and store over the
/// harness's SQLite file.
/// </summary>
public sealed class ApiModeVerdictReceiptsTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");
    private const string OtherSid = "11111111-2222-4333-8444-555555555555";
    private const string QuietSid = "99999999-8888-4777-8666-555555555555";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private readonly GatewayDatabase _db;
    private readonly ApiModeReceiptStore _receipts;
    private readonly ApiModeEventBuffer _buffer = new();

    public ApiModeVerdictReceiptsTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        _db = _harness.Open(_ambient);
        _receipts = new ApiModeReceiptStore(_db, () => T0.UtcDateTime);
    }

    public void Dispose() => _harness.Dispose();

    private void Push(TenantId tenant, params SessionEvent[] events)
        => Assert.NotNull(new ApiModeEventIngest(_buffer, _receipts, _boundary).Accept(tenant, Director, A(events)));

    private static SessionDto ApiRow(string sid) => new() { SessionId = sid, ConnectionMode = ConnectionModes.Api, WheelHolder = WheelHolders.Protocol };

    // ----- the read itself -----

    [Fact]
    public void Read_SeveralSessions_AnswersEachOneGroupedAndOldestPromptFirst()
    {
        // Arrange: two sessions with receipts, and a third with none.
        Push(Alice, DeliveredThenFailedMidTurn());
        Push(Alice, Queued(7, "p2", PromptQueuedReason.TurnRunning), Queued(8, "p3", PromptQueuedReason.UserHasTheWheel));
        Push(Alice, Queued(1, "q1", PromptQueuedReason.AgentStarting, sid: OtherSid));

        // Act
        var read = new ApiModeVerdictReceipts(_db).Read(Alice, new[] { Sid, OtherSid, QuietSid });

        // Assert
        Assert.Equal(new[] { "p1", "p2", "p3" }, read[Sid].Select(r => r.PromptId));
        var p1 = read[Sid][0];
        Assert.Equal(ApiModeReceiptState.Delivered, p1.State);
        Assert.Equal("failed", p1.TurnOutcome);
        Assert.Equal("Pi exited during the turn (exit code 137)", p1.TurnOutcomeReason);
        Assert.NotNull(p1.TurnId);
        Assert.Equal("userHasTheWheel", read[Sid][2].QueueReason);
        Assert.Equal("q1", Assert.Single(read[OtherSid]).PromptId);
        Assert.Empty(read[QuietSid]);
    }

    [Fact]
    public void Read_AnotherAccountsSessionWithTheSameId_AnswersNothingOfIt()
    {
        // Arrange
        Push(Alice, DeliveredThenDone());

        // Act
        var bobs = new ApiModeVerdictReceipts(_db).Read(Bob, new[] { Sid });

        // Assert
        Assert.Empty(bobs[Sid]);
    }

    [Fact]
    public void Read_NoSessionIds_AnswersEmptyWithoutAQuery()
    {
        // Act
        var read = new ApiModeVerdictReceipts(_db).Read(Alice, Array.Empty<string>());

        // Assert
        Assert.Empty(read);
    }

    [Fact]
    public void Read_ThroughTheBatchReader_FoldsTheSameVerdictAsTheStoresOwnRead()
    {
        // Arrange
        Push(Alice, DeliveredThenCancelled());
        Push(Alice, Queued(9, "p2", PromptQueuedReason.TurnRunning));
        IReadOnlyList<ApiModeReceiptDto> stored;
        using (_boundary.EnterScope(Alice))
            stored = _receipts.Read(Sid);
        var fromTheStore = stored.Select(r => new ApiModeVerdictReceipt(r.PromptId, r.State, r.QueueReason, r.TurnId, r.TurnOutcome, r.TurnOutcomeReason)).ToList();

        // Act
        var fromTheBatch = new ApiModeVerdictReceipts(_db).Read(Alice, new[] { Sid })[Sid];

        // Assert
        Assert.Equal(fromTheStore, fromTheBatch);
    }

    // ----- the stamp reads once per roster -----

    [Fact]
    public void Stamp_ARosterOfManyApiModeRows_ReadsTheReceiptsOnceForAllOfThem()
    {
        // Arrange: five API-mode rows and two terminal-mode rows; a reader that counts.
        Push(Alice, DeliveredThenDone());
        var reads = new List<IReadOnlyCollection<string>>();
        var batch = new ApiModeVerdictReceipts(_db);
        var facts = new ApiModeDisplayFacts(_buffer, (t, ids) => { reads.Add(ids.ToList()); return batch.Read(t, ids); });
        var apiSids = new[] { Sid, OtherSid, QuietSid, "a0000000-0000-4000-8000-000000000001", "a0000000-0000-4000-8000-000000000002" };
        var rows = apiSids.Select(ApiRow)
            .Append(new SessionDto { SessionId = "t0000000-0000-4000-8000-000000000001", ConnectionMode = ConnectionModes.Terminal })
            .Append(new SessionDto { SessionId = "t0000000-0000-4000-8000-000000000002" })
            .ToList();

        // Act
        ApiModeRowStamp.Stamp(rows, facts, Alice);

        // Assert: one read, for exactly the API-mode rows, and every API-mode row has its verdict.
        var read = Assert.Single(reads);
        Assert.Equal(apiSids.OrderBy(s => s, StringComparer.Ordinal), read.OrderBy(s => s, StringComparer.Ordinal));
        Assert.All(rows.Take(5), r => Assert.NotNull(r.ApiMode));
        Assert.All(rows.Skip(5), r => Assert.Null(r.ApiMode));
        Assert.Equal(1, rows[0].ApiMode!.TurnNumber);
    }

    [Fact]
    public void Stamp_ARosterWithNoApiModeRow_ReadsNothing()
    {
        // Arrange
        var reads = 0;
        var facts = new ApiModeDisplayFacts(_buffer, (_, _) => { reads++; throw new InvalidOperationException("no read was expected"); });
        var rows = new[] { new SessionDto { SessionId = Sid, ConnectionMode = ConnectionModes.Terminal } };

        // Act
        ApiModeRowStamp.Stamp(rows, facts, Alice);

        // Assert
        Assert.Equal(0, reads);
        Assert.Null(rows[0].ApiMode);
    }
}
