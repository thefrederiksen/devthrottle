using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE OWNER'S 82% TEST SWITCH (mission "Licenses - Always Ready, Loud When Not"): one machine's current license reads as
/// 82% of its weekly limit while it is on, so the owner can watch the yellow warning open on a real machine and clear when
/// it is signed in to the recommended license. Over the real store and settings on a throwaway SQLite file. What these hold
/// down: it only ever raises a number, never lowers one; it switches nothing; it is the owner's own; it turns off.
/// </summary>
public sealed class LicenseTestUsageTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly TenantSettingsStore _settings;
    private readonly LicenseTestUsage _testUsage;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly DateTime _now = T0;

    public LicenseTestUsageTests()
    {
        GatewayDatabase db = _harness.Open();
        _store = new LicenseStore(db);
        _settings = new TenantSettingsStore(db);
        _testUsage = new LicenseTestUsage(_settings);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store), _testUsage.MachineFor);
    }

    public void Dispose() => _harness.Dispose();

    private void Reports(string machine, params LicenseHolding[] holdings)
        => _store.ApplyReport(Tenant, "dir-" + machine, 1, Report(machine, _now, holdings), _now, TimeSpan.Zero);

    private LicensePicture Picture() => _snapshots.ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>());

    private Guid Id(string machine) => Picture().Overview.Machines.Single(m => m.MachineName == machine).MachineId;

    private double Weekly(string account) => Picture().Snapshot.Accounts.Single(a => a.AccountEmail == account).SevenDay!.Utilization;

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private static (int Status, JsonElement Json) Answer(DefaultHttpContext ctx, IResult result)
    {
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static readonly IResult NotTheOwner = Results.Json(new { code = "owner_only" }, statusCode: StatusCodes.Status403Forbidden);

    private (int Status, JsonElement Json) Put(string? machineId, bool owner = true)
    {
        var ctx = Context();
        return Answer(ctx, LicenseEndpoints.TurnOnTestUsage(ctx, _ => owner ? null : NotTheOwner, _ => Tenant, _testUsage, _snapshots,
            () => _now, new LicenseTestUsageRequest { MachineId = machineId }));
    }

    private (int Status, JsonElement Json) Delete(bool owner = true)
    {
        var ctx = Context();
        return Answer(ctx, LicenseEndpoints.TurnOffTestUsage(ctx, _ => owner ? null : NotTheOwner, _ => Tenant, _testUsage, _snapshots, () => _now));
    }

    // ----- what it reads -----

    [Fact]
    public void On_TheMachinesLicenseReadsAs82_AndNoOtherLicenseChanges()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));
        Reports("m2", Holding(B, true, sevenDay: 30));

        Put(Id("m1").ToString("N"));

        Assert.Equal((82.0, 30.0), (Weekly(A), Weekly(B)));
        Assert.Equal(82.0, Picture().Overview.Accounts.Single(a => a.AccountEmail == A).Freshest!.SevenDayUtilization);
    }

    // It only ever raises a number: a license really above 82% reads as it is, so a real switch is never hidden.
    [Fact]
    public void On_ALicenseAbove82_ReadsAsItIs()
    {
        Reports("m1", Holding(A, true, sevenDay: 99));
        Reports("m2", Holding(B, true, sevenDay: 30));

        Put(Id("m1").ToString("N"));

        Assert.Equal(99.0, Weekly(A));
    }

    // At 82% nothing is switched: it is under the threshold.
    [Fact]
    public void On_TheEngineDecidesNoSwitch()
    {
        Reports("m1", Holding(A, true, sevenDay: 20), Holding(B, false, sevenDay: 5));

        Put(Id("m1").ToString("N"));

        Assert.Empty(LicenseDecisionEngine.Decide(Picture().Snapshot).Switches);
    }

    // The point of the switch: a machine with no ready next license reads as needing one (yellow), and once it is signed in
    // to the recommended license it is ready.
    [Fact]
    public void On_AMachineWithNoReadyNextLicense_NeedsOne_UntilItIsSignedIn()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));
        Reports("m2", Holding(B, true, sevenDay: 5));
        Assert.Equal(LicenseNextLicenseStates.NotNeeded, NextOf(Picture()).Single(v => v.MachineName == "m1").State);

        Put(Id("m1").ToString("N"));
        var needed = NextOf(Picture()).Single(v => v.MachineName == "m1");
        Assert.Equal((LicenseNextLicenseStates.Needed, LicenseTones.Warning, B), (needed.State, needed.Tone, needed.RecommendedAccountEmail));

        _store.ApplyReport(Tenant, "dir-m1", 2, Report("m1", _now, Holding(A, true, sevenDay: 20), Holding(B, false, sevenDay: 5)), _now, TimeSpan.Zero);
        Assert.Equal(LicenseNextLicenseStates.Ready, NextOf(Picture()).Single(v => v.MachineName == "m1").State);
    }

    // ----- the routes -----

    [Fact]
    public void Put_NotTheOwner_IsRefused_AndNothingIsStored()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));

        var (status, _) = Put(Id("m1").ToString("N"), owner: false);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Null(_settings.Get(Tenant, TenantSettingKeys.LicenseTestUsageMachine));
        Assert.Equal(20.0, Weekly(A));
    }

    [Fact]
    public void Put_NotAnIdentifier_Is400_AndAMachineNotOnThePage_Is409()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));

        var (bad, badJson) = Put("not-a-machine");
        var (unknown, unknownJson) = Put(Guid.NewGuid().ToString("N"));

        Assert.Equal((StatusCodes.Status400BadRequest, "invalid_machine"), (bad, badJson.GetProperty("code").GetString()));
        Assert.Equal((StatusCodes.Status409Conflict, "unknown_machine"), (unknown, unknownJson.GetProperty("code").GetString()));
        Assert.Null(_settings.Get(Tenant, TenantSettingKeys.LicenseTestUsageMachine));
    }

    [Fact]
    public void Put_ThenDelete_TurnsItOnAndOff_AndTheAnswerSaysSo()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));

        var (on, onJson) = Put(Id("m1").ToString("N"));
        Assert.Equal((StatusCodes.Status200OK, true, Id("m1").ToString("N"), 82.0),
            (on, onJson.GetProperty("isOn").GetBoolean(), onJson.GetProperty("machineId").GetString(), onJson.GetProperty("percent").GetDouble()));
        Assert.StartsWith("The test switch is on:", onJson.GetProperty("text").GetString());

        var (off, offJson) = Delete();
        Assert.Equal((StatusCodes.Status200OK, false, "The test switch is off."),
            (off, offJson.GetProperty("isOn").GetBoolean(), offJson.GetProperty("text").GetString()));
        Assert.Equal(20.0, Weekly(A));
    }

    [Fact]
    public void Delete_NotTheOwner_IsRefused_AndItStaysOn()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));
        Put(Id("m1").ToString("N"));

        var (status, _) = Delete(owner: false);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Equal(82.0, Weekly(A));
    }

    // ----- the pure part -----

    [Fact]
    public void Apply_Off_OrAMachineOnNoLicense_ChangesNothing()
    {
        Reports("m1", Holding(A, true, sevenDay: 20));
        Reports("m2");
        var overview = Picture().Overview;

        Assert.Same(overview, LicenseTestUsage.Apply(overview, null));
        Assert.Same(overview, LicenseTestUsage.Apply(overview, Id("m2")));
        Assert.Same(overview, LicenseTestUsage.Apply(overview, Guid.NewGuid()));
    }
}
