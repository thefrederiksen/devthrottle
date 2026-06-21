using System.Diagnostics.CodeAnalysis;
using CcDirector.Core.Account;
using Xunit;

namespace CcDirector.Core.Tests.Account;

public sealed class JwtAccessTokenValidatorTests
{
    private static JwtAccessTokenValidator MakeValidator(DateTime nowUtc)
    {
        var time = new FakeTimeProvider(nowUtc);
        return new JwtAccessTokenValidator(TestJwt.SigningSecret, time);
    }

    [Fact]
    public void Validate_WellSignedUnexpiredToken_IsValid()
    {
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var validator = MakeValidator(now);
        var token = TestJwt.Create(now.AddHours(1));

        var result = validator.Validate(token);

        Assert.True(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_WellSignedExpiredToken_IsExpiredButWellFormed()
    {
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var validator = MakeValidator(now);
        var token = TestJwt.Create(now.AddHours(-1));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.True(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_TamperedSignature_IsNotValidAndNotWellFormed()
    {
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var validator = MakeValidator(now);
        var token = TestJwt.Tamper(TestJwt.Create(now.AddHours(1)));

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Fact]
    public void Validate_TokenSignedWithWrongSecret_IsNotValidAndNotWellFormed()
    {
        var now = new DateTime(2026, 6, 21, 12, 0, 0, DateTimeKind.Utc);
        var validator = MakeValidator(now);
        var token = TestJwt.Create(now.AddHours(1), secret: "a-completely-different-secret");

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("only.two")]
    public void Validate_MalformedToken_IsNotValid(string token)
    {
        var validator = MakeValidator(DateTime.UtcNow);

        var result = validator.Validate(token);

        Assert.False(result.IsValid);
        Assert.False(result.IsExpiredButWellFormed);
    }

    [SuppressMessage("Performance", "CA1812", Justification = "Instantiated by tests via MakeValidator.")]
    private sealed class FakeTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _now;
        public FakeTimeProvider(DateTime nowUtc) => _now = new DateTimeOffset(nowUtc, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
    }
}
