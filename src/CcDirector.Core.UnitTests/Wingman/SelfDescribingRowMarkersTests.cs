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

    // ------------------------------------------------------------------------------------------
    // Every declared marker carries a row of its own
    // ------------------------------------------------------------------------------------------

    /// <summary>
    /// ONE ROW PER DECLARED MARKER, AND THAT MARKER IS THE ONLY REASON THE ROW IS SUPPRESSED.
    ///
    /// The theories above prove that some rows are suppressed; they do not prove that each ENTRY
    /// earns its place. Several of those rows carry two or three markers, so an entry could be
    /// deleted while a neighbour on the same row kept the test green, and two entries were
    /// exercised by nothing at all. Here each row carries exactly one declared marker, and the
    /// test removes that marker and requires the row to come back as gained content.
    ///
    /// WHERE THE ROWS COME FROM. Most are the evidence rows in the mission record
    /// (docs/missions/turn-detection-2026-09-15/chrome-markers-evidence.md). Three are not, and
    /// are marked below: where the corpus row carries several markers at once it is TRIMMED to the
    /// fragment under test, because what is being pinned is the entry rather than the screen; and
    /// the two entries the corpus never recorded - "auto-accept" and "IDE disconnected" - are
    /// written from the agent's own interface strings. Those two are the weakest lines in this
    /// table and are the first to delete if the list is ever cut back.
    /// </summary>
    public static TheoryData<AgentKind, string, string> OneRowPerMarker => new()
    {
        // Claude Code - from the corpus evidence, truncated as the terminal truncates it.
        { AgentKind.ClaudeCode, "new task?", "  new task? /clear to sav" },
        // Trimmed from "new task? /clear to save 23k tokens", which carries both markers.
        { AgentKind.ClaudeCode, "/clear to save", "  /clear to save 23k tokens" },
        { AgentKind.ClaudeCode, "ctrl+o to expand", "  +12 lines (ctrl+o to expand)" },
        { AgentKind.ClaudeCode, "esc to cancel", "  Continuing shortly - esc to cancel" },
        { AgentKind.ClaudeCode, "esc to interrupt", "  Herding for 41s (esc to interrupt)" },
        { AgentKind.ClaudeCode, "bypass permissions", "  >> bypass permissions on (shift+tab to cycle)" },
        // Not in the corpus evidence: the agent's own interface string.
        { AgentKind.ClaudeCode, "auto-accept", "  >> auto-accept edits on (shift+tab to cycle)" },
        { AgentKind.ClaudeCode, "context left", "  43% context left until auto-compact" },
        { AgentKind.ClaudeCode, "Checking for updates", "  Checking for updates..." },
        { AgentKind.ClaudeCode, "Update installed", "  Update installed - restart to apply" },
        // Not in the corpus evidence: the agent's own interface string.
        { AgentKind.ClaudeCode, "IDE disconnected", "  IDE disconnected - run /ide to reconnect" },

        // Codex - one physical row carries the first three, so each is trimmed to its fragment.
        { AgentKind.Codex, "background terminal running", "  background terminal running (npm run dev)" },
        { AgentKind.Codex, "/ps to view", "  /ps to view the output" },
        { AgentKind.Codex, "/stop to close", "  /stop to close it" },
        { AgentKind.Codex, "Token usage:", "  Token usage: total=12043 input=9821 output=2222" },
        { AgentKind.Codex, "To continue this session, run:", "  To continue this session, run:" },
        { AgentKind.Codex, "codex resume", "    codex resume 019bd3f0-1c2e-7abc-9d10-6f2b41c9e5aa" },
    };

    [Theory]
    [MemberData(nameof(OneRowPerMarker))]
    public void Each_declared_marker_is_the_only_reason_its_row_is_suppressed(
        AgentKind kind, string marker, string chromeRow)
    {
        var declared = AgentDrivers.For(kind).SelfDescribingRowMarkers;
        var settled = new[] { "  The deployment finished successfully." };
        var current = new[] { "  The deployment finished successfully.", chromeRow };

        Assert.Contains(marker, declared);
        Assert.False(TerminalContentNovelty.GainedContent(settled, current, declared, out _),
            $"this row would have turned a settled session blue: {chromeRow}");

        // The row must carry exactly ONE declared marker, or deleting that entry would leave a
        // neighbour holding the row up and this test would prove nothing about the entry.
        var alsoMatching = declared
            .Where(m => chromeRow.Contains(m, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert.Equal(new[] { marker }, alsoMatching);

        // And with that one entry gone, the row is new content again - so the entry is what is
        // doing the work, and deleting it cannot pass unnoticed.
        var without = declared.Where(m => m != marker).ToArray();
        Assert.True(TerminalContentNovelty.GainedContent(settled, current, without, out var row),
            $"removing the marker '{marker}' changed nothing, so nothing depends on it");
        Assert.Equal(chromeRow, row);
    }

    [Fact]
    public void No_declared_marker_is_left_without_a_row()
    {
        // The guard that keeps the table honest as the lists change: an entry added to a driver
        // and not to the table above fails here rather than shipping unexercised, which is how
        // "auto-accept" and "IDE disconnected" came to have no test at all.
        foreach (var kind in new[] { AgentKind.ClaudeCode, AgentKind.Codex })
        {
            foreach (var marker in AgentDrivers.For(kind).SelfDescribingRowMarkers)
            {
                Assert.True(
                    OneRowPerMarker.Any(row => Equals(row[0], kind) && Equals(row[1], marker)),
                    $"{kind} declares the marker '{marker}' and no row in this file depends on it");
            }
        }
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
