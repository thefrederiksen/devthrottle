using System.Text;
using CcDirector.Terminal.Core;
using Xunit;
using static CcDirector.Core.Tests.TerminalTestHelper;

namespace CcDirector.Core.Tests;

/// <summary>
/// Mouse-reporting and alternate-screen mode tracking. A full-screen application
/// such as Claude Code switches to the alternate screen (?1049) and requests
/// mouse reporting (?1000/?1002/?1003, with ?1006 SGR coordinates). The terminal
/// control reads this state to decide whether to forward the wheel to the
/// application instead of scrolling the (empty) local scrollback. These tests
/// pin the parser-side state machine that decision depends on, plus the
/// mouse-report byte encoding the control sends.
/// </summary>
public class AnsiParserMouseModeTests
{
    [Fact]
    public void FreshParser_HasNoMouseReportingOrAlternateScreen()
    {
        var (parser, _, _) = CreateParser();

        Assert.False(parser.IsAlternateScreen);
        Assert.False(parser.MouseReportingEnabled);
        Assert.False(parser.MouseSgrCoordinates);
    }

    [Theory]
    [InlineData("\x1b[?1000h")] // normal (click) tracking
    [InlineData("\x1b[?1002h")] // button-event tracking
    [InlineData("\x1b[?1003h")] // any-event tracking
    public void EnablingAnyMouseTrackingMode_SetsMouseReporting(string sequence)
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, sequence);

        Assert.True(parser.MouseReportingEnabled);
    }

    [Fact]
    public void DisablingMouseTracking_ClearsMouseReporting()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?1003h");
        Assert.True(parser.MouseReportingEnabled);

        Parse(parser, "\x1b[?1003l");
        Assert.False(parser.MouseReportingEnabled);
    }

    [Fact]
    public void Sgr1006_TogglesSgrCoordinates()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?1006h");
        Assert.True(parser.MouseSgrCoordinates);

        Parse(parser, "\x1b[?1006l");
        Assert.False(parser.MouseSgrCoordinates);
    }

    [Fact]
    public void AlternateScreen_1049_TogglesIsAlternateScreen()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?1049h");
        Assert.True(parser.IsAlternateScreen);

        Parse(parser, "\x1b[?1049l");
        Assert.False(parser.IsAlternateScreen);
    }

    [Fact]
    public void ClaudeCodeStartupSequence_EntersAltScreenWithSgrMouseReporting()
    {
        // The exact private-mode burst Claude Code emits at startup: enter the
        // alternate screen and request button-event + any-event mouse reporting
        // with SGR extended coordinates. This is the state in which the wheel
        // must be forwarded to the application rather than scrolling local history.
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?1049h\x1b[?1000h\x1b[?1002h\x1b[?1003h\x1b[?1006h");

        Assert.True(parser.IsAlternateScreen);
        Assert.True(parser.MouseReportingEnabled);
        Assert.True(parser.MouseSgrCoordinates);
    }

    [Fact]
    public void FullReset_ClearsMouseAndAlternateScreenState()
    {
        var (parser, _, _) = CreateParser();

        Parse(parser, "\x1b[?1049h\x1b[?1003h\x1b[?1006h");
        Assert.True(parser.IsAlternateScreen);
        Assert.True(parser.MouseReportingEnabled);
        Assert.True(parser.MouseSgrCoordinates);

        // RIS (full reset). Written as a concatenation because the single literal
        // "\x1bc" would parse as the one character U+01BC -- C#'s \x escape
        // greedily consumes the 'c' as a hex digit. Splitting keeps ESC and 'c'
        // as two characters.
        Parse(parser, "\x1b" + "c");

        Assert.False(parser.IsAlternateScreen);
        Assert.False(parser.MouseReportingEnabled);
        Assert.False(parser.MouseSgrCoordinates);
    }

    [Fact]
    public void EncodeSgr_WheelUp_ProducesExpectedSequence()
    {
        // ESC [ < 64 ; col ; row M  (1-based coordinates, M = press).
        byte[] bytes = MouseReportEncoder.EncodeSgr(MouseReportEncoder.WheelUp, col: 12, row: 5);

        Assert.Equal("\x1b[<64;12;5M", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void EncodeSgr_WheelDown_ProducesExpectedSequence()
    {
        byte[] bytes = MouseReportEncoder.EncodeSgr(MouseReportEncoder.WheelDown, col: 1, row: 1);

        Assert.Equal("\x1b[<65;1;1M", Encoding.ASCII.GetString(bytes));
    }

    [Fact]
    public void EncodeX10_WheelUp_OffsetsEachValueBy32()
    {
        // ESC [ M  Cb Cx Cy  where Cb = 32 + button, Cx = 32 + col, Cy = 32 + row.
        byte[] bytes = MouseReportEncoder.EncodeX10(MouseReportEncoder.WheelUp, col: 1, row: 1);

        Assert.Equal(new byte[] { 0x1b, (byte)'[', (byte)'M', 32 + 64, 32 + 1, 32 + 1 }, bytes);
    }

    [Fact]
    public void EncodeX10_LargeCoordinates_AreClampedTo223()
    {
        byte[] bytes = MouseReportEncoder.EncodeX10(MouseReportEncoder.WheelDown, col: 5000, row: 5000);

        // 32 + 223 = 255 is the largest single-byte coordinate the X10 form allows.
        Assert.Equal(255, bytes[4]);
        Assert.Equal(255, bytes[5]);
    }
}
