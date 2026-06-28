using CcDirector.Cockpit.Services;
using Xunit;

namespace CcDirector.Cockpit.Tests;

/// <summary>
/// Tests for <see cref="HistoryText.CleanForReading"/> - the cleaning pass that strips coding-agent
/// transcript machinery (command wrapper tags, system-reminder blocks, ANSI codes) out of the mobile
/// "basic history" view while leaving real prose and ordinary angle-bracket text intact.
/// </summary>
public class HistoryTextTests
{
    [Fact]
    public void CleanForReading_PlainProse_Unchanged()
    {
        const string text = "Can you move yourself onto the cc-director now?";
        Assert.Equal(text, HistoryText.CleanForReading(text));
    }

    [Fact]
    public void CleanForReading_OrdinaryAngleBrackets_Kept()
    {
        // List<string> and a generic comparison must survive - only KNOWN wrapper tags are stripped.
        const string text = "Return a List<string> and check a < b in the loop.";
        Assert.Equal(text, HistoryText.CleanForReading(text));
    }

    [Fact]
    public void CleanForReading_SlashCommandWrapper_KeepsOnlyTheCommand()
    {
        var raw = "<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args></command-args>";
        Assert.Equal("/compact", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_LocalCommandCaveat_Dropped()
    {
        var raw = "<local-command-caveat>Caveat: do not respond to these messages.</local-command-caveat>";
        Assert.Equal("", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_AnsiEscapeCodes_Stripped()
    {
        // Real ESC ("") color sequences as they appear in terminal stdout.
        var raw = "[2mCompacted (ctrl+o to see full summary)[22m";
        Assert.Equal("Compacted (ctrl+o to see full summary)", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_SystemReminderBlock_Dropped()
    {
        var raw = "Real question here.\n<system-reminder>Big injected context block.</system-reminder>";
        Assert.Equal("Real question here.", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_StrayUnclosedTag_Removed()
    {
        // A block truncated mid-way loses its close tag; the stray open tag must still be removed.
        var raw = "<system-reminder>truncated context that was cut off";
        Assert.Equal("truncated context that was cut off", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_TaskNotificationBlock_Dropped()
    {
        var raw = "Working on it.\n<task-notification><task-id>abc</task-id><status>completed</status><summary>done</summary></task-notification>";
        Assert.Equal("Working on it.", HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_PlaceholderTag_WrappedAsInlineCode()
    {
        // A standalone placeholder reads as broken HTML; wrapping it in backticks renders a tidy chip.
        Assert.Equal("Type `<your reply>` here.", HistoryText.CleanForReading("Type <your reply> here."));
    }

    [Fact]
    public void CleanForReading_PlaceholderTagsWithHashAndSlash_Wrapped()
    {
        Assert.Equal("File `<issue#>` then `</task-id>`.",
            HistoryText.CleanForReading("File <issue#> then </task-id>."));
    }

    [Fact]
    public void CleanForReading_GenericGluedToWord_NotWrapped()
    {
        // List<string> is real code: the "<" is glued to a word, so it is left exactly as-is.
        Assert.Equal("Return a List<string>.", HistoryText.CleanForReading("Return a List<string>."));
    }

    [Fact]
    public void CleanForReading_TagInsideFencedCode_Untouched()
    {
        var raw = "Example:\n```\nvar x = foo<bar>();\n```";
        var result = HistoryText.CleanForReading(raw);
        // The fenced code block must be preserved verbatim - no backticks injected into the code.
        Assert.Contains("var x = foo<bar>();", result);
    }

    [Fact]
    public void CleanForReading_TagInsideInlineCodeSpan_Untouched()
    {
        // A placeholder already inside an inline code span is the agent's deliberate code: leaving it
        // exactly as-is keeps the span intact (wrapping it would inject stray backticks and break it).
        var raw = "Run `/implementation-loop <issue#>` now.";
        Assert.Equal(raw, HistoryText.CleanForReading(raw));
    }

    [Fact]
    public void CleanForReading_NullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal("", HistoryText.CleanForReading(null));
        Assert.Equal("", HistoryText.CleanForReading(""));
    }
}
