using CcDirector.Core.Agents;
using CcDirector.Core.Drivers;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Work item three of the turn-detection phase: the rows an agent's own interface draws about
/// itself are per-agent knowledge, so they are a driver trait. This mirrors
/// <c>ContinuousIdleStateTests</c>, which does the same for the other terminal-behaviour trait.
///
/// The interesting assertions here are not "the lists are not empty". They are that every other
/// agent inherits EMPTY - because a guessed list would suppress an agent's real output and nobody
/// has the evidence to write one - and that each marker actually suppresses the row it was written
/// for when run through the real rule, which is what stops the list being a collection of strings
/// nobody ever checked against a screen.
/// </summary>
public sealed class SelfDescribingRowMarkersTests
{
    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    public void The_two_measured_agents_declare_markers(AgentKind kind)
    {
        Assert.NotEmpty(AgentDrivers.For(kind).SelfDescribingRowMarkers);
    }

    [Theory]
    [InlineData(AgentKind.Gemini)]
    [InlineData(AgentKind.OpenCode)]
    [InlineData(AgentKind.Pi)]
    [InlineData(AgentKind.Copilot)]
    [InlineData(AgentKind.Cursor)]
    [InlineData(AgentKind.Grok)]
    public void Every_other_agent_inherits_the_empty_default(AgentKind kind)
    {
        // Not an oversight. The evidence behind the two lists above is one machine, where every
        // other supported agent had no measured cases at all. An empty list leaves an agent
        // exactly as well off as it is today; a guessed one could swallow its real replies.
        Assert.Empty(AgentDrivers.For(kind).SelfDescribingRowMarkers);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    public void Markers_are_plain_ascii_and_long_enough_not_to_match_prose(AgentKind kind)
    {
        foreach (var marker in AgentDrivers.For(kind).SelfDescribingRowMarkers)
        {
            Assert.False(string.IsNullOrWhiteSpace(marker));
            foreach (char c in marker)
                Assert.InRange(c, ' ', '~');

            // A marker short enough to fall inside an ordinary word would suppress real replies,
            // which is the one failure mode that costs the owner a turn.
            Assert.True(marker.Trim().Length >= 8, $"marker too short to be safe: {marker}");
        }
    }

    /// <summary>
    /// The rows the markers were written for, reproduced from the evidence file in the mission
    /// record. Only interface strings appear here; the conversation content those screens also
    /// carried is real client work and stays in the private repository.
    /// </summary>
    [Theory]
    [InlineData("  new task? /clear to save 23k tokens")]
    [InlineData("  new task? /clear to sav")]
    [InlineData("  +12 lines (ctrl+o to expand)")]
    [InlineData("  Continuing shortly - esc to cancel")]
    [InlineData("  >> bypass permissions on (shift+tab to cycle)")]
    [InlineData("  Herding for 41s (esc to interrupt)")]
    [InlineData("  Checking for updates...")]
    [InlineData("  Update installed - restart to apply")]
    [InlineData("  43% context left until auto-compact")]
    public void A_claude_code_chrome_row_never_counts_as_gained_content(string chromeRow)
    {
        var markers = AgentDrivers.For(AgentKind.ClaudeCode).SelfDescribingRowMarkers;
        var settled = new[] { "  The deployment finished successfully." };
        var current = new[] { "  The deployment finished successfully.", chromeRow };

        Assert.False(TerminalContentNovelty.GainedContent(settled, current, markers, out var row),
            $"this row would have turned a settled session blue: {chromeRow}");
        Assert.Null(row);
    }

    [Theory]
    [InlineData("  background terminal running - /ps to view - /stop to close")]
    [InlineData("  Token usage: total=12043 input=9821 output=2222")]
    [InlineData("  To continue this session, run:")]
    [InlineData("    codex resume 019bd3f0-1c2e-7abc-9d10-6f2b41c9e5aa")]
    public void A_codex_chrome_row_never_counts_as_gained_content(string chromeRow)
    {
        var markers = AgentDrivers.For(AgentKind.Codex).SelfDescribingRowMarkers;
        var settled = new[] { "  The deployment finished successfully." };
        var current = new[] { "  The deployment finished successfully.", chromeRow };

        Assert.False(TerminalContentNovelty.GainedContent(settled, current, markers, out _),
            $"this row would have turned a settled session blue: {chromeRow}");
    }

    [Theory]
    [InlineData("  I have finished the refactor and the suite is green.")]
    [InlineData("  The migration completed with two warnings, both about indexes.")]
    [InlineData("  I refactored for clarity and left the behaviour alone.")]
    [InlineData("  Updated the installer so it no longer asks twice.")]
    public void A_real_reply_still_counts_with_the_full_marker_list_active(string replyRow)
    {
        // The control, and the more important half. Two of these are written to sit close to a
        // marker on purpose: "I refactored for clarity" carries the fragment the thinking-line
        // marker would have used, and "Updated the installer" sits beside "Update installed".
        // Both must still open the turn.
        var markers = AgentDrivers.For(AgentKind.ClaudeCode).SelfDescribingRowMarkers;
        var settled = new[] { "  The deployment finished successfully." };
        var current = new[] { "  The deployment finished successfully.", replyRow };

        Assert.True(TerminalContentNovelty.GainedContent(settled, current, markers, out var row),
            $"a real reply was swallowed by the marker list: {replyRow}");
        Assert.Equal(replyRow, row);
    }
}
