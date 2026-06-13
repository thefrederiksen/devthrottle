using System;
using CcDirector.Gateway.Util;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for <see cref="PairingPayload"/> (issue #385): the pure pairing deep-link builder
/// that the /pair/qr.png + /pair/payload endpoints render. Asserts the exact
/// <c>ccdirector://pair?u=&amp;t=</c> shape and correct URL-encoding so the sibling phone-scanner
/// slice can decode against this fixed contract.
/// </summary>
public sealed class PairingPayloadTests
{
    [Fact]
    public void Build_TypicalFrontDoorAndToken_ProducesExactSchemeAndEncodedQuery()
    {
        // Arrange
        const string url = "https://machine-a.tail0123.ts.net";
        const string token = "abc123XYZ_token";

        // Act
        var payload = PairingPayload.Build(url, token);

        // Assert: scheme/host pinned, both values URL-encoded into u= and t=.
        Assert.Equal(
            "ccdirector://pair?u=https%3A%2F%2Fmachine-a.tail0123.ts.net&t=abc123XYZ_token",
            payload);
    }

    [Fact]
    public void Build_TokenWithReservedCharacters_EncodesThemSoTheQueryRoundTrips()
    {
        // Arrange: a token carrying chars that are reserved in a query string. The base64url
        // alphabet GatewayAuth uses is safe, but the builder must survive any value.
        const string url = "https://host.example.ts.net";
        const string token = "a+b/c=d&e?f";

        // Act
        var payload = PairingPayload.Build(url, token);

        // Assert: the encoded t= value decodes back to the original token byte-for-byte.
        var query = payload[(payload.IndexOf("&t=", StringComparison.Ordinal) + 3)..];
        Assert.Equal(token, Uri.UnescapeDataString(query));
        // And every reserved char was actually escaped (no raw &, ?, =, /, + leaked into t=).
        Assert.DoesNotContain("+", query);
        Assert.Contains("%2B", query); // '+'
        Assert.Contains("%2F", query); // '/'
        Assert.Contains("%3D", query); // '='
        Assert.Contains("%26", query); // '&'
        Assert.Contains("%3F", query); // '?'
    }

    [Fact]
    public void Build_UrlAndToken_BothDecodeBackToTheOriginalValues()
    {
        // Arrange
        const string url = "https://machine-a.tail0123.ts.net";
        const string token = "tok-with-dashes_and_underscores";

        // Act
        var payload = PairingPayload.Build(url, token);

        // Assert: split on the literal contract and decode each half.
        Assert.StartsWith("ccdirector://pair?u=", payload);
        var afterU = payload["ccdirector://pair?u=".Length..];
        var parts = afterU.Split("&t=", 2);
        Assert.Equal(2, parts.Length);
        Assert.Equal(url, Uri.UnescapeDataString(parts[0]));
        Assert.Equal(token, Uri.UnescapeDataString(parts[1]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_MissingGatewayUrl_Throws_NoPlaceholder(string? url)
    {
        // No-fallback rule (criterion 3): a missing front-door URL is a hard error, never an
        // empty/placeholder value baked into the payload.
        Assert.Throws<ArgumentException>(() => PairingPayload.Build(url!, "token"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_MissingToken_Throws(string? token)
    {
        Assert.Throws<ArgumentException>(() => PairingPayload.Build("https://host.ts.net", token!));
    }
}
