namespace CcDirector.Terminal.Core;

/// <summary>
/// Whether a character in a cell takes two columns. The parser writes a wide character into one
/// cell and a space into the next; a view that trims blank cells needs this to tell that second
/// half from a real space.
/// </summary>
public static class TerminalCellWidth
{
    public static bool IsWide(char c) => CharWidth.ForCodepoint(c) == 2;
}
