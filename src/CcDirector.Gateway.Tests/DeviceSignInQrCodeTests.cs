using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Add-a-device sign-in QR code (issue #856) carries ONLY a plain http/https sign-in URL - never a
/// pairing code or any other secret. These tests pin that it renders a PNG for a real URL and rejects
/// anything that is not a plain absolute http/https URL.
/// </summary>
public sealed class DeviceSignInQrCodeTests
{
    // PNG signature: the 8-byte magic header every PNG starts with.
    private static readonly byte[] PngMagic = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    [Fact]
    public void RenderPng_ForAPlainSignInUrl_ReturnsPngBytes()
    {
        var png = DeviceSignInQrCode.RenderPng("https://devthrottle.com/signin");

        Assert.NotNull(png);
        Assert.True(png.Length > PngMagic.Length, "expected a non-trivial PNG");
        Assert.Equal(PngMagic, png[..PngMagic.Length]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void RenderPng_WithBlankUrl_Throws(string? url)
    {
        Assert.Throws<ArgumentException>(() => DeviceSignInQrCode.RenderPng(url!));
    }

    [Theory]
    [InlineData("not a url")]
    [InlineData("/signin")]                 // relative, not absolute
    [InlineData("ftp://devthrottle.com")]   // wrong scheme
    [InlineData("javascript:alert(1)")]     // not http/https
    public void RenderPng_WithNonHttpUrl_Throws(string url)
    {
        // Guards the rule that the QR only ever carries a plain http/https sign-in URL (never a secret).
        Assert.Throws<ArgumentException>(() => DeviceSignInQrCode.RenderPng(url));
    }
}
