using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Boxes;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Boxes;

/// <summary>
/// The Factory Boxes switch (the Factory Boxes mission, phase 1): on only for the accounts the Gateway's configuration
/// lists, off for every account by default, and the gate in front of the Boxes routes asks it and nothing else.
/// </summary>
public sealed class BoxesSwitchTests
{
    private static readonly TenantId Owner = new("11111111-1111-1111-1111-111111111111");
    private static readonly TenantId Other = new("22222222-2222-2222-2222-222222222222");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsOn_NothingConfigured_IsOffForEveryAccount(string? configured)
    {
        var boxes = new BoxesSwitch(configured);

        Assert.False(boxes.IsOn(Owner));
        Assert.False(boxes.IsOn(TenantId.Local));
        Assert.Equal(0, boxes.ConfiguredCount);
    }

    [Fact]
    public void IsOn_AListedAccount_IsOn()
    {
        var boxes = new BoxesSwitch(Owner.Value);

        Assert.True(boxes.IsOn(Owner));
    }

    [Fact]
    public void IsOn_AnAccountNotListed_IsOff()
    {
        var boxes = new BoxesSwitch(Owner.Value);

        Assert.False(boxes.IsOn(Other));
    }

    [Fact]
    public void IsOn_SeveralAccountsWithSpacesAndBothSeparators_EachIsOn()
    {
        var boxes = new BoxesSwitch($" {Owner.Value} ; {Other.Value} ,, ");

        Assert.True(boxes.IsOn(Owner));
        Assert.True(boxes.IsOn(Other));
        Assert.Equal(2, boxes.ConfiguredCount);
    }

    [Fact]
    public void IsOn_ComparesTheAccountIdExactly()
    {
        var boxes = new BoxesSwitch("ABCDEF00-0000-0000-0000-000000000000");

        Assert.False(boxes.IsOn(new TenantId("abcdef00-0000-0000-0000-000000000000")));
    }

    [Fact]
    public void IsOn_AnInvalidTenant_IsOff()
    {
        var boxes = new BoxesSwitch(Owner.Value);

        Assert.False(boxes.IsOn(default));
    }

    [Fact]
    public void IsOnFor_NoAccountBoundToTheRequest_IsOff()
    {
        var on = BoxesGate.IsOnFor(new DefaultHttpContext(), new BoxesSwitch(Owner.Value), _ => null, out var why);

        Assert.False(on);
        Assert.Equal("no account is bound to this request", why);
    }

    [Fact]
    public void IsOnFor_ARequestForAListedAccount_IsOn()
    {
        Assert.True(BoxesGate.IsOnFor(new DefaultHttpContext(), new BoxesSwitch(Owner.Value), _ => Owner, out _));
    }

    [Fact]
    public void IsOnFor_ARequestForAnotherAccount_IsOff()
    {
        Assert.False(BoxesGate.IsOnFor(new DefaultHttpContext(), new BoxesSwitch(Owner.Value), _ => Other, out _));
    }
}
