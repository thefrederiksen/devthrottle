using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Traffic optimization, phase 2: the conversation tail. A client that sends the cursor it was handed gets only
/// the messages after it - and ONLY when the Gateway can prove the client holds exactly the start of what it
/// would send now. Every other case is a full answer: a generation switch, a shorter conversation, a changed
/// earlier message, another account's or another session's cursor, a tampered or unparseable cursor.
///
/// Revert-proof: make <see cref="ConversationTail.Decide"/> trust the cursor's count without checking its hash
/// and the changed-message, generation, tenant and tampered-hash tests go red; drop the tenant or the session
/// from the hashed preamble and the two cross-account tests go red; start the tail one message late or early
/// and the tail-equals-full tests go red.
/// </summary>
public sealed class ConversationTailTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Tenant = "local";
    private const string OtherTenant = "11111111-1111-1111-1111-111111111111";
    private const string Sid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";
    private const string Gen = "gen-a";

    private readonly GatewayDbTestHarness _harness = new();
    public void Dispose() => _harness.Dispose();

    private static HistoryMessageDto Msg(int i) => new()
    {
        Role = i % 2 == 0 ? "User" : "Assistant",
        Timestamp = new DateTimeOffset(2026, 9, 21, 12, 0, 0, TimeSpan.Zero).AddSeconds(i),
        Parts =
        {
            new HistoryPartDto { Kind = "Text", Text = $"message {i}: " + new string('x', i % 7) },
            new HistoryPartDto { Kind = "ToolUse", Text = "{\"cmd\":\"ls\"}", ToolName = "Bash", ToolId = $"t{i}" },
        },
    };

    private static List<HistoryMessageDto> Conversation(int count) => Enumerable.Range(0, count).Select(Msg).ToList();

    private static string CursorFor(List<HistoryMessageDto> messages, int count, string tenant = Tenant, string sid = Sid, string gen = Gen) =>
        ConversationTail.CursorFor(tenant, sid, gen, messages, count, Web);

    private static string Json(IEnumerable<HistoryMessageDto> messages) => JsonSerializer.Serialize(messages.ToList(), Web);

    [Fact]
    public void NoCursor_AnswersTheWholeConversation_AndTheCursorForAllOfIt()
    {
        var messages = Conversation(5);

        var answer = ConversationTail.Decide("", Tenant, Sid, Gen, messages, Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(Json(messages), Json(answer.Messages));
        Assert.Equal(CursorFor(messages, 5), answer.Cursor);
        Assert.Equal("no cursor", answer.Reason);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(3, 5)]
    [InlineData(10, 40)]
    [InlineData(7, 0)]
    public void TailAfterNewTurns_PlusWhatTheClientHeld_EqualsTheFullAnswer(int held, int added)
    {
        var before = Conversation(held);
        var cursor = ConversationTail.Decide("", Tenant, Sid, Gen, before, Web).Cursor;
        var now = Conversation(held + added);

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, now, Web);

        Assert.Null(answer.Reason);
        Assert.Equal(held, answer.TailFrom);
        Assert.Equal(added, answer.Messages.Count);
        // What the client ends up with is byte-for-byte the full answer's messages: no gap, no duplicate.
        Assert.Equal(Json(now), Json(before.Concat(answer.Messages)));
        // And the next cursor is the one a full read would have handed it.
        Assert.Equal(ConversationTail.Decide("", Tenant, Sid, Gen, now, Web).Cursor, answer.Cursor);
    }

    [Fact]
    public void ARunOfPolls_EachApplyingItsTail_EndsExactlyAtTheFullConversation()
    {
        var held = new List<HistoryMessageDto>();
        var cursor = "";
        foreach (var length in new[] { 0, 1, 1, 4, 4, 9, 30, 31 })
        {
            var now = Conversation(length);
            var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, now, Web);
            held = held.Take(answer.TailFrom).Concat(answer.Messages).ToList();
            cursor = answer.Cursor;
            Assert.Equal(Json(now), Json(held));
        }
    }

    [Fact]
    public void AGenerationSwitch_AnswersInFull()
    {
        var messages = Conversation(6);
        var cursor = CursorFor(messages, 4, gen: "gen-a");

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, "gen-b", messages, Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(6, answer.Messages.Count);
    }

    [Fact]
    public void AConversationShorterThanTheCursor_AnswersInFull()
    {
        var cursor = CursorFor(Conversation(8), 8);

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, Conversation(5), Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(5, answer.Messages.Count);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(3)]  // the LAST message the client holds
    public void AChangedEarlierMessage_AnswersInFull(int changed)
    {
        var before = Conversation(4);
        var cursor = CursorFor(before, 4);
        var now = Conversation(6);
        now[changed].Parts[0].Text += " (edited)";

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, now, Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(Json(now), Json(answer.Messages));
    }

    [Fact]
    public void AnotherAccountsCursor_ForTheSameSessionIdAndTheSameWords_AnswersInFull()
    {
        // The hard case: two accounts, the same session id, byte-identical conversations. Only the tenant in the
        // hashed preamble tells the two cursors apart - so one account's cursor never unlocks a tail of the other's.
        var messages = Conversation(6);
        var theirs = CursorFor(messages, 4, tenant: OtherTenant);

        var answer = ConversationTail.Decide(theirs, Tenant, Sid, Gen, messages, Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.NotEqual(theirs, CursorFor(messages, 4));
    }

    [Fact]
    public void AnotherSessionsCursor_AnswersInFull()
    {
        var messages = Conversation(6);
        var other = CursorFor(messages, 4, sid: "9d0f5f0e-2f1b-4d5c-9b1e-3f2a1c0b9e88");

        Assert.Equal(0, ConversationTail.Decide(other, Tenant, Sid, Gen, messages, Web).TailFrom);
    }

    public static IEnumerable<object[]> Tampered()
    {
        var real = CursorFor(Conversation(6), 4);
        var parts = real.Split('.');
        var flipped = parts[2][..10] + (parts[2][10] == 'a' ? 'b' : 'a') + parts[2][11..];
        yield return new object[] { $"v2.{parts[1]}.{parts[2]}" };            // another version
        yield return new object[] { $"v1.3.{parts[2]}" };                      // the count changed, hash kept
        yield return new object[] { $"v1.5.{parts[2]}" };
        yield return new object[] { $"v1.4.{flipped}" };                       // one hash character changed
        yield return new object[] { $"v1.4.{parts[2][..63]}" };                // a truncated hash
        yield return new object[] { $"v1.4.{new string('z', 64)}" };          // not hex
        yield return new object[] { $"v1.-1.{parts[2]}" };                     // a negative count
        yield return new object[] { $"v1.4" };
        yield return new object[] { "garbage" };
        yield return new object[] { "..." };
        yield return new object[] { real + "." };
        yield return new object[] { new string('v', ConversationTail.MaxCursorLength + 1) };
    }

    [Theory]
    [MemberData(nameof(Tampered))]
    public void ATamperedOrUnparseableCursor_AnswersInFull(string cursor)
    {
        var messages = Conversation(6);

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, messages, Web);

        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(6, answer.Messages.Count);
        Assert.NotNull(answer.Reason);
    }

    [Fact]
    public void ACursorForTheWholeConversation_AnswersAnEmptyTail()
    {
        var messages = Conversation(6);

        var answer = ConversationTail.Decide(CursorFor(messages, 6), Tenant, Sid, Gen, messages, Web);

        Assert.Equal(6, answer.TailFrom);
        Assert.Empty(answer.Messages);
        Assert.Equal(CursorFor(messages, 6), answer.Cursor);
    }

    [Fact]
    public void TheTailBody_CarriesEveryEnvelopeFieldOfTheFullAnswer()
    {
        var messages = Conversation(6);
        var full = new SessionHistoryDto
        {
            SessionId = Sid, DirectorId = "dir-1", Agent = "ClaudeCode", IsSupported = true, IsRawText = false,
            HistoryState = "Working", Messages = messages, StaleNotice = SessionConversationFold.FrozenOfflineNotice,
            EmptyText = null, Status = "ok", Error = null,
        };
        var answer = ConversationTail.Decide(CursorFor(messages, 4), Tenant, Sid, Gen, messages, Web);

        var body = SessionConversationEndpoint.TailBody(full, answer, Web);

        var fullNode = JsonSerializer.SerializeToNode(full, Web)!.AsObject();
        foreach (var (name, value) in fullNode)
        {
            if (name == "messages") continue;
            Assert.True(JsonNode.DeepEquals(value, body[name]), $"{name} differs between the full and the tail answer");
        }
        Assert.Equal(4, (int)body["tailFrom"]!);
        Assert.Equal(answer.Cursor, (string)body["cursor"]!);
        Assert.Equal(2, body["messages"]!.AsArray().Count);
        // The tail form adds exactly the two fields, nothing else.
        Assert.Equal(fullNode.Select(p => p.Key).Append("tailFrom").Append("cursor").OrderBy(k => k),
            body.Select(p => p.Key).OrderBy(k => k));
    }

    // ----- the store: can a stored turn change after it has been served? -----

    private static TurnPushBatch Batch(string generation, int start, params (string Role, string Text)[] turns) => new()
    {
        SessionId = Sid,
        Generation = generation,
        GenerationStartedUtc = new DateTime(2026, 9, 21, 11, 0, 0, DateTimeKind.Utc),
        Agent = "ClaudeCode",
        StartOrdinal = start,
        TotalCount = start + turns.Length,
        Turns = turns.Select((t, i) => new PushedTurn
        {
            Ordinal = start + i,
            Role = t.Role,
            Parts = { new HistoryPartDto { Kind = "Text", Text = t.Text } },
        }).ToList(),
    };

    [Fact]
    public void AStoredTurnNeverChanges_SoTheTailNeverResendsIt()
    {
        // The Director re-sends the last turn with different words - a partial message that grew, say. The store
        // keeps what it first stored, so the conversation a full read serves does not change, the client's cursor
        // still matches, and the tail is empty. This is why a tail does NOT re-send the last message.
        var store = new SessionTurnStore(_harness.Open());
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        store.Append("d1", Batch(@"C:\t\a.jsonl", 0, ("User", "go"), ("Assistant", "partial")), now);
        var first = store.ReadCurrent(Sid)!.Value;
        var cursor = ConversationTail.Decide("", Tenant, Sid, first.Head.Generation, first.Messages, Web).Cursor;

        store.Append("d1", Batch(@"C:\t\a.jsonl", 1, ("Assistant", "partial, now finished")), now.AddSeconds(3));
        var second = store.ReadCurrent(Sid)!.Value;

        Assert.Equal("partial", second.Messages[1].Parts[0].Text);
        var answer = ConversationTail.Decide(cursor, Tenant, Sid, second.Head.Generation, second.Messages, Web);
        Assert.Equal(2, answer.TailFrom);
        Assert.Empty(answer.Messages);
    }

    [Fact]
    public void AGenerationSwitchInTheStore_AnswersInFull()
    {
        var store = new SessionTurnStore(_harness.Open());
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        store.Append("d1", Batch(@"C:\t\a.jsonl", 0, ("User", "one"), ("Assistant", "two"), ("User", "three")), now);
        var first = store.ReadCurrent(Sid)!.Value;
        var cursor = ConversationTail.Decide("", Tenant, Sid, first.Head.Generation, first.Messages, Web).Cursor;

        // /clear: a later transcript. Its first three messages happen to be the same words - only the generation
        // in the hash tells the client its copy belongs to the conversation the session has left.
        var later = Batch(@"C:\t\b.jsonl", 0, ("User", "one"), ("Assistant", "two"), ("User", "three"), ("Assistant", "four"));
        later.GenerationStartedUtc = later.GenerationStartedUtc.AddMinutes(5);
        store.Append("d1", later, now.AddSeconds(5));
        var second = store.ReadCurrent(Sid)!.Value;

        var answer = ConversationTail.Decide(cursor, Tenant, Sid, second.Head.Generation, second.Messages, Web);
        Assert.Equal(0, answer.TailFrom);
        Assert.Equal(4, answer.Messages.Count);
    }

    [Fact]
    public void TwoAccountsWithTheSameSessionIdAndWords_NeverShareATail()
    {
        var mine = new SessionTurnStore(_harness.Open(new FixedTenantContext(TenantId.Local)));
        var theirs = new SessionTurnStore(_harness.Open(new FixedTenantContext(new TenantId(OtherTenant))));
        var now = new DateTime(2026, 9, 21, 12, 0, 0, DateTimeKind.Utc);
        var batch = Batch(@"C:\t\a.jsonl", 0, ("User", "same"), ("Assistant", "words"));
        mine.Append("d1", batch, now);
        theirs.Append("d2", batch, now);

        var theirRead = theirs.ReadCurrent(Sid)!.Value;
        var theirCursor = ConversationTail.Decide("", OtherTenant, Sid, theirRead.Head.Generation, theirRead.Messages, Web).Cursor;
        var myRead = mine.ReadCurrent(Sid)!.Value;

        var answer = ConversationTail.Decide(theirCursor, TenantId.Local.Value, Sid, myRead.Head.Generation, myRead.Messages, Web);

        Assert.Equal(0, answer.TailFrom);
    }
}
