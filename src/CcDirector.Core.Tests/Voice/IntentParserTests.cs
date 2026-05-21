using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Coverage for the regex-based intent parser used by the Director's voice mode.
/// Each test corresponds to one of the six v1 intents plus the Unknown fallback.
/// Tests exercise multiple phrasings per intent so we can extend the rules without
/// silently breaking previously-supported phrasings.
/// </summary>
public sealed class IntentParserTests
{
    [Theory]
    [InlineData("what sessions are running")]
    [InlineData("What sessions are running?")]
    [InlineData("list sessions")]
    [InlineData("List sessions.")]
    [InlineData("what's running")]
    [InlineData("what is running")]
    [InlineData("show me my sessions")]
    [InlineData("show sessions")]
    public void Parse_ListSessions(string transcript)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.ListSessions, cmd.Intent);
        Assert.Null(cmd.Target);
        Assert.Null(cmd.Payload);
    }

    [Theory]
    [InlineData("what's waiting")]
    [InlineData("what is waiting")]
    [InlineData("what's pending")]
    [InlineData("what is pending")]
    [InlineData("what needs me")]
    [InlineData("who's waiting")]
    [InlineData("who is waiting?")]
    public void Parse_ListWaiting(string transcript)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.ListWaiting, cmd.Intent);
        Assert.Null(cmd.Target);
        Assert.Null(cmd.Payload);
    }

    [Theory]
    [InlineData("what is pi doing", "pi")]
    [InlineData("what's pi doing", "pi")]
    [InlineData("what's pi up to", "pi")]
    [InlineData("what is cc-director working on", "cc-director")]
    [InlineData("tell me about pi", "pi")]
    [InlineData("Tell me about the chat session.", "the chat session")]
    public void Parse_DescribeSession(string transcript, string expectedTarget)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.DescribeSession, cmd.Intent);
        Assert.Equal(expectedTarget, cmd.Target?.Trim(), ignoreCase: true);
        Assert.Null(cmd.Payload);
    }

    [Theory]
    [InlineData("open pi", "pi")]
    [InlineData("Open pi.", "pi")]
    [InlineData("switch to pi", "pi")]
    [InlineData("show me pi", "pi")]
    [InlineData("show pi", "pi")]
    [InlineData("open the chat session", "the chat session")]
    public void Parse_OpenSession(string transcript, string expectedTarget)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.OpenSession, cmd.Intent);
        Assert.Equal(expectedTarget, cmd.Target?.Trim(), ignoreCase: true);
        Assert.Null(cmd.Payload);
    }

    [Theory]
    [InlineData("send to pi: run the tests", "pi", "run the tests")]
    [InlineData("send to pi, run the tests", "pi", "run the tests")]
    [InlineData("tell pi to run the tests", "pi", "run the tests")]
    [InlineData("Send to chat: write a haiku.", "chat", "write a haiku")]
    public void Parse_SendToSession(string transcript, string expectedTarget, string expectedPayload)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.SendToSession, cmd.Intent);
        Assert.Equal(expectedTarget, cmd.Target?.Trim(), ignoreCase: true);
        Assert.Equal(expectedPayload, cmd.Payload?.Trim(), ignoreCase: true);
    }

    [Theory]
    [InlineData("interrupt pi", "pi")]
    [InlineData("Interrupt pi.", "pi")]
    [InlineData("stop pi", "pi")]
    [InlineData("cancel pi", "pi")]
    public void Parse_InterruptSession(string transcript, string expectedTarget)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.InterruptSession, cmd.Intent);
        Assert.Equal(expectedTarget, cmd.Target?.Trim(), ignoreCase: true);
        Assert.Null(cmd.Payload);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("hello there")]
    [InlineData("the weather is nice today")]
    [InlineData("um... I don't know what to say")]
    public void Parse_Unknown(string transcript)
    {
        var cmd = IntentParser.Parse(transcript);
        Assert.Equal(VoiceIntent.Unknown, cmd.Intent);
    }

    [Fact]
    public void Parse_handles_trailing_punctuation_and_whitespace()
    {
        var cases = new[]
        {
            "   list sessions   ",
            "list sessions.",
            "list sessions?",
            "list sessions!",
        };
        foreach (var transcript in cases)
        {
            var cmd = IntentParser.Parse(transcript);
            Assert.Equal(VoiceIntent.ListSessions, cmd.Intent);
        }
    }
}
