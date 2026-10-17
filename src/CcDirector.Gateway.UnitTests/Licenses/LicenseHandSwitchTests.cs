using CcDirector.Core.Licenses;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Licenses;
using CcDirector.Gateway.Tests.Data;
using Xunit;
using static CcDirector.Gateway.Tests.Licenses.LicenseTestData;

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A MACHINE SWITCHED BY HAND (License Switching, contract section 2.6), from the license tool's answer to the Gateway's
/// log. The tool's JSON is read by the Director's real parsers, the report is built by the real reporter, and it is
/// judged by the real ingest - the same <c>LicenseUsageIngest.Malformed</c> that refused every report from the machine
/// that reported one account twice on 16 October.
/// </summary>
public sealed class LicenseHandSwitchTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-a");

    private readonly GatewayDbTestHarness _harness = new();
    private readonly LicenseStore _store;
    private readonly LicenseUsageIngest _ingest;
    private readonly string _lockFolder = Path.Combine(Path.GetTempPath(), "hand-switch-tests-" + Guid.NewGuid().ToString("N"));

    public LicenseHandSwitchTests()
    {
        _store = new LicenseStore(_harness.Open());
        _ingest = new LicenseUsageIngest(_store);
        _ingest.RegisterConnection(Tenant, "dir-1", "conn-1");
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_lockFolder)) Directory.Delete(_lockFolder, recursive: true);
    }

    /// <summary>What `license list --json` and `license usage --json` print: the accounts, and the heals of that run.</summary>
    private static string ListAnswer(string healed, params (string Email, string Role)[] accounts) =>
        $$"""{"count": {{accounts.Length}}, "healed": [{{healed}}], "accounts": [{{string.Join(",", accounts.Select(a => $$"""{"email": "{{a.Email}}", "role": "{{a.Role}}"}"""))}}]}""";

    private static string UsageAnswer(string healed, params (string Email, bool Active)[] accounts) =>
        $$"""{"count": {{accounts.Length}}, "healed": [{{healed}}], "accounts": [{{string.Join(",", accounts.Select(a =>
            $$"""{"accountEmail": "{{a.Email}}", "isActive": {{(a.Active ? "true" : "false")}}, "plan": "max", "fiveHour": {"utilization": 30, "resetsAtUtc": "2026-10-11T05:00:00Z"}, "sevenDay": {"utilization": 40, "resetsAtUtc": "2026-10-14T05:00:00Z"}, "readAtUtc": "2026-10-11T03:12:00Z", "folder": "/x"}"""))}}]}""";

    private const string HealedB = """{"accountEmail": "account-b@example.com", "movedFrom": "/store/spares/account-b-example.com", "movedTo": "/store/hand-sign-in-backups/account-b-example.com-20261011T031100Z", "healedAtUtc": "2026-10-11T03:11:00Z", "duplicateKeyRemoved": false}""";

    private async Task<LicenseUsageReport> ReportFromToolAnswers(string listAnswer, string usageAnswer)
    {
        var tool = new AnswerTool(listAnswer, usageAnswer);
        using var holder = new LicenseKeyHolderLock(_lockFolder);
        var sink = new CapturingSink();
        var reporter = new LicenseUsageReporter(tool, holder, () => T0, "machine-a", "owner");
        reporter.AttachSink(sink);
        return (await reporter.TickAsync(CancellationToken.None))!;
    }

    [Fact]
    public void Malformed_TheReportFromBeforeTheHeal_HoldsTheAccountTwice_AndIsRefused()
    {
        // What the machine sent on 16 October, before the heal existed: account B in use and B as a spare.
        var report = Report("machine-a", Holding(B, true), Holding(B, false));
        Assert.Equal(new LicenseReportRefusal(LicenseReportRefusalCodes.AccountHeldTwice,
            $"it holds {B} twice (once in use and once as a spare)"), LicenseUsageIngest.Malformed(report));
    }

    [Fact]
    public async Task HandSignInHealed_TheReportHoldsTheAccountOnce_IsAccepted_AndTheGatewayWritesOneLogEntry()
    {
        // Machine-a was on A with B as a spare. B was signed in by hand; the key store healed it. The tool's answers
        // after the heal hold B once, as active, and name the heal; the report built from them is accepted.
        Assert.Equal(LicenseIngestOutcome.Accepted,
            _ingest.Ingest(Tenant, "dir-1", "conn-1", 1, Report("machine-a", Holding(A, true), Holding(B, false)), T0.AddMinutes(-2)));

        var report = await ReportFromToolAnswers(ListAnswer(HealedB, (B, "active")), UsageAnswer(HealedB, (B, true)));

        Assert.Equal(new[] { (B, true) }, report.Holdings.Select(h => (h.AccountEmail, h.IsActive)));
        Assert.Equal(B, report.SwitchedByHand!.AccountEmail);
        Assert.Null(LicenseUsageIngest.Malformed(report));
        Assert.Equal(LicenseIngestOutcome.Accepted, _ingest.Ingest(Tenant, "dir-1", "conn-1", 2, report, T0));

        var row = Assert.Single(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.SwitchedByHand);
        Assert.Equal("machine-a was switched to account-b@example.com by hand; the old spare for account-b@example.com was moved "
                     + "to a backup on the machine. The account it was on before, account-a@example.com, is no longer held on the "
                     + "machine: the sign-in by hand overwrote it.", row.Message);
        Assert.Equal((A, B), (row.FromAccountEmail, row.ToAccountEmail));
        Assert.Equal(LicenseLogStates.Recorded, row.State);
    }

    [Fact]
    public void ApplyReport_HandSwitchWhereThePreviousAccountIsStillHeld_DoesNotCallItLost()
    {
        // The machine was on A and is now on B by hand, but the report still holds A (as a spare). The Gateway names
        // the previous account as gone only when the report no longer holds it.
        _store.ApplyReport(Tenant, "dir-1", 1, Report("machine-a", Holding(A, true), Holding(B, false)), T0, TimeSpan.Zero);
        var report = Report("machine-a", Holding(B, true), Holding(A, false));
        report.SwitchedByHand = new LicenseHandSwitch { AccountEmail = B, HealedAtUtc = T0 };

        _store.ApplyReport(Tenant, "dir-1", 2, report, T0.AddMinutes(1), TimeSpan.Zero);

        var row = Assert.Single(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.SwitchedByHand);
        Assert.Equal("machine-a was switched to account-b@example.com by hand; the old spare for account-b@example.com was moved "
                     + "to a backup on the machine.", row.Message);
        Assert.Equal(("", B), (row.FromAccountEmail, row.ToAccountEmail));
    }

    [Fact]
    public void ApplyReport_AReportWithoutTheField_WritesNoHandSwitchEntry()
    {
        _store.ApplyReport(Tenant, "dir-1", 1, Report("machine-a", Holding(A, true), Holding(B, false)), T0, TimeSpan.Zero);
        _store.ApplyReport(Tenant, "dir-1", 2, Report("machine-a", Holding(B, true), Holding(A, false)), T0.AddMinutes(1), TimeSpan.Zero);
        Assert.DoesNotContain(_store.ListLog(Tenant, T0.AddDays(-1)), r => r.Kind == LicenseLogKinds.SwitchedByHand);
    }

    [Theory]
    [InlineData("account-c@example.com")]
    [InlineData("")]
    public void Malformed_AHandSwitchThatIsNotTheActiveHolding_IsRefused(string email)
    {
        var report = Report("machine-a", Holding(B, true), Holding(A, false));
        report.SwitchedByHand = new LicenseHandSwitch { AccountEmail = email, HealedAtUtc = T0 };
        Assert.Equal(LicenseReportRefusalCodes.SwitchedByHandNotActive, LicenseUsageIngest.Malformed(report)?.Code);
    }

    /// <summary>The license tool, answering fixed JSON through the Director's own parsers.</summary>
    private sealed class AnswerTool(string listAnswer, string usageAnswer) : ILicenseCommandLine
    {
        private readonly List<LicenseHandSwitch> _healed = new();

        public Task<IReadOnlyList<LicenseHolding>> ListAsync(CancellationToken cancellationToken)
        {
            _healed.AddRange(CcDevthrottleLicenseCommandLine.ReadHandSwitches(listAnswer, "list"));
            return Task.FromResult(CcDevthrottleLicenseCommandLine.ReadListedAccounts(listAnswer));
        }

        public Task<IReadOnlyList<LicenseHolding>> ReadUsageAsync(bool activeOnly, CancellationToken cancellationToken)
        {
            _healed.AddRange(CcDevthrottleLicenseCommandLine.ReadHandSwitches(usageAnswer, "usage"));
            return Task.FromResult(CcDevthrottleLicenseCommandLine.ReadUsageRows(usageAnswer));
        }

        public IReadOnlyList<LicenseHandSwitch> TakeHandSwitches()
        {
            var taken = _healed.ToList();
            _healed.Clear();
            return taken;
        }

        public Task<LicenseExchangeAnswer> SwitchAsync(string targetAccountEmail, string expectedActiveAccountEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<IReadOnlyList<LicenseRenewalRow>> CheckRenewalsAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool?> HasBrowserProfileAsync(string accountEmail, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CcDirector.Core.Licenses.LicenseSignInStart> StartSignInAsync(string accountEmail, bool askAnotherMachineWhenNoBrowser, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<LicenseApproveAnswer> ApproveAsync(string authorizeUrl, string accountEmail, string browserProfile, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class CapturingSink : ILicenseReportSink
    {
        public bool CanSend => true;

        public Task SendAsync(LicenseUsageReport report, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
