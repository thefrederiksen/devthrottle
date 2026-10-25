using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using Xunit;

namespace CcDirector.Gateway.Tests.History;

/// <summary>
/// Issue #4372: the conversation window. A Chat is sent only the last few turns, fetches older turns when the
/// reader scrolls back, and keeps being sent tails exactly as before.
///
/// The property that matters is the same one the tail tests hold: what the client ends up holding is EXACTLY a
/// slice of the full answer - no gap, no duplicate. Revert-proof: start the window one message late (mid-turn) and
/// the turn-boundary test goes red; serve an older page that ends one message early or late and the walk-back test
/// goes red; cut a tail to the window and the window-plus-tails test goes red.
/// </summary>
public sealed class ConversationWindowTests
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private const string Tenant = "local";
    private const string Sid = "5b0e1c52-7f59-4c1e-9d1a-0b8b1f6d0a11";
    private const string Gen = "gen-a";

    private static HistoryMessageDto Prompt(int turn) => new()
    {
        Role = "User",
        Parts = { new HistoryPartDto { Kind = "Text", Text = $"prompt {turn}" } },
    };

    private static HistoryMessageDto ToolResult(int turn, int step) => new()
    {
        Role = "User",
        Parts = { new HistoryPartDto { Kind = "ToolResult", Text = $"result {turn}.{step}", ToolId = $"t{turn}.{step}" } },
    };

    private static HistoryMessageDto Reply(int turn, int step) => new()
    {
        Role = "Assistant",
        Parts =
        {
            new HistoryPartDto { Kind = "Text", Text = $"reply {turn}.{step}" },
            new HistoryPartDto { Kind = "ToolUse", Text = "{}", ToolName = "Bash", ToolId = $"t{turn}.{step}" },
        },
    };

    // A turn the way a real agent writes one: the prompt, then replies and tool results fed back - a varying number,
    // so a window that counted messages instead of turns would land mid-turn.
    private static List<HistoryMessageDto> Conversation(int turns, int firstTurn = 0)
    {
        var list = new List<HistoryMessageDto>();
        for (var t = firstTurn; t < firstTurn + turns; t++)
        {
            list.Add(Prompt(t));
            for (var s = 0; s < t % 4; s++)
            {
                list.Add(Reply(t, s));
                list.Add(ToolResult(t, s));
            }
            list.Add(Reply(t, 99));
        }
        return list;
    }

    private static SessionHistoryDto Full(List<HistoryMessageDto> messages) => new()
    {
        SessionId = Sid, DirectorId = "dir-1", Agent = "ClaudeCode", IsSupported = true, IsRawText = false,
        HistoryState = "Working", Messages = messages, Status = "ok",
    };

    private static string Json(IEnumerable<HistoryMessageDto> messages) => JsonSerializer.Serialize(messages.ToList(), Web);

    // The tail form, answered from a view of these messages as the endpoint answers it (issue #4387).
    private static JsonObject TailOf(SessionHistoryDto full, List<HistoryMessageDto> messages, string cursor, int? turns = null)
    {
        var view = ConversationView.Of(Tenant, Sid, Gen, messages, Web);
        return SessionConversationEndpoint.TailBody(full, view, ConversationTail.Decide(cursor, view), Web, turns);
    }

    private static List<HistoryMessageDto> MessagesOf(JsonObject body) =>
        body["messages"]!.Deserialize<List<HistoryMessageDto>>(Web)!;

    [Fact]
    public void ATurnStartsAtAPromptWithWords_NotAtAToolResultOrAnEmptyMessage()
    {
        Assert.True(ConversationWindow.StartsTurn(Prompt(1)));
        Assert.False(ConversationWindow.StartsTurn(ToolResult(1, 0)));
        Assert.False(ConversationWindow.StartsTurn(Reply(1, 0)));
        Assert.False(ConversationWindow.StartsTurn(new HistoryMessageDto
        {
            Role = "User", Parts = { new HistoryPartDto { Kind = "Text", Text = "   " } },
        }));
    }

    [Fact]
    public void AWindow_StartsExactlyAtTheTenthLastPrompt_AndRunsToTheEnd()
    {
        var messages = Conversation(25);

        var body = TailOf(Full(messages), messages, "", turns: 10);

        var from = (int)body["windowFrom"]!;
        var window = MessagesOf(body);
        Assert.Equal("prompt 15", window[0].Parts[0].Text);
        Assert.Equal(10, window.Count(ConversationWindow.StartsTurn));
        Assert.Equal(Json(messages.Skip(from)), Json(window));
        // The cursor still names the WHOLE conversation, not the window.
        Assert.Equal(ConversationTail.CursorFor(Tenant, Sid, Gen, messages, messages.Count, Web), (string)body["cursor"]!);
        Assert.Equal(0, (int)body["tailFrom"]!);
    }

    [Fact]
    public void AConversationWithFewerTurnsThanTheWindow_IsSentWhole()
    {
        var messages = Conversation(4);

        var body = TailOf(Full(messages), messages, "", turns: 10);

        Assert.Equal(0, (int)body["windowFrom"]!);
        Assert.Equal(Json(messages), Json(MessagesOf(body)));
    }

    [Fact]
    public void WithoutTurns_TheAnswerIsExactlyWhatAnOlderClientAlwaysGot()
    {
        var messages = Conversation(25);

        var body = TailOf(Full(messages), messages, "");

        Assert.Null(body["windowFrom"]);
        Assert.Equal(Json(messages), Json(MessagesOf(body)));
    }

    [Fact]
    public void TheWindowPlusEveryTailAfterIt_EqualsTheEndOfTheFullConversation()
    {
        var all = Conversation(40);
        var messages = all.Take(30).ToList();
        var first = TailOf(Full(messages), messages, "", turns: 10);
        var windowFrom = (int)first["windowFrom"]!;
        var held = MessagesOf(first);
        var cursor = (string)first["cursor"]!;

        foreach (var count in new[] { 31, 35, 35, 60, all.Count })
        {
            var now = all.Take(count).ToList();
            var answer = ConversationTail.Decide(cursor, Tenant, Sid, Gen, now, Web);
            var body = TailOf(Full(now), now, cursor, turns: 10);
            // A tail is what is new, never cut to the window, and carries no windowFrom.
            Assert.Equal(windowFrom + held.Count, (int)body["tailFrom"]!);
            Assert.Null(body["windowFrom"]);
            held.AddRange(MessagesOf(body));
            cursor = (string)body["cursor"]!;
        }

        Assert.Equal(Json(all.Skip(windowFrom)), Json(held));
    }

    [Fact]
    public void WalkingBackOlderPage_ByOlderPage_AssemblesTheWholeConversation()
    {
        var messages = Conversation(37);
        var first = TailOf(Full(messages), messages, "", turns: 10);
        var windowFrom = (int)first["windowFrom"]!;
        var held = MessagesOf(first);

        var pages = 0;
        while (windowFrom > 0)
        {
            var page = SessionConversationEndpoint.OlderBody(Full(messages), ConversationView.Of(Tenant, Sid, Gen, messages, Web), windowFrom, 10, Web);
            var olderFrom = (int)page["olderFrom"]!;
            var older = MessagesOf(page);
            Assert.True(olderFrom < windowFrom);
            Assert.Equal(windowFrom - olderFrom, older.Count);
            // Every page starts on a turn, so a turn is never split across two pages.
            Assert.True(olderFrom == 0 || ConversationWindow.StartsTurn(older[0]));
            // An older page moves nothing about how far the client holds the conversation.
            Assert.Null(page["cursor"]);
            Assert.Null(page["tailFrom"]);
            held.InsertRange(0, older);
            windowFrom = olderFrom;
            pages++;
        }

        Assert.Equal(3, pages);
        Assert.Equal(Json(messages), Json(held));
    }

    [Theory]
    [InlineData("0", null)]
    [InlineData("-3", null)]
    [InlineData("ten", null)]
    [InlineData("501", null)]
    [InlineData("10", "0")]
    [InlineData("10", "x")]
    [InlineData(null, "40")]
    public void AMalformedWindowParameter_IsRefused_NotAnsweredInFull(string? turns, string? before)
    {
        Assert.False(ConversationWindow.TryRead(turns, before, out _, out _, out var error));
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void NoWindowParameters_ReadsAsTheWholeConversation()
    {
        Assert.True(ConversationWindow.TryRead(null, null, out var turns, out var before, out var error));
        Assert.Null(turns);
        Assert.Null(before);
        Assert.Null(error);
    }
}
