using System.Text;
using CcDirector.Terminal.Core;
using Xunit;

namespace CcDirector.Core.Tests;

public sealed class KeyEncoderTests
{
    [Theory]
    [InlineData(TerminalKey.R, TerminalKeyModifiers.Control, "12")]
    [InlineData(TerminalKey.A, TerminalKeyModifiers.Control, "01")]
    [InlineData(TerminalKey.U, TerminalKeyModifiers.Control, "15")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.None, "1B 5B 41")]
    [InlineData(TerminalKey.Up, TerminalKeyModifiers.Alt, "1B 5B 31 3B 33 41")]
    [InlineData(TerminalKey.Left, TerminalKeyModifiers.Control, "1B 5B 31 3B 35 44")]
    [InlineData(TerminalKey.F5, TerminalKeyModifiers.Shift, "1B 5B 31 35 3B 32 7E")]
    public void Encode_XtermKeyAndModifiers_ReturnsExpectedBytes(TerminalKey key, TerminalKeyModifiers modifiers, string expected)
    {
        var actual = KeyEncoder.Encode(key, modifiers);

        Assert.Equal(expected, Convert.ToHexString(actual).Chunk(2).Select(x => new string(x)).Aggregate((a, b) => $"{a} {b}"));
    }

    [Fact]
    public void Encode_ApplicationCursorKeyMode_UsesSs3ForUnmodifiedArrow()
    {
        var actual = KeyEncoder.Encode(TerminalKey.Up, applicationCursorKeys: true);

        Assert.Equal("\x1bOA", Encoding.ASCII.GetString(actual));
    }

    [Fact]
    public void Parse_JoinedEmoji_UsesTwoScreenColumns()
    {
        var parser = new AnsiParser(new TerminalCell[20, 5], 20, 5, new List<TerminalCell[]>(), 100);

        parser.Parse(Encoding.UTF8.GetBytes("\U0001F469\u200D\U0001F4BB"));

        Assert.Equal(2, parser.SnapshotActiveRows().CursorCol);
    }

    [Fact]
    public void Parse_DecPrivate1_TracksApplicationCursorKeyMode()
    {
        var parser = new AnsiParser(new TerminalCell[20, 5], 20, 5, new List<TerminalCell[]>(), 100);

        parser.Parse("\x1b[?1h"u8.ToArray());
        Assert.True(parser.ApplicationCursorKeys);
        parser.Parse("\x1b[?1l"u8.ToArray());
        Assert.False(parser.ApplicationCursorKeys);
    }
}
