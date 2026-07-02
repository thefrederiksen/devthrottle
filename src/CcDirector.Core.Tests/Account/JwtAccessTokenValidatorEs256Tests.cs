using System.Diagnostics.CodeAnalysis;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

/// <summary>
/// Proves the validator accepts the backend's current token shape: ES256 (elliptic-curve P-256)
/// signatures verified against the published PUBLIC key set. This is the shape the live sign-in
/// hands back (the Gateway stored such a token and then rejected it - the "signed in but shows
/// Not signed in" defect), so these tests sign real ES256 tokens with a freshly-generated key and
/// verify every accept/reject path, plus that the HS256 path and the embedded production key set
/// still behave.
/// </summary>
public sealed class JwtAccessTokenValidatorEs256Tests : IDisposable
{
    private static readonly DateTime Now = new(2026, 7, 2, 12, 0, 0, DateTimeKind.Utc);

    private readonly Es256TestKey _key = new();

    public void Dispose() => _key.Dispose();

    private JwtAccessTokenValidator MakeValidator(string? publicKeySetJson) =>
        new(TestJwt.SigningSecret, new FakeTimeProvider(Now), publicKeySetJson);

    [Fact]
    public void Validate_WellSignedUnexpiredEs256Token_IsValid()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = _key.CreateToken(Now.AddHours(1));

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_WellSignedExpiredEs256Token_IsExpiredButWellFormed()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = _key.CreateToken(Now.AddHours(-1));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.True(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_TamperedEs256Signature_IsNotValidAndNotWellFormed()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = TamperMidSignature(_key.CreateToken(Now.AddHours(1)));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    /// <summary>
    /// Flips a character in the MIDDLE of the signature segment. Flipping the LAST character (what
    /// <see cref="TestJwt.Tamper"/> does) is not a reliable tamper: the final base64url character of
    /// an ES256 signature carries four padding bits the decoder discards, so an 'A' to 'B' flip
    /// there can decode to the identical bytes.
    /// </summary>
    private static string TamperMidSignature(string token)
    {
        var lastDot = token.LastIndexOf('.');
        var middleOfSignature = lastDot + (token.Length - lastDot) / 2;
        var replacement = token[middleOfSignature] == 'A' ? 'B' : 'A';
        return token[..middleOfSignature] + replacement + token[(middleOfSignature + 1)..];
    }

    [Fact]
    public void Validate_Es256TokenSignedWithDifferentKey_IsNotValidAndNotWellFormed()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        using var otherKey = new Es256TestKey(_key.KeyId);
        var token = otherKey.CreateToken(Now.AddHours(1));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_Es256TokenWithUnknownKeyId_IsNotValid()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = _key.CreateToken(Now.AddHours(1), keyIdOverride: "some-rotated-away-key");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_Es256TokenWithoutKeyId_VerifiesAgainstTheConfiguredKeys()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = _key.CreateToken(Now.AddHours(1), includeKeyId: false);

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Validate_Es256TokenWithNoPublicKeySetConfigured_IsNotValid()
    {
        var validator = MakeValidator(publicKeySetJson: null);
        var token = _key.CreateToken(Now.AddHours(1));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_Hs256TokenStillVerifies_WhenPublicKeySetIsConfigured()
    {
        var validator = MakeValidator(_key.PublicKeySetJson());
        var token = TestJwt.Create(Now.AddHours(1));

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
    }

    [Fact]
    public void Constructor_EmbeddedProductionPublicKeySet_ParsesAndRejectsAForeignToken()
    {
        // The embedded production key set must always construct a working validator, and a token
        // signed by any other key - even one claiming the production key id - must not verify.
        var validator = MakeValidator(DevThrottleSigningKeys.ProductionPublicKeySetJson);
        var token = _key.CreateToken(Now.AddHours(1), keyIdOverride: "78abda78-683e-480c-9111-a8f320011550");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Constructor_PublicKeySetWithoutKeysArray_Throws()
    {
        Assert.Throws<ArgumentException>(() => MakeValidator("""{"not_keys":[]}"""));
    }

    [Fact]
    public void Constructor_EllipticCurveKeyMissingACoordinate_Throws()
    {
        Assert.Throws<ArgumentException>(() => MakeValidator(
            """{"keys":[{"kty":"EC","crv":"P-256","x":"AAAA"}]}"""));
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by tests via MakeValidator.")]
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
