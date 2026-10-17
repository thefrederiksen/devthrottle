using System.Text;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// SWITCH NOW, THE ROUTE (License Switching): POST /gateway/licenses/switch-now run over the real switch service and the
/// real store on a throwaway SQLite file, with only the Director on the other end of the send made here. What these
/// hold down: the exact shape of the accepted answer; that every refusal is a status, a word and a finished sentence
/// with nothing written or sent; that the account is the caller's own and never anything in the body; that the
/// Licenses page offers Switch now exactly where this route accepts it; and that a machine is called by its label -
/// its name, with its user when two users share a computer - in the answer and in the engine's own sentences.
/// </summary>
public sealed class LicenseSwitchNowEndpointTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId TenantOne = new("acct-one");
    private static readonly TenantId TenantTwo = new("acct-two");
    private const string Windows = "the-windows-machine";
    private const string Mac = "the-mac";

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseSignInLog _signIns;
    private readonly LicenseStore _store;
    private readonly LicenseSnapshotSource _snapshots;
    private readonly LicenseSwitchService _service;
    private readonly List<(string DirectorId, LicenseSwitchRequest Request)> _sent = new();
    private Func<LicenseSwitchRequest, DirectorCommandResult?> _director;
    private DateTime _now = T0;
    private long _sequence;

    public LicenseSwitchNowEndpointTests()
    {
        var db = _harness.Open();
        _store = new LicenseStore(db);
        _approvers = new LicenseApproverStore(new CcDirector.Gateway.Settings.TenantSettingsStore(db));
        _signIns = new LicenseSignInLog(db);
        _snapshots = new LicenseSnapshotSource(_store, new LicenseReadModel(_store));
        _director = request => Answer(request, LicenseSwitchOutcomes.Switched);
        _service = new LicenseSwitchService(_store, _snapshots,
            (directorId, command, _) =>
            {
                var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)!;
                _sent.Add((directorId, request));
                return Task.FromResult(_director(request));
            },
            (_, _, _) => Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null)),
            () => _now);
    }

    public void Dispose() => _harness.Dispose();

    // ---- plumbing ----

    private DirectorCommandResult Answer(LicenseSwitchRequest request, string outcome, string? reasonCode = null)
    {
        var switched = outcome != LicenseSwitchOutcomes.Failed;
        return DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = outcome,
            ReasonCode = reasonCode,
            Message = switched ? "The Director switched." : "The Director refused.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = switched ? request.TargetAccountEmail : request.ExpectedActiveAccountEmail,
            VerifiedByUsageRead = switched,
            StartedAtUtc = _now,
            FinishedAtUtc = _now.AddSeconds(1),
            ExchangeMilliseconds = switched ? 41 : 0,
        }, Web));
    }

    private void Offline() => _director = _ => null;

    private void TimesOut() => _director = _ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout,
        DirectorCommandRouter.DescribeTimeout(Windows, LicenseContract.SwitchCommandTimeout));

    /// <summary>A machine reports now: on <paramref name="active"/>, holding the spares named.</summary>
    private void Reports(TenantId tenant, string machine, string director, string active, string[] spares, string osUser = "owner", DateTime? at = null)
    {
        var when = at ?? _now;
        var report = Report(machine, when, new[] { Holding(active, true, 50, readAt: when) }
            .Concat(spares.Select(s => Holding(s, false, 4, readAt: when))).ToArray());
        report.OsUser = osUser;
        _store.ApplyReport(tenant, director, ++_sequence, report, when, TimeSpan.Zero);
    }

    private static DefaultHttpContext Context()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        return ctx;
    }

    private (int Status, string Text, JsonElement Json) Post(TenantId? tenant, LicenseSwitchNowRequest? body)
    {
        var ctx = Context();
        var result = LicenseEndpoints.SwitchNowAsync(ctx, _ => null, _ => tenant, () => tenant, _service, _store, body).GetAwaiter().GetResult();
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, text, JsonDocument.Parse(text).RootElement.Clone());
    }

    private static LicenseSwitchNowRequest Body(string? account, string? target = null) => new() { AccountEmail = account, TargetAccountEmail = target };

    private JsonElement AccountOnThePage(TenantId tenant, string email)
    {
        var ctx = Context();
        var result = LicenseEndpoints.Overview(ctx, _ => tenant, _snapshots, () => _now, _approvers, _signIns, (_, _) => false);
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray())).RootElement.Clone();
        return json.GetProperty("accounts").EnumerateArray().Single(a => a.GetProperty("accountEmail").GetString() == email);
    }

    private List<LicenseSwitchLogEntity> Switches(TenantId tenant)
        => _store.ListLog(tenant, T0.AddDays(-1)).Where(r => r.Kind == LicenseLogKinds.Switch).OrderBy(r => r.CreatedAtUtc).ToList();

    private static string[] Names(JsonElement json) => json.EnumerateObject().Select(p => p.Name).ToArray();

    private static (string Code, string Error) Refusal(JsonElement json) => (json.GetProperty("code").GetString()!, json.GetProperty("error").GetString()!);

    // ---- accepted ----

    [Fact]
    public void SwitchNow_Accepted_AnswersTheExactShape_AndEachMachinesSwitchAsItsLogRowStands()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });

        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.Equal(200, status);
        Assert.Equal(new[] { "targetAccountEmail", "text", "switches", "skipped" }, Names(json));
        Assert.Equal(B, json.GetProperty("targetAccountEmail").GetString());
        Assert.Equal($"{Windows} is now on {B}.", json.GetProperty("text").GetString());
        Assert.Equal(0, json.GetProperty("skipped").GetArrayLength());
        var sent = Assert.Single(json.GetProperty("switches").EnumerateArray());
        Assert.Equal(new[] { "switchId", "entryId", "machineName", "osUser", "label", "state", "stateText", "stateTone", "summary" }, Names(sent));
        var row = Assert.Single(Switches(TenantOne));
        // The switch identifier the Director was sent, and the row of the log that the page lists it under.
        Assert.Equal((_sent.Single().Request.SwitchId, row.SwitchId, row.Id.ToString("N")),
            (sent.GetProperty("switchId").GetString(), sent.GetProperty("switchId").GetString(), sent.GetProperty("entryId").GetString()));
        Assert.Equal((Windows, "owner", Windows), (sent.GetProperty("machineName").GetString(), sent.GetProperty("osUser").GetString(), sent.GetProperty("label").GetString()));
        Assert.Equal((LicenseLogStates.Switched, "Switched", LicenseTones.Good),
            (sent.GetProperty("state").GetString(), sent.GetProperty("stateText").GetString(), sent.GetProperty("stateTone").GetString()));
        // The same sentence the switch log shows for that row.
        Assert.Equal(LicenseDisplayFold.LogEntry(row).Summary, sent.GetProperty("summary").GetString());
        Assert.Equal((A, B, LicenseSwitchReasons.Manual), (_sent.Single().Request.ExpectedActiveAccountEmail, _sent.Single().Request.TargetAccountEmail, _sent.Single().Request.Reason));
    }

    [Fact]
    public void SwitchNow_ANamedTarget_IsTheOneTheMachinesAreSentTo_WhateverItsSpelling()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B, C });

        var (status, _, json) = Post(TenantOne, Body("  Account-A@Example.com ", " ACCOUNT-C@example.com"));

        Assert.Equal((200, C), (status, json.GetProperty("targetAccountEmail").GetString()));
        Assert.Equal(C, _sent.Single().Request.TargetAccountEmail);
    }

    [Fact]
    public void SwitchNow_ToAMachineWhoseDirectorIsNotConnected_IsAccepted_AndSaysItHasNotFinished()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        Offline();

        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.Equal(200, status);
        var sent = json.GetProperty("switches")[0];
        Assert.Equal((LicenseLogStates.Pending, "Waiting for the machine", LicenseTones.Warning),
            (sent.GetProperty("state").GetString(), sent.GetProperty("stateText").GetString(), sent.GetProperty("stateTone").GetString()));
        Assert.Equal($"The switch of {Windows} to {B} has not finished; the switch log shows how it ends.", json.GetProperty("text").GetString());
    }

    [Fact]
    public void SwitchNow_TheDirectorRefuses_IsStillAnAcceptedCall_AndSaysTheSwitchFailed()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        _director = request => Answer(request, LicenseSwitchOutcomes.Failed, LicenseSwitchReasonCodes.SpareNotUsable);

        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.Equal(200, status);
        Assert.Equal((LicenseLogStates.Failed, "Failed", LicenseTones.Bad),
            (json.GetProperty("switches")[0].GetProperty("state").GetString(), json.GetProperty("switches")[0].GetProperty("stateText").GetString(),
             json.GetProperty("switches")[0].GetProperty("stateTone").GetString()));
        Assert.Equal($"The switch of {Windows} to {B} failed; the switch log says why.", json.GetProperty("text").GetString());
    }

    [Fact]
    public void SwitchNow_TwoMachines_OneSwitchedAndOneWaiting_SaysBoth_AndAMachineLeftOutIsNamedWithItsReason()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        Reports(TenantOne, Mac, "dir-mac", A, new[] { B });
        Reports(TenantOne, "the-linux-machine", "dir-linux", A, new[] { B }, at: _now.AddHours(-2));   // its last report is too old
        _director = request => _sent[^1].DirectorId == "dir-mac" ? null : Answer(request, LicenseSwitchOutcomes.Switched);

        var (status, _, json) = Post(TenantOne, Body(A, B));

        Assert.Equal(200, status);
        Assert.Equal(2, json.GetProperty("switches").GetArrayLength());
        var skipped = Assert.Single(json.GetProperty("skipped").EnumerateArray());
        Assert.Equal(new[] { "machineName", "osUser", "label", "why" }, Names(skipped));
        var why = skipped.GetProperty("why").GetString()!;
        Assert.StartsWith("the-linux-machine is not switched: ", why);
        Assert.EndsWith(".", why);
        Assert.Equal($"{Windows} is now on {B}. The switch of {Mac} to {B} has not finished; the switch log shows how it ends. {why}",
            json.GetProperty("text").GetString());
    }

    [Fact]
    public void SwitchNow_WithNoTarget_ThatSendsMachinesToTwoAccounts_SaysWhereEachOneIs_AndNamesNoSingleTarget()
    {
        // The best account (B, ordinal tie-break) is held by the Windows machine only; the Mac holds only C. With no
        // target the moves are the automatic ones, so the two machines go to two accounts - and the answer says so.
        Reports(TenantOne, Windows, "dir-win", A, new[] { B, C });
        Reports(TenantOne, Mac, "dir-mac", A, new[] { C });

        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.Equal(200, status);
        Assert.Equal(new[] { ("dir-mac", C), ("dir-win", B) },
            _sent.Select(s => (s.DirectorId, s.Request.TargetAccountEmail)).OrderBy(s => s.DirectorId, StringComparer.Ordinal).ToArray());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("targetAccountEmail").ValueKind);
        Assert.Equal($"{Mac} is now on {C}. {Windows} is now on {B}.", json.GetProperty("text").GetString());
    }

    // ---- refused: a status, a word, a finished sentence - and nothing written or sent ----

    [Fact]
    public void SwitchNow_ToAnAccountAMachineHoldsNoSpareFor_IsRefused409_WithTheSentenceThePageAlreadyShows()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        Reports(TenantOne, Mac, "dir-mac", C, new[] { A });
        var shown = AccountOnThePage(TenantOne, A).GetProperty("switchTargets").EnumerateArray()
            .Single(t => t.GetProperty("accountEmail").GetString() == C).GetProperty("unavailableText").GetString();

        var (status, _, json) = Post(TenantOne, Body(A, C));

        Assert.Equal(409, status);
        Assert.Equal(new[] { "code", "error" }, Names(json));
        Assert.Equal((LicenseManualRefusals.TargetNotHeld, $"{C} has room; {Windows} holds no sign-in for it."), Refusal(json));
        Assert.Equal(shown, json.GetProperty("error").GetString());
        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    [Fact]
    public void SwitchNow_ToTheAccountTheMachinesAreAlreadyOn_IsRefused409()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });

        var (status, _, json) = Post(TenantOne, Body(A, A));

        Assert.Equal((409, LicenseManualRefusals.TargetIsSource), (status, Refusal(json).Code));
        Assert.Equal($"The machines are already on {A}.", Refusal(json).Error);
        Assert.Empty(_sent);
    }

    [Fact]
    public void SwitchNow_EveryRefusalTheEngineKnows_IsAnswered409_WithItsWordAndASentenceThatEndsInAFullStop()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        Reports(TenantOne, Mac, "dir-mac", C, new[] { A });

        var codes = new HashSet<string>();
        foreach (var (account, target) in new[] { (A, (string?)A), (A, C), (B, null), ("nobody@example.com", null), (A, "nobody@example.com") })
        {
            var (status, _, json) = Post(TenantOne, Body(account, target));

            Assert.Equal(409, status);
            Assert.Contains(Refusal(json).Code, new[]
            {
                LicenseManualRefusals.TargetIsSource, LicenseManualRefusals.TargetNotHeld, LicenseManualRefusals.NoMachineToSwitch,
                // An account no machine reports is judged on its own first: no reading shows it has room.
                LicenseManualRefusals.TargetStale,
            });
            codes.Add(Refusal(json).Code);
            Assert.EndsWith(".", Refusal(json).Error);
            Assert.DoesNotContain("  ", Refusal(json).Error);
        }
        Assert.Equal(4, codes.Count);
        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    [Fact]
    public void SwitchNow_RefusedWithAMachineLeftOut_SaysTheReasonForThatMachineToo()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B }, at: _now.AddHours(-2));   // the only machine, and stale

        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.Equal((409, LicenseManualRefusals.NoMachineToSwitch), (status, Refusal(json).Code));
        Assert.StartsWith($"No machine on {A} can be switched now: every machine on it is stale or already mid-switch. {Windows} is not switched: ", Refusal(json).Error);
        // The same sentence the page shows for that account.
        Assert.False(AccountOnThePage(TenantOne, A).GetProperty("canSwitchNow").GetBoolean());
    }

    [Fact]
    public void SwitchNow_WithNoBody_NoAccount_OrNoDevThrottleAccount_IsRefusedBeforeAnythingIsAsked()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });

        var noBody = Post(TenantOne, null);
        var noEmail = Post(TenantOne, Body("  "));
        var noTenant = Post(null, Body(A));

        Assert.Equal((400, "invalid_body"), (noBody.Status, Refusal(noBody.Json).Code));
        Assert.Equal((400, "account_required"), (noEmail.Status, Refusal(noEmail.Json).Code));
        Assert.Equal((403, "no_account"), (noTenant.Status, Refusal(noTenant.Json).Code));
        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    [Fact]
    public void SwitchNow_AnAccountOrATargetLongerThanItsColumn_OrHoldingAControlCharacter_IsRefused_AndNeverSaidBack()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        var tooLong = new string('a', 321);

        foreach (var body in new[] { Body(tooLong), Body("x\ny"), Body(A, tooLong), Body(A, "x\ry"), Body(A, "x\u0000y"), Body("x\u2028y"), Body(A, "x\u202Ey") })
        {
            var (status, text, json) = Post(TenantOne, body);

            Assert.Equal((400, "invalid_account"), (status, Refusal(json).Code));
            Assert.DoesNotContain("aaaa", text);
            Assert.DoesNotContain("\\n", text);
        }
        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    [Fact]
    public async Task SwitchNow_FromACallerThatIsNotTheOwnersOwnDevice_IsRefusedFirst_AndNothingIsWrittenOrSent()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        var ctx = Context();
        var refusal = Results.Json(new { code = "owner_only", error = "only the owner may" }, statusCode: 403);

        var result = await LicenseEndpoints.SwitchNowAsync(ctx, _ => refusal, _ => TenantOne, () => TenantOne, _service, _store, Body(A));

        Assert.Same(refusal, result);
        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    // ---- whose account ----

    [Fact]
    public void SwitchNow_MovesOnlyTheCallersOwnMachines_AndAnothersAccountIsAnAccountNoMachineIsOn()
    {
        Reports(TenantOne, Windows, "dir-one", A, new[] { B });
        Reports(TenantTwo, Mac, "dir-two", A, new[] { B });
        Reports(TenantTwo, "the-linux-machine", "dir-two-linux", C, new[] { B });

        var others = Post(TenantOne, Body(C));              // an account only the other DevThrottle account has
        var nobodys = Post(TenantOne, Body("nobody@example.com"));
        var own = Post(TenantOne, Body(A));                 // the same email in both: only the caller's machine moves

        Assert.Equal((409, LicenseManualRefusals.NoMachineToSwitch), (others.Status, Refusal(others.Json).Code));
        Assert.Equal(Refusal(nobodys.Json).Error.Replace("nobody@example.com", C), Refusal(others.Json).Error);
        Assert.Equal(200, own.Status);
        Assert.Equal("dir-one", Assert.Single(_sent).DirectorId);
        Assert.Empty(Switches(TenantTwo));
    }

    [Fact]
    public async Task SwitchNow_OutsideTheScopeOfTheCallersOwnAccount_IsADefect_AndNothingIsWrittenOrSent()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });

        foreach (var scope in new TenantId?[] { TenantTwo, null })
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                LicenseEndpoints.SwitchNowAsync(Context(), _ => null, _ => TenantOne, () => scope, _service, _store, Body(A)));

        Assert.Empty(_sent);
        Assert.Empty(Switches(TenantOne));
    }

    // ---- the page offers Switch now exactly where the route accepts it ----

    [Fact]
    public void TheSameSwitchNow_AskedAgainWhileItsSwitchWaitsForTheDirector_AnswersWithTheWaitingSwitch_AndWritesNothing()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        Offline();
        var first = Post(TenantOne, Body(A, B));
        var waiting = Assert.Single(Switches(TenantOne));

        var named = Post(TenantOne, Body(A, B));          // the same target, named
        var chosen = Post(TenantOne, Body(A));            // the same target, chosen by the Gateway

        foreach (var again in new[] { named, chosen })
        {
            Assert.Equal(200, again.Status);
            Assert.Equal(first.Json.GetProperty("switches")[0].GetProperty("switchId").GetString(),
                again.Json.GetProperty("switches")[0].GetProperty("switchId").GetString());
            Assert.Equal(first.Json.GetProperty("text").GetString(), again.Json.GetProperty("text").GetString());
        }
        // One row, still the one that was waiting, untouched - and nothing more was sent for it.
        var row = Assert.Single(Switches(TenantOne));
        Assert.Equal((waiting.Id, LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline, waiting.SendCount),
            (row.Id, row.State, row.ReasonCode, row.SendCount));
        Assert.Single(_sent);
    }

    [Fact]
    public void AMachineWhoseSwitchIsOnlyWaitingForItsDirector_CanBeSwitchedNow_OnThePage_AndByTheRoute()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B, C });
        Offline();
        Assert.Equal(200, Post(TenantOne, Body(A, B)).Status);
        var waiting = Assert.Single(Switches(TenantOne));
        Assert.Equal((LicenseLogStates.Pending, LicenseGatewayReasonCodes.DirectorOffline), (waiting.State, waiting.ReasonCode));

        var account = AccountOnThePage(TenantOne, A);
        Assert.True(account.GetProperty("canSwitchNow").GetBoolean());
        Assert.StartsWith($"Moves {Windows} to ", account.GetProperty("switchNowText").GetString());

        // A DIFFERENT decision - another target - is a new switch: the waiting one is ended, never sent beside it.
        _director = request => Answer(request, LicenseSwitchOutcomes.Switched);
        var (status, _, json) = Post(TenantOne, Body(A, C));

        Assert.Equal((200, LicenseLogStates.Switched), (status, json.GetProperty("switches")[0].GetProperty("state").GetString()));
        var rows = Switches(TenantOne);
        // The switch that was waiting is ended, never sent beside the new one; the new one ran.
        Assert.Equal((LicenseLogStates.Failed, LicenseGatewayReasonCodes.DirectorOffline), (rows.Single(r => r.Id == waiting.Id).State, rows.Single(r => r.Id == waiting.Id).ReasonCode));
        Assert.Equal(LicenseLogStates.Switched, rows.Single(r => r.Id != waiting.Id).State);
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void AMachineWhoseSwitchWasSentAndNotAnswered_CannotBeSwitchedNow_OnThePage_OrByTheRoute()
    {
        Reports(TenantOne, Windows, "dir-win", A, new[] { B });
        TimesOut();
        Assert.Equal(200, Post(TenantOne, Body(A)).Status);
        Assert.Equal(LicenseLogStates.Unknown, Assert.Single(Switches(TenantOne)).State);

        var account = AccountOnThePage(TenantOne, A);
        var (status, _, json) = Post(TenantOne, Body(A));

        Assert.False(account.GetProperty("canSwitchNow").GetBoolean());
        Assert.Equal((409, LicenseManualRefusals.NoMachineToSwitch), (status, Refusal(json).Code));
        // The page and the route say the same thing about it.
        Assert.Equal(account.GetProperty("switchNowText").GetString(), Refusal(json).Error);
        Assert.Single(_sent);
        Assert.Single(Switches(TenantOne));
    }

    // ---- two users of one computer are two machines, and each sentence says which ----

    [Fact]
    public void TwoUsersOfOneComputer_AreNamedWithTheirUser_InARefusal_AndInAnAcceptedAnswer()
    {
        Reports(TenantOne, Windows, "dir-first", A, new[] { B }, osUser: "first");
        Reports(TenantOne, Windows, "dir-second", A, new[] { B }, osUser: "second");
        Reports(TenantOne, Mac, "dir-mac", C, new[] { A });

        var refused = Post(TenantOne, Body(A, C));
        var accepted = Post(TenantOne, Body(A, B));

        Assert.Equal((409, $"{C} has room; {Windows} (first) holds no sign-in for it; {Windows} (second) holds no sign-in for it."),
            (refused.Status, Refusal(refused.Json).Error));
        Assert.Equal(200, accepted.Status);
        Assert.Equal(new[] { $"{Windows} (first)", $"{Windows} (second)" },
            accepted.Json.GetProperty("switches").EnumerateArray().Select(s => s.GetProperty("label").GetString()).OrderBy(l => l, StringComparer.Ordinal));
        Assert.Equal($"{Windows} (first) and {Windows} (second) are now on {B}.",
            accepted.Json.GetProperty("text").GetString()!.Replace($"{Windows} (second) and {Windows} (first)", $"{Windows} (first) and {Windows} (second)"));
    }
}
