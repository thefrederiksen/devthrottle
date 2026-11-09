using System.Windows.Media;
using CcDirector.Terminal.Core;

namespace CcDirector.Terminal;

/// <summary>
/// Extension methods to convert between platform-independent TerminalColor
/// and WPF System.Windows.Media.Color.
/// </summary>
internal static class ColorConversion
{
    internal static Color ToWpf(this TerminalColor c) => Color.FromArgb(c.A, c.R, c.G, c.B);

    internal static TerminalColor ToTerminal(this Color c) => new(c.R, c.G, c.B, c.A);
}
