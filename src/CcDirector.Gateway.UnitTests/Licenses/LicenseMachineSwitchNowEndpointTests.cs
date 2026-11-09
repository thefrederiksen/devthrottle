using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// SWITCH THIS MACHINE NOW (the Licenses page's machine drawer, 9 November 2026): POST /gateway/licenses/machines/switch-now
/// run over the real switch service and the real store on a throwaway SQLite file, with only the Director on the other end
/// of the send made here. What these hold down: ONE machine moves and the others on its license stay where they are; the
/// license it leaves is the Gateway's own reading, never the caller's; the drawer offers exactly the targets this route
/// accepts, with the same sentence for one it refuses; and a machine that is not on the page, or a body that names none,
/// is refused with nothing written or sent.
/// </summary>
public sealed class LicenseMachineSwitchNowEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId TenantOne = new("acct-one");
    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseSignInLog _signIns;
    private readonly LicenseReadinessService _readiness;
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseSwitchService _service;
    private readonly List<(string DirectorId, LicenseSwitchRequest Request)> _sent = new();
    private readonly DateTime _now = T0;
    private long _sequence;

    public LicenseMachineSwitchNowEndpointTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _approvers = new LicenseApproverStore(new CcDirector.Gateway.Settings.TenantSettingsStore(db));
        _signIns = new LicenseSignInLog(db);
        _readiness = LicenseReadinessTestKit.ForReading(db, _store, (_, _) => false, () => _now);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        _service = new LicenseSwitchService(_store, _snapshots,
            (directorId, command, _) =>
            {
                var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)!;
                _sent.Add((directorId, request));
                return Task.FromResult<DirectorCommandResult?>(Switched(request));
            },
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            () => _now);
    }

    public void Dispose() => _harness.Dispose();

    // ---- plumbing ----

    private DirectorCommandResult Switched(LicenseSwitchRequest request)
        => DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            Message = "The Director switched.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.TargetAccountEmail,
            VerifiedByUsageRead = true,
            StartedAtUtc = _now,
            FinishedAtUtc = _now.AddSeconds(1),
            ExchangeMilliseconds = 41,
        }, Web));

    private void Reports(string machine, string director, string active, string[] spares)
    {
        var report = Report(machine, _now, new[] { Holding(active, true, 50, readAt: _now) }
            .Concat(spares.Select(s => Holding(s, false, 4, readAt: _now))).ToArray());
        report.OsUser = "owner";
        _store.ApplyReport(TenantOne, director, ++_sequence, report, _now, TimeSpan.Zero);
    }

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private (int Status, JsonElement Json) Post(LicenseMachineSwitchNowRequest? body)
    {
        var ctx = Context();
        var result = LicenseEndpoints.SwitchMachineNowAsync(ctx, _ => null, _ => TenantOne, () => TenantOne, _service, _store, body)
            .GetAwaiter().GetResult();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private JsonElement MachineOnThePage(string name)
    {
        var ctx = Context();
        LicenseEndpoints.Overview(ctx, _ => TenantOne, _snapshots, () => _now, _approvers, _signIns, (_, _) => false, _readiness)
            .ExecuteAsync(ctx).GetAwaiter().GetResult();
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray())).RootElement.Clone();
        return json.GetProperty("machines").EnumerateArray().Single(m => m.GetProperty("machineName").GetString() == name);
    }

    private string IdOf(string name) => MachineOnThePage(name).GetProperty("machineId").GetString()!;

    private List<LicenseSwitchLogEntity> Switches()
        => _store.ListLog(TenantOne, T0.AddDays(-1)).Where(r => r.Kind == LicenseLogKinds.Switch).ToList();

    // ---- what it does ----

    [Fact]
    public void SwitchMachineNow_TwoMachinesOnOneLicense_MovesOnlyTheOneNamed()
    {
        Reports(Windows, "dir-win", A, new[] { B });
        Reports(Mac, "dir-mac", A, new[] { B });

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = IdOf(Windows), TargetAccountEmail = B });

        Assert.Equal(200, status);
        Assert.Equal($"{Windows} is now on {B}.", json.GetProperty("text").GetString());
        var sent = Assert.Single(_sent);
        Assert.Equal(("dir-win", A, B, LicenseSwitchReasons.Manual),
            (sent.DirectorId, sent.Request.ExpectedActiveAccountEmail, sent.Request.TargetAccountEmail, sent.Request.Reason));
        Assert.Equal(Windows, Assert.Single(Switches()).MachineName);
        Assert.Equal(A, MachineOnThePage(Mac).GetProperty("activeAccountEmail").GetString());
    }

    [Fact]
    public void SwitchMachineNow_WithNoTarget_GoesWhereTheGatewayWouldSendIt()
    {
        Reports(Windows, "dir-win", A, new[] { B });

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = IdOf(Windows) });

        Assert.Equal((200, B), (status, json.GetProperty("targetAccountEmail").GetString()));
    }

    [Fact]
    public void TheDrawer_OffersExactlyTheTargetsTheRouteAccepts_AndRefusesTheOthersWithTheSameSentence()
    {
        Reports(Windows, "dir-win", A, new[] { B });
        // The Mac holds C, so C has a reading; the Windows machine holds no sign-in for it.
        Reports(Mac, "dir-mac", A, new[] { B, C });

        var machine = MachineOnThePage(Windows);
        Assert.True(machine.GetProperty("canSwitchNow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, machine.GetProperty("switchNowText").ValueKind);
        var targets = machine.GetProperty("switchTargets").EnumerateArray()
            .ToDictionary(t => t.GetProperty("accountEmail").GetString()!, t => t);
        Assert.Equal(new[] { B, C }, targets.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray());
        Assert.True(targets[B].GetProperty("canChoose").GetBoolean());
        Assert.True(targets[B].GetProperty("isAutomaticChoice").GetBoolean());
        Assert.False(targets[C].GetProperty("canChoose").GetBoolean());

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = machine.GetProperty("machineId").GetString(), TargetAccountEmail = C });

        Assert.Equal(409, status);
        Assert.Equal(LicenseManualRefusals.TargetNotHeld, json.GetProperty("code").GetString());
        Assert.Equal(targets[C].GetProperty("unavailableText").GetString(), json.GetProperty("error").GetString());
        Assert.Empty(_sent);
        Assert.Empty(Switches());
    }

    [Fact]
    public async Task TheDrawer_ForAMachineWithASwitchSentAndNotAnswered_OffersNone_AndTheRouteRefuses()
    {
        Reports(Windows, "dir-win", A, new[] { B });
        var id = IdOf(Windows);
        // A first switch whose Director never answers: its outcome is not known, so the machine is mid-switch.
        var service = new LicenseSwitchService(_store, _snapshots,
            (_, _, _) => Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer")),
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)), () => _now);
        await service.SwitchMachineNowAsync(TenantOne, Guid.ParseExact(id, "N"), B, CancellationToken.None);

        var machine = MachineOnThePage(Windows);
        Assert.False(machine.GetProperty("canSwitchNow").GetBoolean());
        Assert.Equal(0, machine.GetProperty("switchTargets").GetArrayLength());
        Assert.StartsWith($"{Windows} cannot be switched now", machine.GetProperty("switchNowText").GetString());

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = id, TargetAccountEmail = B });

        Assert.Equal(409, status);
        Assert.Equal(LicenseManualRefusals.NoMachineToSwitch, json.GetProperty("code").GetString());
        Assert.Empty(_sent);
    }

    // ---- refused before anything is asked ----

    [Fact]
    public void SwitchMachineNow_AMachineNotOnThePage_IsRefused409_AndNothingIsWrittenOrSent()
    {
        Reports(Windows, "dir-win", A, new[] { B });

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = Guid.NewGuid().ToString("N"), TargetAccountEmail = B });

        Assert.Equal(409, status);
        Assert.Equal((LicenseManualRefusals.NoMachineToSwitch, "That machine is not on the Licenses page any more."),
            (json.GetProperty("code").GetString(), json.GetProperty("error").GetString()));
        Assert.Empty(_sent);
        Assert.Empty(Switches());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-machine")]
    public void SwitchMachineNow_WithoutAMachineId_IsRefused400(string? machineId)
    {
        Reports(Windows, "dir-win", A, new[] { B });

        var (status, json) = Post(new LicenseMachineSwitchNowRequest { MachineId = machineId, TargetAccountEmail = B });

        Assert.Equal((400, "invalid_machine"), (status, json.GetProperty("code").GetString()));
        Assert.Empty(_sent);
    }

    [Fact]
    public void SwitchMachineNow_WithNoBody_IsRefused400()
    {
        var (status, json) = Post(null);

        Assert.Equal((400, "invalid_body"), (status, json.GetProperty("code").GetString()));
    }

    [Fact]
    public async Task SwitchMachineNow_FromACallerThatIsNotTheOwnersOwnDevice_IsRefusedFirst_AndNothingIsSent()
    {
        Reports(Windows, "dir-win", A, new[] { B });
        var refusal = Results.Json(new { code = "owner_only" }, statusCode: 403);

        var result = await LicenseEndpoints.SwitchMachineNowAsync(Context(), _ => refusal, _ => TenantOne, () => TenantOne, _service, _store,
            new LicenseMachineSwitchNowRequest { MachineId = IdOf(Windows), TargetAccountEmail = B });

        Assert.Same(refusal, result);
        Assert.Empty(_sent);
    }
}
