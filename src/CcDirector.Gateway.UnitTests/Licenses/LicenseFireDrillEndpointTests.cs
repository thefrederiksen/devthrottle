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
/// THE FIRE DRILL ROUTES (mission "Licenses - Always Ready, Loud When Not", step 7): GET the drill, PUT its machine,
/// POST run now - over the real drill service, switch service, alarm service and settings on a throwaway SQLite file,
/// with a fake Director that switches. What they hold down: a caller who is not the owner is refused before anything
/// is read or written; every other refusal is a status, a word and a sentence; and a drill run from the route runs on
/// the Gateway's lifetime, so a caller who leaves does not stop it between the way out and the way back.
/// </summary>
public sealed class LicenseFireDrillEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Tenant = new("acct-a");
    private const string Windows = "the-windows-machine";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly TenantSettingsStore _settings;
    private readonly LicenseFireDrillService _drill;
    private readonly List<LicenseSwitchRequest> _sent = new();
    private readonly DateTime _now = T0;
    private long _sequence;

    public LicenseFireDrillEndpointTests()
    {
        GatewayDatabase db = _harness.Open();
        _store = new LicenseStore(db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        var switches = new LicenseSwitchService(_store, _snapshots, Send,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)), () => _now);
        var alarms = new LicenseAlarmService(db,
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            (_, _, _) => Task.CompletedTask, () => _now, (_, _) => false);
        _settings = new TenantSettingsStore(db);
        _drill = new LicenseFireDrillService(_store, _snapshots, switches, alarms, new LicenseFireDrillLog(db), _settings, () => _now);
        Reports(Holding(A, true, readAt: _now), Holding(B, false, readAt: _now));
    }

    public void Dispose() => _harness.Dispose();

    private Task<DirectorCommandResult?> Send(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)!;
        _sent.Add(request);
        return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.TargetAccountEmail,
            VerifiedByUsageRead = true,
            StartedAtUtc = _now,
            FinishedAtUtc = _now,
        }, Web)));
    }

    private void Reports(params LicenseHolding[] holdings)
    {
        foreach (var h in holdings)
            h.SignInExpiresAtUtc = _now.AddDays(20);
        _store.ApplyReport(Tenant, "dir-" + Windows, ++_sequence, Report(Windows, _now, holdings), _now, TimeSpan.Zero);
    }

    private string WindowsId()
        => _snapshots.ReadPicture(Tenant, _now, Array.Empty<LicenseExhaustion>()).Overview.Machines.Single().MachineId.ToString("N");

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
        var result = LicenseEndpoints.SetFireDrillMachineAsync(ctx, _ => owner ? null : NotTheOwner, _ => Tenant, _drill,
            new LicenseFireDrillMachineRequest { MachineId = machineId }).GetAwaiter().GetResult();
        return Answer(ctx, result);
    }

    private (int Status, JsonElement Json) Run(bool owner = true, CancellationToken requestAborted = default, TenantId? inScope = null)
    {
        var ctx = Context();
        ctx.RequestAborted = requestAborted;
        var result = LicenseEndpoints.RunFireDrillAsync(ctx, _ => owner ? null : NotTheOwner, _ => Tenant, () => inScope ?? Tenant, _drill,
            CancellationToken.None).GetAwaiter().GetResult();
        return Answer(ctx, result);
    }

    [Fact]
    public void FireDrillAnswer_NothingChosen_SaysTheDrillIsNotSetUp()
    {
        var answer = LicenseEndpoints.FireDrillAnswer(Tenant, _drill);

        Assert.Null(answer.MachineId);
        Assert.Equal("The license fire drill is not set up: no drill machine is chosen, so it does not run.", answer.Line!.Text);
    }

    [Fact]
    public void PutMachine_NotTheOwner_IsRefused_AndNothingIsStored()
    {
        var (status, _) = Put(WindowsId(), owner: false);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Null(_settings.Get(Tenant, TenantSettingKeys.LicenseFireDrillMachine));
    }

    [Fact]
    public void PutMachine_NotAnIdentifier_Is400_AndAMachineNotOnThePage_Is409()
    {
        var (bad, badJson) = Put("the-windows-machine");
        var (unknown, unknownJson) = Put(Guid.NewGuid().ToString("N"));

        Assert.Equal((StatusCodes.Status400BadRequest, "invalid_machine"), (bad, badJson.GetProperty("code").GetString()));
        Assert.Equal((StatusCodes.Status409Conflict, "unknown_machine"), (unknown, unknownJson.GetProperty("code").GetString()));
        Assert.Null(_settings.Get(Tenant, TenantSettingKeys.LicenseFireDrillMachine));
    }

    [Fact]
    public void PutMachine_AMachineOnThePage_IsChosen_AndNullClearsIt()
    {
        var (status, json) = Put(WindowsId());
        Assert.Equal((StatusCodes.Status200OK, WindowsId()), (status, json.GetProperty("machineId").GetString()));

        var (cleared, clearedJson) = Put(null);
        Assert.Equal((StatusCodes.Status200OK, JsonValueKind.Null), (cleared, clearedJson.GetProperty("machineId").ValueKind));
        Assert.Null(_drill.DrillMachine(Tenant));
    }

    [Fact]
    public void Run_NotTheOwner_IsRefused_AndNothingIsSent()
    {
        Put(WindowsId());

        var (status, _) = Run(owner: false);

        Assert.Equal(StatusCodes.Status403Forbidden, status);
        Assert.Empty(_sent);
    }

    [Fact]
    public void Run_NoDrillMachine_Is409()
    {
        var (status, json) = Run();

        Assert.Equal((StatusCodes.Status409Conflict, "no_drill_machine"), (status, json.GetProperty("code").GetString()));
        Assert.Empty(_sent);
    }

    [Fact]
    public async Task Run_ACallerWhoHasAlreadyLeft_DoesNotStopTheDrill_ItRunsOutAndBack()
    {
        Put(WindowsId());
        using var gone = new CancellationTokenSource();
        gone.Cancel();

        var ctx = Context();
        ctx.RequestAborted = gone.Token;
        var result = await LicenseEndpoints.RunFireDrillAsync(ctx, _ => null, _ => Tenant, () => Tenant, _drill, CancellationToken.None);

        // Nothing is written back to a caller who has gone; the drill itself ran to the end.
        Assert.NotNull(result);
        Assert.Equal(2, _sent.Count);
        Assert.True(_drill.ReportLine(Tenant)!.Passed);
    }

    [Fact]
    public void Run_OutsideTheCallersOwnScope_Throws_AndSendsNothing()
    {
        Put(WindowsId());

        Assert.Throws<InvalidOperationException>(() => Run(inScope: new TenantId("acct-other")));
        Assert.Empty(_sent);
    }
}
