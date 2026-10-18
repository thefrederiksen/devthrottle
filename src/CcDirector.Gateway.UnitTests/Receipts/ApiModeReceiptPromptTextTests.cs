using System.Text.Json;
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

namespace CcDirector.Gateway.Tests.Receipts;

/// <summary>
/// A prompt's own text on its receipt (mission #4070, phase 2 follow-on). PromptQueued, PromptDelivered and
/// PromptRefused each carry the prompt's text; the Gateway keeps it on the receipt row and serves it on the receipt and
/// on the live receipt item. These pin that it is kept VERBATIM (leading and trailing spaces and newlines unchanged -
/// the spoken words reach the agent verbatim, and the Gateway never trims, rewords or truncates them), that it is null
/// when no event carried any (a Director older than the text on the events), and that the first event carrying it sets
/// it and no later event blanks or replaces it - in the store, through the real ingest from the events' JSON, and in
/// the live ruling.
/// </summary>
public sealed class ApiModeReceiptPromptTextTests : IDisposable
{
    private static readonly TenantId Alice = new("aaaaaaaa-0000-4000-8000-00000000000a");
    private const string Director = "dir-devlinux";
    private const string Sid = "0b6c2d9e-4f1a-4c3b-9d2e-7a8b9c0d1e2f";
    private const string StartedAck = """{"id":"req-96c7b87f","type":"response","command":"prompt","success":true,"data":{"disposition":"started"}}""";
    private const string BusyRefusal = "Agent is already processing. Specify streamingBehavior ('steer' or 'followUp') to queue the message.";

    /// <summary>Leading and trailing spaces, an inner newline and a trailing one: every one must survive.</summary>
    private const string Spoken = "  run the tests\nthen tell me what failed  \n";

    private static readonly DateTime GenAStarted = new(2026, 10, 16, 11, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime T0 = new(2026, 10, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private ApiModeReceiptStore Store() => new(_harness.Open(), () => T0);

    private static ApiModeEventPosition At(long sequence) => new("gen-a", GenAStarted, sequence);

    private static ApiModeReceiptDto Only(ApiModeReceiptStore store) => Assert.Single(store.Read(Sid));

    // ----- the store: each of the three events stores it verbatim -----

    [Fact]
    public void RecordQueued_WithPromptText_StoresItVerbatim()
    {
        var store = Store();

        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, Spoken);

        Assert.Equal(Spoken, Only(store).PromptText);
    }

    [Fact]
    public void RecordDelivered_WithPromptText_StoresItVerbatim()
    {
        var store = Store();

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0, Spoken);

        Assert.Equal(Spoken, Only(store).PromptText);
    }

    [Fact]
    public void RecordRefused_WithPromptText_StoresItVerbatim()
    {
        var store = Store();

        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(1), T0, Spoken);

        Assert.Equal(Spoken, Only(store).PromptText);
    }

    [Fact]
    public void RecordDelivered_AfterAQueuedReceiptWithoutText_SetsTheText()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0);

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(2), T0, Spoken);

        Assert.Equal(Spoken, Only(store).PromptText);
    }

    // ----- null when absent -----

    [Fact]
    public void RecordDelivered_NoPromptText_PromptTextIsNull()
    {
        var store = Store();

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);

        Assert.Null(Only(store).PromptText);
    }

    [Fact]
    public void RecordQueued_EmptyPromptText_PromptTextIsNull()
    {
        var store = Store();

        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, "");

        Assert.Null(Only(store).PromptText);
    }

    // ----- a later event never blanks or replaces it -----

    [Fact]
    public void RecordDelivered_LaterEventWithoutText_DoesNotBlankIt()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, Spoken);

        var changed = store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(2), T0, null);

        Assert.True(changed);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Equal(Spoken, r.PromptText);
    }

    [Fact]
    public void RecordRefused_LaterEventWithEmptyText_DoesNotBlankIt()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, Spoken);

        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(2), T0, "");

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Refused, r.State);
        Assert.Equal(Spoken, r.PromptText);
    }

    [Fact]
    public void RecordQueued_LaterEventWithOtherText_KeepsTheFirst()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, Spoken);

        store.RecordQueued(Director, Sid, "p1", "userHasTheWheel", At(2), T0, "something else");

        var r = Only(store);
        Assert.Equal("userHasTheWheel", r.QueueReason);
        Assert.Equal(Spoken, r.PromptText);
    }

    // ----- the bound -----

    [Fact]
    public void RecordDelivered_PromptTextLongerThanTheVerbatimLimit_StoresItWhole()
    {
        var store = Store();
        var longPrompt = new string('w', ApiModeReceiptStore.MaxVerbatimLength * 2) + "\n";

        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0, longPrompt);

        Assert.Equal(longPrompt, Only(store).PromptText);
    }

    [Fact]
    public void RecordDelivered_PromptTextOverTheBatchLimit_RefusedAndNothingWritten()
    {
        var store = Store();
        var tooLong = new string('w', ApiModeReceiptStore.MaxPromptTextLength + 1);

        Assert.Throws<ArgumentException>(() => store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0, tooLong));

        Assert.Empty(store.Read(Sid));
    }

    // ----- through the real ingest, from the events' JSON as the Director sends them -----

    [Fact]
    public void Accept_EachOfTheThreeEvents_StoresThePromptTextVerbatim()
    {
        var ambient = new AsyncLocalTenantContext();
        var path = _harness.LegacyPath("devices.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var boundary = new HostedTenantBoundary(ambient, new DeviceRegistry(_harness.Open(), path), hosted: false);
        var receipts = new ApiModeReceiptStore(_harness.Open(ambient), () => T0);
        var ingest = new ApiModeEventIngest(new ApiModeEventBuffer(), receipts, new ApiModeConversationStore(_harness.Open(ambient), () => T0), boundary);
        var at = new DateTimeOffset(T0);
        SessionEvent[] events =
        {
            new PromptQueued { SessionId = Sid, Sequence = 1, At = at, PromptId = "queued", PromptText = Spoken + "q", Reason = PromptQueuedReason.TurnRunning, Position = 1 },
            new PromptDelivered { SessionId = Sid, Sequence = 2, At = at, PromptId = "delivered", PromptText = Spoken + "d", Disposition = PromptDisposition.Started, Acknowledgement = StartedAck },
            new PromptRefused { SessionId = Sid, Sequence = 3, At = at, PromptId = "refused", PromptText = Spoken + "r", Reason = BusyRefusal, RefusedBy = PromptRefusedBy.Agent },
        };

        var watermark = ingest.Accept(Alice, Director, new ApiModeEventBatch
        {
            SessionId = Sid,
            Generation = "gen-a",
            GenerationStartedUtc = GenAStarted,
            Events = events.Select(e => JsonSerializer.Serialize(e, SessionEventJson.Options)).ToList(),
        });

        Assert.NotNull(watermark);
        using (boundary.EnterScope(Alice))
        {
            var stored = receipts.Read(Sid).ToDictionary(r => r.PromptId, r => r.PromptText);
            Assert.Equal(Spoken + "q", stored["queued"]);
            Assert.Equal(Spoken + "d", stored["delivered"]);
            Assert.Equal(Spoken + "r", stored["refused"]);
        }
    }

    // ----- a retry under the same id keeps the text (Supersede, pull request #4142) -----
    // A retry reuses its Gateway id exactly, so it is the same prompt and carries the same words: Supersede clears what
    // the refused attempt wrote and keeps the text, and the retry's text fills it only when the refused attempt had none.

    [Fact]
    public void RecordDelivered_RetryAfterARefusalWithText_KeepsTheText()
    {
        var store = Store();
        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(3), T0, Spoken);

        var changed = store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(9), T0, Spoken);

        Assert.True(changed);
        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Delivered, r.State);
        Assert.Null(r.RefusalReason);
        Assert.Equal(Spoken, r.PromptText);
    }

    [Fact]
    public void RecordQueued_RetryWithoutTextAfterARefusalWithText_DoesNotBlankIt()
    {
        var store = Store();
        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(3), T0, Spoken);

        store.RecordQueued(Director, Sid, "p1", "agentStarting", At(9), T0);

        var r = Only(store);
        Assert.Equal(ApiModeReceiptState.Queued, r.State);
        Assert.Equal(Spoken, r.PromptText);
    }

    [Fact]
    public void RecordRefused_RetryWithTextAfterARefusalWithout_SetsTheText()
    {
        var store = Store();
        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(3), T0);

        store.RecordRefused(Director, Sid, "p1", BusyRefusal, "director", At(9), T0, Spoken);

        var r = Only(store);
        Assert.Equal("director", r.RefusedBy);
        Assert.Equal(Spoken, r.PromptText);
    }

    [Fact]
    public void Rule_RetryAfterARefusal_KeepsTheTextAsTheStoreDoes()
    {
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Refused(3, Spoken));

        var item = Assert.Single(ruling.Rule(Delivered(9, null)));

        Assert.Equal("Delivered", item.Text);
        Assert.Equal(Spoken, item.PromptText);
    }

    [Fact]
    public void Rule_RetryWithTextAfterARefusalWithout_SetsTheTextAsTheStoreDoes()
    {
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Refused(3, null));

        var item = Assert.Single(ruling.Rule(Delivered(9, Spoken)));

        Assert.Equal(Spoken, item.PromptText);
    }

    // ----- an event the store sets aside sets no text in the live ruling either -----
    // After Delivered, a late Queued or Refused is set aside by the store (a delivered prompt is never sent again), text
    // and all. The ruling keeps the text under the same condition, so a live reader is never shown text the stored
    // receipt does not have, and the next reset does not take it away.

    private static TurnStarted TurnStart(long seq) => new() { SessionId = Sid, Sequence = seq, At = new DateTimeOffset(T0), TurnId = "t1", PromptIds = new[] { "p1" } };
    private static TurnEnded TurnFailed(long seq) => new() { SessionId = Sid, Sequence = seq, At = new DateTimeOffset(T0), TurnId = "t1", Outcome = TurnOutcome.Failed, Reason = "Pi exited during the turn (exit code 137)", AgentStopReason = null, Usage = new TurnUsage { Input = 0, Output = 0, CacheRead = 0, CacheWrite = 0 } };

    public static TheoryData<string> SetAsideAfterDelivered => new() { "queued", "refused" };

    [Theory]
    [MemberData(nameof(SetAsideAfterDelivered))]
    public void Rule_ALateEventWithTextAfterDelivered_SetsNoTextAsTheStoreDoesNot(string late)
    {
        // Arrange: delivered without text, then a late event with text that both keepers set aside.
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0);
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Delivered(1, null));
        SessionEvent lateEvent = late == "queued" ? Queued(2, Spoken) : Refused(2, Spoken);
        var storeChanged = late == "queued"
            ? store.RecordQueued(Director, Sid, "p1", "turnRunning", At(2), T0, Spoken)
            : store.RecordRefused(Director, Sid, "p1", BusyRefusal, "agent", At(2), T0, Spoken);

        // Act: the late event, then a turn that changes the receipt's words, so the ruling shows its receipt again.
        var lateItems = ruling.Rule(lateEvent);
        ruling.Rule(TurnStart(3));
        var afterTurn = ruling.Rule(TurnFailed(4));

        // Assert: the store kept no text, and the receipt item the live reader is handed has none either.
        Assert.False(storeChanged);
        Assert.Null(Only(store).PromptText);
        Assert.Empty(lateItems);
        var receipt = Assert.Single(afterTurn, i => i.Kind == ApiModeLiveItemKinds.Receipt);
        Assert.Null(receipt.PromptText);
    }

    // ----- the live ruling: the receipt item carries it -----

    private static PromptQueued Queued(long seq, string? text) => new() { SessionId = Sid, Sequence = seq, At = new DateTimeOffset(T0), PromptId = "p1", PromptText = text, Reason = PromptQueuedReason.TurnRunning, Position = 1 };
    private static PromptDelivered Delivered(long seq, string? text) => new() { SessionId = Sid, Sequence = seq, At = new DateTimeOffset(T0), PromptId = "p1", PromptText = text, Disposition = PromptDisposition.Started, Acknowledgement = StartedAck };
    private static PromptRefused Refused(long seq, string? text) => new() { SessionId = Sid, Sequence = seq, At = new DateTimeOffset(T0), PromptId = "p1", PromptText = text, Reason = BusyRefusal, RefusedBy = PromptRefusedBy.Agent };

    [Fact]
    public void Rule_EachOfTheThreeEvents_TheReceiptItemCarriesThePromptTextVerbatim()
    {
        var items = new[] { (SessionEvent)Queued(1, Spoken), Delivered(1, Spoken), Refused(1, Spoken) }
            .Select(e => Assert.Single(new ApiModeLiveRuling().Rule(e)));

        Assert.All(items, item => Assert.Equal(Spoken, item.PromptText));
    }

    [Fact]
    public void Rule_NoPromptText_TheReceiptItemHasNone()
    {
        var item = Assert.Single(new ApiModeLiveRuling().Rule(Delivered(1, null)));

        Assert.Null(item.PromptText);
    }

    [Fact]
    public void Rule_LaterEventWithoutText_DoesNotBlankIt()
    {
        var ruling = new ApiModeLiveRuling();
        ruling.Rule(Queued(1, Spoken));

        var item = Assert.Single(ruling.Rule(Delivered(2, "")));

        Assert.Equal("Delivered", item.Text);
        Assert.Equal(Spoken, item.PromptText);
    }

    [Fact]
    public void ReceiptItem_StoredReceiptWithText_CarriesItVerbatim()
    {
        var store = Store();
        store.RecordDelivered(Director, Sid, "p1", "started", StartedAck, At(1), T0, Spoken);

        var item = ApiModeLiveRuling.ReceiptItem(Only(store), 0);

        Assert.Equal(Spoken, item.PromptText);
    }

    [Fact]
    public void Rule_StartedFromStoredReceipts_KeepsTheStoredText()
    {
        var store = Store();
        store.RecordQueued(Director, Sid, "p1", "turnRunning", At(1), T0, Spoken);
        var ruling = new ApiModeLiveRuling(store.ReadWithPositions(Sid));

        var item = Assert.Single(ruling.Rule(new ApiModeHeldEvent("gen-a", GenAStarted, Delivered(2, null))));

        Assert.Equal(Spoken, item.PromptText);
    }
}
