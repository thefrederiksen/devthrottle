using CcDirector.DeliveryQualification;
using Xunit;

namespace CcDirector.DeliveryQualification.Tests;

public sealed class ConformanceTests
{
    [Fact]
    public void FixtureName_AgentVersionAndState_IsStableAndPortable()
    {
        var name = Conformance.FixtureName("Claude", "2.1.310 (Claude Code)", "model picker");

        Assert.Equal("claude-2.1.310-claude-code-model-picker.json", name);
    }

    [Fact]
    public void Expectations_EveryApplicableAgentState_HasAnAnswer()
    {
        foreach (var agent in new[] { "claude", "codex", "pi" })
        foreach (var state in ConformanceExpectations.States.Where(state => ConformanceExpectations.Applies(agent, state)))
        {
            var expected = ConformanceExpectations.For(agent, state);

            Assert.NotEmpty(expected.Composer);
            Assert.NotEmpty(expected.Activity);
        }
    }

    [Fact]
    public void ValidateComplete_MissingApplicableState_RefusesToRecordVersion()
    {
        var fixture = ModelPicker("2.1.310 (Claude Code)", "Enter to set as default · s to use this session only · Esc to cancel");

        var error = Assert.Throws<InvalidDataException>(() => Conformance.ValidateComplete([fixture]));

        Assert.Contains("Refusing to record", error.Message);
        Assert.Contains("idle", error.Message);
    }

    [Fact]
    public void Judge_CurrentClaudeModelPicker_Passes()
    {
        var fixture = ModelPicker("2.1.310 (Claude Code)", "Enter to set as default · s to use this session only · Esc to cancel");

        var verdict = Conformance.Judge(fixture);

        Assert.True(verdict.Pass, string.Join("; ", verdict.Differences));
    }

    [Fact]
    public void Judge_OlderClaudeModelPickerWhoseControlStringChanged_FailsLoudly()
    {
        var fixture = ModelPicker("2.1.293 (Claude Code)", "Enter to confirm · Esc to cancel");

        var verdict = Conformance.Judge(fixture);

        Assert.False(verdict.Pass);
        Assert.Contains(verdict.Differences, difference => difference.Contains("enter-to-set-default", StringComparison.Ordinal));
    }

    private static ConformanceFixture ModelPicker(string version, string footer)
    {
        var rows = new[]
        {
            "   Select model",
            "   ❯ 1. Default",
            "     2. Other",
            "",
            "   " + footer,
        };
        return new ConformanceFixture("claude", version, "model-picker", DateTime.UnixEpoch, 1, 3, false, rows, null,
            new ConformanceAnswer("MenuOpen", "", true, true, false, "MenuOpen", []));
    }
}
