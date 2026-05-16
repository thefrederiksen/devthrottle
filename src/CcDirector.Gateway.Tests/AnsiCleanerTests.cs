using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

public sealed class AnsiCleanerTests
{
    // Using string constants instead of char constants because some editor/transport layers
    // mangle bare control characters. We construct them at runtime from explicit code points.
    private static readonly string Esc = char.ConvertFromUtf32(0x1B);
    private static readonly string Bel = char.ConvertFromUtf32(0x07);
    private static readonly string Del = char.ConvertFromUtf32(0x7F);

    [Fact]
    public void Plain_text_is_unchanged()
    {
        Assert.Equal("hello world", AnsiCleaner.Clean("hello world"));
    }

    [Fact]
    public void Empty_returns_empty()
    {
        Assert.Equal(string.Empty, AnsiCleaner.Clean(""));
        Assert.Equal(string.Empty, AnsiCleaner.Clean(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void Strips_csi_color_codes()
    {
        var input = $"{Esc}[31mred{Esc}[0m text";
        Assert.Equal("red text", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Strips_cursor_movement_csi()
    {
        var input = $"{Esc}[2J{Esc}[Hhome";
        Assert.Equal("home", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Strips_show_hide_cursor_dec_private()
    {
        var input = $"{Esc}[?25l{Esc}[?25hhidden";
        Assert.Equal("hidden", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Strips_osc_with_bel_terminator()
    {
        var input = $"{Esc}]0;window title{Bel}after";
        Assert.Equal("after", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Strips_bel_and_del()
    {
        var input = $"a{Bel}b{Del}c";
        Assert.Equal("abc", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Normalises_lone_cr_to_lf()
    {
        var input = "line1\rline2";
        Assert.Equal("line1\nline2", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void Preserves_crlf_pair()
    {
        var input = "line1\r\nline2";
        Assert.Equal("line1\r\nline2", AnsiCleaner.Clean(input));
    }

    [Fact]
    public void LastLines_returns_last_n()
    {
        var text = "a\nb\nc\nd\ne";
        Assert.Equal("c\nd\ne", AnsiCleaner.LastLines(text, 3));
    }

    [Fact]
    public void LastLines_returns_all_when_fewer_than_n()
    {
        var text = "a\nb";
        Assert.Equal("a\nb", AnsiCleaner.LastLines(text, 5));
    }

    [Fact]
    public void Strips_complex_terminal_output()
    {
        var input =
            $"{Esc}[?25l{Esc}[2K{Esc}[1G> Reading file auth.js...\r\n" +
            $"{Esc}[?25h{Esc}[32m[+]{Esc}[0m Found issue";
        var cleaned = AnsiCleaner.Clean(input);
        Assert.Contains("Reading file auth.js", cleaned);
        Assert.Contains("[+]", cleaned);
        Assert.Contains("Found issue", cleaned);
        Assert.False(cleaned.Contains((char)0x1B), "cleaned text should not contain any ESC characters");
    }
}
