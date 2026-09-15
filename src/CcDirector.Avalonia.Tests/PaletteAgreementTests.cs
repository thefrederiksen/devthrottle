using CcDirector.Gateway.Contracts;
using CcDirector.StateAgreementCheck;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The "Dumb Clients" palette guard: ONE canonical name-&gt;hex map, every surface sourced from it, and a
/// machine-checked assertion that the three can never drift.
///
/// <see cref="SessionColorPalette"/> (CcDirector.Gateway.Contracts) is the single source. The Gateway
/// stamps it onto <see cref="SessionDto.EffectiveColorHex"/> - the pixel the web session dot paints
/// verbatim - and the desktop <see cref="StatusPalette"/> references it compile-time. The web client
/// cannot reference C#, so it ships its own COLORS table (packages/client-core/src/sessions/ordering.ts)
/// for its legend swatches. This test reads that SHIPPING table - never a copy typed here, which would be
/// a fourteenth private palette that agrees with itself and proves nothing - and asserts
/// canonical == desktop == web COLORS for every colour name. Change one and this goes red and names it.
///
/// This is the exhaustive companion to the live per-row check in <c>AgreementCheck.Compare</c> section 5:
/// that one only sees colours a live fleet is currently showing, this one covers the whole vocabulary,
/// always, in CI.
/// </summary>
public sealed class PaletteAgreementTests
{
    // The canonical vocabulary, spelled out literally rather than read from the palette under test - a test
    // that iterates the values it is checking proves nothing. "unknown" is a real fold colour (grey), so it
    // is in the list; both "grey" and "unknown" map to the one grey on every surface.
    private static readonly string[] Names =
        { "red", "yellow", "orange", "green", "cyan", "blue", "purple", "supporting", "error", "grey", "unknown" };

    [Fact]
    public void Canonical_Desktop_AndWebColors_AgreeOnEveryName()
    {
        var web = ClientPalette.Read(RepoRoot());

        foreach (var name in Names)
        {
            var canonical = SessionColorPalette.HexFor(name).ToUpperInvariant();

            Assert.Equal(canonical, StatusPalette.HexFor(name).ToUpperInvariant());

            Assert.True(web.TryGetValue(name, out var webHex),
                $"the web COLORS table ({ClientPalette.RelativePath}) has no entry for '{name}' - the legend " +
                "swatch cannot paint it.");
            Assert.Equal(canonical, webHex!.ToUpperInvariant());
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "packages")))
            dir = dir.Parent;
        return dir?.FullName
               ?? throw new InvalidOperationException(
                   "Could not locate the repository root (no 'packages' directory above the test binary).");
    }
}
