using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.MailDesk;
using Xunit;
using static CcDirector.Gateway.Tests.MailDesk.MailDeskHandOffRig;

namespace CcDirector.Gateway.Tests.MailDesk;

/// <summary>
/// FILING IN LIVE MODE (Mail Desk phase 4 contract, section 2; task A). A live channel's <c>archive</c> and <c>junk</c>
/// decisions are queued in the memory the Gateway keeps, oldest first; the check carries the queue out on its next run;
/// what it reports is written on each decision row as <c>filed</c>, <c>already-gone</c> or <c>failed</c>. Only a decision
/// recorded in live mode, on a channel still live, is ever queued; a mailbox written back after a model failure queues
/// nothing until the run that rereads it; a result for a message with no decision fails the run; a queue that would
/// push the memory over its cap keeps no memory at all.
///
/// Through <see cref="MailDeskHandOffRig"/>: the real trigger service and Mail Desk service over a throwaway database,
/// a scripted model, a fake starter, and the stand-in check, which carries out a live channel's queue as
/// <c>checks/screen.py</c> does.
/// </summary>
[Trait("Category", "MailDesk")]
public sealed class MailDeskFilingTests : IAsyncLifetime
{
    private const string BoxA = "inbox@example.com";
    private const string BoxB = "support@example.org";
    private const string Stranger = "someone@example.net";
    private const string Builds = "ci@notifications.example.com";   // allowed: archive
    private const string Spammer = "x@spammy.example";              // blocked: junk
    private MailDeskHandOffRig _rig = null!;

    public Task InitializeAsync()
    {
        _rig = new MailDeskHandOffRig();
        _rig.AddMailDeskTrigger();
        _rig.SaveRules(MailDeskHandOffRig.Document());
        return Task.CompletedTask;
    }

    public async Task DisposeAsync() => await _rig.DisposeAsync();

    private DateTime At(int minutes) => new DateTime(2026, 10, 18, 8, 0, 0, DateTimeKind.Utc).AddMinutes(minutes);

    private StandInMail Mail(string key, int minute, string sender, string subject = "A message")
        => new(key, sender, subject, At(minute));

    private MailDeskChannelEntity Live(string address)
    {
        var channel = _rig.Channel(address);
        SetMode(address, MailDeskWords.Modes.Live);
        return channel;
    }

    private void SetMode(string address, string mode)
        => Assert.NotNull(_rig.Channels.SetMode(MailDeskHandOffRig.Account, address, mode, MailDeskWho.Person("owner"), "set for the test", _rig.Now).Channel);

    private async Task<TriggerRunEntity> Screen(StandInCheck.Plan plan, Func<TriggerCheckReport, TriggerCheckReport>? alter = null)
    {
        _rig.Now = _rig.Now.AddMinutes(5);
        var report = StandInCheck.Run(_rig.Handed(), plan, _rig.Now);
        if (alter is not null) report = alter(report);
        var (result, run) = await _rig.ReportAsync(report);
        Assert.Equal(TriggerReportRefusal.None, result.Refusal);
        return run!;
    }

    /// <summary>The kept queue of a channel, as (key, decision) pairs in order; empty when it has none.</summary>
    private List<(string Key, string Decision)> Queue(Guid channelId)
        => Entry(_rig.KeptMemory(), channelId)?["filingQueue"] is JsonArray q
            ? q.OfType<JsonObject>().Select(i => (i["messageKey"]!.GetValue<string>(), i["decision"]!.GetValue<string>())).ToList()
            : new List<(string, string)>();

    private MailDeskDecisionEntity Row(string key) => _rig.DecisionRows().Single(d => d.MessageKey == key);

    /// <summary>The report with one more filing result in <paramref name="channelId"/>'s section - what a check that
    /// reports a key it was never handed, or retries a move, would send.</summary>
    private static Func<TriggerCheckReport, TriggerCheckReport> WithFilingResult(Guid channelId, string key, string decision, string result)
        => report =>
        {
            var output = JsonNode.Parse(report.Output!)!.AsObject();
            var context = JsonNode.Parse(output["context"]!.GetValue<string>())!.AsObject();
            var section = context["channels"]!.AsArray().OfType<JsonObject>().Single(c => c["id"]!.GetValue<string>() == channelId.ToString("D"));
            section["filing"]!.AsArray().Add(new JsonObject { ["messageKey"] = key, ["decision"] = decision, ["result"] = result });
            output["context"] = context.ToJsonString();
            return new TriggerCheckReport
            {
                CheckedAtUtc = report.CheckedAtUtc, ExitCode = report.ExitCode, State = report.State, Output = output.ToJsonString(),
            };
        };

    // ---------- 2.4 test 1: what is queued, and in what order ----------

    [Fact]
    public async Task ScreenAsync_LiveChannel_QueuesOnlyArchiveAndJunkOldestFirst()
    {
        // Arrange: no model routes, so a known sender is decided mail-desk-agent without the model; the spend limit at
        // zero, so an unknown sender is decided failed without a call - and the mailbox is not written back.
        _rig.SaveRules(MailDeskHandOffRig.Document() with { Routes = Array.Empty<MailDeskRouteEntry>() });
        _rig.Settings.Set(MailDeskHandOffRig.Account, MailDeskSettingNames.SpendLimit, 0m, MailDeskWho.Person("owner"), "no model for the test", _rig.Now);
        var a = Live(BoxA);
        var mail = new[]
        {
            Mail("k-archive-late", 9, Builds, "build 43"),
            Mail("k-junk", 1, Spammer, "buy now"),
            Mail("k-archive", 2, Builds, "build 42"),
            Mail("k-route", 3, Stranger, "my citizen papers"),
            Mail("k-keep", 4, "news@example.org", "this week"),
            new StandInMail("k-agent", Stranger, "hello again", At(5), Known: true),
            Mail("k-failed", 6, Stranger, "who knows"),
        };

        // Act
        var run = await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert: every decision made, and only the archive and junk ones queued, oldest receive time first.
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.Equal(
            new[] { "archive", "failed", "junk", "keep-for-owner", "mail-desk-agent", "route" },
            _rig.DecisionRows().Select(d => d.Decision).Distinct().OrderBy(d => d, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            new[] { ("k-junk", "junk"), ("k-archive", "archive"), ("k-archive-late", "archive") },
            Queue(a.Id));
        Assert.All(_rig.DecisionRows(), d => Assert.Null(d.Outcome));   // queued is not filed
        Assert.Empty(_rig.Starts);
    }

    // ---------- 2.4 test 2: observe mode, and a channel switched to live ----------

    [Fact]
    public async Task ScreenAsync_ObserveChannel_NeverQueues_AndSwitchedToLiveDoesNotQueueItsObserveDecisions()
    {
        // Arrange: an observe channel decides an archive and a junk.
        var a = _rig.Channel(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds), Mail("k-2", 2, Spammer) })));
        Assert.Empty(Queue(a.Id));

        // Act: switched to live, a run with nothing new, then a run with one new archive.
        SetMode(BoxA, MailDeskWords.Modes.Live);
        await Screen(StandInCheck.Reads());
        var afterSwitch = Queue(a.Id);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-3", 3, Builds) })));

        // Assert: the observe decisions are never queued; only the one decided live is.
        Assert.Empty(afterSwitch);
        Assert.Equal(new[] { ("k-3", "archive") }, Queue(a.Id));
        Assert.Equal(MailDeskWords.Modes.Observe, Row("k-1").Mode);
        Assert.Equal(MailDeskWords.Modes.Live, Row("k-3").Mode);
    }

    [Fact]
    public async Task ScreenAsync_ChannelSwitchedBackToObserve_StopsQueuingItsLiveDecisions()
    {
        // Arrange: a live run whose mailbox is written back after a model failure, so its archive is not queued yet.
        var a = Live(BoxA);
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Failure();
        var mail = new[] { Mail("k-1", 1, Builds), Mail("k-broken", 2, Stranger) };
        await Screen(StandInCheck.Reads((BoxA, mail)));
        Assert.Empty(Queue(a.Id));

        // Act: switched back to observe, the reread run.
        SetMode(BoxA, MailDeskWords.Modes.Observe);
        _rig.Model.Answer = (q, _) => ScriptedMailDeskModel.Label(q == ScriptedMailDeskModel.PersonOrSpam ? ScreeningLabels.Person : "A", 0.99);
        var run = await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert: the live archive decision stays unqueued and unfiled.
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.Empty(Queue(a.Id));
        Assert.Equal(MailDeskWords.Modes.Live, Row("k-1").Mode);
        Assert.Null(Row("k-1").Outcome);
    }

    // ---------- 2.4 test 3: a written-back mailbox, then the reread ----------

    [Fact]
    public async Task ScreenAsync_ModelFailureOnALiveMailbox_QueuesNothing_ThenTheRereadQueuesEveryDecision()
    {
        // Arrange: an archive, a junk, and a stranger the model fails on.
        var a = Live(BoxA);
        var mail = new[] { Mail("k-archive", 1, Builds), Mail("k-junk", 2, Spammer), Mail("k-stranger", 3, Stranger, "cheap pills") };
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Failure();

        // Act 1: the failing run.
        var first = await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert 1: decided, but the mailbox is written back and nothing is queued.
        Assert.Equal(TriggerRunOutcome.Screened, first.Outcome);
        Assert.Equal("archive", Row("k-archive").Decision);
        Assert.Equal("failed", Row("k-stranger").Decision);
        Assert.Null(Entry(_rig.KeptMemory(), a.Id)!["keys"]);
        Assert.Empty(Queue(a.Id));

        // Act 2: the reread, the model answering spam now.
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Label(ScreeningLabels.Spam, 0.99);
        var second = await Screen(StandInCheck.Reads((BoxA, mail)));

        // Assert 2: the two decided on the first run and the one decided now, oldest first.
        Assert.Equal(TriggerRunOutcome.Screened, second.Outcome);
        Assert.Equal("junk", Row("k-stranger").Decision);
        Assert.Equal(new[] { ("k-archive", "archive"), ("k-junk", "junk"), ("k-stranger", "junk") }, Queue(a.Id));
    }

    // ---------- 2.4 test 4: never queued twice ----------

    [Fact]
    public async Task ScreenAsync_KeyAlreadyQueued_IsNotQueuedTwice_EvenWhenTheEmailIsReread()
    {
        // Arrange: an archive queued on the first run.
        var a = Live(BoxA);
        var mail = new[] { Mail("k-1", 1, Builds) };
        await Screen(StandInCheck.Reads((BoxA, mail)));
        Assert.Equal(new[] { ("k-1", "archive") }, Queue(a.Id));

        // Act: two runs that carry out no filing, the second rereading the same email.
        await Screen(StandInCheck.Reads().FilingAtMost(0));
        await Screen(StandInCheck.Reads((BoxA, mail)).FilingAtMost(0));

        // Assert: still one item, one decision, no model call for the reread.
        Assert.Equal(new[] { ("k-1", "archive") }, Queue(a.Id));
        Assert.Single(_rig.DecisionRows());
        Assert.Empty(_rig.Model.Calls);
    }

    // ---------- 2.4 test 5: the filing results ----------

    [Fact]
    public async Task ScreenAsync_FilingResults_WriteFiledAlreadyGoneAndFailedWithTheirDetails()
    {
        // Arrange: two archives and two junks queued.
        var a = Live(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[]
        {
            Mail("k-archive", 1, Builds), Mail("k-junk", 2, Spammer), Mail("k-gone", 3, Spammer), Mail("k-refused", 4, Builds),
        })));
        Assert.Equal(4, Queue(a.Id).Count);

        // Act: the next run carries the queue out.
        var run = await Screen(StandInCheck.Reads().Filing("k-gone", "already-gone").Filing("k-refused", "refused"));

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.Equal(4, run.Count);                                   // four filing items attempted
        Assert.Equal(("filed", "moved to Archive"), (Row("k-archive").Outcome, Row("k-archive").OutcomeDetail));
        Assert.Equal(("filed", "moved to Junk Email"), (Row("k-junk").Outcome, Row("k-junk").OutcomeDetail));
        Assert.Equal(("already-gone", "not in the Inbox any more when the move was tried (moved or deleted before)"),
            (Row("k-gone").Outcome, Row("k-gone").OutcomeDetail));
        Assert.Equal(("failed", "the screening check refused the move: the request guard refused the call"),
            (Row("k-refused").Outcome, Row("k-refused").OutcomeDetail));
        Assert.Empty(Queue(a.Id));                                   // the check removed what it attempted; nothing re-queued
        Assert.Contains("filing: 2 filed, 1 already gone, 1 refused", run.Reason);
    }

    [Fact]
    public async Task ScreenAsync_ResultForADecisionAlreadySettled_IsIgnoredAndTheFirstFiledStands()
    {
        // Arrange: an archive queued, then filed.
        var a = Live(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds) })));
        await Screen(StandInCheck.Reads());
        Assert.Equal("filed", Row("k-1").Outcome);

        // Act: a retried move after a channel failure reports it already gone.
        var run = await Screen(StandInCheck.Reads(), WithFilingResult(a.Id, "k-1", "archive", "already-gone"));

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.Equal(("filed", "moved to Archive"), (Row("k-1").Outcome, Row("k-1").OutcomeDetail));
    }

    [Fact]
    public async Task ScreenAsync_ResultForAnUnknownKey_FailsTheRunAndKeepsNoMemory()
    {
        // Arrange: an archive queued.
        var a = Live(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds) })));
        var kept = _rig.KeptMemory();

        // Act: the next report files k-1 and a key this channel has no decision for.
        var run = await Screen(StandInCheck.Reads(), WithFilingResult(a.Id, "k-unknown", "archive", "moved"));

        // Assert: failed, nothing written, the memory as it was.
        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Contains("k-unknown", run.Reason);
        Assert.Contains("no memory was kept", run.Reason);
        Assert.Null(Row("k-1").Outcome);
        Assert.Equal(kept, _rig.KeptMemory());
    }

    [Fact]
    public async Task ScreenAsync_ResultThatIsNotAResultWord_FailsTheRunAndKeepsNoMemory()
    {
        // Arrange
        var a = Live(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds) })));
        var kept = _rig.KeptMemory();

        // Act
        var run = await Screen(StandInCheck.Reads().FilingAtMost(0), WithFilingResult(a.Id, "k-1", "archive", "filed"));

        // Assert
        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Contains("'filed', which is not 'moved', 'already-gone' or 'refused'", run.Reason);
        Assert.Null(Row("k-1").Outcome);
        Assert.Equal(kept, _rig.KeptMemory());
    }

    [Fact]
    public async Task ScreenAsync_AfterAnUnknownKeyFailedTheRun_TheMovedEmailComesBackAlreadyGoneAndTheRowsEndRight()
    {
        // Arrange: an archive and a junk queued; a report that moved both but also names a key with no decision fails.
        var a = Live(BoxA);
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds), Mail("k-2", 2, Spammer) })));
        var failed = await Screen(StandInCheck.Reads(), WithFilingResult(a.Id, "k-unknown", "archive", "moved"));
        Assert.Equal(TriggerRunOutcome.Failed, failed.Outcome);
        Assert.Equal(new[] { ("k-1", "archive"), ("k-2", "junk") }, Queue(a.Id));          // the kept queue still holds both

        // Act: the next run retries the kept queue; Graph gave the moved emails new ids, so both are gone.
        var run = await Screen(StandInCheck.Reads().Filing("k-1", "already-gone").Filing("k-2", "already-gone"));

        // Assert: screened, each row ends already-gone (never filed, never failed), nothing left queued or moved twice.
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.All(new[] { Row("k-1"), Row("k-2") }, d => Assert.Equal(
            ("already-gone", "not in the Inbox any more when the move was tried (moved or deleted before)"), (d.Outcome, d.OutcomeDetail)));
        Assert.Empty(Queue(a.Id));
    }

    // ---------- review of 4274, finding 1: a decision whose key never comes back ends ----------

    [Fact]
    public async Task ScreenAsync_WrittenBackEmailGoneBeforeTheReread_EndsFailedSayingItWasNotSeenAgain()
    {
        // Arrange: an archive, and a stranger the model fails on, so the mailbox is written back and nothing is queued.
        var a = Live(BoxA);
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Failure();
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-archive", 1, Builds), Mail("k-stranger", 2, Stranger) })));
        Assert.Empty(Queue(a.Id));

        // Act: the owner deleted the archive before the reread, so the reread reports only the stranger.
        _rig.Model.Answer = (q, _) => ScriptedMailDeskModel.Label(q == ScriptedMailDeskModel.PersonOrSpam ? ScreeningLabels.Person : "A", 0.99);
        var run = await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-stranger", 2, Stranger) })));

        // Assert: the check read past it, so it ends failed with the sentence, and is never queued.
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.Equal(("failed", MailDeskService.NotSeenAgain), (Row("k-archive").Outcome, Row("k-archive").OutcomeDetail));
        Assert.Empty(Queue(a.Id));
        Assert.Contains("0 waiting to be read again, 1 not seen again", run.Reason);
        Assert.Empty(_rig.Decisions.AwaitingFiling(MailDeskHandOffRig.Account, a.Id));
    }

    [Fact]
    public async Task ScreenAsync_WrittenBackEmailNotReachedByTheRereadYet_WaitsThenIsQueuedWhenRead()
    {
        // Arrange: a stranger the model fails on, then a later archive; the mailbox is written back.
        var a = Live(BoxA);
        _rig.Model.Answer = (_, _) => ScriptedMailDeskModel.Failure();
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-stranger", 1, Stranger), Mail("k-archive", 2, Builds) })));
        _rig.Model.Answer = (q, _) => ScriptedMailDeskModel.Label(q == ScriptedMailDeskModel.PersonOrSpam ? ScreeningLabels.Person : "A", 0.99);

        // Act 1: the reread's batch stops before the archive.
        var partial = await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-stranger", 1, Stranger) })));

        // Assert 1: the cursor has not passed it, so it waits - not queued, not ended.
        Assert.Contains("1 waiting to be read again, 0 not seen again", partial.Reason);
        Assert.Null(Row("k-archive").Outcome);
        Assert.Empty(Queue(a.Id));

        // Act 2: the next batch reads it.
        await Screen(StandInCheck.Reads((BoxA, new[] { Mail("k-archive", 2, Builds) })));

        // Assert 2: queued.
        Assert.Equal(new[] { ("k-archive", "archive") }, Queue(a.Id));
        Assert.Null(Row("k-archive").Outcome);
    }

    // ---------- 2.4 test 6: two channels, one live and one observe ----------

    [Fact]
    public async Task ScreenAsync_OneLiveOneObserveChannel_OnlyTheLiveQueueGrows()
    {
        // Arrange
        var a = Live(BoxA);
        var b = _rig.Channel(BoxB);

        // Act
        await Screen(StandInCheck.Reads(
            (BoxA, new[] { Mail("a-1", 1, Builds), Mail("a-2", 2, Spammer) }),
            (BoxB, new[] { Mail("b-1", 1, Builds), Mail("b-2", 2, Spammer) })));

        // Assert
        Assert.Equal(new[] { ("a-1", "archive"), ("a-2", "junk") }, Queue(a.Id));
        Assert.Empty(Queue(b.Id));
        Assert.Null(Entry(_rig.KeptMemory(), b.Id)!["filingQueue"]);
    }

    // ---------- 2.4 test 7: the memory cap reached by a queue ----------

    [Fact]
    public async Task ScreenAsync_QueueThatPushesTheMemoryOverItsCap_FailsTheRunAndKeepsNoMemory()
    {
        // Arrange: a report whose memory is a few bytes under the cap before anything is queued.
        var a = Live(BoxA);
        var plan = StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds) }));
        var padded = PadTo(StandInCheck.Run(_rig.Handed(), plan, _rig.Now.AddMinutes(5)), a.Id, MailDeskMemory.MaxBytes - 10);
        var before = _rig.KeptMemory();

        // Act
        var run = await Screen(plan, _ => padded);

        // Assert: refused for its size, no memory kept, the decision kept for the reread.
        Assert.Equal(TriggerRunOutcome.Failed, run.Outcome);
        Assert.Contains("bytes, over the", run.Reason);
        Assert.Equal(before, _rig.KeptMemory());
        Assert.Null(Entry(_rig.KeptMemory() is { Length: > 0 } k ? k : null, a.Id));
        Assert.Equal("archive", Row("k-1").Decision);
    }

    [Fact]
    public async Task ScreenAsync_SameMemoryUnderTheCapWithNothingToQueue_IsKept()
    {
        // Arrange: the same report on an observe channel, which queues nothing - the queue is what tips it over.
        var a = _rig.Channel(BoxA);
        var plan = StandInCheck.Reads((BoxA, new[] { Mail("k-1", 1, Builds) }));
        var padded = PadTo(StandInCheck.Run(_rig.Handed(), plan, _rig.Now.AddMinutes(5)), a.Id, MailDeskMemory.MaxBytes - 10);

        // Act
        var run = await Screen(plan, _ => padded);

        // Assert
        Assert.Equal(TriggerRunOutcome.Screened, run.Outcome);
        Assert.NotNull(Entry(_rig.KeptMemory(), a.Id));
    }

    /// <summary>The report with its memory padded, in a field the Gateway keeps as it is, to exactly <paramref name="bytes"/>.</summary>
    private static TriggerCheckReport PadTo(TriggerCheckReport report, Guid channelId, int bytes)
    {
        var memory = JsonNode.Parse(report.State!)!.AsObject();
        var entry = memory["channels"]!.AsArray().OfType<JsonObject>().Single(e => e["id"]!.GetValue<string>() == channelId.ToString("D"));
        entry["pad"] = "";
        var short_ = bytes - Encoding.UTF8.GetByteCount(memory.ToJsonString());
        Assert.True(short_ > 0);
        entry["pad"] = new string('x', short_);
        Assert.Equal(bytes, Encoding.UTF8.GetByteCount(memory.ToJsonString()));
        return new TriggerCheckReport
        {
            CheckedAtUtc = report.CheckedAtUtc, ExitCode = report.ExitCode, Output = report.Output, State = memory.ToJsonString(),
        };
    }
}
