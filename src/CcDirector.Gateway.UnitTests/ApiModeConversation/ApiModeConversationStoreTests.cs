using CcDirector.Core.Tenancy;
using CcDirector.Gateway.ApiModeConversation;
using CcDirector.Gateway.ApiModeDisplay;
using CcDirector.Gateway.ApiModeEvents;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Receipts;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.ApiModeEvents.ApiModeEventTestData;

namespace CcDirector.Gateway.Tests.ApiModeConversation;

/// <summary>
/// The durable conversation of an API-mode session (<see cref="ApiModeConversationStore"/>, mission #4070 phase 2
/// follow-on), written the way production writes it: through the REAL ingest, with the REAL receipt store and buffer,
/// over the harness database, inside a REAL hosted tenant boundary (two accounts over one database). A Gateway restart is
/// a new buffer, new stores and a new ingest over the same database file. The clock is injected; nothing sleeps.
/// </summary>
public sealed class ApiModeConversationStoreTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private static readonly TenantId Bob = new("bbbbbbbb-0000-4000-8000-00000000000b");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly AsyncLocalTenantContext _ambient = new();
    private readonly HostedTenantBoundary _boundary;
    private DateTime _now = T0.UtcDateTime;
    private ApiModeConversationStore _conversation = null!;
    private ApiModeEventIngest _ingest = null!;

    public ApiModeConversationStoreTests()
    {
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _boundary = new HostedTenantBoundary(_ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        StartGateway();
    }

    public void Dispose() => _harness.Dispose();

    /// <summary>A Gateway process: an empty buffer, and stores over the one database file.</summary>
    private void StartGateway()
    {
        _conversation = new ApiModeConversationStore(_harness.Open(_ambient), () => _now);
        _ingest = new ApiModeEventIngest(new ApiModeEventBuffer(), new ApiModeReceiptStore(_harness.Open(_ambient), () => _now), _conversation, _boundary);
    }

    private void Push(ApiModeEventBatch batch, TenantId? tenant = null) => Assert.NotNull(_ingest.Accept(tenant ?? Alice, Director, batch));

    private IReadOnlyList<ApiModeStoredConversationItem> Stored(TenantId? tenant = null)
    {
        using (_boundary.EnterScope(tenant ?? Alice))
            return _conversation.Read(Sid);
    }

    private static ToolCallStarted ToolStart(long seq, string id, string name = "bash", string input = "ls -la")
        => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = id, ToolName = name, Kind = ToolCallKind.Execute, Input = input };

    private static ToolCallFinished ToolEnd(long seq, string id)
        => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = "t1", ToolCallId = id, Outcome = ToolCallOutcome.Succeeded, Output = "total 0", DurationMilliseconds = 12 };

    private static MessageText Final(long seq, string messageId, string text)
        => new() { SessionId = Sid, Sequence = seq, At = T0, TurnId = "t1", MessageId = messageId, Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Final, Text = text };

    // ----- what is kept, and where -----

    [Fact]
    public void Record_DeliveredThenDone_KeepsTheFinalFormsInTheOrderTheyArrived()
    {
        // Act
        Push(A(DeliveredThenDone()));

        // Assert: the connection line, the reply's final under the live reply item's id, the turn line. The receipt is
        // the receipt store's, and the deltas are never stored.
        Assert.Equal(new[]
        {
            ("connection:gen-a:1", ApiModeLiveItemKinds.Connection, "Agent connected (gpt-5.6-sol)"),
            ("reply:m1", ApiModeLiveItemKinds.ReplyText, "PONG"),
            ("turn:t1", ApiModeLiveItemKinds.TurnEnded, "Turn finished"),
        }, Stored().Select(s => (s.Item.ItemId, s.Item.Kind, s.Item.Text)));
        Assert.All(Stored(), s => Assert.True(s.HasFinal));
        Assert.All(Stored(), s => Assert.Equal(ApiModeLiveItemModes.Replace, s.Item.Mode));
    }

    [Fact]
    public void Record_AReplysFirstDelta_HoldsItsPlaceUntilTheFinalFillsIt()
    {
        // Arrange: the reply starts, then a tool starts while it streams.
        Push(A(Started(1, "t1"), Text(2, "t1", "Lo"), ToolStart(3, "c1"), Text(4, "t1", "oking")));

        // Act
        var before = Stored();
        Push(A(Text(5, "t1", "Looking", MessageTextKind.Final)));

        // Assert: the reply keeps the place its first delta took, before the tool, as a live reader saw it.
        Assert.Equal(new[] { ("reply:m1", false), ("tool:c1", true) }, before.Select(s => (s.Item.ItemId, s.HasFinal)));
        Assert.Equal(new[] { ("reply:m1", "Looking"), ("tool:c1", "bash") }, Stored().Select(s => (s.Item.ItemId, s.Item.Text)));
    }

    [Fact]
    public void Record_ALaterFinalWithTheSameItemId_ReplacesItInPlaceAndKeepsItsPlace()
    {
        // Arrange
        Push(A(Started(1, "t1"), ToolStart(2, "c1"), Final(3, "m1", "Listing the files")));

        // Act
        Push(A(ToolEnd(4, "c1")));

        // Assert: still first, now done, with the title and input its start gave it, under the finish's sequence.
        var stored = Stored();
        Assert.Equal(new[] { "tool:c1", "reply:m1" }, stored.Select(s => s.Item.ItemId));
        Assert.Equal(("bash", "done", "ls -la", "green", 4L), (stored[0].Item.Text, stored[0].Item.Status, stored[0].Item.Detail, stored[0].Item.Tone, stored[0].Item.Sequence));
    }

    [Fact]
    public void Record_TheSameBatchSentAgain_ChangesNothing()
    {
        // Arrange
        Push(A(DeliveredThenDone()));
        var first = Stored();

        // Act: a Director re-sends a batch it was not sure arrived; a new Gateway sees it fresh in its buffer.
        StartGateway();
        Push(A(DeliveredThenDone()));

        // Assert
        Assert.Equal(first.Select(s => (s.Item.ItemId, s.Item.Text, s.Item.Sequence)), Stored().Select(s => (s.Item.ItemId, s.Item.Text, s.Item.Sequence)));
    }

    [Fact]
    public void Record_ThoughtsReceiptsAndATurnStarting_AreNeverStored()
    {
        // Act
        Push(A(DeliveredThenCancelled()));

        // Assert: the thought is hidden by the ruling, so it is not kept either.
        Assert.Equal(new[] { "connection:gen-a:1", "turn:t1" }, Stored().Select(s => s.Item.ItemId));
    }

    [Fact]
    public void Record_ConnectionLinesOfTwoGenerations_AreTwoItems()
    {
        // Act: the Director restarts; the new generation's sequence starts again at 1.
        Push(A(Ready(1)));
        Push(B(Ready(1)));

        // Assert
        Assert.Equal(new[] { "connection:gen-a:1", "connection:gen-b:1" }, Stored().Select(s => s.Item.ItemId));
    }

    [Fact]
    public void Record_AToolFinishAfterAGatewayRestart_KeepsTheTitleItsStartGave()
    {
        // Arrange: the tool starts; the Gateway restarts, so no buffer and no ruling remembers the start.
        Push(A(Started(1, "t1"), ToolStart(2, "c1", "read", "README.md")));
        StartGateway();

        // Act
        Push(A(ToolEnd(3, "c1")));

        // Assert
        var card = Assert.Single(Stored()).Item;
        Assert.Equal(("read", "done", "README.md"), (card.Text, card.Status, card.Detail));
    }

    [Fact]
    public void Accept_AMessageIdLongerThanTheConversationKeeps_IsRefusedWholeAndStoresNothing()
    {
        // Arrange
        var tooLong = new string('m', ApiModeConversationStore.MaxSourceIdLength + 1);
        var batch = A(Ready(1), new MessageText { SessionId = Sid, Sequence = 2, At = T0, TurnId = "t1", MessageId = tooLong, Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Final, Text = "hi" });

        // Act + Assert
        var refused = Assert.Throws<ArgumentException>(() => _ingest.Accept(Alice, Director, batch));
        Assert.Contains("messageId", refused.Message, StringComparison.Ordinal);
        Assert.Empty(Stored());
    }

    // ----- the bound -----

    [Fact]
    public void Record_OverTheItemBound_DropsTheOldestAndMarksTheFirstKept()
    {
        // Arrange: one item more than the bound, each its own reply.
        var finals = Enumerable.Range(1, ApiModeConversationStore.MaxItemsPerSession + 1)
            .Select(i => (SessionEvent)Final(i, "m" + i, "reply " + i)).ToArray();

        // Act
        foreach (var chunk in finals.Chunk(ApiModeEventBatch.MaxEvents))
            Push(A(chunk));

        // Assert: the first reply went; the second is now first and says older items were let go.
        var stored = Stored();
        Assert.Equal(ApiModeConversationStore.MaxItemsPerSession, stored.Count);
        Assert.Equal(("reply:m2", true), (stored[0].Item.ItemId, stored[0].EarlierDropped));
        Assert.All(stored.Skip(1), s => Assert.False(s.EarlierDropped));
        Assert.Equal($"reply:m{ApiModeConversationStore.MaxItemsPerSession + 1}", stored[^1].Item.ItemId);
    }

    [Fact]
    public void Record_OverTheCharacterBound_DropsTheOldestAndKeepsTheNewest()
    {
        // Arrange: three replies of 700,000 characters are 2,100,000, over the 2,000,000 kept per session.
        var big = new string('w', 700_000);

        // Act
        Push(A(Final(1, "m1", big)));
        Push(A(Final(2, "m2", big)));
        Push(A(Final(3, "m3", big)));

        // Assert
        var stored = Stored();
        Assert.Equal(new[] { "reply:m2", "reply:m3" }, stored.Select(s => s.Item.ItemId));
        Assert.True(stored[0].EarlierDropped);
    }

    [Fact]
    public void Record_OneItemLargerThanTheWholeBound_IsKeptAlone()
    {
        // Arrange: a tool's input can be large; the newest item is never let go, or the opening would hold nothing.
        Push(A(Final(1, "m1", "short")));

        // Act
        Push(A(Started(2, "t1"), ToolStart(3, "c1", "write", new string('i', 1_000_000)), ToolEnd(4, "c1")));
        Push(A(Final(5, "m2", new string('r', 2_100_000))));

        // Assert
        var stored = Stored();
        Assert.Equal(new[] { "reply:m2" }, stored.Select(s => s.Item.ItemId));
        Assert.True(stored[0].EarlierDropped);
    }

    [Fact]
    public void Record_TheFirstItemLargerThanTheWholeBound_IsKeptWithNoMarker()
    {
        // Act
        Push(A(Final(1, "m1", new string('r', 2_100_000))));

        // Assert: nothing earlier was let go, so nothing says so.
        var stored = Assert.Single(Stored());
        Assert.Equal(("reply:m1", false), (stored.Item.ItemId, stored.EarlierDropped));
    }

    // ----- retention and who may read -----

    [Fact]
    public void PurgeOlderThan_AnIdleSessionGoesWhole_AndOneStillInUseKeepsItsStart()
    {
        // Arrange: Alice's session was written 100 days ago and again today; Bob's only 100 days ago.
        _now = T0.UtcDateTime.AddDays(-100);
        Push(A(Ready(1)));
        Push(A(Ready(1)), Bob);
        _now = T0.UtcDateTime;
        Push(A(Final(2, "m1", "still here")));

        // Act
        int alice, bob;
        using (_boundary.EnterScope(Alice))
            alice = _conversation.PurgeOlderThan(_now - ApiModeConversationStore.Retention);
        using (_boundary.EnterScope(Bob))
            bob = _conversation.PurgeOlderThan(_now - ApiModeConversationStore.Retention);

        // Assert: never a silent gap at the start of a session still in use.
        Assert.Equal((0, 1), (alice, bob));
        Assert.Equal(new[] { "connection:gen-a:1", "reply:m1" }, Stored().Select(s => s.Item.ItemId));
        Assert.Empty(Stored(Bob));
    }

    [Fact]
    public void Read_AnotherAccountsSessionOfTheSameId_SeesOnlyItsOwnItems()
    {
        // Act: two accounts, one session id.
        Push(A(Final(1, "m1", "Alice's reply")));
        Push(A(Final(1, "m1", "Bob's reply")), Bob);

        // Assert
        Assert.Equal("Alice's reply", Assert.Single(Stored()).Item.Text);
        Assert.Equal("Bob's reply", Assert.Single(Stored(Bob)).Item.Text);
    }

    // ----- the ruling's part -----

    [Fact]
    public void ConversationItems_AReceiptEvent_KeepsNothing()
    {
        // Act
        var items = ApiModeLiveRuling.ConversationItems(new ApiModeHeldEvent("gen-a", GenAStarted, Delivered(1, "p1", PlainAck)), null);

        // Assert
        Assert.Empty(items);
    }

    [Fact]
    public void ConversationItems_AFinishWithItsStoredCard_KeepsTheStoredTitle()
    {
        // Arrange
        var stored = new ApiModeLiveItem { ItemId = "tool:c1", Kind = ApiModeLiveItemKinds.ToolCard, Mode = ApiModeLiveItemModes.Replace, Text = "bash", Status = ApiModeLiveRuling.ToolRunningStatus, Detail = "ls", Tone = "neutral", TurnId = "t1" };

        // Act
        var card = Assert.Single(ApiModeLiveRuling.ConversationItems(new ApiModeHeldEvent("gen-a", GenAStarted, ToolEnd(5, "c1")), stored));

        // Assert
        Assert.Equal(("bash", "done", "ls"), (card.Text, card.Status, card.Detail));
    }
}
