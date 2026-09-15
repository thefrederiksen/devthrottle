using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// Work item two of the turn-detection phase, the half that needs no clock: the screen body is now
/// available as ROWS as well as joined into one string, and the two cannot drift apart because the
/// joined one is built from the rows.
///
/// The existing behaviour of <c>TryExtractBody</c> is covered by <c>ContinuousIdleStateTests</c>
/// and was deliberately not touched. What is asserted here is the RELATIONSHIP: whatever the row
/// form returns, the joined form is exactly those rows joined with newlines, for every shape of
/// input including the ones where extraction fails.
/// </summary>
public sealed class TerminalBodyRowsTests
{
    [Fact]
    public void The_rows_are_everything_above_the_cursor()
    {
        var rows = new[] { "answer line 1", "answer line 2", "> composer", "shortcuts -" };

        Assert.True(TerminalStateDetector.TryExtractBodyRows(rows, cursorRow: 2, out var body));
        Assert.Equal(new[] { "answer line 1", "answer line 2" }, body);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void No_rows_when_the_body_cannot_be_isolated(int cursorRow)
    {
        // Cursor at the top, or no grid at all. The caller then treats the frame as activity
        // rather than risk a false idle - the same conservative outcome the byte rule gives.
        var rows = new[] { "> composer", "shortcuts -" };

        Assert.False(TerminalStateDetector.TryExtractBodyRows(rows, cursorRow, out var body));
        Assert.Empty(body);
    }

    [Fact]
    public void No_rows_when_there_is_no_grid()
    {
        Assert.False(TerminalStateDetector.TryExtractBodyRows(
            System.Array.Empty<string>(), cursorRow: 5, out var body));
        Assert.Empty(body);
    }

    [Fact]
    public void A_cursor_past_the_end_of_the_grid_takes_the_whole_grid()
    {
        var rows = new[] { "one", "two" };

        Assert.True(TerminalStateDetector.TryExtractBodyRows(rows, cursorRow: 99, out var body));
        Assert.Equal(new[] { "one", "two" }, body);
    }

    [Fact]
    public void The_rows_are_a_copy_so_a_caller_cannot_edit_the_grid_it_was_handed()
    {
        var rows = new[] { "one", "two", "> composer" };
        Assert.True(TerminalStateDetector.TryExtractBodyRows(rows, cursorRow: 2, out var body));

        body[0] = "tampered";
        Assert.Equal("one", rows[0]);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(1)]
    [InlineData(4)]
    [InlineData(0)]
    [InlineData(-1)]
    public void The_joined_body_is_exactly_the_rows_joined(int cursorRow)
    {
        // The two forms exist side by side and are read by different callers - the continuous-idle
        // comparison takes the joined one, the content rule takes the rows. This is the assertion
        // that stops them becoming two different definitions of "the body".
        var rows = new[] { "answer line 1", "", "answer line 2", "> composer" };

        bool joinedOk = TerminalStateDetector.TryExtractBody(rows, cursorRow, out var joined);
        bool rowsOk = TerminalStateDetector.TryExtractBodyRows(rows, cursorRow, out var body);

        Assert.Equal(rowsOk, joinedOk);
        Assert.Equal(string.Join("\n", body), joined);
    }
}
