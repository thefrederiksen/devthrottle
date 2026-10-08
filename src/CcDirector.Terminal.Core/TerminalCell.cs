namespace CcDirector.Terminal.Core;

/// <summary>
/// A single cell in the terminal grid. Stores character and style attributes.
/// </summary>
public struct TerminalCell
{
    public char Character;
    public TerminalColor Foreground;
    public TerminalColor Background;

    /// <summary>
    /// The on/off style attributes, one bit each. They share one byte so a cell stays within 16 bytes across 5,000 rows
    /// of scrollback; the properties below read and write the bits, so callers set them exactly as they did when each
    /// was its own field.
    /// </summary>
    private byte _flags;

    private const byte BoldBit = 1;
    private const byte ItalicBit = 2;
    private const byte UnderlineBit = 4;
    private const byte WrapsToNextRowBit = 8;
    private const byte FaintBit = 16;

    public bool Bold { readonly get => Has(BoldBit); set => Set(BoldBit, value); }

    public bool Italic { readonly get => Has(ItalicBit); set => Set(ItalicBit, value); }

    public bool Underline { readonly get => Has(UnderlineBit); set => Set(UnderlineBit, value); }

    /// <summary>
    /// Set on a row's LAST cell when the terminal's auto-wrap carried the line on to the next
    /// row. Recorded by the parser at the moment the wrap fires, so it never has to be guessed
    /// from the cells. It lives in the cell, so it moves with the row into scrollback and
    /// through grid copies; any later write or erase of the cell replaces the cell and clears it.
    /// </summary>
    public bool WrapsToNextRow { readonly get => Has(WrapsToNextRowBit); set => Set(WrapsToNextRowBit, value); }

    /// <summary>
    /// The character was drawn FAINT (SGR 2, "dim"). Kept because it carries meaning, not just looks: Claude Code draws
    /// text that is NOT in its input - its grey guess at the owner's next prompt, and the 'Try "..."' placeholder - faint
    /// inside an empty composer, and draws everything typed at normal weight. Reading the composer without this bit read
    /// the guess as typed text, and every send to an idle session was refused (the Prompt Delivery mission, 7 October 2026).
    /// </summary>
    public bool Faint { readonly get => Has(FaintBit); set => Set(FaintBit, value); }

    /// <summary>
    /// The hyperlink the program wrapped this cell in with OSC 8, as an id into the parser's link
    /// table (<see cref="AnsiParser.GetHyperlink"/>); 0 when the cell is in no link. The program names
    /// the exact target, so a cell carrying one is a link that is never guessed from the text. An id
    /// rather than the address itself, so a cell stays within 16 bytes across 5,000 rows of scrollback.
    /// </summary>
    public ushort HyperlinkId;

    private readonly bool Has(byte bit) => (_flags & bit) != 0;

    private void Set(byte bit, bool on) => _flags = on ? (byte)(_flags | bit) : (byte)(_flags & ~bit);
}
