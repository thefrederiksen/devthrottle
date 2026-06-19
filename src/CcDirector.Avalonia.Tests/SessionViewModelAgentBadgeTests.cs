using Avalonia.Media;
using CcDirector.Avalonia;
using CcDirector.Core.Agents;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Regression tests for the session-rail agent badge (issue #517). The Cursor provider was
/// wired through the factory, New Session dialog, and drivers, but the badge mapping in
/// <see cref="SessionViewModel"/> had no <see cref="AgentKind.Cursor"/> arm, so a session
/// running <c>cursor-agent</c> fell through to the Claude default and showed a blue
/// "Claude Code" badge in the SESSIONS rail. These tests pin the per-provider label and
/// brush so that can never silently regress again.
///
/// Pure mapping tests against <see cref="SessionViewModel.LabelFor"/> /
/// <see cref="SessionViewModel.BadgeBrushFor"/> - no Avalonia window or live Session is built.
/// </summary>
public sealed class SessionViewModelAgentBadgeTests
{
    [Fact]
    public void LabelFor_Cursor_IsCursor_NotClaudeCode()
    {
        Assert.Equal("Cursor", SessionViewModel.LabelFor(AgentKind.Cursor));
        Assert.NotEqual("Claude Code", SessionViewModel.LabelFor(AgentKind.Cursor));
    }

    [Fact]
    public void BadgeBrushFor_Cursor_HasOwnBrush_NotClaudeBlue()
    {
        var cursor = Assert.IsType<SolidColorBrush>(SessionViewModel.BadgeBrushFor(AgentKind.Cursor));
        var claude = Assert.IsType<SolidColorBrush>(SessionViewModel.BadgeBrushFor(AgentKind.ClaudeCode));

        // Cursor's own cyan badge (#06B6D4), distinct from the Claude default (#2563EB).
        Assert.Equal(Color.FromRgb(0x06, 0xB6, 0xD4), cursor.Color);
        Assert.NotEqual(claude.Color, cursor.Color);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode, "Claude Code")]
    [InlineData(AgentKind.Pi, "Pi")]
    [InlineData(AgentKind.Codex, "Codex")]
    [InlineData(AgentKind.Gemini, "Gemini")]
    [InlineData(AgentKind.OpenCode, "OpenCode")]
    [InlineData(AgentKind.Cursor, "Cursor")]
    [InlineData(AgentKind.RawCli, "Custom CLI")]
    public void LabelFor_EveryProvider_HasItsOwnLabel(AgentKind kind, string expected)
    {
        Assert.Equal(expected, SessionViewModel.LabelFor(kind));
    }

    [Fact]
    public void BadgeBrushFor_EveryProvider_HasADistinctColor()
    {
        AgentKind[] kinds =
        {
            AgentKind.ClaudeCode, AgentKind.Pi, AgentKind.Codex,
            AgentKind.Gemini, AgentKind.OpenCode, AgentKind.Cursor, AgentKind.RawCli,
        };

        var colors = new HashSet<Color>();
        foreach (var kind in kinds)
        {
            var brush = Assert.IsType<SolidColorBrush>(SessionViewModel.BadgeBrushFor(kind));
            Assert.True(colors.Add(brush.Color), $"Brush color for {kind} is not distinct from another provider's.");
        }
    }
}
