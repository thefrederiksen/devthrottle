using System;
using System.Text;

namespace CcDirector.Terminal.Core;

/// <summary>
/// Encodes mouse events into the byte sequences a terminal application expects on
/// its input stream. Used to forward wheel events to applications that have
/// switched to the alternate screen and requested mouse reporting (e.g. Claude
/// Code), so they scroll their own view instead of the terminal's local
/// scrollback (which is empty on the alternate screen).
/// </summary>
public static class MouseReportEncoder
{
    /// <summary>xterm mouse button code for a wheel-up notch.</summary>
    public const int WheelUp = 64;

    /// <summary>xterm mouse button code for a wheel-down notch.</summary>
    public const int WheelDown = 65;

    /// <summary>
    /// SGR extended mouse report (DECSET ?1006):
    /// <c>ESC [ &lt; button ; col ; row M</c>. Column and row are 1-based. The
    /// trailing <c>M</c> marks a button press; wheel events have no release.
    /// </summary>
    public static byte[] EncodeSgr(int button, int col, int row)
    {
        int c = Math.Max(col, 1);
        int r = Math.Max(row, 1);
        return Encoding.ASCII.GetBytes($"\x1b[<{button};{c};{r}M");
    }

    /// <summary>
    /// Legacy X10 mouse report: <c>ESC [ M Cb Cx Cy</c>, with each value offset
    /// by 32. Column and row are 1-based. Coordinates above 223 cannot be encoded
    /// in a single byte and are clamped; applications that need larger
    /// coordinates request SGR encoding via ?1006.
    /// </summary>
    public static byte[] EncodeX10(int button, int col, int row)
    {
        int cb = 32 + button;
        int cx = 32 + Math.Min(Math.Max(col, 1), 223);
        int cy = 32 + Math.Min(Math.Max(row, 1), 223);
        return new byte[] { 0x1b, (byte)'[', (byte)'M', (byte)cb, (byte)cx, (byte)cy };
    }
}
