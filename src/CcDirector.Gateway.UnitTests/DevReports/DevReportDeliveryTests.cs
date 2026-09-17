using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.DevReports;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.DevReports;

/// <summary>
/// The delivery rules of PLAN-phase-2.md over the REAL store and the real migrated schema. The only fakes are
/// the two things outside the Gateway's own record: the session's reach (the roster) and the Director on the
/// other end of the tunnel, which counts every prompt it is handed. "Nothing was typed" here is a counted fact
/// about the one seam that can type.
/// </summary>
public sealed class DevReportDeliveryTests : IDisposable
{
    private const string DirectorId = "director-dr";
    private static readonly TenantId Tenant = new("tenant-dev-reports-a");
    private static readonly TenantId OtherTenant = new("tenant-dev-reports-b");
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly string _sid = Guid.NewGuid().ToString("D");

    /// <summary>What the fake roster says about the session.</summary>
    private DevReportSessionReach _reach = DevReportSessionReach.Busy;

    /// <summary>What the fake Director answers a prompt with; null means the command never left the Gateway. It may
    /// throw, which is how a test stands in for the Gateway dying after the prompt went out.</summary>
    private Func<DirectorCommandResult?> _answer = () => DirectorCommandResult.Success(
        JsonSerializer.Serialize(new PromptResponse { Accepted = true, ActivityState = "Working" }));

    private readonly ConcurrentQueue<PromptRequest> _prompts = new();

    /// <summary>Set by a fake Director that has just accepted a prompt: the Gateway's next clock read - the one taken to
    /// record what became of the send - throws, which is the Gateway dying between the send and its commit.</summary>
    private bool _dieOnTheNextClockRead;

    /// <summary>The account scope in effect, as the fake scope seam sets it; the fake Director records it per send.</summary>
    private static readonly AsyncLocal<string?> ScopeInEffect = new();
    private readonly ConcurrentQueue<string?> _scopeAtSend = new();

    private sealed class Scope : IDisposable
    {
        private readonly string? _prior;
        public Scope(TenantId tenant) { _prior = ScopeInEffect.Value; ScopeInEffect.Value = tenant.Value; }
        public void Dispose() => ScopeInEffect.Value = _prior;
    }

    public void Dispose() => _h.Dispose();

    private DevReportStore Store() => new(_h.Open());

    /// <summary>The Gateway's clock. A test moves it forward to stand in for time passing between two processes.</summary>
    private DateTime _now = Now;

    /// <summary>Awaited by the fake Director before it answers, so a test can hold one send open while another process
    /// acts on the same database.</summary>
    private Func<Task> _beforeAnswer = () => Task.CompletedTask;

    private DevReportDelivery Delivery(DevReportStore store) => new(store,
        (tenant, sid) => new DevReportSessionLiveness(_reach, _reach == DevReportSessionReach.Ended ? null : DirectorId, "test roster"),
        (tenant, directorId) => new SessionVerbClient(new DirectorDto { DirectorId = directorId, MachineName = "TEST" }, SendAsync),
        () => _dieOnTheNextClockRead ? throw new InvalidOperationException("the Gateway process died here") : _now,
        tenant => new Scope(tenant));

    private async Task<DirectorCommandResult?> SendAsync(string directorId, DirectorCommand command, CancellationToken ct)
    {
        Assert.Equal("prompt", command.Verb);
        _scopeAtSend.Enqueue(ScopeInEffect.Value);
        await _beforeAnswer();
        var answer = _answer();
        if (answer is not null)
            _prompts.Enqueue(JsonSerializer.Deserialize<PromptRequest>(command.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        return answer;
    }

    /// <summary>A claim taken long enough ago that a settle pass may rule it orphaned.</summary>
    private static DateTime ExpiredClaimTime => Now - DevReportDelivery.SendingClaimTimeout - TimeSpan.FromMinutes(1);

    private static DevReportItem Note(string id, string text = "This number is wrong")
        => new(id, DevReportItem.Note, text,
            new DevReportAnchor(DevReportAnchor.TableCell, "#t td", "42", "Gateway", "Failures", null), "", "", "", "", "");

    private static DevReportItem Answer(string id, string questionId, string value, string comment = "")
        => new(id, DevReportItem.Answer, "", null, questionId, "When should we deploy?", value, value.ToUpperInvariant(), comment);

    private DevReportEntity Publish(DevReportStore store, string key = @"C:\r\report.html", TenantId? tenant = null)
        => store.Publish(tenant ?? Tenant, _sid, key, "<p>html</p>", "waiting-on-you", "Report", Now).Report;

    [Fact]
    public async Task SendAsync_SessionWorking_HoldsTheItemsAndTypesNothing()
    {
        var store = Store();
        var report = Publish(store);
        _reach = DevReportSessionReach.Busy;

        var updates = await Delivery(store).SendAsync(Tenant, report, [Note("n1"), Answer("a1", "deploy", "tonight")], "device", default);

        Assert.Equal(["n1", "a1"], updates.Select(u => u.Id));
        Assert.All(updates, u => Assert.Equal(("held", "Delivered when the agent finishes its turn"), (u.Status, u.StatusLabel)));
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task SendAsync_SessionIdle_DeliversOnePromptInTheSameRequest()
    {
        var store = Store();
        var report = Publish(store);
        _reach = DevReportSessionReach.Idle;

        var updates = await Delivery(store).SendAsync(Tenant, report, [Note("n1"), Answer("a1", "deploy", "tonight")], "device", default);

        Assert.All(updates, u => Assert.Equal(("delivered", "Delivered to the session"), (u.Status, u.StatusLabel)));
        var prompt = Assert.Single(_prompts);
        Assert.Contains("row \"Gateway\", column \"Failures\"", prompt.Text);
        Assert.Contains("\"TONIGHT\" (value \"tonight\")", prompt.Text);
        // The owner's turn: not an agent prompting another, and the dev report door on the ledger row.
        Assert.False(prompt.AgentDriven);
        Assert.True(prompt.AppendEnter);
        Assert.Equal(SubmissionRoutes.GatewayDevReport, prompt.Provenance!.Route);
        Assert.Equal("device", prompt.Provenance.IdentityKind);
        Assert.All(store.Items(Tenant, report.Id), i => Assert.Equal(Now, i.DeliveredAtUtc));
    }

    [Fact]
    public async Task SendAsync_SameIdTwice_IsOneItemAndOneDelivery()
    {
        var store = Store();
        var report = Publish(store);
        _reach = DevReportSessionReach.Idle;
        var delivery = Delivery(store);

        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        var again = await delivery.SendAsync(Tenant, report, [Note("n1", "different words the second time")], "device", default);

        Assert.Equal(("n1", "delivered"), (again.Single().Id, again.Single().Status));
        Assert.Single(_prompts);
        var row = Assert.Single(store.Items(Tenant, report.Id));
        Assert.Equal("This number is wrong", row.Text);
    }

    [Fact]
    public async Task SendAsync_SameIdResentWhileHeld_ReturnsHeldAndStoresOnce()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);

        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        var again = await delivery.SendAsync(Tenant, report, [Note("n1"), Note("n1")], "device", default);

        Assert.Equal(["held", "held"], again.Select(u => u.Status));
        Assert.Single(store.Items(Tenant, report.Id));

        _reach = DevReportSessionReach.Idle;
        Assert.Equal(1, await delivery.SettleAsync(Tenant, _sid, default));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task SendAsync_LaterAnswerWhileEarlierHeld_ReplacesItAndOnlyTheNewerGoes()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);

        await delivery.SendAsync(Tenant, report, [Answer("a1", "deploy", "tonight")], "device", default);
        var later = await delivery.SendAsync(Tenant, report, [Answer("a2", "deploy", "monday")], "device", default);

        Assert.Equal("held", later.Single().Status);
        var first = store.Items(Tenant, report.Id).Single(i => i.ClientItemId == "a1");
        Assert.Equal(("replaced", "Replaced by a later answer", "a2"), (first.Status, first.StatusLabel, first.ReplacedBy));

        // A resend of the replaced id answers its current state rather than reviving it.
        var resend = await delivery.SendAsync(Tenant, report, [Answer("a1", "deploy", "tonight")], "device", default);
        Assert.Equal("replaced", resend.Single().Status);

        _reach = DevReportSessionReach.Idle;
        await delivery.SettleAsync(Tenant, _sid, default);
        var prompt = Assert.Single(_prompts);
        Assert.Contains("\"MONDAY\" (value \"monday\")", prompt.Text);
        Assert.DoesNotContain("tonight", prompt.Text);
        Assert.DoesNotContain("changes the owner's earlier answer", prompt.Text);
    }

    [Fact]
    public async Task SendAsync_LaterAnswerAfterEarlierDelivered_IsDeliveredAsAChange()
    {
        var store = Store();
        var report = Publish(store);
        _reach = DevReportSessionReach.Idle;
        var delivery = Delivery(store);

        await delivery.SendAsync(Tenant, report, [Answer("a1", "deploy", "tonight")], "device", default);
        await delivery.SendAsync(Tenant, report, [Answer("a2", "deploy", "monday")], "device", default);

        Assert.Equal(2, _prompts.Count);
        Assert.Contains("This changes the owner's earlier answer to this question.", _prompts.Last().Text);
        Assert.Equal("delivered", store.Items(Tenant, report.Id).Single(i => i.ClientItemId == "a1").Status);
    }

    [Fact]
    public async Task SendAsync_EndedSession_RefusesEveryNewItemAndStoresNothing()
    {
        var store = Store();
        var report = Publish(store);
        _reach = DevReportSessionReach.Ended;

        var updates = await Delivery(store).SendAsync(Tenant, report, [Note("n1"), Answer("a1", "deploy", "tonight")], "device", default);

        Assert.All(updates, u => Assert.Equal(("refused", "This session has ended"), (u.Status, u.StatusLabel)));
        Assert.Empty(store.Items(Tenant, report.Id));
        Assert.Empty(_prompts);
    }

    [Fact]
    public async Task SettleAsync_HeldAcrossTwoReports_GoesAsOnePrompt()
    {
        var store = Store();
        var first = Publish(store, @"C:\r\one.html");
        var second = Publish(store, @"C:\r\two.html");
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, first, [Note("n1")], "device", default);
        await delivery.SendAsync(Tenant, second, [Answer("a1", "deploy", "tonight")], "device", default);
        Assert.Empty(_prompts);

        _reach = DevReportSessionReach.Idle;
        var delivered = await delivery.SettleAsync(Tenant, _sid, default);

        Assert.Equal(2, delivered);
        var prompt = Assert.Single(_prompts);
        Assert.Contains(@"file ""C:\\r\\one.html""", prompt.Text);
        Assert.Contains(@"file ""C:\\r\\two.html""", prompt.Text);
        // A second turn end has nothing left to send.
        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task SettleAsync_SessionBusyAgain_KeepsTheItemsHeld()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);

        _reach = DevReportSessionReach.Busy;
        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));

        Assert.Empty(_prompts);
        Assert.Equal("held", store.Items(Tenant, report.Id).Single().Status);
    }

    [Fact]
    public async Task SettleAsync_SendNeverLeftTheGateway_StaysHeldAndGoesNextTime()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;
        _answer = () => null;

        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));
        Assert.Equal("held", store.Items(Tenant, report.Id).Single().Status);

        _answer = () => DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse { Accepted = true }));
        Assert.Equal(1, await delivery.SettleAsync(Tenant, _sid, default));
        Assert.Equal("delivered", store.Items(Tenant, report.Id).Single().Status);
    }

    [Fact]
    public async Task SettleAsync_SendUnanswered_IsSentNotConfirmedAndNeverRetried()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;
        _answer = () => DirectorCommandResult.Fail(DirectorCommandStatus.Timeout, "the Director did not answer");

        await delivery.SettleAsync(Tenant, _sid, default);
        await delivery.SettleAsync(Tenant, _sid, default);

        var row = store.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (row.Status, row.StatusLabel));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task SettleAsync_GatewayDiesAfterTheDirectorAccepted_TheNextProcessNeverSendsItAgain()
    {
        // Review Critical 1. The Director accepts the prompt and types the owner's words; the Gateway then dies before
        // it records that. The fake Director accepts, and the Gateway's very next step - reading the clock to record
        // the answer - throws, so nothing after the send is written.
        var before = Store();
        var report = Publish(before);
        await Delivery(before).SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;
        _answer = () =>
        {
            _dieOnTheNextClockRead = true;
            return DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse { Accepted = true }));
        };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Delivery(before).SettleAsync(Tenant, _sid, default));
        Assert.Single(_prompts);

        // A new process over the same database, past the claim timeout, and a Director that would accept anything sent now.
        _dieOnTheNextClockRead = false;
        _now = Now + DevReportDelivery.SendingClaimTimeout + TimeSpan.FromSeconds(1);
        _answer = () => DirectorCommandResult.Success(JsonSerializer.Serialize(new PromptResponse { Accepted = true }));
        var after = Store();
        Assert.Equal(0, await Delivery(after).SettleAsync(Tenant, _sid, default));
        await Delivery(after).SettleAsync(Tenant, _sid, default);

        Assert.Single(_prompts);
        var row = after.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (row.Status, row.StatusLabel));
    }

    [Fact]
    public async Task SettleAsync_AnItemFoundSendingInTheStore_IsSettledNotConfirmedAndNotSent()
    {
        var store = Store();
        var report = Publish(store);
        await Delivery(store).SendAsync(Tenant, report, [Note("n1")], "device", default);
        Assert.Single(store.ClaimWaiting(Tenant, _sid, Guid.NewGuid(), ExpiredClaimTime));
        Assert.Equal(1, store.OpenItemCounts(Tenant, [report.Id])[report.Id]);

        _reach = DevReportSessionReach.Idle;
        await Delivery(Store()).SettleAsync(Tenant, _sid, default);

        Assert.Empty(_prompts);
        var row = store.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (row.Status, row.StatusLabel));
        Assert.Empty(store.OpenItemCounts(Tenant, [report.Id]));
    }

    [Fact]
    public async Task SettleAsync_HeldAndTheSessionHasEnded_RefusesThemAndTypesNothing()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1"), Answer("a1", "deploy", "tonight")], "device", default);

        _reach = DevReportSessionReach.Ended;
        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));

        Assert.Empty(_prompts);
        Assert.All(store.Items(Tenant, report.Id),
            row => Assert.Equal(("refused", "This session has ended"), (row.Status, row.StatusLabel)));
        Assert.Empty(store.OpenItemCounts(Tenant, [report.Id]));
    }

    [Theory]
    [InlineData(DirectorCommandStatus.Conflict, "session has exited")]
    [InlineData(DirectorCommandStatus.NotFound, "session not found")]
    public async Task SettleAsync_DirectorRefusesAndTheSessionHasEnded_IsRefusedNotDelivered(DirectorCommandStatus status, string error)
    {
        // Review High 4, with the Director's real refusal (SessionCommandExecutor.PromptAsync): the roster still
        // said idle, the session exited before the prompt reached its Director, and the Director refused it.
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;
        _answer = () =>
        {
            _reach = DevReportSessionReach.Ended;
            return DirectorCommandResult.Fail(status, error);
        };

        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));

        var row = store.Items(Tenant, report.Id).Single();
        Assert.Equal(("refused", "This session has ended"), (row.Status, row.StatusLabel));
        Assert.Null(row.DeliveredAtUtc);
    }

    [Fact]
    public async Task SettleAsync_DirectorRefusesWhileTheRosterStillSaysIdle_StaysHeldAndIsNotSentAgainInThatPass()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;
        _answer = () => DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "session not found");

        Assert.Equal(0, await delivery.SettleAsync(Tenant, _sid, default));

        Assert.Single(_prompts);
        Assert.Equal(("held", "Delivered when the agent finishes its turn"),
            (store.Items(Tenant, report.Id).Single().Status, store.Items(Tenant, report.Id).Single().StatusLabel));
    }

    [Fact]
    public async Task SendAsync_AnAnswerWhileAnEarlierOneIsSending_DoesNotReplaceTheSendingOne()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        await delivery.SendAsync(Tenant, report, [Answer("a1", "deploy", "tonight")], "device", default);
        Assert.Single(store.ClaimWaiting(Tenant, _sid, Guid.NewGuid(), Now));

        await delivery.SendAsync(Tenant, report, [Answer("a2", "deploy", "monday")], "device", default);

        Assert.NotEqual("replaced", store.Items(Tenant, report.Id).Single(i => i.ClientItemId == "a1").Status);
    }

    [Fact]
    public async Task SendAndDrain_RacingEachOther_DeliverEveryItemExactlyOnce()
    {
        var store = Store();
        var report = Publish(store);
        var delivery = Delivery(store);
        _reach = DevReportSessionReach.Idle;

        var work = new List<Task>();
        for (var i = 0; i < 20; i++)
        {
            var id = "n" + i;
            work.Add(Task.Run(() => delivery.SendAsync(Tenant, report, [Note(id, "note " + id)], "device", default)));
            work.Add(Task.Run(() => delivery.SettleAsync(Tenant, _sid, default)));
        }
        await Task.WhenAll(work);

        for (var i = 0; i < 20; i++)
        {
            var words = "\nnote n" + i + "\nowner-text-";
            Assert.Equal(1, _prompts.Count(p => p.Text.Contains(words, StringComparison.Ordinal)));
        }
        Assert.All(store.Items(Tenant, report.Id), row => Assert.Equal("delivered", row.Status));
    }

    // ---------------------------------------------------------------- two Gateway processes, one database
    //
    // Phase 2 review round 2, finding 2: during a deploy swap two Gateway processes share the database and not the
    // per-process lock. Each test below is two delivery services over two stores on ONE database file, so the only
    // thing they share is the database - exactly the swap.

    [Fact]
    public async Task TwoProcesses_OneSendOutrunsTheClaimTimeoutAndTheOtherSettlesIt_ExactlyOneFinalWriteWins()
    {
        // The interleaving the review describes: process A claims the item and is mid-send; process B's settle rules
        // it orphaned; A's send then comes back accepted. B's ruling must stand - never overwritten to delivered.
        var storeA = Store();
        var storeB = Store();
        var report = Publish(storeA);
        await Delivery(storeA).SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;

        var sendIsOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTheAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeAnswer = async () => { sendIsOut.TrySetResult(); await releaseTheAnswer.Task; };
        var processA = Task.Run(() => Delivery(storeA).SettleAsync(Tenant, _sid, default));
        await sendIsOut.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("sending", storeB.Items(Tenant, report.Id).Single().Status);

        // Process B, after the claim timeout, while A's send is still out.
        _now = Now + DevReportDelivery.SendingClaimTimeout + TimeSpan.FromSeconds(1);
        _beforeAnswer = () => Task.CompletedTask;
        Assert.Equal(0, await Delivery(storeB).SettleAsync(Tenant, _sid, default));
        var ruled = storeB.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (ruled.Status, ruled.StatusLabel));

        // A's Director now accepts. A's finish must write nothing.
        releaseTheAnswer.SetResult();
        Assert.Equal(0, await processA.WaitAsync(TimeSpan.FromSeconds(10)));

        var row = storeA.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Sent to the session, not confirmed"), (row.Status, row.StatusLabel));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task TwoProcesses_TheOtherSettlesInsideTheClaimTimeout_LeavesTheSendAloneAndItFinishesDelivered()
    {
        var storeA = Store();
        var storeB = Store();
        var report = Publish(storeA);
        await Delivery(storeA).SendAsync(Tenant, report, [Note("n1")], "device", default);
        _reach = DevReportSessionReach.Idle;

        var sendIsOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTheAnswer = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _beforeAnswer = async () => { sendIsOut.TrySetResult(); await releaseTheAnswer.Task; };
        var processA = Task.Run(() => Delivery(storeA).SettleAsync(Tenant, _sid, default));
        await sendIsOut.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // Process B settles one second before the claim expires: the send may still be running, so it is not touched,
        // and B has nothing waiting to send.
        _now = Now + DevReportDelivery.SendingClaimTimeout - TimeSpan.FromSeconds(1);
        _beforeAnswer = () => Task.CompletedTask;
        Assert.Equal(0, await Delivery(storeB).SettleAsync(Tenant, _sid, default));
        Assert.Equal("sending", storeB.Items(Tenant, report.Id).Single().Status);

        releaseTheAnswer.SetResult();
        Assert.Equal(1, await processA.WaitAsync(TimeSpan.FromSeconds(10)));

        var row = storeA.Items(Tenant, report.Id).Single();
        Assert.Equal(("delivered", "Delivered to the session"), (row.Status, row.StatusLabel));
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task TwoProcesses_BothDrainTheSameIdleSessionAtOnce_EveryItemGoesInExactlyOnePrompt()
    {
        var storeA = Store();
        var storeB = Store();
        var report = Publish(storeA);
        await Delivery(storeA).SendAsync(Tenant, report,
            [Note("n1", "first note"), Note("n2", "second note"), Answer("a1", "deploy", "tonight")], "device", default);
        _reach = DevReportSessionReach.Idle;

        var results = await Task.WhenAll(
            Task.Run(() => Delivery(storeA).SettleAsync(Tenant, _sid, default)),
            Task.Run(() => Delivery(storeB).SettleAsync(Tenant, _sid, default)));

        Assert.Equal(3, results.Sum());
        foreach (var words in new[] { "\nfirst note\nowner-text-", "\nsecond note\nowner-text-", "(value \"tonight\")" })
            Assert.Equal(1, _prompts.Count(p => p.Text.Contains(words, StringComparison.Ordinal)));
        Assert.All(storeB.Items(Tenant, report.Id), row => Assert.Equal("Delivered to the session", row.StatusLabel));
    }

    [Fact]
    public void Store_FinishClaim_WritesOnlyTheItemsThatStillCarryThatClaim()
    {
        var store = Store();
        var report = Publish(store);
        store.AddItems(Tenant, report, [Note("n1")], DevReportItemStates.HeldState, "device", Now);
        var stale = Guid.NewGuid();
        Assert.Single(store.ClaimWaiting(Tenant, _sid, stale, ExpiredClaimTime));
        Assert.Equal(1, store.SettleExpiredClaims(Tenant, _sid, Now - DevReportDelivery.SendingClaimTimeout, Now));

        Assert.Equal(0, store.FinishClaim(Tenant, stale, DevReportItemStates.DeliveredState, Now));
        Assert.Empty(store.ClaimWaiting(Tenant, _sid, Guid.NewGuid(), Now));
        Assert.Equal("Sent to the session, not confirmed", store.Items(Tenant, report.Id).Single().StatusLabel);
    }

    [Fact]
    public void SendingClaimTimeout_IsWellAboveTheLongestASendCanWait()
    {
        // A send is one tunnel command bounded by the default command timeout. A claim timeout near it would let another
        // process rule a send orphaned while it is still going.
        Assert.True(DevReportDelivery.SendingClaimTimeout >= DirectorCommandRouter.DefaultCommandTimeout * 4,
            $"claim timeout {DevReportDelivery.SendingClaimTimeout} is not well above the command timeout {DirectorCommandRouter.DefaultCommandTimeout}");
    }

    [Fact]
    public async Task SettleAsync_AfterARestartOverTheSameDatabase_DeliversWhatWasHeld()
    {
        var before = Store();
        var report = Publish(before);
        await Delivery(before).SendAsync(Tenant, report, [Note("n1"), Answer("a1", "deploy", "tonight")], "device", default);
        Assert.Empty(_prompts);

        // A new process: a new store over the same database file, a new delivery service with no memory.
        var after = Store();
        _reach = DevReportSessionReach.Idle;
        var delivered = await Delivery(after).SettleAsync(Tenant, _sid, default);

        Assert.Equal(2, delivered);
        Assert.Single(_prompts);
    }

    [Fact]
    public async Task SettleAsync_CalledWithNoAccountInScope_SendsInsideTheAccountsScope()
    {
        // The restart path: the turn-end watcher's catch-up sweep raises the drain with no account in scope, and a
        // hosted tunnel drops a command sent that way. The send itself must carry the scope.
        var store = Store();
        var report = Publish(store);
        await Delivery(store).SendAsync(Tenant, report, [Note("n1")], "device", default);
        Assert.Null(ScopeInEffect.Value);

        _reach = DevReportSessionReach.Idle;
        await Delivery(store).SettleAsync(Tenant, _sid, default);

        Assert.Equal(Tenant.Value, Assert.Single(_scopeAtSend));
    }

    [Fact]
    public void Store_AnotherTenantsReport_IsNotFound()
    {
        var store = Store();
        var report = Publish(store);

        Assert.NotNull(store.Get(Tenant, report.Id));
        Assert.Null(store.Get(OtherTenant, report.Id));
        Assert.Empty(store.List(OtherTenant, null));
        Assert.Null(store.GetVersion(OtherTenant, report.Id, null));
    }

    [Fact]
    public void Publish_SameKeyAgain_IsANewVersionOfTheSameReport()
    {
        var store = Store();

        var (first, created) = store.Publish(Tenant, _sid, "k", "<p>one</p>", "agent-working", "One", Now);
        var (second, createdAgain) = store.Publish(Tenant, _sid, "k", "<p>one</p>", "done", "One again", Now.AddMinutes(1));

        Assert.True(created);
        Assert.False(createdAgain);
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.Version);
        Assert.Equal("<p>one</p>", store.GetVersion(Tenant, first.Id, 1)!.Html);
        Assert.Equal("done", store.GetVersion(Tenant, first.Id, null)!.Status);
        Assert.Equal(Now, second.PublishedAtUtc);
        Assert.Equal(Now.AddMinutes(1), second.UpdatedAtUtc);
    }
}
