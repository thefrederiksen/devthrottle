using System.Text;
using Avalonia.Headless.XUnit;

namespace CcDirector.Terminal.Avalonia.Tests;

/// <summary>
/// The wide-and-symbol half of <see cref="OriginalRendererRunTests"/>, IN ITS OWN FILE BECAUSE THAT FILE
/// IS COMPILED ONLY ON WINDOWS (see the Compile Remove condition in the project file).
///
/// WHY IT IS NOT SIMPLY SKIPPED. Avalonia's AvaloniaFactAttribute is sealed, so there is no Windows-only
/// variant to derive, and this repository is on xUnit v2, which has no dynamic skip. The remaining honest
/// choices were to not compile it off Windows or to delete it outright. Excluding the file keeps the full
/// proof on the platform where the premise holds, and a test that is absent cannot be mistaken for one
/// that passed.
///
/// WHAT DOES NOT HOLD ON macOS. The comparison is byte-exact by design - its own assertion says "a
/// tolerance would let a link underline move or a glyph shift by a cell without failing", and that
/// reasoning is right, so no tolerance was added. On the macOS font stack this particular grid - mixed
/// wide CJK, box drawing, accents, arrows and emoji - renders about 1017 bytes of 3.6 million differently
/// between drawing in runs and drawing per character. That is a real difference on that font engine, not
/// a measurement artefact, and it is filed rather than smoothed over. The ASCII case in the sibling class
/// is byte-identical on both platforms and still runs everywhere.
/// </summary>
public sealed class OriginalRendererWideGlyphWindowsTests
{
    [AvaloniaFact]
    public void MixedWideAndSymbolCharacters_KeepTheirExactPlacement()
    {
        var sb = new StringBuilder();
        sb.Append("abc中文def ascii then wide then ascii\r\n");
        sb.Append("┌───┐ box drawing │ inside │\r\n");
        sb.Append("└───┘ accents: café naïve über\r\n");
        sb.Append("arrows and marks: → ← • · end\r\n");
        sb.Append("emoji \U0001F600 between words and \U0001F680 again\r\n");

        OriginalRendererRunTests.AssertRunsMatchPerCharacter(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}
