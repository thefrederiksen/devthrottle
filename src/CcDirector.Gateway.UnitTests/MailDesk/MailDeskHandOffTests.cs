using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using Microsoft.EntityFrameworkCore;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskHandOffRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// THE MAIL DESK HAND-OFF AND THE RUN (Mail Desk phase 3 contract, sections 1, 2, 3 and 8; task F). Every report of the
/// Mail Desk start goes to <see cref="MailDeskService"/> and never to the count rule; the screening runs off the request;
/// each message is decided once and recorded; a model failure rereads only its own mailbox; the third failure keeps the
/// email for the owner; the spend limit stops calls and says when; nothing is ever started, filed or queued.
///
/// Through <see cref="MailDeskHandOffRig"/>: the real trigger service and Mail Desk service over a throwaway database,
/// a scripted model whose call log proves what was asked, a fake starter that records every start, a stand-in check.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskHandOffTests : IAsyncLifetime
{
    private const string BoxA = "inbox@example.com";
    private const string BoxB = "support@example.org";
    private const string Stranger = "someone@example.net";
    private MailDeskHandOffRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskHandOffRig();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private DateTime At(int minutes) => new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    private StandInMail Mail(string key, int minute, string subject = "A question", string sender = Stranger, bool known = false,
        string preview = "Hello, I have a question about my account.")
        => new(key, sender, subject, At(minute), known, preview);

    private async Task<TriggerRunEntity?> Screen(StandInCheck.Plan plan, string director = DirectorA)
    {
        var report = StandInCheck.Run(_rig.Handed(director), plan, _rig.Now);
        var (result, run) = await _rig.ReportAsync(report, director);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        return run;
    }

    // ---------- section 2: the hand-off and the hard order ----------

    [Fact]
    public async Task ReportCheckAsync_MailDeskReportCountingFive_StartsNoSessionAndWritesAScreenedRun()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);
        var plan = StandInCheck.Reads((BoxA, Enumerable.Range(1, 5).Select(i => Mail($"key-{i}", i)).ToArray()));

        // Act
        var run = await Screen(plan);

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        Assert.Equal(5, run.Count);
        Assert.Empty(_rig.Starts);                                   // the starter fake is never called
        Assert.Equal(5, _rig.DecisionRows().Count);
        Assert.Equal(TriggerRunOutcome.Screened, Assert.Single(_rig.Runs()).Outcome);
    }

    [Fact]
    public async Task ReportCheckAsync_MailDeskReportCountingZero_IsStillScreenedAndKeepsTheMemory()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        var channel = _rig.Channel(BoxA);

        // Act
        var run = await Screen(StandInCheck.Reads());

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        Assert.Equal(0, run.Count);
        Assert.Empty(_rig.Starts);
        Assert.NotNull(Entry(_rig.KeptMemory(), channel.Id));
    }

    [Fact]
    public async Task ReportCheckAsync_NoMailDeskServiceWired_IsRefused409NotReadyAndRecordsNothing()
    {
        // Arrange: a Gateway built without the Mail Desk service, the switch on, a report that counted work.
        await using var bare = new MailDeskHandOffRig(wired: false);
        var trigger = bare.AddMailDeskTrigger();
        bare.Channel(BoxA);
        var report = new TriggerCheckReport
        {
            CheckedAtUtc = bare.Now, ExitCode = 0, State = """{"channels":[]}""",
            Output = """{"count": 5, "context": "{\"version\":2,\"channels\":[]}"}""",
        };

        // Act
        var (status, body, _) = await bare.PostReportAsync(report);
        var (direct, _) = await bare.ReportAsync(report);

        // Assert
        Assert.Equal(409, status);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal("mail_desk_not_ready", json.GetProperty("error").GetString());
        Assert.Contains("cannot screen mail", json.GetProperty("detail").GetString());
        Assert.Equal(TriggerReportRefusal.MailDeskNotReady, direct.Refusal);
        Assert.Empty(bare.Runs());                                   // no run row
        Assert.Equal(0, bare.KeptMemoryRows());                      // no kept memory
        Assert.Empty(bare.Starts);                                   // the starter never called
        Assert.Empty(bare.DecisionRows());                           // no decision
        using var ctx = bare.Db.CreateContext(MailDeskHandOffRig.Account);
        var row = ctx.Triggers.Single(t => t.Id == trigger);
        Assert.Equal("", row.ClaimedByDirectorId);                   // no claim change
        Assert.Null(row.LastCheckUtc);
    }

    [Fact]
    public async Task ReportCheckAsync_MailDeskSwitchOffWithTheServiceWired_IsPhaseOnes409MailDeskOff()
    {
        // Arrange
        await using var off = new MailDeskHandOffRig(switchOn: false);
        off.AddMailDeskTrigger();
        var report = StandInCheck.Run("""{"channels":[]}""", StandInCheck.Reads(), off.Now);

        // Act
        var (status, body, _) = await off.PostReportAsync(report);

        // Assert
        Assert.Equal(409, status);
        Assert.Equal("mail_desk_off", JsonDocument.Parse(body).RootElement.GetProperty("error").GetString());
        Assert.Empty(off.Runs());
        Assert.Equal(0, off.KeptMemoryRows());
        Assert.Empty(off.Starts);
    }

    [Fact]
    public async Task ReportCheckAsync_AMailDeskFactoryTriggerWithAnotherCheck_TakesTheOrdinaryPathAndStartsTheSeat()
    {
        // Arrange: the mail-desk factory, but not checks/screen.py - phase 1's predicate, unchanged.
        _rig.AddMailDeskTrigger(checkPath: "checks/new_mail.py");
        var report = new TriggerCheckReport { CheckedAtUtc = _rig.Now, ExitCode = 0, Output = """{"count": 2}""", State = """{"seen":["m-1"]}""" };

        // Act
        var (result, run) = await _rig.ReportAsync(report);

        // Assert
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        Assert.Equal(TriggerRunOutcome.Started, run!.Outcome);
        Assert.Single(_rig.Starts);
        Assert.Empty(_rig.Model.Calls);
    }

    [Fact]
    public async Task ReportCheckAsync_ASecondReportWhileAScreeningRuns_IsSkippedRunningAndKeepsNoMemory()
    {
        // Arrange: the first screening is held open inside its first model question.
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);
        _rig.Model.Hold = new TaskCompletionSource();
        var first = StandInCheck.Run(_rig.Handed(), StandInCheck.Reads((BoxA, new[] { Mail("key-1", 1) })), _rig.Now);
        var firstResult = await _rig.Triggers.ReportCheckAsync(MailDeskHandOffRig.Account, DirectorA, _rig.TriggerId.ToString("D"), first, CancellationToken.None, MachineA);
        var memoryRowsBefore = _rig.KeptMemoryRows();

        // Act
        var second = StandInCheck.Run(_rig.Handed(), StandInCheck.Reads((BoxA, new[] { Mail("key-2", 2) })), _rig.Now.AddMinutes(5));
        // Not awaited past the report itself: a second screening begun by mistake would wait on the same hold.
        var secondResult = await _rig.Triggers.ReportCheckAsync(MailDeskHandOffRig.Account, DirectorA, _rig.TriggerId.ToString("D"), second, CancellationToken.None, MachineA);
        var began = secondResult.Starting is not null;
        _rig.Model.Hold.SetResult();
        var screened = await firstResult.Starting!;
        if (secondResult.Starting is { } mistaken) await mistaken;
        var skipped = secondResult.Run;

        // Assert
        Assert.False(began, "the second report began a second screening");
        Assert.Equal(TriggerRunOutcome.SkippedRunning, skipped!.Outcome);
        Assert.Equal(TriggerService.ScreeningStillRunning, skipped.Reason);
        Assert.Equal(0, memoryRowsBefore);
        Assert.Equal(TriggerRunOutcome.Screened, screened!.Outcome);
        Assert.Null(_rig.DecisionRows().SingleOrDefault(d => d.MessageKey == "key-2"));   // the skipped report screened nothing
        var kept = Entry(_rig.KeptMemory(), _rig.Channels.List(MailDeskHandOffRig.Account).Single().Id)!;
        Assert.True(kept["keys"]!.AsObject().ContainsKey("key-1"));
        Assert.False(kept["keys"]!.AsObject().ContainsKey("key-2"));                    // the skipped report's memory is not kept
        Assert.Empty(_rig.Starts);
    }

    [Fact]
    public async Task ReportCheckAsync_AFailedMailDeskCheck_IsFailedAndKeepsNoMemory()
    {
        // Arrange: a check that exited non-zero - the whole-run failure of section 6.
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);

        // Act
        var (_, run) = await _rig.ReportAsync(StandInCheck.Failed(_rig.Now, _rig.Handed()!));

        // Assert
        Assert.Equal(TriggerRunOutcome.Failed, run!.Outcome);
        Assert.Equal(0, _rig.KeptMemoryRows());
        Assert.Empty(_rig.Model.Calls);
        Assert.Empty(_rig.Starts);
    }

    // ---------- section 3: a model failure rereads only its own mailbox; the third failure moves on ----------

    [Fact]
    public async Task ScreenAsync_ModelFailsOnOneEmailOfThreeOnMailboxA_RereadsOnlyAAndDecidesTheThirdNextRun()
    {
        // Arrange: mailbox A has three unknown senders; the model fails on the second. Mailbox B has one.
        _rig.AddMailDeskTrigger();
        var a = _rig.Channel(BoxA);
        var b = _rig.Channel(BoxB);
        _rig.SaveRules(MailDeskHandOffRig.Document());
        // A first run so each mailbox has a cursor to write back.
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("a-0", 0) }), (BoxB, new[] { Mail("b-0", 0) })));
        var keptBefore = _rig.KeptMemory();
        var aBefore = Only(Entry(keptBefore, a.Id)!, MailDeskMemory.CheckFields);
        _rig.Model.Answer = (q, email) => email.Subject == "second"
            ? ScriptedMailDeskModel.Failure(MailDeskModelErrorKinds.Timeout)
            : q == ScriptedMailDeskModel.PersonOrSpam ? ScriptedMailDeskModel.Label(ScreeningLabels.Person, 0.99) : ScriptedMailDeskModel.Label("A", 0.99);
        _rig.Now = _rig.Now.AddMinutes(5);
        var aMail = new[] { Mail("a-1", 10, "first"), Mail("a-2", 11, "second"), Mail("a-3", 12, "third") };

        // Act: the run with the failure.
        var run = await Screen(StandInCheck.Reads((BoxA, aMail), (BoxB, new[] { Mail("b-1", 10, "bee") })));

        // Assert: two decided, one failed; the run screened; A written back as it was, B moved on; A's failure row.
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        var rows = _rig.DecisionRows();
        Assert.Equal(MailDeskWords.Decisions.Route, rows.Single(d => d.MessageKey == "a-1").Decision);
        var failed = rows.Single(d => d.MessageKey == "a-2");
        Assert.Equal(MailDeskWords.Decisions.Failed, failed.Decision);
        Assert.Equal(1, failed.AttemptCount);
        Assert.Equal(MailDeskWords.Decisions.Route, rows.Single(d => d.MessageKey == "a-3").Decision);
        var kept = _rig.KeptMemory();
        Assert.Equal(aBefore, Only(Entry(kept, a.Id)!, MailDeskMemory.CheckFields));     // byte for byte as it was
        Assert.Equal("2026-10-18T08:10:00Z", Entry(kept, b.Id)!["cursor"]!["newest"]!.GetValue<string>());
        var checks = _rig.Checks.ByChannel(MailDeskHandOffRig.Account);
        var aFailure = checks[a.Id].Last();
        Assert.False(aFailure.Ok);
        Assert.Equal($"the model could not be asked about 1 of 3 emails on {BoxA} (timeout); they are read again next run", aFailure.Sentence);
        Assert.True(checks[b.Id].Last().Ok);
        var problem = Assert.Single(_rig.OpenProblems());
        Assert.Equal(CronRunProblems.MailboxFailed, problem.Kind);
        Assert.Contains(BoxA, problem.Sentence);

        // Act again: the next run rereads A only (the stand-in reads what its memory says), and the model now answers.
        _rig.Model.Answer = (q, _) => q == ScriptedMailDeskModel.PersonOrSpam ? ScriptedMailDeskModel.Label(ScreeningLabels.Person, 0.99) : ScriptedMailDeskModel.Label("A", 0.99);
        var callsBefore = _rig.Model.Calls.Count;
        var spentBefore = _rig.Decisions.SpentMicros(MailDeskHandOffRig.Account);
        _rig.Now = _rig.Now.AddMinutes(5);
        var again = await Screen(StandInCheck.Reads((BoxA, aMail)));

        // Assert: no call and no charge for A's two decided emails; the third decided in place; B not reread.
        Assert.Equal(TriggerRunOutcome.Screened, again!.Outcome);
        Assert.Equal(new[] { "second", "second" }, _rig.Model.Calls.Skip(callsBefore).Select(c => c.Subject).ToArray());
        var redone = _rig.DecisionRows().Single(d => d.MessageKey == "a-2");
        Assert.Equal(failed.Id, redone.Id);                           // updated in place, not a new row
        Assert.Equal(MailDeskWords.Decisions.Route, redone.Decision);
        Assert.Equal(2, redone.AttemptCount);
        Assert.Equal(6, _rig.DecisionRows().Count);                   // a-0, b-0, a-1..a-3, b-1 and nothing more
        Assert.Equal(spentBefore + 2 * 100, _rig.Decisions.SpentMicros(MailDeskHandOffRig.Account));   // only a-2's two questions were charged
        Assert.Equal("2026-10-18T08:12:00Z", Entry(_rig.KeptMemory(), a.Id)!["cursor"]!["newest"]!.GetValue<string>());
        Assert.True(_rig.Checks.ByChannel(MailDeskHandOffRig.Account)[a.Id].Last().Ok);  // the good check clears A's problem
        Assert.Empty(_rig.OpenProblems());
    }

    [Fact]
    public async Task ScreenAsync_TheThirdFailedAttempt_KeepsTheEmailForTheOwnerAndLetsTheMailboxMoveOn()
    {
        // Arrange: the model always fails on one email.
        _rig.AddMailDeskTrigger();
        var a = _rig.Channel(BoxA);
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Failure(MailDeskModelErrorKinds.NotAChatCompletion);
        var mail = new[] { Mail("a-1", 1, "never answered") };

        // Act: three runs over the same email, five minutes apart.
        await Screen(StandInCheck.Reads((BoxA, mail)));
        Assert.Null(Entry(_rig.KeptMemory(), a.Id)!["cursor"]);      // written back as it was: no cursor, so read again
        _rig.Now = _rig.Now.AddMinutes(5);
        await Screen(StandInCheck.Reads((BoxA, mail)));
        Assert.Equal(MailDeskWords.Decisions.Failed, _rig.DecisionRows().Single().Decision);
        _rig.Now = _rig.Now.AddMinutes(5);
        var third = await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, third!.Outcome);
        var row = Assert.Single(_rig.DecisionRows());
        Assert.Equal(MailDeskWords.Decisions.KeepForOwner, row.Decision);
        Assert.Equal(MailDeskReasons.KeptAfterFailures(MailDeskModelErrorKinds.NotAChatCompletion), row.Reason);
        Assert.Equal(3, row.AttemptCount);
        Assert.NotNull(Entry(_rig.KeptMemory(), a.Id)!["cursor"]);   // the mailbox moved on
        Assert.True(_rig.Checks.ByChannel(MailDeskHandOffRig.Account)[a.Id].Last().Ok);
        Assert.Empty(_rig.OpenProblems());
    }

    [Fact]
    public async Task ScreenAsync_AnEmailAlreadyDecided_IsNotAskedAgainAndCostsNothingOnAReread()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);
        var mail = new[] { Mail("a-1", 1) };
        await Screen(StandInCheck.Reads((BoxA, mail)));
        var calls = _rig.Model.Calls.Count;
        var spent = _rig.Decisions.SpentMicros(MailDeskHandOffRig.Account);
        var row = Assert.Single(_rig.DecisionRows());

        // Act: the same email reported again.
        await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert
        Assert.Equal(calls, _rig.Model.Calls.Count);
        Assert.Equal(spent, _rig.Decisions.SpentMicros(MailDeskHandOffRig.Account));
        var after = Assert.Single(_rig.DecisionRows());
        Assert.Equal((row.Id, row.DecidedUtc, row.AttemptCount), (after.Id, after.DecidedUtc, after.AttemptCount));
    }

    // ---------- section 3: the spend limit ----------

    [Fact]
    public async Task ScreenAsync_TheSpendLimitReached_MakesNoCallFailsTheEmailNamingTheLimitAndRecordsTheStop()
    {
        // Arrange: a limit of $0.0002 - two questions' worth. Three unknown senders: the first takes both questions.
        _rig.AddMailDeskTrigger();
        var a = _rig.Channel(BoxA);
        _rig.SaveRules(MailDeskHandOffRig.Document());
        _rig.Settings.Set(MailDeskHandOffRig.Account, MailDeskSettingNames.SpendLimit, 0.0002m, MailDeskWho.Person("owner"), "a tight limit for the test", _rig.Now);

        // Act
        var run = await Screen(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1, "one"), Mail("a-2", 2, "two"), Mail("a-3", 3, "three") })));

        // Assert: the call that would start at the limit is never made - the fake's log holds only a-1's two questions.
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        Assert.Equal(new[] { "one", "one" }, _rig.Model.Calls.Select(c => c.Subject).ToArray());
        var rows = _rig.DecisionRows();
        Assert.Equal(MailDeskWords.Decisions.Route, rows.Single(d => d.MessageKey == "a-1").Decision);
        foreach (var key in new[] { "a-2", "a-3" })
        {
            var stopped = rows.Single(d => d.MessageKey == key);
            Assert.Equal(MailDeskWords.Decisions.Failed, stopped.Decision);
            Assert.Contains("spend limit of $0.0002 is reached; the model was not asked", stopped.Reason);
        }
        Assert.Equal(_rig.Now, _rig.Settings.SpendStoppedUtc(MailDeskHandOffRig.Account));                 // the stop is recorded
        Assert.NotNull(Entry(_rig.KeptMemory(), a.Id)!["cursor"]);                       // not retried: the mailbox moves on
        Assert.True(_rig.Checks.ByChannel(MailDeskHandOffRig.Account)[a.Id].Last().Ok);
        var spend = Assert.Single(_rig.OpenProblems(), p => p.Kind == CronRunProblems.SpendLimitReached);
        Assert.Contains("Mail Desk stopped asking the model", spend.Sentence);
        Assert.True((await _rig.PersonGetAsync(P + "/settings")).GetProperty("stopped").GetBoolean());
    }

    // ---------- section 3: this phase acts on no mode ----------

    [Theory]
    [InlineData(MailDeskWords.Modes.Observe)]
    [InlineData(MailDeskWords.Modes.Live)]
    public async Task ScreenAsync_EveryDecisionKindInEitherMode_StartsNothingQueuesNoFilingAndRecordsTheMode(string mode)
    {
        // Arrange: one email for each decision the layers can make.
        _rig.AddMailDeskTrigger();
        var a = _rig.Channel(BoxA);
        if (mode == MailDeskWords.Modes.Live)
            _rig.Channels.SetMode(MailDeskHandOffRig.Account, BoxA, MailDeskWords.Modes.Live, MailDeskWho.Person("owner"), "live for the test", _rig.Now);
        _rig.SaveRules(MailDeskHandOffRig.Document());
        _rig.Model.Answer = (q, email) => (q, email.Subject) switch
        {
            (ScriptedMailDeskModel.PersonOrSpam, "spam") => ScriptedMailDeskModel.Label(ScreeningLabels.Spam, 0.99),
            (ScriptedMailDeskModel.Route, "unsure") => ScriptedMailDeskModel.Label("unsure", 0.99),
            (ScriptedMailDeskModel.PersonOrSpam, "broken") => ScriptedMailDeskModel.Failure(),
            (ScriptedMailDeskModel.PersonOrSpam, _) => ScriptedMailDeskModel.Label(ScreeningLabels.Person, 0.99),
            _ => ScriptedMailDeskModel.Label("A", 0.99),
        };
        var mail = new[]
        {
            Mail("k-route", 1, "routed by the model"),
            Mail("k-rule", 2, "my citizen papers"),
            Mail("k-archive", 3, "build 42", "ci@notifications.example.com"),
            Mail("k-keep", 4, "this week", "news@example.org"),
            Mail("k-junk", 5, "buy now", "x@spammy.example"),
            Mail("k-spam", 6, "spam"),
            Mail("k-agent", 7, "unsure"),
            Mail("k-failed", 8, "broken"),
        };

        // Act
        await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert: every decision word made, each recorded in the channel's mode, nothing done.
        var rows = _rig.DecisionRows();
        Assert.Equal(
            new[] { "archive", "failed", "junk", "keep-for-owner", "mail-desk-agent", "route" },
            rows.Select(d => d.Decision).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.All(rows, d => Assert.Equal(mode, d.Mode));
        Assert.All(rows, d => Assert.Null(d.Outcome));
        Assert.Empty(_rig.Starts);                                                       // the starter fake never called
        Assert.Null(Entry(_rig.KeptMemory(), a.Id)!["filingQueue"]);                     // no filing queued
    }

    // ---------- section 8: the decision row ----------

    [Fact]
    public async Task ScreenAsync_ADecision_WritesEveryValueWithTheRulesVersionAndAttemptCount()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        var a = _rig.Channel(BoxA);
        _rig.SaveRules(MailDeskHandOffRig.Document());

        // Act
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1, "A Question About Billing", "Someone@Example.NET") })));

        // Assert
        var row = Assert.Single(_rig.DecisionRows());
        Assert.Equal(a.Id, row.ChannelId);
        Assert.Equal("a-1", row.MessageKey);
        Assert.Equal(At(1), DateTime.SpecifyKind(row.ReceivedUtc, DateTimeKind.Utc));
        Assert.Equal("someone@example.net", row.SenderAddress);
        Assert.Equal("A Question About Billing", row.Subject);
        Assert.Equal(MailDeskWords.Layers.RouteModel, row.Layer);
        Assert.Equal(MailDeskWords.Decisions.Route, row.Decision);
        Assert.Equal(("devthrottle", "mail-desk"), (row.RouteFactory, row.RouteSeat));
        Assert.Equal("route-model: devthrottle/mail-desk at 0.99 (threshold 0.90)", row.Reason);
        Assert.Equal(0.99, row.Confidence);
        Assert.Equal(200, row.CostMicros);                                               // two questions at $0.0001
        Assert.Equal(MailDeskWords.Modes.Observe, row.Mode);
        Assert.Null(row.Outcome);
        Assert.Equal(_rig.Rules.Newest(MailDeskHandOffRig.Account).Version, row.RulesVersion);
        Assert.Equal(1, row.AttemptCount);
    }

    [Fact]
    public async Task ScreenAsync_AnEmptySenderAddress_IsUnknownAndRecordedEmpty()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);

        // Act
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1, "no sender", "") })));

        // Assert
        var row = Assert.Single(_rig.DecisionRows());
        Assert.Equal("", row.SenderAddress);
        Assert.Contains(_rig.Model.Calls, c => c.Question == ScriptedMailDeskModel.PersonOrSpam);
    }

    // NO BODY TEXT, EVER (section 8): a marker in a preview is shown to the model and found in no table and no log line.
    [Fact]
    public async Task ScreenAsync_AMarkerInThePreview_IsInNoTableAndNoLogLine()
    {
        // Arrange
        const string marker = "BODY-MARKER-4f1c9a";
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);
        var lines = new List<string>();
        FileLog.LineObserverForTest = line => { lock (lines) lines.Add(line); };
        try
        {
            // Act
            await Screen(StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1, "subject", preview: $"Hello {marker} there") })));
        }
        finally
        {
            FileLog.LineObserverForTest = null;
        }

        // Assert: the model was shown it (so the test is real), and nothing kept it.
        Assert.Contains(_rig.Model.Emails, e => e.Preview.Contains(marker, StringComparison.Ordinal));
        Assert.NotEmpty(lines);
        lock (lines) Assert.DoesNotContain(lines, l => l.Contains(marker, StringComparison.Ordinal));
        Assert.Empty(TablesHolding(marker));
    }

    /// <summary>Every column of every table in the database file that holds <paramref name="text"/>, as "table.column".
    /// Read from the database itself (its table list and each table's columns), so a table the model does not name is
    /// searched too.</summary>
    private List<string> TablesHolding(string text)
    {
        var found = new List<string>();
        using var ctx = _rig.Db.CreateContext(MailDeskHandOffRig.Account);
        var connection = ctx.Database.GetDbConnection();
        ctx.Database.OpenConnection();
        var tables = new List<string>();
        using (var list = connection.CreateCommand())
        {
            list.CommandText = "SELECT name FROM sqlite_master WHERE type = 'table'";
            using var reader = list.ExecuteReader();
            while (reader.Read()) tables.Add(reader.GetString(0));
        }
        Assert.Contains("mail_desk_decisions", tables);
        foreach (var table in tables)
        {
            var columns = new List<string>();
            using (var info = connection.CreateCommand())
            {
                info.CommandText = $"PRAGMA table_info(\"{table}\")";
                using var reader = info.ExecuteReader();
                while (reader.Read()) columns.Add(reader.GetString(1));
            }
            foreach (var column in columns)
            {
                using var command = connection.CreateCommand();
                command.CommandText = $"SELECT COUNT(*) FROM \"{table}\" WHERE CAST(\"{column}\" AS TEXT) LIKE '%' || @t || '%'";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "@t";
                parameter.Value = text;
                command.Parameters.Add(parameter);
                if (Convert.ToInt64(command.ExecuteScalar()) > 0) found.Add($"{table}.{column}");
            }
        }
        return found;
    }

    // ---------- the context the check printed (phase 2 section 4.1, version 2) ----------

    [Theory]
    [InlineData("""{"version":1,"channels":[]}""", "is not version 2")]
    [InlineData("""{"version":2}""", "has no channels list")]
    [InlineData("""{"version":2,"channels":[{"address":"inbox@example.com","messages":[]}]}""", "names no channel id")]
    [InlineData("not json", "is not JSON")]
    public void Read_AContextThatBreaksTheContract_IsRefusedNamingWhy(string context, string why)
    {
        // Act
        var (sections, refusal) = MailDeskReport.Read(context);

        // Assert
        Assert.Empty(sections);
        Assert.StartsWith("the screening check broke its contract: ", refusal);
        Assert.Contains(why, refusal);
    }

    [Fact]
    public async Task ReportCheckAsync_AContextOfVersionOne_IsAFailedRunAndScreensNothing()
    {
        // Arrange
        _rig.AddMailDeskTrigger();
        _rig.Channel(BoxA);
        var report = StandInCheck.Run(_rig.Handed(), StandInCheck.Reads((BoxA, new[] { Mail("a-1", 1) })), _rig.Now);
        report.Output = report.Output!.Replace("\\u0022version\\u0022:2", "\\u0022version\\u0022:1");
        Assert.Contains("\\u0022version\\u0022:1", report.Output);

        // Act
        var (_, run) = await _rig.ReportAsync(report);

        // Assert
        Assert.Equal(TriggerRunOutcome.Failed, run!.Outcome);
        Assert.Contains("is not version 2", run.Reason);
        Assert.Empty(_rig.DecisionRows());
        Assert.Equal(0, _rig.KeptMemoryRows());
    }

    // ---------- phase 5, test 10: a backup machine whose sign-in is missing ----------

    [Fact]
    public async Task ScreenAsync_ABackupMachineWithNoSignIn_GivesAMailboxProblemNamingTheBackupMachine()
    {
        // Arrange: the start runs on machine-a with machine-b as its backup; machine-a has gone quiet, so machine-b's
        // Director takes the lapsed claim and its check finds no sign-in for the mailbox.
        _rig.AddMailDeskTrigger(backup: true);
        _rig.Channel(BoxA, MachineA, MachineB);
        await Screen(StandInCheck.Reads());
        _rig.Now = _rig.Now.Add(TriggerStore.ClaimLapse(300)).AddSeconds(1);
        var sentence = $"{BoxA} is not signed in on {MachineB}: run cc-outlook -a inbox auth";

        // Act
        var run = await Screen(StandInCheck.Reads().With(BoxA, sentence), DirectorB);

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run!.Outcome);
        var problem = Assert.Single(_rig.OpenProblems());
        Assert.Equal(CronRunProblems.MailboxFailed, problem.Kind);
        Assert.Equal($"{BoxA} could not be checked on {MachineB}: {sentence}", problem.Sentence);
    }

    // ---------- helpers ----------

    private static string Only(JsonObject entry, IReadOnlyList<string> fields)
    {
        var copy = new JsonObject();
        foreach (var f in fields)
            if (entry[f] is { } v) copy[f] = v.DeepClone();
        return copy.ToJsonString();
    }
}
