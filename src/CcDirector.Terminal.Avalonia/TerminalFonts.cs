namespace CcDirector.Terminal.Avalonia;

/// <summary>
/// Shared monospace font fallback chain for terminal rendering, used by every
/// renderer and terminal control so the list cannot drift between them.
///
/// The order matters. Windows-only fonts (Cascadia Mono, Consolas) come first to
/// preserve the existing appearance on Windows; <c>Menlo</c> covers macOS and
/// <c>DejaVu Sans Mono</c> covers most Linux desktops; Courier New is a last resort.
///
/// History: the chain used to be "Cascadia Mono, Consolas, Courier New" -- all three
/// are absent on a stock macOS install, so Avalonia fell back to the default
/// PROPORTIONAL UI font. A terminal draws each glyph in a fixed cell (col * cellWidth),
/// so a proportional font misaligns every character and mangles box-drawing glyphs,
/// which is why the terminal rendered correctly on Windows but garbled on macOS.
/// Menlo (always present on macOS) has full box-drawing/Unicode coverage and fixes it.
/// </summary>
internal static class TerminalFonts
{
    public const string Family = "Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, Courier New";
}
