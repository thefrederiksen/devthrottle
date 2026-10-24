using CcDirector.Core.Claude;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

public sealed class ClaudeStateHookEventParserTests
{
    public static TheoryData<string, string> CapturedPayloads => new()
    {
        { "claude-code-2.1.302-user-prompt-submit.json", "turn_started" },
        { "claude-code-2.1.302-stop.json", "turn_ended" },
        { "claude-code-2.1.302-notification-permission.json", "permission_requested" },
    };

    [Theory]
    [MemberData(nameof(CapturedPayloads))]
    public void Parse_CapturedClaudeCodePayload_ReturnsContractFact(string fileName, string expected)
    {
        var body = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "claude-state-hooks", fileName));

        var result = ClaudeStateHookEventParser.Parse(body);

        Assert.Equal(expected, result.Event);
        Assert.Null(result.UnsupportedReason);
    }

    [Theory]
    [InlineData("PreToolUse", "AskUserQuestion", "menu_opened")]
    [InlineData("PostToolUse", "AskUserQuestion", "turn_started")]
    [InlineData("PostToolUseFailure", "AskUserQuestion", "turn_started")]
    [InlineData("PreToolUse", "Bash", "turn_started")]
    [InlineData("PostToolUse", "Bash", "turn_started")]
    public void Parse_ToolLifecyclePayload_ReturnsToolOrMenuFact(string hook, string tool, string expected)
    {
        var body = $$"""{"hook_event_name":"{{hook}}","tool_name":"{{tool}}"}""";

        var result = ClaudeStateHookEventParser.Parse(body);

        Assert.Equal(expected, result.Event);
        Assert.Null(result.UnsupportedReason);
    }

    [Theory]
    [InlineData("{}", "no hook_event_name")]
    [InlineData("{\"hook_event_name\":\"SessionEnd\"}", "not supported by terminal-state contract version 1")]
    [InlineData("{\"hook_event_name\":\"Notification\",\"notification_type\":\"auth_success\"}", "notification type 'auth_success'")]
    public void Parse_UnsupportedPayload_ReturnsExplicitReason(string body, string expectedReason)
    {
        var result = ClaudeStateHookEventParser.Parse(body);

        Assert.Null(result.Event);
        Assert.NotNull(result.UnsupportedReason);
        Assert.Contains(expectedReason, result.UnsupportedReason, StringComparison.Ordinal);
    }
}
