using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// GET /gateway/licenses/alarms (License Switching phase 6): the caller's own account's open alarms, with the banner's
/// words, and no other account's; no account bound is refused.
/// </summary>
public sealed class LicenseAlarmsEndpointTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");
    private static readonly LicenseAlarmKind NotReady = new("not-ready-at-80", "Not ready to switch");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseAlarmService _alarms;

    public LicenseAlarmsEndpointTests()
    {
        _alarms = new LicenseAlarmService(_harness.Open(),
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, _, _) => Task.CompletedTask, () => T0);
    }

    public void Dispose() => _harness.Dispose();

    private (int Status, JsonElement Json) Get(TenantId? tenant)
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        LicenseEndpoints.Alarms(ctx, _ => tenant, _alarms, () => T0).ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    [Fact]
    public async Task Alarms_AnswersTheCallersOwnAlarms_InTheBannersWords_AndNoOtherAccounts()
    {
        await _alarms.RaiseAsync(One, NotReady, new[]
        {
            new LicenseAlarmCondition(Guid.NewGuid(), "machine-a", "alice@example.com", "machine-a is not ready.", "Add a spare to machine-a."),
        }, CancellationToken.None);

        var (status, json) = Get(One);
        var (_, other) = Get(Two);

        Assert.Equal(200, status);
        Assert.Equal(1, json.GetProperty("count").GetInt32());
        Assert.Equal("Open Licenses", json.GetProperty("linkText").GetString());
        Assert.Equal("machine-a is not ready. Add a spare to machine-a.", json.GetProperty("alarms")[0].GetProperty("text").GetString());
        Assert.Equal(0, other.GetProperty("count").GetInt32());
        Assert.Equal(0, other.GetProperty("alarms").GetArrayLength());
    }

    [Fact]
    public void Alarms_NoAccountBound_IsRefused()
    {
        var (status, json) = Get(null);

        Assert.Equal(403, status);
        Assert.Equal("no_account", json.GetProperty("code").GetString());
    }
}
