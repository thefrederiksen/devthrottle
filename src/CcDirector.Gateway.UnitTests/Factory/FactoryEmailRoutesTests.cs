using System.Text;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory;

/// <summary>
/// The morning report's switch and its kept-report routes (Factory Control, step 4). The switch is off unless set, and a
/// body that does not say on or off is refused rather than guessed. Who the report was once emailed to is gone with the
/// email itself (Factory Inbox mission, phase 5 "Email off"): it is written into Your inbox, and nothing is sent.
/// </summary>
public sealed class FactoryEmailRoutesTests : IDisposable
{
    private readonly GatewayDbTestHarness _h = new();

    public void Dispose() => _h.Dispose();

    // ---- the switch ----------------------------------------------------------------------------------------------

    private static int Status(IResult result) => Assert.IsAssignableFrom<IStatusCodeHttpResult>(result).StatusCode ?? 200;

    private static DefaultHttpContext Put(string body)
    {
        var ctx = new DefaultHttpContext();
        ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return ctx;
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"enabled\":null}")]
    [InlineData("{\"enabled\":\"yes\"}")]
    [InlineData("not json")]
    public async Task PutSwitchAsync_WithoutABooleanEnabled_IsRefused_AndChangesNothing(string body)
    {
        var set = new List<bool>();

        var result = await FactoryEmailEndpoints.PutSwitchAsync(Put(body), _ => TenantId.Local, (_, on) => set.Add(on));

        Assert.Equal(400, Status(result));
        Assert.Empty(set);
    }

    [Fact]
    public async Task PutSwitchAsync_On_SetsIt()
    {
        var set = new List<bool>();

        var result = await FactoryEmailEndpoints.PutSwitchAsync(Put("{\"enabled\":true}"), _ => TenantId.Local, (_, on) => set.Add(on));

        Assert.Equal(200, Status(result));
        Assert.Equal(new[] { true }, set);
    }

    [Fact]
    public void FactoryEmailEnabled_NeverSet_IsOff()
    {
        var db = _h.Open();
        var resolver = new CcDirector.Gateway.Settings.TenantSettingsResolver(new CcDirector.Gateway.Settings.TenantSettingsStore(db));

        Assert.False(resolver.FactoryEmailEnabled(TenantId.Local));
        resolver.SetFactoryEmailEnabled(TenantId.Local, true, new DateTime(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc));
        Assert.True(resolver.FactoryEmailEnabled(TenantId.Local));
    }

    [Theory]
    [InlineData("weekly", null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, 101, null)]
    [InlineData(null, null, "failed")]
    public void ListEmails_ABadKindLimitOrState_IsRefused(string? kind, int? limit, string? state)
    {
        var store = new FactoryEmailStore(_h.Open());

        var result = FactoryEmailEndpoints.ListEmails(new DefaultHttpContext(), null, kind, limit, state, _ => TenantId.Local, store);

        Assert.Equal(400, Status(result));
    }

    [Fact]
    public void ListEmails_NothingKept_IsCountZero()
    {
        var store = new FactoryEmailStore(_h.Open());

        var result = FactoryEmailEndpoints.ListEmails(new DefaultHttpContext(), "outreach", null, null, null, _ => TenantId.Local, store);

        var json = Assert.IsType<JsonHttpResult<Contracts.FactoryEmailsDto>>(result);
        Assert.Equal((0, "outreach"), (json.Value!.Count, json.Value.Factory));
    }

    [Theory]
    [InlineData("not-an-id", 400)]
    [InlineData("4b0f0c6e-0000-4000-8000-000000000001", 404)]
    public void GetEmail_ABadOrUnknownId_SaysWhich(string id, int status)
    {
        var store = new FactoryEmailStore(_h.Open());

        Assert.Equal(status, Status(FactoryEmailEndpoints.GetEmail(new DefaultHttpContext(), id, _ => TenantId.Local, store)));
    }
}
