using System.Text.Json;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Issue #4387: a cursor request is decided from the store's index of the conversation, and rebuilds only the
/// messages it sends. These pin the two halves of that: the index gives exactly the answers the whole conversation
/// gives (every cursor, every window start, every slice), and a poll with nothing new rebuilds nothing.
///
/// Revert-proof: hash a message differently in the index, or index one late, and the every-count tests go red; build
/// the index again on every read and the nothing-new test goes red; keep an index across a generation switch or for
/// another tenant and the switch and tenant tests go red.
/// </summary>
public sealed class ConversationViewTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Tenant = "local";
    private const string Sid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";
    private static readonly DateTime Now = new(2026, 10, 23, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    public void Dispose() => _harness.Dispose();

    // A prompt, a reply with a tool call, the tool result fed back (a user message that is NOT a turn), a reply.
    private static PushedTurn Turn(int ordinal) => (ordinal % 4) switch
    {
        0 => new PushedTurn { Ordinal = ordinal, Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = $"prompt {ordinal}" } } },
        1 => new PushedTurn
        {
            Ordinal = ordinal,
            Role = "Assistant",
            Timestamp = new DateTimeOffset(Now).AddSeconds(ordinal),
            Parts =
            {
                new HistoryPartDto { Kind = "Text", Text = $"working {ordinal}" },
                new HistoryPartDto { Kind = "ToolUse", Text = "{\"cmd\":\"ls\"}", ToolName = "Bash", ToolId = $"t{ordinal}" },
            },
        },
        2 => new PushedTurn { Ordinal = ordinal, Role = "User", Parts = { new HistoryPartDto { Kind = "ToolResult", Text = "a.txt", ToolId = $"t{ordinal - 1}" } } },
        _ => new PushedTurn { Ordinal = ordinal, Role = "Assistant", Parts = { new HistoryPartDto { Kind = "Text", Text = $"done {ordinal}" } } },
    };

    private static TurnPushBatch Batch(int start, int count, string generation = @"C:\t\a.jsonl", int minutes = 0) => new()
    {
        SessionId = Sid,
        Generation = generation,
        GenerationStartedUtc = Now.AddMinutes(minutes - 60),
        Agent = "ClaudeCode",
        StartOrdinal = start,
        TotalCount = start + count,
        Turns = Enumerable.Range(start, count).Select(Turn).ToList(),
    };

    private static string Json(IEnumerable<HistoryMessageDto> messages) => JsonSerializer.Serialize(messages.ToList(), Web);

    private static string CursorOf(ConversationView view) => ConversationTail.Decide("", view).Cursor;

    [Fact]
    public void EveryCursor_EveryWindowStart_AndEverySlice_AreWhatTheWholeConversationGives()
    {
        var store = new SessionTurnStore(_harness.Open());
        store.Append("d1", Batch(0, 30), Now);
        // Read once so the index exists, then grow it, so the answers below come from an index that was EXTENDED.
        store.ReadView(Sid, Tenant, Web);
        store.Append("d1", Batch(30, 13), Now.AddSeconds(5));

        var view = store.ReadView(Sid, Tenant, Web);
        var (head, messages) = store.ReadCurrent(Sid)!.Value;
        Assert.Equal(messages.Count, view.Count);

        for (var k = 0; k <= messages.Count; k++)
        {
            var cursor = ConversationTail.CursorFor(Tenant, Sid, head.Generation, messages, k, Web);
            var verdict = ConversationTail.Decide(cursor, view);
            var whole = ConversationTail.Decide(cursor, Tenant, Sid, head.Generation, messages, Web);
            Assert.Null(verdict.Reason);
            Assert.Equal(k, verdict.TailFrom);
            Assert.Equal(whole.Cursor, verdict.Cursor);
        }
        for (var end = 0; end <= messages.Count; end++)
            for (var turns = 1; turns <= 4; turns++)
                Assert.Equal(
                    ConversationWindow.StartOfLastTurns(messages, end, turns),
                    ConversationWindow.StartOfLastTurns(view, end, turns));
        Assert.Equal(Json(messages), Json(view.Messages(0, view.Count)));
        Assert.Equal(Json(messages.GetRange(17, 9)), Json(view.Messages(17, 26)));
    }

    [Fact]
    public void ARefusedCursor_IsRefusedForTheSameReason_AsAgainstTheWholeConversation()
    {
        var store = new SessionTurnStore(_harness.Open());
        store.Append("d1", Batch(0, 12), Now);
        var view = store.ReadView(Sid, Tenant, Web);
        var (head, messages) = store.ReadCurrent(Sid)!.Value;
        var good = ConversationTail.CursorFor(Tenant, Sid, head.Generation, messages, 8, Web);
        var tampered = good[..^1] + (good[^1] == '0' ? '1' : '0');
        var tooLong = ConversationTail.CursorFor(Tenant, Sid, head.Generation, messages, 12, Web).Replace("v1.12.", "v1.13.");

        foreach (var cursor in new[] { "", "garbage", tampered, tooLong, "v2.8." + new string('a', 64) })
        {
            var verdict = ConversationTail.Decide(cursor, view);
            var whole = ConversationTail.Decide(cursor, Tenant, Sid, head.Generation, messages, Web);
            Assert.Equal(0, verdict.TailFrom);
            Assert.Equal(whole.Reason, verdict.Reason);
            Assert.Equal(whole.Cursor, verdict.Cursor);
        }
    }

    [Fact]
    public void APollWithNothingNew_RebuildsNoMessage_AndANewTurnRebuildsOnlyItself()
    {
        var store = new SessionTurnStore(_harness.Open());
        store.Append("d1", Batch(0, 40), Now);
        var first = store.ReadView(Sid, Tenant, Web);
        var cursor = CursorOf(first);

        var before = store.RowsRebuilt;
        var poll = store.ReadView(Sid, Tenant, Web);
        Assert.Equal(40, ConversationTail.Decide(cursor, poll).TailFrom);
        Assert.Empty(poll.Messages(40, poll.Count));
        Assert.Equal(before, store.RowsRebuilt);

        store.Append("d1", Batch(40, 2), Now.AddSeconds(3));
        before = store.RowsRebuilt;
        var next = store.ReadView(Sid, Tenant, Web);
        var verdict = ConversationTail.Decide(cursor, next);
        Assert.Equal(40, verdict.TailFrom);
        Assert.Equal(2, next.Messages(verdict.TailFrom, next.Count).Count);
        // Two to index them, two to send them.
        Assert.Equal(before + 4, store.RowsRebuilt);
    }

    [Fact]
    public void AGenerationSwitch_ThrowsTheIndexAway_SoTheOldCursorIsRefused()
    {
        var store = new SessionTurnStore(_harness.Open());
        store.Append("d1", Batch(0, 8), Now);
        var cursor = CursorOf(store.ReadView(Sid, Tenant, Web));

        // /clear: the same words under a later transcript.
        store.Append("d1", Batch(0, 8, @"C:\t\b.jsonl", minutes: 5), Now.AddSeconds(5));
        var view = store.ReadView(Sid, Tenant, Web);
        var (head, messages) = store.ReadCurrent(Sid)!.Value;

        var verdict = ConversationTail.Decide(cursor, view);
        Assert.Equal(0, verdict.TailFrom);
        Assert.Equal(ConversationTail.CursorFor(Tenant, Sid, head.Generation, messages, 8, Web), verdict.Cursor);
    }

    [Fact]
    public void AnotherTenantsHash_IsNeverTakenFromThisOnesIndex()
    {
        var store = new SessionTurnStore(_harness.Open());
        store.Append("d1", Batch(0, 6), Now);
        var ours = CursorOf(store.ReadView(Sid, Tenant, Web));
        var theirs = CursorOf(store.ReadView(Sid, "11111111-1111-1111-1111-111111111111", Web));

        Assert.NotEqual(ours, theirs);
        Assert.Equal(0, ConversationTail.Decide(theirs, store.ReadView(Sid, Tenant, Web)).TailFrom);
    }

    [Fact]
    public void AnIndexLetGoWhenIdle_IsRebuiltToTheSameHashes()
    {
        var clock = Now;
        var store = new SessionTurnStore(_harness.Open(), () => clock);
        store.Append("d1", Batch(0, 10), Now);
        var cursor = CursorOf(store.ReadView(Sid, Tenant, Web));

        clock += SessionTurnStore.HeldIdleLimit + TimeSpan.FromMinutes(1);
        var view = store.ReadView(Sid, Tenant, Web);

        Assert.Equal(10, ConversationTail.Decide(cursor, view).TailFrom);
    }

    [Fact]
    public void ASessionWithNothingStored_AnswersTheCursorOfAnEmptyConversation()
    {
        var store = new SessionTurnStore(_harness.Open());
        var view = store.ReadView(Sid, Tenant, Web);

        Assert.Null(view.Head);
        Assert.Equal(0, view.Count);
        Assert.Equal(
            ConversationTail.Decide("", Tenant, Sid, "", new List<HistoryMessageDto>(), Web).Cursor,
            CursorOf(view));
    }
}
