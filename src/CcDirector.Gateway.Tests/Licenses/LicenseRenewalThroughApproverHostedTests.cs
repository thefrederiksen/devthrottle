using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// THE MONTHLY RENEWAL THROUGH THE STANDING APPROVERS, END TO END WITH NO PERSON (License Switching phase 6, Track C,
/// item 10). A real hosted Gateway, and behind each fake tunnel Director the REAL Director code: the daily
/// <see cref="LicenseRenewer"/>, the <see cref="LicenseSignInCoordinator"/> that asks the relay, the
/// <see cref="LicenseApprover"/> that approves in a browser profile, the <see cref="LicenseApprovalExchange"/> the
/// answer meets the waiting sign-in in, and the <see cref="LicenseUsageReporter"/> that tells the Gateway. Only the
/// license command line (the browser and the claude login) and the seal are fakes.
///
/// Three things are proved. A spare due for renewal on a machine with no browser profile of its own is renewed by the
/// machine the owner set to approve it, and the Licenses log records the approval. A Director that is BOTH the machine
/// renewed and its approver renews its own sign-in: the sign-in waiting for its approval and the approval it waits for
/// run on one Director at once, with no deadlock. And an approver whose approval page asks for a fresh claude.ai
/// sign-in fails the renewal with that reason and its plain sentence, never a generic failure.
///
/// Nothing here waits on a clock to pass: the wait for an approval ends only when the answer arrives, and background
/// relays are awaited through <c>WhenIdleAsync</c>. One bound exists, and it only ever FAILS a test: every renewal,
/// report and command here runs under <see cref="_hang"/>, cancelled after two minutes, so a deadlock - the thing the
/// self-approval test is for - fails the named test with a cancellation instead of hanging the run. A passing run
/// takes a few seconds and never reaches it.
/// </summary>
public sealed class LicenseRenewalThroughApproverHostedTests : IAsyncLifetime
{
    private const string Due = "alice@example.com";
    private const string Active = "bob@example.com";
    private const string Profile = "work-chrome";
    private const string RenewedMachine = "machine-b";
    private const string ApprovingMachine = "machine-a";

    private static readonly string RenewedPublicHalf = Convert.ToBase64String(Enumerable.Range(1, 32).Select(i => (byte)i).ToArray());
    private static readonly string ApprovingPublicHalf = Convert.ToBase64String(Enumerable.Range(41, 32).Select(i => (byte)i).ToArray());

    private static readonly DateTime Now = new(2026, 10, 17, 9, 0, 0, DateTimeKind.Utc);

    /// <summary>One clock for the Gateway and every Director here, so a machine's report is never from its future.</summary>
    private sealed class TestClock : TimeProvider
    {
        public DateTime UtcNow { get; set; } = Now;

        public override DateTimeOffset GetUtcNow() => new(UtcNow);
    }

    private readonly TestClock _clock = new();

    /// <summary>Cancelled two minutes after the test starts: a hang fails the test, it never decides a pass.</summary>
    private readonly CancellationTokenSource _hang = new(TimeSpan.FromMinutes(2));
    private readonly string _run = Guid.NewGuid().ToString("N")[..12];
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-license-renewal-" + Guid.NewGuid().ToString("N"));
    private readonly List<IAsyncDisposable> _toDispose = new();
    private readonly List<IDisposable> _toDisposeToo = new();

    private GatewayHost _gateway = null!;
    private HostedTestDevice _owner;
    private HttpClient _ownerHttp = null!;

    public async Task InitializeAsync()
    {
        _gateway = new GatewayHost(port: GatewayHost.OperatingSystemAssignedPort, token: "test-token", authEnabled: true,
            instancesDirectory: Path.Combine(_root, "instances"),
            workListsPath: Path.Combine(_root, "worklists", "worklists.json"),
            snoozePath: Path.Combine(_root, "snooze", "snooze.json"),
            streamMode: true,
            options: new GatewayHostOptions { Hosted = true });
        _gateway.LicenseClock = _clock;
        await _gateway.StartAsync();

        // The Gateway database is shared by every host this test process starts, so the account is a new one each test.
        _owner = HostedTestEnrollment.Enroll(_gateway, $"sub-renew-{_run}", $"owner-{_run}@example.com", $"dev-renew-{_run}", "MR");
        _ownerHttp = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{_gateway.Port}/") };
        _ownerHttp.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer",
            _gateway.Devices.RegisterForTenant(_owner.Tenant, $"sub-renew-{_run}", $"dev-owner-{_run}", "OWNER", deviceType: "browser").DeviceKey);
    }

    public async Task DisposeAsync()
    {
        _ownerHttp.Dispose();
        _hang.Dispose();
        foreach (var item in _toDispose) await item.DisposeAsync();
        foreach (var item in _toDisposeToo) item.Dispose();
        await _gateway.StopAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
        catch { /* best-effort */ }
    }

    // ---- the tests ----

    [Fact]
    public async Task ADueSpare_OnAMachineWithNoProfileOfItsOwn_IsRenewedByTheRegisteredApprover_AndTheLogRecordsIt()
    {
        var approving = await StartMachineAsync(ApprovingMachine, ApprovingPublicHalf);
        var renewed = await StartMachineAsync(RenewedMachine, RenewedPublicHalf, Holding(Active, true), Holding(Due, false));
        await ReportAsync(approving);
        await ReportAsync(renewed);
        await SetApproverAsync(Due, ApprovingMachine);

        var rows = await renewed.Licenses.Renewer.TickAsync(_hang.Token);
        await _gateway.LicenseApprovals.WhenIdleAsync();

        var row = Assert.Single(rows!, r => r.AccountEmail == Due);
        Assert.Equal((LicenseRenewalResults.Renewed, (string?)null), (row.Result, row.Error));
        // Approved on the machine the owner set, in the profile the owner set, and nowhere else.
        var approve = Payload<LicenseApproveRequest>(Assert.Single(approving.Director.AllOf(LicenseContract.ApproveVerb)));
        Assert.Equal((Due, Profile, RenewedPublicHalf), (approve.AccountEmail, approve.BrowserProfile, approve.SealToRecipient));
        Assert.Equal((Due, Profile), Assert.Single(approving.Tool.Approvals));
        Assert.Empty(renewed.Director.AllOf(LicenseContract.ApproveVerb));
        Assert.Empty(renewed.Tool.Approvals);
        Assert.Equal(1, renewed.Tool.Completed);

        // The Gateway sees the new sign-in in the next report, and the Licenses log records the approval.
        await ReportAsync(renewed, afterSpareRead: true);
        Assert.Equal(_clock.UtcNow - LicenseContract.SpareReadInterval, SignedInAt(RenewedMachine, Due));
        var entry = Assert.Single(await LogEntriesAsync("approval"));
        Assert.Equal((RenewedMachine, Due, "approved"), (entry.GetProperty("machineName").GetString(),
            entry.GetProperty("toAccountEmail").GetString(), entry.GetProperty("state").GetString()));
        Assert.Contains(ApprovingMachine, entry.GetProperty("summary").GetString());
    }

    [Fact]
    public async Task ADirectorThatIsItsOwnApprover_RenewsItsOwnSignIn_WithNoDeadlock()
    {
        // machine-a holds the due spare AND is the machine set to approve it: the profile is registered with the
        // Gateway, not set in the tool here, so the renewal asks the relay and the relay asks this same Director.
        var machine = await StartMachineAsync(ApprovingMachine, ApprovingPublicHalf, Holding(Active, true), Holding(Due, false));
        await ReportAsync(machine);
        await SetApproverAsync(Due, ApprovingMachine);

        var rows = await machine.Licenses.Renewer.TickAsync(_hang.Token);
        await _gateway.LicenseApprovals.WhenIdleAsync();

        Assert.Equal(LicenseRenewalResults.Renewed, Assert.Single(rows!, r => r.AccountEmail == Due).Result);
        Assert.Single(machine.Director.AllOf(LicenseContract.ApproveVerb));
        Assert.Single(machine.Director.AllOf(LicenseContract.ApprovalResultVerb));
        Assert.Equal((Due, Profile), Assert.Single(machine.Tool.Approvals));
        Assert.Equal(1, machine.Tool.Completed);

        await ReportAsync(machine, afterSpareRead: true);
        Assert.Equal(_clock.UtcNow - LicenseContract.SpareReadInterval, SignedInAt(ApprovingMachine, Due));
        var entry = Assert.Single(await LogEntriesAsync("approval"));
        Assert.Equal((ApprovingMachine, "approved"), (entry.GetProperty("machineName").GetString(), entry.GetProperty("state").GetString()));
    }

    [Fact]
    public async Task AnApproverThatNeedsAFreshSignIn_FailsTheRenewalWithThatReason_AndItsPlainSentence()
    {
        var approving = await StartMachineAsync(ApprovingMachine, ApprovingPublicHalf);
        approving.Tool.ApproveAnswer = LicenseApproveAnswer.Refused(LicenseRenewalErrors.NeedsFreshSignIn);
        var renewed = await StartMachineAsync(RenewedMachine, RenewedPublicHalf, Holding(Active, true), Holding(Due, false));
        await ReportAsync(approving);
        await ReportAsync(renewed);
        await SetApproverAsync(Due, ApprovingMachine);

        var rows = await renewed.Licenses.Renewer.TickAsync(_hang.Token);
        await _gateway.LicenseApprovals.WhenIdleAsync();

        var row = Assert.Single(rows!, r => r.AccountEmail == Due);
        Assert.Equal((LicenseRenewalResults.Failed, LicenseRenewalErrors.NeedsFreshSignIn), (row.Result, row.Error));
        Assert.Equal(0, renewed.Tool.Completed);
        Assert.Equal(1, renewed.Tool.Abandoned);

        // The report carries the reason word and the Director's own sentence, and the Licenses page says it plainly.
        var report = await ReportAsync(renewed);
        var holding = report.Holdings.Single(h => h.AccountEmail == Due);
        Assert.Equal(LicenseRenewalErrors.NeedsFreshSignIn, holding.RenewalError);
        Assert.Equal(LicenseRenewalSentences.For(LicenseRenewalErrors.NeedsFreshSignIn), holding.RenewalErrorDetail);
        var spare = await OverviewHoldingAsync(RenewedMachine, Due);
        var shown = spare.GetProperty("renewalFailedText").GetString()!;
        Assert.StartsWith("Renewing this sign-in failed", shown);
        Assert.EndsWith(": the approving browser needs a fresh claude.ai sign-in on the approval page.", shown);

        var entry = Assert.Single(await LogEntriesAsync("approval"));
        Assert.Equal(("refused", LicenseRenewalErrors.NeedsFreshSignIn),
            (entry.GetProperty("state").GetString(), entry.GetProperty("reasonCode").GetString()));
    }

    // ---- one machine: the real Director code behind a fake tunnel Director ----

    private sealed record Machine(string Name, FakeTunnelDirector Director, LicenseSwitching Licenses, FakeTool Tool);

    private async Task<Machine> StartMachineAsync(string name, string publicHalf, params LicenseHolding[] holdings)
    {
        var tool = new FakeTool(() => _clock.UtcNow, holdings);
        var licenses = new LicenseSwitching(tool, Path.Combine(_root, name, "director"), () => _clock.UtcNow, name, "test", enabled: true,
            signInDelay: (_, ct) => Task.Delay(Timeout.Infinite, ct));
        _toDisposeToo.Add(licenses);
        var director = await FakeTunnelDirector.StartAsync(_gateway, _owner.DeviceKey, $"d-{name}-{_run}", name,
            secretTransferPublicKey: publicHalf);
        _toDispose.Add(director);

        var seal = new TestSeal(publicHalf);
        licenses.SignIns.AttachRelay(new TunnelRelay(director));
        licenses.SignIns.AttachSeal(seal);
        licenses.Approver.AttachSeal(seal);
        licenses.Reporter.AttachSink(new TunnelSink(director));
        // The Director's own command surface for the two verbs the relay sends, exactly as its tunnel runs them.
        director.OnCommandAsync(command => command.Verb switch
        {
            LicenseContract.ApproveVerb => LicenseSignInExecutor.ApproveAsync(licenses.Approver, command, _hang.Token),
            LicenseContract.ApprovalResultVerb => Task.FromResult(LicenseSignInExecutor.DeliverApproval(licenses.Approvals, command)),
            _ => Task.FromResult(DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unexpected verb {command.Verb}")),
        });
        return new Machine(name, director, licenses, tool);
    }

    /// <summary>Send the machine's next report the way its reporter does. With <paramref name="afterSpareRead"/>, the
    /// clock moves past the spare read interval first, so the report reads the spares again.</summary>
    private async Task<LicenseUsageReport> ReportAsync(Machine machine, bool afterSpareRead = false)
    {
        if (afterSpareRead) _clock.UtcNow += LicenseContract.SpareReadInterval;
        machine.Licenses.Reporter.RequestReport(LicenseReportTriggers.Changed);
        var report = await machine.Licenses.Reporter.TickAsync(_hang.Token);
        return report ?? throw new InvalidOperationException($"{machine.Name} sent no report.");
    }

    private async Task SetApproverAsync(string account, string machine)
    {
        using var set = await _ownerHttp.PutAsJsonAsync(LicenseSignInRoutes.Approvers.TrimStart('/'),
            new LicenseSetApproverRequest { AccountEmail = account, MachineName = machine, OsUser = "test", BrowserProfile = Profile });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
    }

    private DateTime? SignedInAt(string machine, string account)
        => _gateway.LicenseReads.Read(_owner.Tenant, _clock.UtcNow).Machines.Single(m => m.MachineName == machine)
            .Holdings.Single(h => h.AccountEmail == account).SignedInAtUtc;

    private async Task<IReadOnlyList<JsonElement>> LogEntriesAsync(string kind)
        => (await GetAsync(LicenseRoutes.Log)).GetProperty("entries").EnumerateArray()
            .Where(e => e.GetProperty("kind").GetString() == kind).ToList();

    private async Task<JsonElement> OverviewHoldingAsync(string machine, string account)
    {
        var shown = (await GetAsync(LicenseRoutes.Overview)).GetProperty("machines").EnumerateArray()
            .Single(m => m.GetProperty("machineName").GetString() == machine);
        return shown.GetProperty("spares").EnumerateArray().Single(h => h.GetProperty("accountEmail").GetString() == account);
    }

    private async Task<JsonElement> GetAsync(string route)
    {
        using var response = await _ownerHttp.GetAsync(route.TrimStart('/'));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }

    private static T Payload<T>(DirectorCommand command)
        => JsonSerializer.Deserialize<T>(command.PayloadJson, FakeTunnelDirector.WebJson)
           ?? throw new InvalidOperationException($"The {command.Verb} command carried no payload.");

    private static LicenseHolding Holding(string email, bool active) => new()
    {
        AccountEmail = email,
        IsActive = active,
        Plan = "max",
        FiveHour = new LicenseWindow { Utilization = 10, ResetsAtUtc = Now.AddHours(2) },
        SevenDay = new LicenseWindow { Utilization = 20, ResetsAtUtc = Now.AddDays(4) },
        ReadAtUtc = Now,
        // The spare ends within five days, so it is due; the active account was signed in recently.
        SignedInAtUtc = active ? Now.AddDays(-2) : Now.AddDays(-26),
        SignInExpiresAtUtc = active ? Now.AddDays(27) : Now.AddDays(3),
    };

    // ---- the fakes: the license command line, the seal, and the two tunnel seams ----

    /// <summary>
    /// The license command line of one machine. The account that ends within five days is due; no browser profile is
    /// set in the tool for any account, so a renewal asks the relay. A sign-in waits for an approval and completes
    /// only with the one this fake's approver hands out for that account; an approval is answered with
    /// <see cref="ApproveAnswer"/> when one is set.
    /// </summary>
    private sealed class FakeTool : ILicenseCommandLine
    {
        private readonly Func<DateTime> _now;
        private readonly object _gate = new();
        private readonly List<LicenseHolding> _holdings;

        public FakeTool(Func<DateTime> now, IEnumerable<LicenseHolding> holdings)
        {
            _now = now;
            _holdings = holdings.ToList();
        }

        public LicenseApproveAnswer? ApproveAnswer { get; set; }

        public List<(string Account, string Profile)> Approvals { get; } = new();

        public int Completed;

        public int Abandoned;

        public static string ApprovalFor(string account) => "approval-for-" + account;

        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken) => Task.FromResult(Copy());

        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<LicenseHolding>>(Copy().Where(h => !activeOnly || h.IsActive).ToList());

        public Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken)
            => throw new NotSupportedException("no switch happens in a renewal");

        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken)
        {
            lock (_gate)
                return Task.FromResult<IReadOnlyList<LicenseRenewalRow>>(_holdings.Select(h => new LicenseRenewalRow
                {
                    AccountEmail = h.AccountEmail,
                    IsActive = h.IsActive,
                    Result = h.SignInExpiresAtUtc <= _now().AddDays(5) ? LicenseRenewalResults.Due : LicenseRenewalResults.NotDue,
                    HasBrowserProfileHere = false,
                    SignedInAtUtc = h.SignedInAtUtc,
                    SignInExpiresAtUtc = h.SignInExpiresAtUtc,
                }).ToList());
        }

        public Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken) => Task.FromResult<bool?>(false);

        public Task<LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken)
        {
            Assert.True(askAnotherMachineWhenNoBrowser, "with no profile here, the renewal must be allowed to ask another machine");
            return Task.FromResult(new LicenseSignInStart { Waiting = new WaitingSignIn(this, accountEmail) });
        }

        public Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken)
        {
            lock (_gate) Approvals.Add((accountEmail, browserProfile));
            Assert.Equal(WaitingSignIn.UrlFor(accountEmail), authorizeUrl);
            return Task.FromResult(ApproveAnswer ?? new LicenseApproveAnswer { Approved = true, Approval = ApprovalFor(accountEmail) });
        }

        private IReadOnlyList<LicenseHolding> Copy()
        {
            lock (_gate)
                return _holdings.Select(h => JsonSerializer.Deserialize<LicenseHolding>(JsonSerializer.Serialize(h))!).ToList();
        }

        private LicenseSignInAnswer Renew(string account)
        {
            var now = _now();
            lock (_gate)
            {
                var holding = _holdings.Single(h => h.AccountEmail == account);
                holding.SignedInAtUtc = now;
                holding.SignInExpiresAtUtc = now.AddDays(29);
                Completed++;
            }
            return new LicenseSignInAnswer { Outcome = LicenseSignInOutcomes.Renewed, SignedInAtUtc = now, SignInExpiresAtUtc = now.AddDays(29) };
        }

        private sealed class WaitingSignIn : ILicenseRemoteSignIn
        {
            private readonly FakeTool _tool;
            private readonly string _account;
            private bool _ended;

            public WaitingSignIn(FakeTool tool, string account)
            {
                _tool = tool;
                _account = account;
            }

            public static string UrlFor(string account) => "https://claude.example/oauth/authorize?login_hint=" + Uri.EscapeDataString(account);

            public string AuthorizeUrl => UrlFor(_account);

            public Task<LicenseSignInAnswer> CompleteAsync(string approval, CancellationToken cancellationToken)
            {
                _ended = true;
                return Task.FromResult(approval == ApprovalFor(_account)
                    ? _tool.Renew(_account)
                    : LicenseSignInAnswer.Failed(LicenseRenewalErrors.LoginFailed));
            }

            public Task AbandonAsync(CancellationToken cancellationToken)
            {
                if (!_ended) Interlocked.Increment(ref _tool.Abandoned);
                _ended = true;
                return Task.CompletedTask;
            }

            public ValueTask DisposeAsync() => new(AbandonAsync(CancellationToken.None));
        }
    }

    /// <summary>Seals to a public half and opens only what was sealed to this machine's, for the one sign-in.</summary>
    private sealed class TestSeal : ILicenseApprovalSeal
    {
        private readonly string _myPublicHalf;

        public TestSeal(string myPublicHalf) => _myPublicHalf = myPublicHalf;

        public Task<string?> SealAsync(string approval, string recipient, string signInId, CancellationToken cancellationToken)
            => Task.FromResult<string?>(Convert.ToBase64String(Encoding.UTF8.GetBytes($"{recipient}|{signInId}|{approval}")));

        public Task<string?> OpenAsync(string sealedApproval, string signInId, CancellationToken cancellationToken)
        {
            var parts = Encoding.UTF8.GetString(Convert.FromBase64String(sealedApproval)).Split('|', 3);
            return Task.FromResult(parts.Length == 3 && parts[0] == _myPublicHalf && parts[1] == signInId ? parts[2] : null);
        }
    }

    private sealed class TunnelRelay : ILicenseApprovalRelay
    {
        private readonly FakeTunnelDirector _director;

        public TunnelRelay(FakeTunnelDirector director) => _director = director;

        public bool CanRequest => true;

        public Task RequestAsync(LicenseApprovalRequest request, CancellationToken cancellationToken) => _director.RequestLicenseApprovalAsync(request);
    }

    private sealed class TunnelSink : ILicenseReportSink
    {
        private readonly FakeTunnelDirector _director;

        public TunnelSink(FakeTunnelDirector director) => _director = director;

        public bool CanSend => true;

        public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken) => _director.PushLicenseUsageAsync(report);
    }
}
