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

    // devthrottle_internal#2816: the address recorded for an account comes from the top-level email claim the provider
    // signs, never from user_metadata, which the person can edit to say anything.
    [Fact]
    public void Read_UserMetadataCarriesADifferentEmail_ReturnsTheTopLevelClaim()
    {
        var token = UnsignedToken("{\"sub\":\"u\",\"email\":\"signed@example.com\",\"user_metadata\":{\"email\":\"edited@example.com\",\"email_verified\":true}}");

        Assert.Equal("signed@example.com", JwtIdentityReader.Read(token)!.Email);
    }

    // With only user_metadata carrying an address there is no email claim, so there is no identity.
    [Fact]
    public void Read_OnlyUserMetadataCarriesAnEmail_ReturnsNull()
    {
        var token = UnsignedToken("{\"sub\":\"u\",\"user_metadata\":{\"email\":\"edited@example.com\",\"email_verified\":true}}");

        Assert.Null(JwtIdentityReader.Read(token));
    }

    private static string UnsignedToken(string payloadJson)
    {
        static string Segment(string json) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return Segment("{\"alg\":\"none\"}") + "." + Segment(payloadJson) + ".sig";
    }

    // A non-JSON-Web-Token string yields no identity.
    [Fact]
    public void Read_MalformedToken_ReturnsNull()
    {
        Assert.Null(JwtIdentityReader.Read("not-a-jwt"));
    }

    // Issue #1079: the subject (sub) claim is read from the cached access token's payload.
    [Fact]
    public void ReadSubject_TokenWithSubjectClaim_ReturnsSubject()
    {
        var token = TestJwt.Create(Expiry); // TestJwt sets sub = "test-user"

        Assert.Equal("test-user", JwtIdentityReader.ReadSubject(token));
    }

    // A non-JSON-Web-Token string yields no subject (not a fabricated one).
    [Fact]
    public void ReadSubject_MalformedToken_ReturnsNull()
    {
        Assert.Null(JwtIdentityReader.ReadSubject("not-a-jwt"));
    }

    // A well-formed token whose payload carries no subject claim yields null.
    [Fact]
    public void ReadSubject_TokenWithoutSubjectClaim_ReturnsNull()
    {
        var header = Base64Url("{\"alg\":\"none\",\"typ\":\"JWT\"}");
        var payload = Base64Url("{\"email\":\"user@example.com\"}");
        var token = $"{header}.{payload}.signature";

        Assert.Null(JwtIdentityReader.ReadSubject(token));
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
