using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Deterministic (no-LLM) regression for <see cref="ClaudeScreenReader"/> against the real
/// Claude Code screen captures under docs/features/terminal-state-detector/fixtures. This
/// is the terminal-confirmation half of finish detection (docs/wingman/REDESIGN.md); it
/// must read working-vs-parked from positive evidence on every fixture, with no false
/// turn-ends. Unlike the LLM classifier regression, this is free and runs every build.
/// </summary>
public sealed class ClaudeScreenReaderTests
{
    private static string[] Fixture(string name)
        => File.ReadAllText(Path.Combine(FixturesDir(), name)).Replace("\r\n", "\n").Split('\n');

    [Theory]
    [InlineData("working_spinner.txt", ScreenParkState.Working)]
    [InlineData("waiting_input_bypass_footer.txt", ScreenParkState.ParkedForInput)]
    [InlineData("cancelled.txt", ScreenParkState.ParkedForInput)]
    [InlineData("plan_mode_input.txt", ScreenParkState.ParkedForInput)]
    [InlineData("permission_box.txt", ScreenParkState.ParkedForPermission)]
    // Captured REAL from Claude Code v2.1.150 (live slot-4 run): idle shows only the mode
    // footer (no "? for shortcuts"); working appends "esc to interrupt" to that same footer.
    [InlineData("real_v2150_working.txt", ScreenParkState.Working)]
    [InlineData("real_v2150_idle.txt", ScreenParkState.ParkedForInput)]
    public void Read_classifies_every_fixture(string file, ScreenParkState expected)
    {
        var state = ClaudeScreenReader.Read(Fixture(file));
        Assert.Equal(expected, state);
    }

    [Fact]
    public void Working_footer_beats_everything_else()
    {
        // A spinner footer present alongside leftover idle text must still read Working -
        // the turn is not over until the working footer is gone.
        var rows = new[] { "> some leftover prompt text", "? for shortcuts", "* Building... (esc to interrupt)" };
        Assert.Equal(ScreenParkState.Working, ClaudeScreenReader.Read(rows));
    }

    [Fact]
    public void Plan_numbered_list_is_input_not_permission()
    {
        // Regression for the one ambiguous case: a plan's "1. 2. 3." list must NOT be read
        // as a permission gate, because the idle hint is present.
        var rows = new[]
        {
            "  Here is the plan:",
            "  1. Do the first thing.",
            "  2. Do the second thing.",
            "  3. Do the third thing.",
            "> _",
            "  ? for shortcuts                 plan mode on (shift+tab to cycle)",
        };
        Assert.Equal(ScreenParkState.ParkedForInput, ClaudeScreenReader.Read(rows));
    }

    [Fact]
    public void Empty_or_null_is_unknown_never_parked()
    {
        Assert.Equal(ScreenParkState.Unknown, ClaudeScreenReader.Read(null));
        Assert.Equal(ScreenParkState.Unknown, ClaudeScreenReader.Read(Array.Empty<string>()));
        Assert.False(ClaudeScreenReader.IsParked(ScreenParkState.Unknown));
        Assert.False(ClaudeScreenReader.IsParked(ScreenParkState.Working));
        Assert.True(ClaudeScreenReader.IsParked(ScreenParkState.ParkedForInput));
        Assert.True(ClaudeScreenReader.IsParked(ScreenParkState.ParkedForPermission));
    }

    [Fact]
    public void Yes_no_gate_is_permission()
    {
        var rows = new[] { "  Proceed with the migration? [y/n]" };
        Assert.Equal(ScreenParkState.ParkedForPermission, ClaudeScreenReader.Read(rows));
    }

    private static string FixturesDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "docs", "features", "terminal-state-detector", "fixtures");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate docs/features/terminal-state-detector/fixtures from " + AppContext.BaseDirectory);
    }
}
