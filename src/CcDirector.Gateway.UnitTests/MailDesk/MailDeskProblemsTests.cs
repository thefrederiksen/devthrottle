using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Email;
using CcDirector.Gateway.History;
using CcDirector.Gateway.MailDesk;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// A MAILBOX THAT COULD NOT BE CHECKED IS ITS OWN PROBLEM (Mail Desk mission, phase 3 contract, sections 6.2 and 7).
/// Failures in a row of one mailbox are one <c>mailbox-failed</c> problem, counted, cleared by that mailbox's next good
/// check, resolvable by hand with a one-line reason through the same <c>run resolve</c> every problem uses, and folded
/// into the one problems list as its third source - so the factory's Open problems card (the list narrowed to the
/// mail-desk factory) and the 08:00 email show it with no change of their own. The spend limit's problem is the
/// account's, open while the limit stops the model.
///
/// Over the real <see cref="CronRunResultService"/>, the real problems fold, the real stores and a throwaway database;
/// only the clock and the spend are handed in.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskProblemsTests : IDisposable
{
    private const string Mailbox = "email@devthrottle.com";
    private const string OtherMailbox = "support@example.com";
    private const string Machine = "devlinux";
    private const string NotSignedIn = "email@devthrottle.com is not signed in on devlinux: run cc-outlook -a support auth";
    private static readonly TimeZoneInfo Toronto = TimeZoneInfo.FindSystemTimeZoneById("America/Toronto");
    private static readonly TenantId Account = TenantId.Local;
    private static readonly DateTime T0 = new(2026, 10, 18, 13, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly GatewayDatabase _db;
    private readonly MailDeskSwitch _switch;
    private readonly MailDeskChannelStore _channels;
    private readonly MailDeskChannelCheckStore _checks;
    private readonly CronRunResultService _service;
    private DateTime _now = T0.AddHours(1);
    private MailDeskSpend _spend = new(0, MailDeskSpend.DefaultLimitMicros, null);

    public MailDeskProblemsTests()
    {
        _db = _h.Open();
        _switch = new MailDeskSwitch(new TenantSettingsStore(_db));
        _switch.Set(Account, true, "email@devthrottle.com", "the owner's account", T0);
        _channels = new MailDeskChannelStore(_db);
        _checks = new MailDeskChannelCheckStore(_db);
        var runs = new CronRunHistoryStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".runs.json"));
        _service = new CronRunResultService(runs,
            jobById: _ => null,
            allJobs: () => Array.Empty<CronJobDto>(),
            endingsOf: _ => new Dictionary<string, SessionEndingFact>(StringComparer.Ordinal),
            zoneOf: _ => Toronto,
            writeActivity: (tenant, run, row, correction) => runs.WriteActivityRow(tenant, run, row, correction),
            nowUtc: () => _now,
            triggers: new CcDirector.Gateway.Factory.Triggers.TriggerStore(_db),
            scheduleSessionsByName: (_, _, _) => Array.Empty<string>(),
            settleLastStatus: (_, _, _) => false,
            mailDesk: new MailDeskProblemSource(_switch.IsOn, _channels, _checks, _ => _spend),
            lastKnownSession: (_, _, _) => null);
    }

    public void Dispose() => _h.Dispose();

    // ---------- the rig ----------

    private MailDeskChannelEntity Channel(string address = Mailbox)
        => _channels.Add(Account, address, "outlook", new[] { Machine }, MailDeskWho.Person("seed"), "seeded", T0).Channel!;

    private MailDeskChannelCheckEntity? Fail(MailDeskChannelEntity channel, DateTime at, string sentence = NotSignedIn, string machine = Machine)
        => _checks.Record(Account, MailDeskChannelCheckInput.Failed(channel.Id, Guid.NewGuid(), at, machine, sentence, "no account")).Row;

    private MailDeskChannelCheckEntity? Read(MailDeskChannelEntity channel, DateTime at)
        => _checks.Record(Account, MailDeskChannelCheckInput.Read(channel.Id, Guid.NewGuid(), at, Machine)).Row;

    private List<CronRunProblemDto> Open(string? factory = null)
        => _service.Problems(Account, factory, includeClosed: false).Problems;

    private List<CronRunProblemDto> All()
        => _service.Problems(Account, null, includeClosed: true).Problems;

    // ---------- raised, counted, cleared ----------

    [Fact]
    public void Problems_FirstFailedCheck_RaisesOneOpenMailboxProblemInTheChecksOwnWords()
    {
        // Arrange
        var channel = Channel();
        var head = Fail(channel, T0)!;

        // Act
        var problem = Assert.Single(Open());

        // Assert
        Assert.Equal(CronRunProblems.MailboxFailed, problem.Kind);
        Assert.Equal($"{Mailbox} could not be checked on {Machine}: {NotSignedIn}", problem.Sentence);
        Assert.Equal(head.Id.ToString("D"), problem.RunId);
        Assert.Equal(MailDeskWords.DefaultFactory, problem.Factory);
        Assert.Equal(MailDeskWords.DefaultSeat, problem.Seat);
        Assert.Equal(Mailbox, problem.JobName);
        Assert.Equal(1, problem.Occurrences);
        Assert.Equal(CronRunProblemStates.Open, problem.State);
        Assert.Equal(T0, problem.RaisedAtUtc);
        Assert.Equal("open 1 hour", problem.OpenForText);
    }

    [Fact]
    public void Problems_ThreeFailuresInARow_AreOneProblemCountedThreeInTheNewestWords()
    {
        // Arrange - the third failure is on the backup machine, with a new sentence
        var channel = Channel();
        var head = Fail(channel, T0)!;
        Fail(channel, T0.AddMinutes(10));
        Fail(channel, T0.AddMinutes(20), "the sign-in service did not answer", "backup-box");

        // Act
        var problem = Assert.Single(Open());

        // Assert
        Assert.Equal(head.Id.ToString("D"), problem.RunId);
        Assert.Equal(3, problem.Occurrences);
        Assert.Equal($"{Mailbox} could not be checked on backup-box: the sign-in service did not answer (3 checks in a row failed)", problem.Sentence);
        Assert.Equal(3, _service.Problems(Account, null, includeClosed: false).Occurrences);
    }

    [Fact]
    public void Problems_NextGoodCheck_ClearsTheProblem()
    {
        // Arrange
        var channel = Channel();
        Fail(channel, T0);
        Fail(channel, T0.AddMinutes(10));
        var clearing = Read(channel, T0.AddMinutes(20))!;

        // Act
        var open = Open();
        var all = All();

        // Assert
        Assert.Empty(open);
        var cleared = Assert.Single(all);
        Assert.Equal(CronRunProblemStates.Cleared, cleared.State);
        Assert.Equal(clearing.Id.ToString("D"), cleared.ClearedByRunId);
        Assert.Equal("cleared - the next check of this mailbox read it", cleared.StateText);
    }

    [Fact]
    public void Problems_FailingAgainAfterItCleared_StartsANewProblem()
    {
        var channel = Channel();
        Fail(channel, T0);
        Read(channel, T0.AddMinutes(10));
        var again = Fail(channel, T0.AddMinutes(20))!;

        var problem = Assert.Single(Open());

        Assert.Equal(again.Id.ToString("D"), problem.RunId);
        Assert.Equal(2, All().Count);
    }

    [Fact]
    public void Problems_AsOfBeforeTheFailure_DoesNotListIt()
    {
        var channel = Channel();
        Fail(channel, T0);

        var before = _service.Problems(Account, null, includeClosed: true, T0.AddMinutes(-1));

        Assert.Empty(before.Problems);
    }

    [Fact]
    public void Problems_TwoMailboxesOneFailing_OnlyTheFailingOneHasAProblem()
    {
        // Arrange - one report: the first mailbox failed, the second was read
        var first = Channel();
        var second = Channel(OtherMailbox);
        _checks.RecordAll(Account, new[]
        {
            MailDeskChannelCheckInput.Failed(first.Id, null, T0, Machine, NotSignedIn, null),
            MailDeskChannelCheckInput.Read(second.Id, null, T0, Machine),
        });

        // Act
        var problem = Assert.Single(All());

        // Assert
        Assert.Equal(first.Id.ToString("D"), problem.JobId);
        Assert.StartsWith(Mailbox, problem.Sentence);
    }

    [Fact]
    public void Problems_SwitchOff_ListsNoMailDeskProblem()
    {
        var channel = Channel();
        Fail(channel, T0);

        _switch.Set(Account, false, "email@devthrottle.com", "turned off", T0.AddMinutes(30));

        Assert.Empty(All());
    }

    [Fact]
    public void Problems_RemovedMailbox_ItsProblemLeavesTheList()
    {
        var channel = Channel();
        Fail(channel, T0);

        _channels.Remove(Account, Mailbox, MailDeskWho.Person("owner"), "not screened any more", T0.AddMinutes(30));

        Assert.Empty(All());
    }

    [Fact]
    public void Problems_NarrowedToTheMailDeskFactory_IsWhatTheOpenProblemsCardShows()
    {
        // Arrange
        var channel = Channel();
        Fail(channel, T0);

        // Act - the factory page's Open problems card reads the list narrowed to its factory
        var card = Open(MailDeskWords.DefaultFactory);
        var otherCard = Open("invoices");

        // Assert
        Assert.Equal(CronRunProblems.MailboxFailed, Assert.Single(card).Kind);
        Assert.Empty(otherCard);
    }

    // ---------- resolved by hand ----------

    [Fact]
    public void Resolve_TheProblemsRunId_ResolvesItWithTheReason_AndANewFailureStartsANewProblem()
    {
        // Arrange
        var channel = Channel();
        var head = Fail(channel, T0)!;
        Fail(channel, T0.AddMinutes(10));

        // Act
        _now = T0.AddMinutes(15);
        var outcome = _service.Resolve(Account, head.Id.ToString("D"), CronRunResultService.ResolvedByYou, "the mailbox moved; it is being set up again");

        // Assert - resolved, with who and why
        Assert.Equal(200, outcome.StatusCode);
        Assert.Equal(CronRunProblemStates.Resolved, outcome.Answer!.State);
        Assert.Equal("the mailbox moved; it is being set up again", outcome.Answer.ResolvedReason);
        Assert.Empty(Open());

        // Act - it fails again after the resolve
        _now = T0.AddMinutes(25);
        var again = Fail(channel, T0.AddMinutes(20))!;

        // Assert - a new problem, named by the new failure
        var fresh = Assert.Single(Open());
        Assert.Equal(again.Id.ToString("D"), fresh.RunId);
        Assert.Equal(1, fresh.Occurrences);
    }

    [Fact]
    public void Resolve_ALaterFailureOfTheSameRun_IsRefusedAndNamesTheFirst()
    {
        var channel = Channel();
        Fail(channel, T0);
        var later = Fail(channel, T0.AddMinutes(10))!;

        var outcome = _service.Resolve(Account, later.Id.ToString("D"), "you", "why");

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("earlier check of the same run of failures", outcome.Refusal);
        Assert.Single(Open());
    }

    [Fact]
    public void Resolve_AClearedProblem_IsRefused()
    {
        var channel = Channel();
        var head = Fail(channel, T0)!;
        Read(channel, T0.AddMinutes(10));

        var outcome = _service.Resolve(Account, head.Id.ToString("D"), "you", "why");

        Assert.Equal(409, outcome.StatusCode);
        Assert.Contains("already cleared", outcome.Refusal);
    }

    [Fact]
    public void Resolve_TwiceOverTheSameProblem_TheSecondIsRefused()
    {
        var channel = Channel();
        var head = Fail(channel, T0)!;
        _service.Resolve(Account, head.Id.ToString("D"), "you", "first");

        var second = _service.Resolve(Account, head.Id.ToString("D"), "you", "second");

        Assert.Equal(409, second.StatusCode);
        Assert.Equal("first", _checks.Find(Account, head.Id)!.ResolvedReason);
    }

    [Fact]
    public void Resolve_AReasonOnSeveralLines_IsRefusedAndNothingIsResolved()
    {
        var channel = Channel();
        var head = Fail(channel, T0)!;

        var outcome = _service.Resolve(Account, head.Id.ToString("D"), "you", "one\ntwo");

        Assert.Equal(400, outcome.StatusCode);
        Assert.Null(_checks.Find(Account, head.Id)!.ResolvedUtc);
    }

    // ---------- the spend ----------

    [Fact]
    public void Problems_TheLimitStoppedACall_OpensTheSpendProblemInTheContractsWords()
    {
        // Arrange - $5.0003 spent against the $5.00 limit, and a call was stopped at 13:30 UTC
        _spend = new MailDeskSpend(5_000_300, 5_000_000, T0.AddMinutes(30));

        // Act
        var problem = Assert.Single(Open(MailDeskWords.DefaultFactory));

        // Assert
        Assert.Equal(CronRunProblems.SpendLimitReached, problem.Kind);
        Assert.Equal("Mail Desk stopped asking the model: the spend limit of $5.00 is reached ($5.0003 spent). " +
                     "Raise it with maildesk set spend-limit <dollars> --why \"<reason>\".", problem.Sentence);
        Assert.Equal(T0.AddMinutes(30), problem.RaisedAtUtc);
    }

    [Fact]
    public void Problems_TheLimitRaisedAboveTheSpend_ClosesTheSpendProblem()
    {
        _spend = new MailDeskSpend(5_000_300, 10_000_000, T0.AddMinutes(30));

        Assert.Empty(All());
    }

    [Fact]
    public void Problems_TheLimitReachedButNoCallStopped_HasNoSpendProblem()
    {
        _spend = new MailDeskSpend(5_000_000, 5_000_000, null);

        Assert.Empty(All());
    }

    [Fact]
    public void Resolve_TheSpendProblem_IsRefusedSayingHowItClears()
    {
        _spend = new MailDeskSpend(5_000_300, 5_000_000, T0.AddMinutes(30));
        var problem = Assert.Single(Open());

        var outcome = _service.Resolve(Account, problem.RunId, "you", "why");

        Assert.Equal(409, outcome.StatusCode);
        Assert.Equal(MailDeskProblems.SpendNotResolvableSentence, outcome.Refusal);
        Assert.Single(Open());
    }

    // ---------- in the 08:00 email, with no change of its own ----------

    [Fact]
    public void ComposeMorning_AFailingMailbox_IsListedWithTheOtherProblemsInTheEmail()
    {
        // Arrange
        var channel = Channel();
        Fail(channel, T0);
        Fail(channel, T0.AddMinutes(10));
        var morning = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 10, 19, 8, 0, 0), Toronto);
        _now = morning.AddMinutes(5);
        var slot = FactoryEmailSlots.DueAt(_now, Toronto)!;

        // Act - exactly what the sweep composes from
        var (problems, days) = _service.ProblemsAndFactoryDays(Account, slot.DayStartUtc, slot.BoundaryUtc, slot.CutoffUtc);
        var email = FactoryEmailComposer.Compose(new FactoryEmailInput(slot, Toronto, problems.Problems, days,
            new Dictionary<string, FactoryEmailFactory>(StringComparer.OrdinalIgnoreCase), BaseUrl: null));

        // Assert
        Assert.True(email.Send);
        Assert.Equal(1, email.ProblemCount);
        var sentence = $"{Mailbox} could not be checked on {Machine}: {NotSignedIn} (2 checks in a row failed)";
        Assert.Contains(FactoryEmailComposer.E(sentence), email.Html);
        Assert.Contains(sentence, email.Text);
        Assert.Contains(MailDeskWords.DefaultFactory, email.Factories);
    }
}
