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

namespace CcDirector.Gateway.Tests.Licenses;

/// <summary>
/// A STRANDED MACHINE IS LOUD - THE 26 OCTOBER 2026 REPLAY (mission "Licenses - Loud When Stranded", step 3).
///
/// What happened: at 13:23 UTC on 26 October 2026 the dev account ran out of its WEEKLY limit. The Gateway switched
/// devlinux to the one account with room, and left SOREN_NORTH and the Mac on the full account without a word - no
/// email, no push, no banner line - because neither held a sign-in for that account, its approver could not approve
/// (its daily check had failed every day since 19 October), and a machine ready for no account with room was only a
/// log row. Nothing had said anything at 80% either: the 80% alarm looks only at the five-hour window, and the dev
/// account filled its weekly one.
///
/// THE PICTURES, rebuilt as usage reports applied to the real store on a throwaway SQLite file:
/// - 26 October 13:23 UTC. dev@ weekly 100% (resets 29 Oct 21:00), five-hour 30%; centerconsulting@ weekly 97%
///   (resets 26 Oct 22:00); duksrevo@ weekly 97% (resets 27 Oct 12:00); soren.frederiksen@ weekly 0.5%. devlinux is on
///   dev@ with spares soren.frederiksen@, centerconsulting@ and duksrevo@; SOREN_NORTH and the Mac are on dev@ with
///   spares centerconsulting@ and duksrevo@ only; SORENLAPTOP last reported at 02:43 on 23 October.
/// - 25 October 18:00 UTC, the day before. The same machines and sign-ins, with dev@ at 80% of its WEEKLY window and
///   20% of its five-hour window. Chosen readings: centerconsulting@ and duksrevo@ already at 97% weekly - a weekly
///   reading only rises until its reset, and both read 97% the next day inside the same window, so 97% the day before
///   is the one reading consistent with both days - and soren.frederiksen@ at 0.5%, as the next day (it was not in use).
///   That makes soren.frederiksen@ the only account with room, as it was on the 26th.
/// SORENLAPTOP is stale in both pictures and its last report holds the worst case: dev@ with only centerconsulting@ and
/// duksrevo@ spares (what it really held is not on record; the Tech Lead's ruling). A stale machine's holdings are not
/// current, so it is not stranded by this rule: every loud outcome of the replay names SOREN_NORTH or the Mac, and never
/// SORENLAPTOP. Only the boundary gives it the sign-in too, because its premise is that every machine is ready.
/// soren.frederiksen@'s approver is SOREN_NORTH's browser, whose last check failed - the real fact - so the readiness
/// check cannot repair the gap by signing the machine in.
///
/// ONE GATEWAY MINUTE per case: the readiness check (<see cref="LicenseReadinessService.TickAsync"/>) and the switch check
/// (<see cref="LicenseSwitchService.TickAsync"/>), each built over the same store, the same license alarm, one recorder of
/// owner emails and one recorder of pushes. What is asserted is only what a person would see: the emails the Gateway
/// sent, the pushes it sent, and the banner it answers on GET /gateway/licenses/alarms - matched on the machine's name
/// and the accounts, never on a wording: the banner by its machine and account fields, the email line by line, the push
/// by its machine field.
///
/// THE CASES. Positive: on the 26th devlinux is switched to soren.frederiksen@ (it is today; this guards the replay
/// itself), and SOREN_NORTH and the Mac are each told about loudly - an email, a push and a banner line naming the
/// machine, the account it is stuck on and the account it needs; on the 25th each raises the 80% warning the same way.
/// Negative control: the same two pictures with SOREN_NORTH and the Mac each holding a soren.frederiksen@ sign-in raise
/// nothing loud. Boundary (issue #4249, "usage limits are not to be policed or flagged"): the dev account at 80% or 97%
/// of either window, with every machine ready for an account with room, raises nothing. Instrument check: the 25th with
/// the five-hour window at 85%, which the alarm already covers, is heard on every surface - so a silent positive case
/// is the Gateway's silence, never a recorder that cannot hear.
///
/// Written before the fix: on the code it was cut from, every positive case fails naming the surfaces that stayed silent, and the
/// negative control and boundary pass (docs/missions/license-loud-switch-2026-10-26/proofs/step-3/README.md).
///
/// WHAT A GREEN RUN PROVES. The cases match no alarm kind, so they cannot tell step 1's alarm (a tired account) from
/// step 2's (either window at 80% or more): 100% is above 80%, so once step 2 is built the 26 October cases pass with or
/// without step 1. A green replay proves the Gateway is loud about a stranded machine at 80% and at 100%; step 1's own
/// proof is its own test class and its run of this replay made before step 2 existed.
/// </summary>
public sealed class LicenseStrandedReplayTests : IDisposable
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static readonly TenantId Owner = new("acct-owner");

    private const string Dev = "dev@example.com";
    private const string CenterConsulting = "centerconsulting@example.com";
    private const string Duksrevo = "duksrevo@example.com";
    private const string SorenFrederiksen = "soren.frederiksen@example.com";

    private const string Devlinux = "devlinux";
    private const string SorenNorth = "SOREN_NORTH";
    private const string Mac = "Mac-mini";
    private const string SorenLaptop = "SORENLAPTOP";

    /// <summary>26 October 2026, 13:23 UTC: the dev account's weekly window has just filled.</summary>
    private static readonly DateTime OutAt = new(2026, 10, 26, 13, 23, 0, DateTimeKind.Utc);

    /// <summary>25 October 2026, 18:00 UTC: the dev account at 80% of its weekly window.</summary>
    private static readonly DateTime EightyAt = new(2026, 10, 25, 18, 0, 0, DateTimeKind.Utc);

    /// <summary>SORENLAPTOP's last report.</summary>
    private static readonly DateTime LaptopLastReport = new(2026, 10, 23, 2, 43, 0, DateTimeKind.Utc);

    private static readonly DateTime DevWeekResets = new(2026, 10, 29, 21, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CenterConsultingWeekResets = new(2026, 10, 26, 22, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime DuksrevoWeekResets = new(2026, 10, 27, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime SorenFrederiksenWeekResets = new(2026, 10, 31, 9, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _harness = new();
    private readonly GatewayDatabase _db;
    private readonly LicenseStore _store;
    private readonly LicenseReadModel _reads;
    private readonly LicenseSignInLog _signInLog;
    private readonly FakeLicenseDirectors _directors = new();
    private readonly LicenseSignInService _signIns;
    private readonly LicenseApproverStore _approvers;
    private readonly LicenseAlarmService _alarms;
    private readonly List<LicenseOwnerEmail> _emails = new();
    private readonly List<LicenseAlarmOpened> _pushed = new();
    private readonly List<(string Machine, LicenseSwitchRequest Request)> _switches = new();
    private DateTime _now = OutAt;
    private long _sequence;

    public LicenseStrandedReplayTests()
    {
        _db = _harness.Open();
        _store = new LicenseStore(_db);
        _reads = new LicenseReadModel(_store);
        _signInLog = new LicenseSignInLog(_db);
        _signIns = new LicenseSignInService(_signInLog, _reads, _directors.IsConnected, _directors.SendAsync, _directors.EnterScope,
            () => _now, CancellationToken.None);
        _approvers = new LicenseApproverStore(new TenantSettingsStore(_db));
        _alarms = new LicenseAlarmService(_db, Mail, Push, () => _now);
    }

    public void Dispose()
    {
        _signIns.WhenIdleAsync().GetAwaiter().GetResult();
        _harness.Dispose();
    }

    // ----- the recorders: what the owner would have been told -----

    private Task<LicenseOwnerEmailResult> Mail(TenantId tenant, LicenseOwnerEmail email, CancellationToken ct)
    {
        lock (_emails) _emails.Add(email);
        return Task.FromResult(new LicenseOwnerEmailResult(LicenseOwnerEmailOutcome.Sent, null));
    }

    private Task Push(TenantId tenant, IReadOnlyList<LicenseAlarmOpened> opened, CancellationToken ct)
    {
        lock (_pushed) _pushed.AddRange(opened);
        return Task.CompletedTask;
    }

    /// <summary>Every Director takes its switch and reports it done, so a switch that is sent is a switch that happened.</summary>
    private Task<DirectorCommandResult?> SendSwitch(string directorId, DirectorCommand command, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<LicenseSwitchRequest>(command.PayloadJson, Web)
            ?? throw new InvalidOperationException("The command carried no switch request.");
        lock (_switches) _switches.Add((directorId["dir-".Length..], request));
        return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(new LicenseSwitchResult
        {
            SwitchId = request.SwitchId,
            Outcome = LicenseSwitchOutcomes.Switched,
            Message = "The Director switched.",
            PreviousActiveAccountEmail = request.ExpectedActiveAccountEmail,
            ActiveAccountEmailAfter = request.TargetAccountEmail,
            VerifiedByUsageRead = true,
            StartedAtUtc = _now,
            FinishedAtUtc = _now,
        }, Web)));
    }

    // ----- the pictures -----

    /// <summary>The usage of each account at the moment of a picture.</summary>
    private sealed record Readings(double DevFiveHour, double DevWeekly, double CenterConsultingWeekly, double DuksrevoWeekly,
        double SorenFrederiksenWeekly);

    /// <summary>26 October 13:23 UTC.</summary>
    private static readonly Readings OutOfWeek = new(30, 100, 97, 97, 0.5);

    /// <summary>25 October 18:00 UTC.</summary>
    private static readonly Readings EightyOfWeek = new(20, 80, 97, 97, 0.5);

    private LicenseHolding Holding(string account, bool active, Readings r, DateTime readAt)
    {
        var (five, week, weekResets) = account switch
        {
            Dev => (r.DevFiveHour, r.DevWeekly, DevWeekResets),
            CenterConsulting => (10.0, r.CenterConsultingWeekly, CenterConsultingWeekResets),
            Duksrevo => (10.0, r.DuksrevoWeekly, DuksrevoWeekResets),
            SorenFrederiksen => (0.0, r.SorenFrederiksenWeekly, SorenFrederiksenWeekResets),
            _ => throw new ArgumentException($"Not an account of the 26 October picture: {account}", nameof(account)),
        };
        return new LicenseHolding
        {
            AccountEmail = account,
            IsActive = active,
            Plan = "max",
            RateLimitTier = "default_claude_max_20x",
            FiveHour = new LicenseWindow { Utilization = five, ResetsAtUtc = readAt.AddHours(3) },
            SevenDay = new LicenseWindow { Utilization = week, ResetsAtUtc = weekResets },
            ExtraUsage = new LicenseExtraUsage { IsEnabled = false },
            ReadAtUtc = readAt,
        };
    }

    private void Reports(string machine, DateTime at, Readings r, params string[] spares)
    {
        var holdings = new List<LicenseHolding> { Holding(Dev, true, r, at) };
        holdings.AddRange(spares.Select(s => Holding(s, false, r, at)));
        var report = new LicenseUsageReport
        {
            MachineName = machine,
            OsUser = "owner",
            SentAtUtc = at,
            Trigger = LicenseReportTriggers.Interval,
            Holdings = holdings,
        };
        _store.ApplyReport(Owner, DirectorOf(machine), ++_sequence, report, at, TimeSpan.Zero);
    }

    private static string DirectorOf(string machine) => "dir-" + machine;

    /// <summary>
    /// Builds one picture at <paramref name="at"/>. <paramref name="strandedHoldTheSignIn"/> false is the real picture:
    /// SOREN_NORTH and the Mac hold no sign-in for soren.frederiksen@. True is the negative control: each holds one.
    /// <paramref name="laptopHoldsTheSignIn"/> is true only for the boundary, whose premise is that EVERY machine is ready.
    /// </summary>
    private void Picture(DateTime at, Readings r, bool strandedHoldTheSignIn, bool laptopHoldsTheSignIn = false)
    {
        _now = at;
        // SORENLAPTOP: one report, three days before, then nothing. Its Director is not connected.
        Reports(SorenLaptop, LaptopLastReport, r,
            laptopHoldsTheSignIn ? new[] { SorenFrederiksen, CenterConsulting, Duksrevo } : new[] { CenterConsulting, Duksrevo });
        Reports(Devlinux, at, r, SorenFrederiksen, CenterConsulting, Duksrevo);
        var stranded = strandedHoldTheSignIn
            ? new[] { SorenFrederiksen, CenterConsulting, Duksrevo }
            : new[] { CenterConsulting, Duksrevo };
        Reports(SorenNorth, at, r, stranded);
        Reports(Mac, at, r, stranded);
        foreach (var live in new[] { Devlinux, SorenNorth, Mac })
            _directors.Connect(Owner, DirectorOf(live));

        // soren.frederiksen@'s approver is a browser on SOREN_NORTH whose daily check failed, as every day from 19 October.
        _approvers.Change(Owner, records =>
        {
            var next = records.Approvers.Where(a => a.AccountEmail != SorenFrederiksen).ToList();
            next.Add(new LicenseApprover(SorenFrederiksen, SorenNorth, "owner", "Profile 3", LicenseApproverSources.Registered,
                LicenseApproverStates.CheckFailed, at.AddHours(-5), null, null, "Personal"));
            return ((IReadOnlyList<LicenseApprover>?)next, 0);
        }, at);
    }

    // ----- one Gateway minute -----

    private LicenseReadinessService Readiness()
    {
        var approverReadiness = new LicenseApproverReadiness(_approvers, _reads, _directors.IsConnected, () => _now);
        return new LicenseReadinessService(_store, new LicenseSnapshotSource(_store, _reads), _signInLog, _signIns, approverReadiness.WhyNotReady,
            _directors.IsConnected, _alarms, () => _now);
    }

    /// <summary>The approver alarm's raise, as the Gateway runs it after a check report and every minute: over the real
    /// approver store and alarm service, with the readiness check's own judgement.</summary>
    private Task ApproverAlarmAsync()
    {
        var readiness = Readiness();
        var alarm = new LicenseApproverAlarm(_approvers, _reads, _alarms, _directors.IsConnected, _ => new NoScope(), () => _now,
            CancellationToken.None, judge: readiness.JudgeNow);
        return alarm.RaiseAsync(Owner, CancellationToken.None);
    }

    private sealed class NoScope : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private async Task OneMinuteAsync()
    {
        var snapshots = new LicenseSnapshotSource(_store, _reads);
        var readiness = Readiness();
        var switching = new LicenseSwitchService(_store, snapshots, SendSwitch, Mail, () => _now);

        await readiness.TickAsync(Owner, CancellationToken.None);
        await switching.TickAsync(Owner, CancellationToken.None);
        await _signIns.WhenIdleAsync();
    }

    /// <summary>One line of the banner: the machine and the account the alarm is about, as fields, and its text.</summary>
    private sealed record BannerEntry(string? MachineName, string? AccountEmail, string Text);

    /// <summary>GET /gateway/licenses/alarms for the owner: the banner's lines, as every Cockpit page and the phone show them.</summary>
    private List<BannerEntry> Banner()
    {
        var ctx = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        ctx.Response.Body = new MemoryStream();
        LicenseEndpoints.Alarms(ctx, _ => Owner, _alarms, () => _now).ExecuteAsync(ctx).GetAwaiter().GetResult();
        Assert.Equal(200, ctx.Response.StatusCode);
        var json = JsonDocument.Parse(Encoding.UTF8.GetString(((MemoryStream)ctx.Response.Body).ToArray())).RootElement;
        return json.GetProperty("alarms").EnumerateArray()
            .Select(a => new BannerEntry(Field(a, "machineName"), Field(a, "accountEmail"), a.GetProperty("text").GetString() ?? ""))
            .ToList();
    }

    private static string? Field(JsonElement alarm, string name)
        => alarm.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static bool Same(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The lines of an email, subject first: an alarm email gives each alarm its own line (or, for one alarm, its
    /// own paragraph), so matching within one line keeps one machine's words from answering for another's.</summary>
    private static IEnumerable<string> Lines(LicenseOwnerEmail email)
        => new[] { email.Subject }.Concat(email.BodyText.Split('\n'));

    private static bool Names(string text, params string[] words) => words.All(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    /// <summary>The owner was told loudly about <paramref name="machine"/>: an email line naming it, the account it is
    /// stuck on and the account it needs; a push about it (a push never carries an account: it shows on a locked screen);
    /// and a banner line about it and the account it is stuck on, naming the account it needs.</summary>
    private void AssertToldLoudly(string machine, string stuckOn, string needs)
    {
        // The picture itself: the account it needs has an approver that cannot approve, so no automatic sign-in was
        // started - the gap stood, as it did on the day.
        Assert.Empty(_directors.Of(LicenseContract.SignInVerb));

        // All three surfaces are looked at before failing, so a failure says which of them stayed silent. The banner is
        // matched on its fields (the machine, and the account it is stuck on) and the needed account in that entry's own
        // text; the email line by line; the push by its machine field.
        var named = new[] { machine, stuckOn, needs };
        var banner = Banner();
        var silent = new List<string>();
        if (!_emails.Any(e => Lines(e).Any(line => Names(line, named))))
            silent.Add($"no line of an owner email names {string.Join(", ", named)} (emails sent: {_emails.Count})");
        if (!_pushed.Any(p => Same(p.MachineName, machine)))
            silent.Add($"no push is about {machine} (pushes sent: {_pushed.Count})");
        if (!banner.Any(b => Same(b.MachineName, machine) && Same(b.AccountEmail, stuckOn) && Names(b.Text, needs)))
            silent.Add($"no banner line is about {machine} on {stuckOn} and names {needs} "
                       + $"(banner lines: [{string.Join(" | ", banner.Select(b => $"{b.MachineName}/{b.AccountEmail}: {b.Text}"))}])");
        Assert.True(silent.Count == 0, $"The owner was not told about {machine}: {string.Join("; ", silent)}.");
    }

    /// <summary>Every alarm that opened is about a stranded machine: never the stale one, whose holdings are not current.
    /// Read from the push and the banner entry each opened alarm has, by their machine field - the email is built from the
    /// same alarms, and may name another machine in another role (a skipped machine, an approver's machine).</summary>
    private void AssertOnlyTheStrandedAreNamed()
    {
        var stranded = new[] { SorenNorth, Mac };
        Assert.All(_pushed, p => Assert.Contains(stranded, m => Same(p.MachineName, m)));
        Assert.All(Banner(), b => Assert.True(stranded.Any(m => Same(b.MachineName, m)),
            $"A banner line is about {b.MachineName ?? "no machine"}, not a stranded one: {b.Text}"));
    }

    private void AssertNothingLoud()
    {
        Assert.Empty(_emails);
        Assert.Empty(_pushed);
        Assert.Empty(Banner());
    }

    // ----- positive: the real pictures -----

    [Fact]
    public async Task OutOfWeek_26October_DevlinuxIsSwitchedToTheAccountWithRoom()
    {
        // The part that worked on the day. It guards the replay: if this fails, the picture is wrong, not the Gateway.
        Picture(OutAt, OutOfWeek, strandedHoldTheSignIn: false);

        await OneMinuteAsync();

        var (machine, sent) = Assert.Single(_switches);
        Assert.Equal((Devlinux, Dev, SorenFrederiksen), (machine, sent.ExpectedActiveAccountEmail, sent.TargetAccountEmail));
    }

    [Theory]
    [InlineData(SorenNorth)]
    [InlineData(Mac)]
    public async Task OutOfWeek_26October_AMachineThatCannotSwitch_IsToldByEmailPushAndBanner(string machine)
    {
        Picture(OutAt, OutOfWeek, strandedHoldTheSignIn: false);

        await OneMinuteAsync();

        AssertToldLoudly(machine, stuckOn: Dev, needs: SorenFrederiksen);
        AssertOnlyTheStrandedAreNamed();
    }

    [Theory]
    [InlineData(SorenNorth)]
    [InlineData(Mac)]
    public async Task EightyOfWeek_25October_AMachineReadyForNoAccountWithRoom_RaisesTheWarning(string machine)
    {
        Picture(EightyAt, EightyOfWeek, strandedHoldTheSignIn: false);

        await OneMinuteAsync();

        AssertToldLoudly(machine, stuckOn: Dev, needs: SorenFrederiksen);
        AssertOnlyTheStrandedAreNamed();
    }

    [Theory]
    [InlineData(SorenNorth)]
    [InlineData(Mac)]
    public async Task FiveHourAtEightyFive_25October_TheAlarmThatExistsToday_IsHeard(string machine)
    {
        // The instrument check: the same 25 October picture, but with the dev account's FIVE-HOUR window at 85%, which
        // the alarm already covers. It passes on the code the replay was cut from, so the cases above fail because the
        // Gateway is silent - not because these recorders and this banner read could never hear anything. It does not
        // ask that only the stranded are named: today's five-hour alarm also names the stale SORENLAPTOP.
        Picture(EightyAt, EightyOfWeek with { DevFiveHour = 85 }, strandedHoldTheSignIn: false);

        await OneMinuteAsync();

        AssertToldLoudly(machine, stuckOn: Dev, needs: SorenFrederiksen);
    }

    // ----- negative control: the same pictures, with the sign-in held -----

    [Fact]
    public async Task OutOfWeek_26October_WithTheSignInHeld_EveryMachineIsSwitched_AndNothingIsLoud()
    {
        Picture(OutAt, OutOfWeek, strandedHoldTheSignIn: true);

        await OneMinuteAsync();

        Assert.Equal(new[] { Devlinux, Mac, SorenNorth }.Order(StringComparer.Ordinal), _switches.Select(s => s.Machine).Order(StringComparer.Ordinal));
        Assert.All(_switches, s => Assert.Equal(SorenFrederiksen, s.Request.TargetAccountEmail));
        AssertNothingLoud();
    }

    [Fact]
    public async Task EightyOfWeek_25October_WithTheSignInHeld_NothingIsLoud()
    {
        Picture(EightyAt, EightyOfWeek, strandedHoldTheSignIn: true);

        await OneMinuteAsync();

        Assert.Empty(_switches);
        AssertNothingLoud();
    }

    // ----- step 4: one root cause, one alert, with the approver alarm running -----
    //
    // The phase review of the Gateway track, observation 2: on the 26 October picture the approver alarm opened BESIDE the
    // stranded alarm - soren.frederiksen@'s approver could not approve, and that is also exactly why SOREN_NORTH and the Mac
    // were stranded - so one cause gave two banner lines and two emails. The machine's own alarm now wins: it already says
    // the approver's sentence as its one thing to do. Run in both orders, because the approver alarm is raised after a
    // check report as well as every minute, and may come first.

    public static TheoryData<string, bool> OneCause => new()
    {
        { "out-of-week-26-october", true }, { "out-of-week-26-october", false },
        { "eighty-of-week-25-october", true }, { "eighty-of-week-25-october", false },
    };

    [Theory]
    [MemberData(nameof(OneCause))]
    public async Task ApproverAlarmRunning_AMachineStrandedForWantOfAnApprover_IsToldOnce(string picture, bool approverAlarmFirst)
    {
        if (picture == "out-of-week-26-october") Picture(OutAt, OutOfWeek, strandedHoldTheSignIn: false);
        else Picture(EightyAt, EightyOfWeek, strandedHoldTheSignIn: false);

        if (approverAlarmFirst) await ApproverAlarmAsync();
        await OneMinuteAsync();
        await ApproverAlarmAsync();

        AssertToldLoudly(SorenNorth, Dev, SorenFrederiksen);
        AssertToldLoudly(Mac, Dev, SorenFrederiksen);
        // One line, one push for each stranded machine, and nothing about the approver beside them.
        Assert.DoesNotContain(_alarms.ListOpen(Owner), a => a.Kind == LicenseApproverAlarm.Kind.Key);
        Assert.Equal(2, Banner().Count);
        Assert.Equal(2, _pushed.Count);
        Assert.Single(_emails);
        AssertOnlyTheStrandedAreNamed();
    }

    [Fact]
    public async Task ApproverAlarmRunning_WithTheSignInHeld_NothingIsLoud()
    {
        // The negative control: the approver still cannot approve, but no machine needs it, so the approver alarm is the
        // early warning it always was - one line about the approver, never about a machine.
        Picture(OutAt, OutOfWeek, strandedHoldTheSignIn: true);

        await OneMinuteAsync();
        await ApproverAlarmAsync();

        var open = Assert.Single(_alarms.ListOpen(Owner));
        Assert.Equal((LicenseApproverAlarm.Kind.Key, SorenFrederiksen, Guid.Empty), (open.Kind, open.AccountEmail, open.MachineId));
    }

    // ----- boundary: usage alone is never policed (#4249) -----

    [Theory]
    [InlineData(80, 50)]
    [InlineData(20, 80)]
    [InlineData(97, 50)]
    [InlineData(20, 97)]
    public async Task UsageAlone_AtEightyOrNinetySevenOfEitherWindow_WithEveryMachineReady_RaisesNothing(double devFiveHour, double devWeekly)
    {
        Picture(OutAt, OutOfWeek with { DevFiveHour = devFiveHour, DevWeekly = devWeekly }, strandedHoldTheSignIn: true,
            laptopHoldsTheSignIn: true);

        await OneMinuteAsync();

        AssertNothingLoud();
    }
}
