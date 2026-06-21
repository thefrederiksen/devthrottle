using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

public sealed class JwtIdentityReaderTests
{
    private static readonly DateTime Expiry = new(2026, 6, 21, 13, 0, 0, DateTimeKind.Utc);

    // Issue #582 AC1: the identity (email + provider) is read from the cached access token's claims.
    [Fact]
    public void Read_TokenWithEmailAndProvider_ReturnsIdentity()
    {
        var token = TestJwt.CreateWithIdentity(Expiry, "user@example.com", "google");

        var identity = JwtIdentityReader.Read(token);

        Assert.NotNull(identity);
        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("google", identity.Provider);
    }

    // When the provider claim is absent the email still resolves, with an honest "unknown" provider.
    [Fact]
    public void Read_TokenWithEmailButNoProvider_ReturnsIdentityWithUnknownProvider()
    {
        var token = TestJwt.CreateWithIdentity(Expiry, "user@example.com", provider: null);

        var identity = JwtIdentityReader.Read(token);

        Assert.NotNull(identity);
        Assert.Equal("user@example.com", identity.Email);
        Assert.Equal("unknown", identity.Provider);
    }

    // No email claim -> no identity (the account area shows an explicit unavailable state, not a fake one).
    [Fact]
    public void Read_TokenWithoutEmailClaim_ReturnsNull()
    {
        var token = TestJwt.Create(Expiry);

        Assert.Null(JwtIdentityReader.Read(token));
    }

    // A non-JSON-Web-Token string yields no identity.
    [Fact]
    public void Read_MalformedToken_ReturnsNull()
    {
        Assert.Null(JwtIdentityReader.Read("not-a-jwt"));
    }
}
