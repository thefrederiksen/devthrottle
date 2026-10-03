namespace CcDirector.Terminal.Core;

/// <summary>
/// A single cell in the terminal grid. Stores character and style attributes.
/// </summary>
public struct TerminalCell
{
    public char Character;
    public TerminalColor Foreground;
    public TerminalColor Background;
    public bool Bold;
    public bool Italic;
    public bool Underline;

    /// <summary>
    /// Set on a row's LAST cell when the terminal's auto-wrap carried the line on to the next
    /// row. Recorded by the parser at the moment the wrap fires, so it never has to be guessed
    /// from the cells. It lives in the cell, so it moves with the row into scrollback and
    /// through grid copies; any later write or erase of the cell replaces the cell and clears it.
    /// </summary>
    public bool WrapsToNextRow;
}
