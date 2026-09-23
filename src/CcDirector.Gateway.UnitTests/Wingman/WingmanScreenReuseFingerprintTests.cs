using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

public sealed class WingmanScreenReuseFingerprintTests
{
    [Fact]
    public void FooterHints_DoNotChangeTheReuseFingerprint_ButDoChangeTheExactGridHash()
    {
        var first = Screen(
            "The change is complete and the checks pass.",
            "────────────────────────────────────────",
            "❯",
            "new task? /clear to save 154k tokens");
        var second = Screen(
            "The change is complete and the checks pass.",
            "────────────────────────────────────────────────",
            "❯",
            "4% until auto-compact");

        Assert.NotEqual(Exact(first), Exact(second));
        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    public void VisibleComposerRow_DoesNotChangeTheReuseFingerprint_ButStillChangesTheExactGridHash()
    {
        var first = Screen("The release is ready.", "❯ continue", "status", cursorRow: 1);
        var second = Screen("The release is ready.", "❯ a draft not yet sent", "status", cursorRow: 1);

        Assert.NotEqual(Exact(first), Exact(second));
        Assert.Equal(Hash(first), Hash(second));
    }

    [Fact]
    public void WhitespaceAndLineWrapping_DoNotChangeTheReuseFingerprint()
    {
        var wrapped = Screen("The migration is", "complete and safe.", cursorVisible: false);
        var unwrapped = Screen("  The   migration is complete", "and safe.  ", cursorVisible: false);

        Assert.NotEqual(Exact(wrapped), Exact(unwrapped));
        Assert.Equal(Hash(wrapped), Hash(unwrapped));
    }

    [Fact]
    public void ASubstantiveCharacterChange_ChangesTheReuseFingerprint()
    {
        var passing = Screen("The migration is complete and the checks pass.", cursorVisible: false);
        var failing = Screen("The migration is complete and the checks fail.", cursorVisible: false);

        Assert.NotEqual(Hash(passing), Hash(failing));
    }

    [Fact]
    public void AlternateScreenCursorRows_RemainPartOfTheIdentity()
    {
        var first = Screen("Choose one", "❯ first", alternate: true, cursorRow: 1);
        var second = Screen("Choose one", "❯ second", alternate: true, cursorRow: 1);

        Assert.NotEqual(Hash(first), Hash(second));
    }

    private static ScreenGridResponse Screen(
        string first,
        string? second = null,
        string? third = null,
        string? fourth = null,
        int cursorRow = -1,
        bool cursorVisible = true,
        bool alternate = false)
    {
        var rows = new[] { first, second, third, fourth }.Where(x => x is not null).Cast<string>().ToList();
        return new ScreenGridResponse
        {
            Rows = rows,
            HasGrid = true,
            CursorRow = cursorRow,
            CursorVisible = cursorVisible,
            IsAlternateScreen = alternate,
        };
    }

    private static string Exact(ScreenGridResponse screen) => WingmanScreenVerdictCache.HashRows(screen.Rows);
    private static string Hash(ScreenGridResponse screen) => WingmanScreenReuseFingerprint.Hash(screen);
}
