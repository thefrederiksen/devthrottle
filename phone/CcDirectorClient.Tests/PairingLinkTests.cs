using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

// Issue #386: the phone scanner parses the Gateway "Connect a phone" QR
// (ccdirector://pair?u=<url-encoded url>&t=<url-encoded token>) and fills both prefs. These tests
// pin the parser as the exact inverse of the Gateway's PairingPayload.Build (issue #385): valid
// codes round-trip, URL-encoded reserved characters survive, a trailing slash is normalized off,
// and any malformed / non-pairing QR is rejected (so the page leaves the saved prefs untouched).
public class PairingLinkTests
{
    // Mirrors CcDirector.Gateway.Util.PairingPayload.Build so the tests exercise the real wire
    // format the Gateway mints, not a hand-written guess at it.
    private static string Build(string url, string token)
        => $"ccdirector://pair?u={Uri.EscapeDataString(url)}&t={Uri.EscapeDataString(token)}";

    [Fact]
    public void Parse_ValidCode_ReturnsDecodedUrlAndToken()
    {
        var link = Build("https://gw.tail0123.ts.net", "abc123token");

        var r = PairingLink.Parse(link);

        Assert.True(r.Ok);
        Assert.Equal("https://gw.tail0123.ts.net", r.Url);
        Assert.Equal("abc123token", r.Token);
        Assert.Equal("", r.Error);
    }

    [Fact]
    public void Parse_RoundTripsTheGatewayPayloadExactly()
    {
        // A front-door URL with the reserved :// and a base64url token with -, _ (the real token
        // alphabet) - both must survive the EscapeDataString -> Parse round trip unchanged.
        const string url = "https://my-gateway.tailfa11.ts.net";
        const string token = "v1.aB3-_xYz9Qw0kP-token_value";

        var r = PairingLink.Parse(Build(url, token));

        Assert.True(r.Ok);
        Assert.Equal(url, r.Url);
        Assert.Equal(token, r.Token);
    }

    [Fact]
    public void Parse_UrlEncodedValues_AreFullyDecoded()
    {
        // A token containing characters that MUST be percent-encoded in a query (=, &, space, +)
        // proves the parser URL-decodes rather than naively splitting on raw delimiters.
        const string token = "a b+c=d&e";

        var r = PairingLink.Parse(Build("https://gw.ts.net", token));

        Assert.True(r.Ok);
        Assert.Equal("https://gw.ts.net", r.Url);
        Assert.Equal(token, r.Token);
    }

    [Fact]
    public void Parse_TrailingSlashOnUrl_IsNormalizedOff()
    {
        var r = PairingLink.Parse(Build("https://gw.ts.net/", "tok"));

        Assert.True(r.Ok);
        Assert.Equal("https://gw.ts.net", r.Url);
    }

    [Fact]
    public void Parse_MultipleTrailingSlashes_AllTrimmed()
    {
        var r = PairingLink.Parse(Build("https://gw.ts.net///", "tok"));

        Assert.True(r.Ok);
        Assert.Equal("https://gw.ts.net", r.Url);
    }

    [Fact]
    public void Parse_NoTrailingSlash_LeftAsIs()
    {
        var r = PairingLink.Parse(Build("https://gw.ts.net", "tok"));

        Assert.True(r.Ok);
        Assert.Equal("https://gw.ts.net", r.Url);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_NullOrBlank_IsRejected(string? scanned)
    {
        var r = PairingLink.Parse(scanned);

        Assert.False(r.Ok);
        Assert.Equal("", r.Url);
        Assert.Equal("", r.Token);
        Assert.Contains("Not a CC Director pairing code", r.Error);
    }

    [Fact]
    public void Parse_WrongScheme_IsRejected()
    {
        // https:// instead of ccdirector:// - a QR for some other website, not a pairing code.
        var r = PairingLink.Parse("https://pair?u=https%3A%2F%2Fgw.ts.net&t=tok");

        Assert.False(r.Ok);
        Assert.Contains("Not a CC Director pairing code", r.Error);
    }

    [Fact]
    public void Parse_WrongHost_IsRejected()
    {
        var r = PairingLink.Parse("ccdirector://connect?u=https%3A%2F%2Fgw.ts.net&t=tok");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_MissingUrl_IsRejected()
    {
        var r = PairingLink.Parse("ccdirector://pair?t=tok");

        Assert.False(r.Ok);
        Assert.Contains("Not a CC Director pairing code", r.Error);
    }

    [Fact]
    public void Parse_MissingToken_IsRejected()
    {
        var r = PairingLink.Parse("ccdirector://pair?u=https%3A%2F%2Fgw.ts.net");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_EmptyUrlValue_IsRejected()
    {
        var r = PairingLink.Parse("ccdirector://pair?u=&t=tok");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_EmptyTokenValue_IsRejected()
    {
        var r = PairingLink.Parse("ccdirector://pair?u=https%3A%2F%2Fgw.ts.net&t=");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_PlainText_IsRejected()
    {
        var r = PairingLink.Parse("just some random qr content");

        Assert.False(r.Ok);
    }

    [Fact]
    public void Parse_SchemeIsCaseInsensitive()
    {
        // Some encoders upper-case the scheme; a URI lower-cases it, so this must still pass.
        var r = PairingLink.Parse("CCDIRECTOR://pair?u=https%3A%2F%2Fgw.ts.net&t=tok");

        Assert.True(r.Ok);
        Assert.Equal("https://gw.ts.net", r.Url);
        Assert.Equal("tok", r.Token);
    }
}
