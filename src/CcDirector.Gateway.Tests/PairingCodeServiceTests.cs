using CcDirector.Gateway.Pairing;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The pairing code is the Anchor-B grant (issue #469): exactly 4 numeric digits, 5-minute
/// lifetime, single-use. These tests pin every locked decision with a deterministic injected clock.
/// </summary>
public sealed class PairingCodeServiceTests
{
    [Fact]
    public void Mint_ProducesExactlyFourNumericDigits()
    {
        var service = new PairingCodeService();

        var state = service.Mint();

        Assert.Equal(4, state.Code.Length);
        Assert.All(state.Code, c => Assert.True(char.IsDigit(c)));
    }

    [Fact]
    public void TryVerifyAndConsume_WithCorrectCode_Succeeds()
    {
        var service = new PairingCodeService();
        var state = service.Mint();

        var ok = service.TryVerifyAndConsume(state.Code);

        Assert.True(ok);
    }

    [Fact]
    public void TryVerifyAndConsume_WrongCode_IsRejectedAndLeavesCodeUsable()
    {
        var service = new PairingCodeService();
        var state = service.Mint();

        Assert.False(service.TryVerifyAndConsume("0000".Equals(state.Code) ? "1111" : "0000"));
        // A wrong guess must NOT burn the real code.
        Assert.True(service.TryVerifyAndConsume(state.Code));
    }

    [Fact]
    public void TryVerifyAndConsume_SecondTime_IsRejected_SingleUse()
    {
        var service = new PairingCodeService();
        var state = service.Mint();

        Assert.True(service.TryVerifyAndConsume(state.Code));
        // Single-use: the same code can never be redeemed twice.
        Assert.False(service.TryVerifyAndConsume(state.Code));
    }

    [Fact]
    public void TryVerifyAndConsume_AfterExpiry_IsRejected()
    {
        var now = DateTime.UtcNow;
        var service = new PairingCodeService(() => now);
        var state = service.Mint();

        // Advance the clock past the 5-minute lifetime.
        now = now.AddMinutes(5).AddSeconds(1);

        Assert.False(service.TryVerifyAndConsume(state.Code));
    }

    [Fact]
    public void Current_AfterExpiry_ReturnsNull()
    {
        var now = DateTime.UtcNow;
        var service = new PairingCodeService(() => now);
        service.Mint();

        now = now.AddMinutes(6);

        Assert.Null(service.Current());
    }

    [Fact]
    public void Mint_ReplacesAnyPriorCode()
    {
        var service = new PairingCodeService();
        var first = service.Mint();
        var second = service.Mint();

        // The first code is no longer the active one.
        Assert.Equal(second.Code, service.Current()?.Code);
        if (!string.Equals(first.Code, second.Code, StringComparison.Ordinal))
            Assert.False(service.TryVerifyAndConsume(first.Code));
    }

    [Fact]
    public void Cancel_MakesTheActiveCodeUnusable()
    {
        var service = new PairingCodeService();
        var state = service.Mint();

        service.Cancel();

        Assert.Null(service.Current());
        Assert.False(service.TryVerifyAndConsume(state.Code));
    }
}
