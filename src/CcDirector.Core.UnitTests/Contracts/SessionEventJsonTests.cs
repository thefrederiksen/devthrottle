using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts.ApiMode;
using Xunit;

namespace CcDirector.Core.UnitTests.Contracts;

/// <summary>
/// The session event model on the wire (API mode, issue #4070). The Director writes these events and the
/// Gateway reads them, so the field names and enumeration words are a contract: the ones written in
/// docs/missions/api-mode-pi-2026-10-16/event-model.md, section 2. These pin that every event serialises
/// under those names, that a list of mixed events comes back as the same events, and that a record the
/// model does not define is refused rather than read as something else.
/// </summary>
public sealed class SessionEventJsonTests
{
    private static readonly JsonSerializerOptions Json = SessionEventJson.Options;
    private static readonly DateTimeOffset At = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);

    /// <summary>One of every event, with every optional field filled, in the order a real session could
    /// produce them.</summary>
    private static List<SessionEvent> OneOfEach() =>
    [
        new ConnectionReady { SessionId = "s1", Sequence = 1, At = At, AgentSessionId = "pi-1", Model = "gpt-5.6-sol" },
        new PromptQueued { SessionId = "s1", Sequence = 2, At = At, PromptId = "p1", Reason = PromptQueuedReason.AgentStarting, Position = 1 },
        new PromptDelivered
        {
            SessionId = "s1", Sequence = 3, At = At, PromptId = "p1", Disposition = PromptDisposition.Started,
            Acknowledgement = "{\"id\":\"req-1\",\"type\":\"response\",\"command\":\"prompt\",\"success\":true,\"data\":{\"disposition\":\"started\"}}",
        },
        new TurnStarted { SessionId = "s1", Sequence = 4, At = At, TurnId = "t1", PromptIds = ["p1"] },
        new MessageText { SessionId = "s1", Sequence = 5, At = At, TurnId = "t1", MessageId = "m1", Channel = MessageTextChannel.Thought, Kind = MessageTextKind.Delta, Text = "Thinking" },
        new ToolCallStarted { SessionId = "s1", Sequence = 6, At = At, TurnId = "t1", ToolCallId = "c1", ToolName = "bash", Kind = ToolCallKind.Execute, Input = "{\"command\":\"ls\"}" },
        new PermissionRequested
        {
            SessionId = "s1", Sequence = 7, At = At, TurnId = "t1", RequestId = "r1", ToolCallId = "c1", Title = "Run ls?",
            Options =
            [
                new PermissionOption { OptionId = "o1", Name = "Allow", Kind = PermissionOptionKind.AllowOnce },
                new PermissionOption { OptionId = "o2", Name = "Reject", Kind = PermissionOptionKind.RejectAlways },
            ],
        },
        new ToolCallFinished { SessionId = "s1", Sequence = 8, At = At, TurnId = "t1", ToolCallId = "c1", Outcome = ToolCallOutcome.Succeeded, Output = "a.txt\n", DurationMilliseconds = 12 },
        new MessageText { SessionId = "s1", Sequence = 9, At = At, TurnId = "t1", MessageId = "m2", Channel = MessageTextChannel.Reply, Kind = MessageTextKind.Final, Text = "PONG" },
        new TurnEnded
        {
            SessionId = "s1", Sequence = 10, At = At, TurnId = "t1", Outcome = TurnOutcome.Done, Reason = null, AgentStopReason = "stop",
            Usage = new TurnUsage { Input = 3, Output = 8, CacheRead = 3871, CacheWrite = 26 },
        },
        new PromptRefused { SessionId = "s1", Sequence = 11, At = At, PromptId = "p2", Reason = "Agent is already processing.", RefusedBy = PromptRefusedBy.Agent },
        new ConnectionEnded { SessionId = "s1", Sequence = 12, At = At, ExitCode = 137, Reason = "Pi exited (exit code 137)", StderrTail = ["line one", "line two"] },
    ];

    [Fact]
    public void Serialize_MixedList_RoundTripsToTheSameEvents()
    {
        // Arrange
        var events = OneOfEach();

        // Act
        var json = JsonSerializer.Serialize(events, Json);
        var back = JsonSerializer.Deserialize<List<SessionEvent>>(json, Json)!;

        // Assert: the same concrete type in the same place, and the same JSON when written again. Records with
        // list fields compare lists by reference, so the JSON is the equality that covers every field.
        Assert.Equal(events.Select(e => e.GetType()), back.Select(e => e.GetType()));
        Assert.Equal(json, JsonSerializer.Serialize(back, Json));
        var permission = Assert.IsType<PermissionRequested>(back[6]);
        Assert.Equal(PermissionOptionKind.RejectAlways, permission.Options[1].Kind);
        Assert.Equal(At, back[0].At);
    }

    [Theory]
    [InlineData(0, "ConnectionReady", "sessionId,sequence,at,agentSessionId,model")]
    [InlineData(1, "PromptQueued", "sessionId,sequence,at,promptId,reason,position")]
    [InlineData(2, "PromptDelivered", "sessionId,sequence,at,promptId,disposition,acknowledgement")]
    [InlineData(3, "TurnStarted", "sessionId,sequence,at,turnId,promptIds")]
    [InlineData(4, "MessageText", "sessionId,sequence,at,turnId,messageId,channel,kind,text")]
    [InlineData(5, "ToolCallStarted", "sessionId,sequence,at,turnId,toolCallId,toolName,kind,input")]
    [InlineData(6, "PermissionRequested", "sessionId,sequence,at,turnId,requestId,toolCallId,title,options")]
    [InlineData(7, "ToolCallFinished", "sessionId,sequence,at,turnId,toolCallId,outcome,output,durationMilliseconds")]
    [InlineData(9, "TurnEnded", "sessionId,sequence,at,turnId,outcome,reason,agentStopReason,usage")]
    [InlineData(10, "PromptRefused", "sessionId,sequence,at,promptId,reason,refusedBy")]
    [InlineData(11, "ConnectionEnded", "sessionId,sequence,at,exitCode,reason,stderrTail")]
    public void Serialize_EachEvent_WritesTheDiscriminatorFirstThenTheEventModelFieldNames(int index, string type, string fields)
    {
        // Arrange
        SessionEvent e = OneOfEach()[index];

        // Act
        var node = JsonNode.Parse(JsonSerializer.Serialize(e, Json))!.AsObject();

        // Assert
        var names = node.Select(p => p.Key).ToList();
        Assert.Equal("type", names[0]);
        Assert.Equal(type, (string)node["type"]!);
        Assert.Equal(fields.Split(',').OrderBy(n => n, StringComparer.Ordinal), names.Skip(1).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void Serialize_Enumerations_WritesTheEventModelWords()
    {
        // Arrange
        var events = OneOfEach();

        // Act
        var nodes = events.Select(e => JsonNode.Parse(JsonSerializer.Serialize(e, Json))!.AsObject()).ToList();

        // Assert
        Assert.Equal("agentStarting", (string)nodes[1]["reason"]!);
        Assert.Equal("started", (string)nodes[2]["disposition"]!);
        Assert.Equal("thought", (string)nodes[4]["channel"]!);
        Assert.Equal("delta", (string)nodes[4]["kind"]!);
        Assert.Equal("execute", (string)nodes[5]["kind"]!);
        Assert.Equal("allowOnce", (string)nodes[6]["options"]![0]!["kind"]!);
        Assert.Equal("rejectAlways", (string)nodes[6]["options"]![1]!["kind"]!);
        Assert.Equal("succeeded", (string)nodes[7]["outcome"]!);
        Assert.Equal("final", (string)nodes[8]["kind"]!);
        Assert.Equal("reply", (string)nodes[8]["channel"]!);
        Assert.Equal("done", (string)nodes[9]["outcome"]!);
        Assert.Equal("agent", (string)nodes[10]["refusedBy"]!);
        var usage = nodes[9]["usage"]!.AsObject();
        Assert.Equal(["input", "output", "cacheRead", "cacheWrite"], usage.Select(p => p.Key));
    }

    [Theory]
    [InlineData(typeof(PromptQueuedReason), "userHasTheWheel,turnRunning,agentStarting")]
    [InlineData(typeof(PromptDisposition), "started,queuedByAgent,handled")]
    [InlineData(typeof(PromptRefusedBy), "agent,director")]
    [InlineData(typeof(MessageTextChannel), "reply,thought")]
    [InlineData(typeof(MessageTextKind), "delta,final")]
    [InlineData(typeof(ToolCallKind), "read,edit,execute,search,fetch,other")]
    [InlineData(typeof(ToolCallOutcome), "succeeded,failed,cancelled")]
    [InlineData(typeof(TurnOutcome), "done,cancelled,failed")]
    [InlineData(typeof(PermissionOptionKind), "allowOnce,allowAlways,rejectOnce,rejectAlways")]
    public void Serialize_EveryEnumerationValue_IsExactlyTheEventModelList(Type enumType, string words)
    {
        // Act
        var written = Enum.GetValues(enumType).Cast<object>()
            .Select(v => JsonSerializer.Deserialize<string>(JsonSerializer.Serialize(v, enumType, Json))!)
            .ToList();

        // Assert
        Assert.Equal(words.Split(','), written);
    }

    [Fact]
    public void Deserialize_UnknownEventType_Throws()
    {
        // Arrange
        const string json = """{"type":"TurnPaused","sessionId":"s1","sequence":1,"at":"2026-10-16T12:00:00+00:00"}""";

        // Act + Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, Json));
    }

    [Fact]
    public void Deserialize_MissingRequiredField_Throws()
    {
        // Arrange: a PromptRefused with no reason - a receipt that cannot say why must not read as one that can.
        const string json = """{"type":"PromptRefused","sessionId":"s1","sequence":1,"at":"2026-10-16T12:00:00+00:00","promptId":"p1","refusedBy":"agent"}""";

        // Act + Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, Json));
    }

    [Fact]
    public void Deserialize_UnknownEnumerationWord_Throws()
    {
        // Arrange
        const string json = """{"type":"TurnEnded","sessionId":"s1","sequence":1,"at":"2026-10-16T12:00:00+00:00","turnId":"t1","outcome":"paused","reason":null,"agentStopReason":null,"usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""";

        // Act + Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, Json));
    }

    /// <summary>Rewrites an object's keys the way PostgreSQL <c>jsonb</c> returns them: shortest first, then
    /// bytewise, at every level. That puts <c>at</c> ahead of <c>type</c> on every event.</summary>
    private static JsonNode? JsonbOrder(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => Encoding.UTF8.GetByteCount(p.Key)).ThenBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, JsonbOrder(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(i => JsonbOrder(i?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };

    [Fact]
    public void Deserialize_KeysInTheOrderJsonbReturnsThem_ReadsBackEveryEvent()
    {
        // Arrange: the events as the Gateway's jsonb storage hands them back, with "type" no longer first.
        var events = OneOfEach();
        var json = JsonSerializer.Serialize(events, Json);
        var stored = JsonbOrder(JsonNode.Parse(json))!.ToJsonString();
        Assert.StartsWith("""[{"at":""", stored);

        // Act
        var back = JsonSerializer.Deserialize<List<SessionEvent>>(stored, Json)!;

        // Assert
        Assert.Equal(events.Select(e => e.GetType()), back.Select(e => e.GetType()));
        Assert.Equal(json, JsonSerializer.Serialize(back, Json));
    }

    [Fact]
    public void Deserialize_DiscriminatorNotFirstWithoutTheSharedOptions_Throws()
    {
        // Arrange: why SessionEventJson.Options exists - a reader with default options cannot read a stored event.
        const string json = """{"at":"2026-10-16T12:00:00+00:00","type":"PromptRefused","reason":"busy","sequence":1,"promptId":"p1","refusedBy":"agent","sessionId":"s1"}""";

        // Act + Assert
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<SessionEvent>(json));
        Assert.IsType<PromptRefused>(JsonSerializer.Deserialize<SessionEvent>(json, Json));
    }

    [Fact]
    public void Deserialize_NumberInPlaceOfAnEnumerationWord_Throws()
    {
        // Arrange: 7 names no outcome; read as a number it would reach the Gateway's fold as an undefined value.
        const string json = """{"type":"TurnEnded","sessionId":"s1","sequence":1,"at":"2026-10-16T12:00:00+00:00","turnId":"t1","outcome":7,"reason":null,"agentStopReason":null,"usage":{"input":0,"output":0,"cacheRead":0,"cacheWrite":0}}""";

        // Act + Assert
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SessionEvent>(json, Json));
    }

    [Fact]
    public void Options_AreReadOnly()
    {
        // Act + Assert: one caller cannot change how every other caller reads.
        Assert.True(Json.IsReadOnly);
        Assert.Throws<InvalidOperationException>(() => Json.WriteIndented = true);
    }
}
