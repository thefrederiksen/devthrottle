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
/// THE SIGN-IN ROUTES OF THE LICENSES PAGE (License Switching, contract section 8), each handler run over the real
/// stores on a throwaway SQLite file with fake Directors behind the command, and its answer read back as the JSON the
/// Cockpit receives. What these hold down: the exact shape of each answer (the page is built from these names); every
/// verdict a finished word, sentence or boolean; each refusal a status, a word and a sentence; the overview and the log
/// saying the same of a sign-in as the routes do; and one account never seeing or reaching another's.
/// </summary>
public sealed class LicenseSignInEndpointsTests : IDisposable
{
    private static readonly TenantId One = new("acct-one");
    private static readonly TenantId Two = new("acct-two");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseSignInLog _log;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseSignInService _signIns;
    private readonly DateTime _now = T0;
    private long _sequence;

    public LicenseSignInEndpointsTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _reads = new LicenseReadModel(_store);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _log = new LicenseSignInLog(_db);
        _signIns = new LicenseSignInService(_log, _reads, _directors.IsConnected, _directors.SendAsync, _directors.EnterScope,
            () => _now, CancellationToken.None);
    }

    public void Dispose() => _harness.Dispose();

    // ---- plumbing ----

    private static DefaultHttpContext Context(string query = "", object? body = null, string? rawBody = null, string contentType = "application/json")
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Request.QueryString = new QueryString(query);
        ctx.Response.Body = new MemoryStream();
        var text = rawBody ?? (body is null ? null : JsonSerializer.Serialize(body, FakeLicenseDirectors.Web));
        if (text is not null)
        {
            ctx.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(text));
            ctx.Request.ContentType = contentType;
        }
        return ctx;
    }

    private static (int Status, JsonElement Json) Read(DefaultHttpContext ctx, IResult result)
    {
        result.ExecuteAsync(ctx).GetAwaiter().GetResult();
        var text = Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray());
        return (ctx.Response.StatusCode, JsonDocument.Parse(text).RootElement.Clone());
    }

    private (int Status, JsonElement Json) List(TenantId? tenant)
    {
        var ctx = Context();
        return Read(ctx, LicenseSignInEndpoints.ListApprovers(ctx, _ => tenant, _approvers, _reads, _directors.IsConnected, () => _now));
    }

    private (int Status, JsonElement Json) Set(TenantId? tenant, object? body, string? raw = null, string contentType = "application/json")
    {
        var ctx = Context(body: body, rawBody: raw, contentType: contentType);
        return Read(ctx, LicenseSignInEndpoints.SetApproverAsync(ctx, _ => tenant, _approvers, _reads, _directors.IsConnected, () => _now)
            .GetAwaiter().GetResult());
    }

    private (int Status, JsonElement Json) Remove(TenantId? tenant, string query)
    {
        var ctx = Context(query);
        return Read(ctx, LicenseSignInEndpoints.RemoveApprover(ctx, _ => tenant, _approvers, () => _now));
    }

    private (int Status, JsonElement Json) Start(TenantId? tenant, string reason, object? body, string? raw = null)
    {
        var ctx = Context(body: body, rawBody: raw);
        return Read(ctx, LicenseSignInEndpoints.StartSignInAsync(ctx, _ => tenant, _signIns, reason).GetAwaiter().GetResult());
    }

    private (int Status, JsonElement Json) AddSpare(TenantId? tenant, string machineId, string account = B)
        => Start(tenant, LicenseSignInReasons.Manual, new LicenseStartSignInRequest { AccountEmail = account, MachineId = machineId });

    private (int Status, JsonElement Json) Renew(TenantId? tenant, string machineId, string account = A)
        => Start(tenant, LicenseSignInReasons.Renewal, new LicenseStartSignInRequest { AccountEmail = account, MachineId = machineId });

    private JsonElement Overview(TenantId tenant)
    {
        var ctx = Context();
        var (status, json) = Read(ctx, LicenseEndpoints.Overview(ctx, _ => tenant, new LicenseSnapshotSource(_store, _reads), () => _now,
            _approvers, _log, _directors.IsConnected));
        Assert.Equal(200, status);
        return json;
    }

    private JsonElement LogEntries(TenantId tenant)
    {
        var ctx = Context();
        var (status, json) = Read(ctx, LicenseEndpoints.Log(ctx, _ => tenant, _store, () => _now));
        Assert.Equal(200, status);
        return json.GetProperty("entries");
    }

    /// <summary>The machine's Director reports these holdings now and is connected. Returns the machine's identifier.</summary>
    private string Reports(TenantId tenant, string machine, string director, params LicenseHolding[] holdings)
    {
        var id = _store.ApplyReport(tenant, director, ++_sequence, Report(machine, _now, holdings), _now, TimeSpan.Zero).MachineId;
        _directors.Connect(tenant, director);
        return id.ToString("N");
    }

    private static LicenseSetApproverRequest Approver(string account = A, string machine = "the-mac", string user = "owner", string profile = "work")
        => new() { AccountEmail = account, MachineName = machine, OsUser = user, BrowserProfile = profile };

    private static string[] Names(JsonElement element) => element.EnumerateObject().Select(p => p.Name).ToArray();

    private static void AssertRefusal((int Status, JsonElement Json) answer, int status, string code)
    {
        Assert.Equal(status, answer.Status);
        Assert.Equal(new[] { "code", "error" }, Names(answer.Json));
        Assert.Equal(code, answer.Json.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(answer.Json.GetProperty("error").GetString()));
    }

    // ---- who approves ----

    [Fact]
    public void ListApprovers_WhenNoneIsSet_IsEmptyAndSaysSoInASentence()
    {
        var (status, json) = List(One);

        Assert.Equal(200, status);
        Assert.Equal(new[] { "asOfUtc", "isEmpty", "emptyText", "approvers" }, Names(json));
        Assert.True(json.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(LicenseSignInFold.NoApproversText, json.GetProperty("emptyText").GetString());
        Assert.Equal(0, json.GetProperty("approvers").GetArrayLength());
        Assert.Equal("2026-10-11T03:12:00Z", json.GetProperty("asOfUtc").GetString());
    }

    [Fact]
    public void SetApprover_AnswersTheApproverInTheExactShapeThePageIsBuiltFrom_AndTheListShowsIt()
    {
        var mac = Reports(One, "the-mac", "dir-mac");

        var (status, set) = Set(One, Approver(account: " Account-A@Example.com "));

        Assert.Equal(200, status);
        Assert.Equal(new[]
        {
            "accountEmail", "machineName", "osUser", "browserProfile", "machineId", "label", "canApproveNow", "status", "statusText",
            "statusTone", "text", "browserProfileName", "source", "state", "checkedAtUtc", "healthText", "whatToDo",
        }, Names(set));
        Assert.Equal((A, "the-mac", "owner", "work", mac, "the-mac"),
            (set.GetProperty("accountEmail").GetString(), set.GetProperty("machineName").GetString(), set.GetProperty("osUser").GetString(),
             set.GetProperty("browserProfile").GetString(), set.GetProperty("machineId").GetString(), set.GetProperty("label").GetString()));
        // Just set by the owner, so never checked: an absence is never read as healthy, and the page says so.
        Assert.False(set.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(("cannot-approve", "Not checked", "warning", "The approver for account-a@example.com (browser profile \"work\" on the-mac) has never been checked. Make sure the Director on the-mac is running with license switching on; it checks its approvers once a day."),
            (set.GetProperty("status").GetString(), set.GetProperty("statusText").GetString(), set.GetProperty("statusTone").GetString(),
             set.GetProperty("text").GetString()));
        Assert.Equal(("owner", "not-checked", JsonValueKind.Null),
            (set.GetProperty("source").GetString(), set.GetProperty("state").GetString(), set.GetProperty("checkedAtUtc").ValueKind));
        Assert.Equal("The approver for account-a@example.com (browser profile \"work\" on the-mac) has never been checked.",
            set.GetProperty("healthText").GetString());
        Assert.Equal("Make sure the Director on the-mac is running with license switching on; it checks its approvers once a day.",
            set.GetProperty("whatToDo").GetString());

        var list = List(One).Json;
        Assert.False(list.GetProperty("isEmpty").GetBoolean());
        Assert.Equal(JsonValueKind.Null, list.GetProperty("emptyText").ValueKind);
        Assert.Equal(set.GetRawText(), Assert.Single(list.GetProperty("approvers").EnumerateArray().ToList()).GetRawText());
    }

    [Fact]
    public void AnApprover_WhoseDirectorIsNotConnected_SaysSo_AndCannotApproveNow()
    {
        Reports(One, "the-mac", "dir-mac");
        _directors.Disconnect(One, "dir-mac");

        var set = Set(One, Approver()).Json;

        Assert.False(set.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(("not-connected", "Not connected", "warning"),
            (set.GetProperty("status").GetString(), set.GetProperty("statusText").GetString(), set.GetProperty("statusTone").GetString()));
        Assert.Equal("Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work. The Director on the-mac is not connected, so no sign-in can be approved there now.",
            set.GetProperty("text").GetString());
    }

    [Fact]
    public void AnApprover_OnAMachineThatNeverReported_SaysSo_AndHasNoMachineIdentifier()
    {
        var set = Set(One, Approver()).Json;

        Assert.False(set.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(JsonValueKind.Null, set.GetProperty("machineId").ValueKind);
        Assert.Equal(("never-reported", "Not reporting yet", "warning"),
            (set.GetProperty("status").GetString(), set.GetProperty("statusText").GetString(), set.GetProperty("statusTone").GetString()));
        Assert.Equal("Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work. No Director on the-mac has reported to this account, so no sign-in can be approved there yet.",
            set.GetProperty("text").GetString());
    }

    [Fact]
    public void AnApprover_OnAMachineConnectedOnlyInAnotherAccount_IsNotReadyInThisOne()
    {
        Reports(Two, "the-mac", "dir-mac");

        var set = Set(One, Approver()).Json;

        Assert.Equal("never-reported", set.GetProperty("status").GetString());
        Assert.False(set.GetProperty("canApproveNow").GetBoolean());
    }

    [Fact]
    public void SetApprover_AValueThatIsNotWhatItClaimsToBe_IsRefusedWithAWordAndASentence_AndNothingIsSet()
    {
        AssertRefusal(Set(One, Approver(account: "not-an-email")), 400, "invalid_account");
        AssertRefusal(Set(One, Approver(machine: "")), 400, "invalid_machine");
        AssertRefusal(Set(One, Approver(profile: "--user-data-dir=x")), 400, "invalid_browser_profile");

        Assert.True(List(One).Json.GetProperty("isEmpty").GetBoolean());
    }

    [Fact]
    public void SetApprover_ABodyThatCannotBeRead_IsRefusedAsTheCallersMistake()
    {
        AssertRefusal(Set(One, null, raw: "{ not JSON"), 400, "bad_request_body");
        AssertRefusal(Set(One, null, raw: "null"), 400, "bad_request_body");
        AssertRefusal(Set(One, Approver(), contentType: "text/plain"), 400, "bad_request_body");
    }

    [Fact]
    public void RemoveApprover_SaysWhetherThereWasOne_InASentence()
    {
        Set(One, Approver());

        var (status, removed) = Remove(One, "?accountEmail=Account-A%40Example.com");
        var again = Remove(One, "?accountEmail=" + Uri.EscapeDataString(A)).Json;

        Assert.Equal(200, status);
        Assert.Equal(new[] { "accountEmail", "removed", "text" }, Names(removed));
        Assert.Equal((A, true, "No machine is set to approve sign-ins for account-a@example.com any more."),
            (removed.GetProperty("accountEmail").GetString(), removed.GetProperty("removed").GetBoolean(), removed.GetProperty("text").GetString()));
        Assert.Equal((false, "No machine was set to approve sign-ins for account-a@example.com."),
            (again.GetProperty("removed").GetBoolean(), again.GetProperty("text").GetString()));
        Assert.True(List(One).Json.GetProperty("isEmpty").GetBoolean());
    }

    [Theory]
    [InlineData("")]
    [InlineData("?accountEmail=")]
    [InlineData("?accountEmail=not-an-email")]
    public void RemoveApprover_WithNoAccount_IsRefused(string query)
        => AssertRefusal(Remove(One, query), 400, "invalid_account");

    [Fact]
    public void Approvers_OfOneAccount_AreNeverListedSetOverOrRemovedByAnother()
    {
        Set(One, Approver(profile: "profile-of-one"));
        Set(Two, Approver(profile: "profile-of-two"));

        Assert.Equal("profile-of-one", List(One).Json.GetProperty("approvers")[0].GetProperty("browserProfile").GetString());
        Assert.Equal("profile-of-two", List(Two).Json.GetProperty("approvers")[0].GetProperty("browserProfile").GetString());

        Assert.True(Remove(Two, "?accountEmail=" + Uri.EscapeDataString(A)).Json.GetProperty("removed").GetBoolean());
        Assert.Equal(1, List(One).Json.GetProperty("approvers").GetArrayLength());
        Assert.True(List(Two).Json.GetProperty("isEmpty").GetBoolean());
    }

    [Fact]
    public void EveryRoute_WithNoAccountBoundToTheRequest_IsRefused_AndDoesNothing()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true));

        AssertRefusal(List(null), 403, "no_account");
        AssertRefusal(Set(null, Approver()), 403, "no_account");
        AssertRefusal(Remove(null, "?accountEmail=" + Uri.EscapeDataString(A)), 403, "no_account");
        AssertRefusal(AddSpare(null, mac), 403, "no_account");
        AssertRefusal(Renew(null, mac), 403, "no_account");

        Assert.Empty(_directors.Sends);
        Assert.True(List(One).Json.GetProperty("isEmpty").GetBoolean());
    }

    // ---- starting a sign-in ----

    [Fact]
    public async Task AddSpare_AnswersAcceptedAtOnce_InTheExactShapeThePageIsBuiltFrom()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true));
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;

        // The call is given a limit so that a call that waits for the Director is a red test, not a run that never ends.
        var (status, json) = await Task.Run(() => AddSpare(One, mac)).WaitAsync(TimeSpan.FromSeconds(60));

        // The route has answered while the Director still holds the command.
        Assert.Equal(202, status);
        Assert.Equal(new[] { "signInId", "accountEmail", "machine", "text", "entry" }, Names(json));
        Assert.Matches(LicenseContract.SignInIdShape, json.GetProperty("signInId").GetString()!);
        Assert.Equal(B, json.GetProperty("accountEmail").GetString());
        Assert.Equal(new[] { "machineId", "machineName", "osUser", "label" }, Names(json.GetProperty("machine")));
        Assert.Equal((mac, "the-mac"), (json.GetProperty("machine").GetProperty("machineId").GetString(), json.GetProperty("machine").GetProperty("label").GetString()));
        Assert.Equal("Asked the Director on the-mac to sign in to account-b@example.com as a new spare. It can take a few minutes; the outcome appears in the log.",
            json.GetProperty("text").GetString());
        var entry = json.GetProperty("entry");
        Assert.Equal(("sign-in", "Sign-in", "pending", "Signing in", "neutral", true, "manual", "Add a spare"),
            (entry.GetProperty("kind").GetString(), entry.GetProperty("kindText").GetString(), entry.GetProperty("state").GetString(),
             entry.GetProperty("stateText").GetString(), entry.GetProperty("stateTone").GetString(), entry.GetProperty("isOpen").GetBoolean(),
             entry.GetProperty("why").GetString(), entry.GetProperty("whyText").GetString()));
        // The entry is the same row the log route answers, name for name.
        var logged = Assert.Single(LogEntries(One).EnumerateArray().ToList());
        Assert.Equal(Names(logged), Names(entry));
        Assert.Equal(entry.GetProperty("entryId").GetString(), logged.GetProperty("entryId").GetString());

        answering.SetResult(null);
        await _signIns.WhenIdleAsync();
    }

    [Fact]
    public async Task TheOutcomeOfASignIn_LandsInTheLog_AsItsOwnKindOfRow_InWords()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true));
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
        {
            SignInId = ((LicenseSignInRequest)sent.Payload).SignInId,
            Outcome = LicenseSignInOutcomes.Failed,
            ReasonCode = LicenseRenewalErrors.ApproverOffline,
            Message = "The approving machine was offline.",
            FinishedAtUtc = _now,
        }));
        Assert.Equal(202, AddSpare(One, mac).Status);
        await _signIns.WhenIdleAsync();
        _directors.AnswerWith(sent => FakeLicenseDirectors.Ok(new LicenseSignInResult
            { SignInId = ((LicenseSignInRequest)sent.Payload).SignInId, Outcome = LicenseSignInOutcomes.Renewed, Message = "Renewed.", FinishedAtUtc = _now }));
        Assert.Equal(202, Renew(One, mac).Status);
        await _signIns.WhenIdleAsync();

        var entries = LogEntries(One).EnumerateArray().ToList();

        Assert.Equal(2, entries.Count);
        var failed = entries.Single(e => e.GetProperty("state").GetString() == "failed");
        Assert.Equal(("sign-in", "Sign-in", "Failed", "bad", false, "approver-offline", "The machine set to approve this account had no Director connected.",
                "The approving machine was offline.", "the-mac was not signed in to account-b@example.com.", "Add a spare"),
            (failed.GetProperty("kind").GetString(), failed.GetProperty("kindText").GetString(), failed.GetProperty("stateText").GetString(),
             failed.GetProperty("stateTone").GetString(), failed.GetProperty("isOpen").GetBoolean(), failed.GetProperty("reasonCode").GetString(),
             failed.GetProperty("reasonText").GetString(), failed.GetProperty("message").GetString(), failed.GetProperty("summary").GetString(),
             failed.GetProperty("whyText").GetString()));
        var renewed = entries.Single(e => e.GetProperty("state").GetString() == "renewed");
        Assert.Equal(("Renewed", "good", "The sign-in for account-a@example.com on the-mac was renewed.", "Renew now", A),
            (renewed.GetProperty("stateText").GetString(), renewed.GetProperty("stateTone").GetString(), renewed.GetProperty("summary").GetString(),
             renewed.GetProperty("whyText").GetString(), renewed.GetProperty("toAccountEmail").GetString()));
    }

    [Fact]
    public async Task StartingASignIn_ThatCannotBeStarted_IsRefusedWithAStatusAWordAndAFinishedSentence()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true));
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;

        AssertRefusal(AddSpare(One, mac, "not-an-email"), 400, "invalid_account");
        AssertRefusal(AddSpare(One, "the-mac"), 400, "invalid_machine");
        AssertRefusal(AddSpare(One, Guid.NewGuid().ToString("N")), 404, "unknown_machine");
        AssertRefusal(AddSpare(One, mac, A), 409, "already_held");
        AssertRefusal(Renew(One, mac, B), 409, "not_held");
        AssertRefusal(Start(One, LicenseSignInReasons.Manual, null, raw: "{ not JSON"), 400, "bad_request_body");

        Assert.Equal(202, AddSpare(One, mac).Status);
        var running = AddSpare(One, mac);
        AssertRefusal(running, 409, "sign_in_running");
        Assert.Equal("A sign-in for account-b@example.com on the-mac is already running.", running.Json.GetProperty("error").GetString());

        _directors.Disconnect(One, "dir-mac");
        var offline = AddSpare(One, mac, C);
        AssertRefusal(offline, 409, "director_offline");
        Assert.Equal("The Director on the-mac is not connected, so a sign-in cannot be started there.", offline.Json.GetProperty("error").GetString());

        answering.SetResult(null);
        await _signIns.WhenIdleAsync();
        Assert.Single(_directors.Sends);
    }

    [Fact]
    public void StartingASignIn_OnAMachineOfAnotherAccount_IsRefusedAsUnknown_AndThatMachineIsSentNothing()
    {
        var macOfTwo = Reports(Two, "the-mac", "dir-mac-of-two", Holding(A, true));

        AssertRefusal(AddSpare(One, macOfTwo), 404, "unknown_machine");
        AssertRefusal(Renew(One, macOfTwo), 404, "unknown_machine");

        Assert.Empty(_directors.Sends);
        Assert.Equal(0, LogEntries(Two).GetArrayLength());
    }

    // ---- what the overview says of all this ----

    [Fact]
    public void Overview_SaysPerAccountWhoApprovesIt_AsFinishedWords()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true), Holding(B, false));
        Set(One, Approver());

        var accounts = Overview(One).GetProperty("accounts").EnumerateArray().ToList();

        var a = accounts.Single(x => x.GetProperty("accountEmail").GetString() == A);
        Assert.Equal(List(One).Json.GetProperty("approvers")[0].GetRawText(), a.GetProperty("approver").GetRawText());
        Assert.Equal(("The approver for account-a@example.com (browser profile \"work\" on the-mac) has never been checked. Make sure the Director on the-mac is running with license switching on; it checks its approvers once a day.", "warning"),
            (a.GetProperty("approverText").GetString(), a.GetProperty("approverTone").GetString()));
        var b = accounts.Single(x => x.GetProperty("accountEmail").GetString() == B);
        Assert.Equal(JsonValueKind.Null, b.GetProperty("approver").ValueKind);
        Assert.Equal((LicenseSignInFold.NoApproverForAccountText, "neutral"), (b.GetProperty("approverText").GetString(), b.GetProperty("approverTone").GetString()));
    }

    [Fact]
    public void Overview_OffersAddASpareAndRenewNow_OnAMachineWhoseDirectorIsConnected()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true), Holding(B, false));

        var machine = Overview(One).GetProperty("machines")[0];

        Assert.True(machine.GetProperty("canAddSpare").GetBoolean());
        Assert.Equal("Signs the-mac in to another account, to be held there as a spare.", machine.GetProperty("addSpareText").GetString());
        Assert.Equal(0, machine.GetProperty("signInsInProgress").GetArrayLength());
        Assert.True(machine.GetProperty("active").GetProperty("canRenewNow").GetBoolean());
        Assert.Equal("Signs the-mac in to account-a@example.com again, replacing the sign-in it holds.", machine.GetProperty("active").GetProperty("renewNowText").GetString());
        Assert.True(machine.GetProperty("spares")[0].GetProperty("canRenewNow").GetBoolean());
    }

    [Fact]
    public void Overview_NamesTheDirectorThatReportsEachMachine_AndOffersItsBrowserProfiles_WhileItIsConnected()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Reports(One, "the-windows-machine", "dir-windows", Holding(B, true));

        var machines = Overview(One).GetProperty("machines").EnumerateArray()
            .ToDictionary(m => m.GetProperty("machineName").GetString()!);

        Assert.Equal("dir-mac", machines["the-mac"].GetProperty("directorId").GetString());
        Assert.Equal("dir-windows", machines["the-windows-machine"].GetProperty("directorId").GetString());
        Assert.True(machines["the-mac"].GetProperty("canListBrowserProfiles").GetBoolean());
        Assert.Equal("The browser profiles of the-mac's Director.", machines["the-mac"].GetProperty("browserProfilesText").GetString());
    }

    [Fact]
    public void Overview_SaysInWordsThatTheBrowserProfilesCannotBeListed_WhenTheMachinesDirectorIsNotConnected()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        _directors.Disconnect(One, "dir-mac");

        var machine = Overview(One).GetProperty("machines")[0];

        Assert.Equal("dir-mac", machine.GetProperty("directorId").GetString());
        Assert.False(machine.GetProperty("canListBrowserProfiles").GetBoolean());
        Assert.Equal("the-mac's Director is not connected, so its browser profiles cannot be listed.",
            machine.GetProperty("browserProfilesText").GetString());
    }

    [Fact]
    public void Overview_OffersNeither_OnAMachineWhoseDirectorIsNotConnected_WithTheSentenceTheRouteRefusesWith()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true), Holding(B, false));
        _directors.Disconnect(One, "dir-mac");

        var machine = Overview(One).GetProperty("machines")[0];
        var refused = AddSpare(One, mac, C).Json.GetProperty("error").GetString();

        Assert.False(machine.GetProperty("canAddSpare").GetBoolean());
        Assert.Equal(refused, machine.GetProperty("addSpareText").GetString());
        Assert.False(machine.GetProperty("active").GetProperty("canRenewNow").GetBoolean());
        Assert.Equal(refused, machine.GetProperty("active").GetProperty("renewNowText").GetString());
        Assert.False(machine.GetProperty("spares")[0].GetProperty("canRenewNow").GetBoolean());
    }

    [Fact]
    public async Task Overview_WhileASignInIsRunning_ShowsItOnItsMachine_AndDoesNotOfferToRenewThatAccount()
    {
        var mac = Reports(One, "the-mac", "dir-mac", Holding(A, true), Holding(B, false));
        var answering = new TaskCompletionSource<DirectorCommandResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _directors.Answer = _ => answering.Task;
        var started = Renew(One, mac, B);
        Assert.Equal(202, started.Status);

        var machine = Overview(One).GetProperty("machines")[0];

        var running = Assert.Single(machine.GetProperty("signInsInProgress").EnumerateArray().ToList());
        Assert.Equal(new[] { "signInId", "accountEmail", "text" }, Names(running));
        Assert.Equal((started.Json.GetProperty("signInId").GetString(), B, "the-mac is renewing its sign-in for account-b@example.com."),
            (running.GetProperty("signInId").GetString(), running.GetProperty("accountEmail").GetString(), running.GetProperty("text").GetString()));
        var spare = machine.GetProperty("spares")[0];
        Assert.False(spare.GetProperty("canRenewNow").GetBoolean());
        Assert.Equal(Renew(One, mac, B).Json.GetProperty("error").GetString(), spare.GetProperty("renewNowText").GetString());
        // The other sign-in on the machine is untouched by it.
        Assert.True(machine.GetProperty("active").GetProperty("canRenewNow").GetBoolean());

        answering.SetResult(null);
        await _signIns.WhenIdleAsync();
        Assert.Equal(0, Overview(One).GetProperty("machines")[0].GetProperty("signInsInProgress").GetArrayLength());
    }

    [Fact]
    public async Task Overview_OfOneAccount_SaysNothingOfTheApproversOrSignInsOfAnother()
    {
        Reports(One, "the-mac", "dir-mac-of-one", Holding(A, true));
        var macOfTwo = Reports(Two, "the-mac", "dir-mac-of-two", Holding(A, true));
        Set(Two, Approver());
        _directors.AnswerWith(_ => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "no answer"));
        Assert.Equal(202, AddSpare(Two, macOfTwo).Status);
        await _signIns.WhenIdleAsync();

        var one = Overview(One);

        Assert.Equal(JsonValueKind.Null, one.GetProperty("accounts")[0].GetProperty("approver").ValueKind);
        Assert.Equal(0, one.GetProperty("machines")[0].GetProperty("signInsInProgress").GetArrayLength());
        Assert.Equal(1, Overview(Two).GetProperty("machines")[0].GetProperty("signInsInProgress").GetArrayLength());
        Assert.Equal(0, LogEntries(One).GetArrayLength());
    }

    [Fact]
    public void TwoMachinesOfOneName_AreToldApartByTheirUser_InEverySentence()
    {
        var other = Report("the-mac", _now, Holding(B, true));
        other.OsUser = "guest";
        _store.ApplyReport(One, "dir-mac-guest", ++_sequence, other, _now, TimeSpan.Zero);
        Reports(One, "the-mac", "dir-mac", Holding(A, true));

        var set = Set(One, Approver()).Json;

        Assert.Equal("the-mac (owner)", set.GetProperty("label").GetString());
        Assert.StartsWith("The approver for account-a@example.com (browser profile \"work\" on the-mac (owner)) has never been checked.",
            set.GetProperty("text").GetString());
        Assert.Contains("the Director on the-mac (owner) is running", set.GetProperty("whatToDo").GetString());
    }

    // ---- the approver's health (phase 6, Track C) ----

    /// <summary>The approver of account A on the-mac, as a check by that machine's Director left it.</summary>
    private void Checked(string state, DateTime checkedAt, string? shown = null, string? detail = null, string source = LicenseApproverSources.Owner,
        string? name = null)
        => _approvers.Change(One, records =>
        {
            var next = records.Approvers.Where(r => r.AccountEmail != A).ToList();
            next.Add(new LicenseApprover(A, "the-mac", "owner", "work", source, state, checkedAt, shown, detail, name));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, _now);

    [Fact]
    public void AnApproverWhoseLastCheckFoundItCanApprove_IsReady_AndSaysWhenItWasChecked()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Checked(LicenseApproverStates.CanApprove, _now.AddHours(-3), shown: A, source: LicenseApproverSources.Registered);

        var row = List(One).Json.GetProperty("approvers")[0];

        Assert.True(row.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(("ready", "Ready", "good", "Sign-ins for account-a@example.com are approved on the-mac, in the browser profile work."),
            (row.GetProperty("status").GetString(), row.GetProperty("statusText").GetString(), row.GetProperty("statusTone").GetString(),
             row.GetProperty("text").GetString()));
        Assert.Equal(("registered", "can-approve", "2026-10-11T00:12:00Z", JsonValueKind.Null),
            (row.GetProperty("source").GetString(), row.GetProperty("state").GetString(), row.GetProperty("checkedAtUtc").GetString(),
             row.GetProperty("whatToDo").ValueKind));
        Assert.Equal("The approver for account-a@example.com (browser profile \"work\" on the-mac) can approve sign-ins; it was last checked 2026-10-11 00:12 UTC.",
            row.GetProperty("healthText").GetString());
    }

    [Fact]
    public void AnApproverThatNeedsAFreshSignIn_CannotApprove_AndTheRowSaysTheOneThingToDo()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Checked(LicenseApproverStates.NeedsFreshSignIn, _now.AddHours(-1));

        var row = List(One).Json.GetProperty("approvers")[0];

        Assert.False(row.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(("cannot-approve", "Cannot approve", "warning", "needs-fresh-sign-in"),
            (row.GetProperty("status").GetString(), row.GetProperty("statusText").GetString(), row.GetProperty("statusTone").GetString(),
             row.GetProperty("state").GetString()));
        Assert.Equal("The approver for account-a@example.com (browser profile \"work\" on the-mac) needs a fresh claude.ai sign-in on the approval page. "
                     + "Open that browser on the-mac and sign in to claude.ai once.", row.GetProperty("text").GetString());
    }

    [Fact]
    public void TheSentences_SayTheProfilesDisplayName_WhenACheckHasSeenOne_AndTheRowKeepsTheIdentifier()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Checked(LicenseApproverStates.SignedOut, _now, name: "Work Chrome");

        var row = List(One).Json.GetProperty("approvers")[0];

        Assert.Equal(("work", "Work Chrome"), (row.GetProperty("browserProfile").GetString(), row.GetProperty("browserProfileName").GetString()));
        Assert.Equal("The approver for account-a@example.com (browser profile \"Work Chrome\" on the-mac) is not signed in to claude.ai.",
            row.GetProperty("healthText").GetString());
    }

    [Fact]
    public void AnApproverWhoseDirectorIsNotConnected_SaysThatFirst_AndStillCarriesItsHealth()
    {
        _store.ApplyReport(One, "dir-mac", ++_sequence, Report("the-mac", _now, Holding(A, true)), _now, TimeSpan.Zero);
        Checked(LicenseApproverStates.CanApprove, _now, shown: A);

        var row = List(One).Json.GetProperty("approvers")[0];

        Assert.False(row.GetProperty("canApproveNow").GetBoolean());
        Assert.Equal(("not-connected", "can-approve"), (row.GetProperty("status").GetString(), row.GetProperty("state").GetString()));
    }

    [Fact]
    public void RemoveApprover_OfARegisteredRecord_KeepsTheOwnersRemoval()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Checked(LicenseApproverStates.CanApprove, _now, shown: A, source: LicenseApproverSources.Registered);

        var (status, json) = Remove(One, "?accountEmail=" + A);

        Assert.Equal(200, status);
        Assert.True(json.GetProperty("removed").GetBoolean());
        Assert.Equal(new[] { new LicenseApproverRemoval(A, "the-mac", "owner", "work", _now) }, _approvers.ListRemovals(One));
    }

    [Fact]
    public void RemoveApprover_OfAnOwnerSetRecord_KeepsNoRemoval()
    {
        Reports(One, "the-mac", "dir-mac", Holding(A, true));
        Set(One, Approver());

        Assert.Equal(200, Remove(One, "?accountEmail=" + A).Status);

        Assert.Empty(_approvers.ListRemovals(One));
    }
}
