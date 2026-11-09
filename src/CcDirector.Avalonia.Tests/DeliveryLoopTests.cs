using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Gateway.Contracts;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Director's ONE command delivery loop (issue #4490). A verb that works synchronously before its
/// first await stops every other command from being RECEIVED, while the Director's reports keep going up -
/// so the machine reads as healthy and every Gateway verb sent to it disappears. Measured twice: fourteen
/// hours of it on devthrottle-mac-mini on 25 October 2026, and 430 seconds of it in the factory on
/// 6 November.
/// </summary>
public sealed class DeliveryLoopOrderTests
{
    [Theory]
    [InlineData("interrupt")]
    [InlineData("escape")]
    [InlineData("terminal-input")]
    [InlineData("terminal-keys")]
    [InlineData("terminal-paste")]
    [InlineData("terminal-mouse")]
    [InlineData("enter")]
    [InlineData("execute-action")]
    public void AVerbThatWritesInputAndReturns_ArrivesInOrder_ButNoLongerOnTheLoop(string verb)
    {
        // Typing a line and then pressing Escape must reach the terminal in that order. Until #4816 the
        // loop was what gave them that order, and the price was deafness: terminal-paste held it for about
        // nineteen seconds for ten kilobytes, during which the Director received no command of any kind.
        // They are ordered PER SESSION now, which is the same order and costs nothing else.
        Assert.True(DeliveryLoopOrder.MustArriveInOrder(verb));
        Assert.False(DeliveryLoopOrder.MustStayOnTheLoop(verb));
    }

    [Theory]
    [InlineData("prompt")]             // a guarded send: answers "delivering" at 20s and types on after it
    [InlineData("clear-context")]      // waits up to 60s for the new transcript
    [InlineData("compact-context")]    // waits up to TWO MINUTES for the compaction
    [InlineData("history-picker")]
    [InlineData("queue-send")]         // types the queued prompt through the same guarded send
    [InlineData("ring")]               // the doorbell line, and it refuses a second ring itself
    public void AVerbThatWAITSForTheAgent_IsOrderedByTheGuardedSend_AndNeverChained(string verb)
    {
        // THE REGRESSION THE #4816 REVIEW CAUGHT, pinned. Chaining these made a session's whole WAIT block
        // every keystroke behind it: compact-context on a session, the person presses Escape ten seconds
        // later to cancel it, and the Escape waits out the compaction - the Gateway's 30-second wait expires
        // showing "the Director did not answer" and the Escape lands up to 110 seconds later in whatever
        // state the session reached. They stay off because of the WAIT, not because the session layer orders
        // keystrokes against them - an earlier version of this comment claimed that and it is false for an
        // ordinary SendTextAsync, which is not a guarded send. What the session layer does give them is
        // serialisation against EACH OTHER: SubmitTextAsync holds _submitGate for the whole of one send.
        Assert.False(DeliveryLoopOrder.MustArriveInOrder(verb),
            $"{verb} waits for the agent; chaining it puts that wait in front of somebody's Escape");
        Assert.False(DeliveryLoopOrder.MustStayOnTheLoop(verb));
    }

    [Theory]
    [InlineData("open-terminal-stream")]
    [InlineData("read-file")]
    [InlineData("screenshot-file")]
    [InlineData("close-stream")]
    [InlineData("open-desktop-stream")]
    public void AConnectionBoundVerb_IsTheOnlyKindThatStaysOnTheLoopNow(string verb)
    {
        // These touch THIS connection's per-stream cancellation registry and return at once. They are not
        // work that can wedge, which is the whole reason they are allowed to keep the loop.
        Assert.True(DeliveryLoopOrder.MustStayOnTheLoop(verb));
        Assert.False(DeliveryLoopOrder.MustArriveInOrder(verb));
    }

    [Theory]
    [InlineData("create")]            // the verb that held the loop for 430 seconds on 6 November 2026
    [InlineData("kill")]
    [InlineData("patch")]
    [InlineData("license-switch")]
    [InlineData("upload-image-chunk")]
    [InlineData("browser-list")]
    public void EveryOtherVerb_RunsLooseOnTheThreadPool(string verb)
    {
        // upload-image-chunk is in this list on purpose: its chunks carry a zero-based Seq, the Director
        // refuses one out of order, and the Gateway awaits each chunk's reply before sending the next, so
        // the protocol keeps their order and neither the loop nor a chain has to.
        Assert.False(DeliveryLoopOrder.MustStayOnTheLoop(verb));
        Assert.False(DeliveryLoopOrder.MustArriveInOrder(verb));
    }

    [Fact]
    public void AMalformedCommand_StaysOnTheLoop()
    {
        // It is refused in a microsecond, and handing nothing to the thread pool buys nothing.
        Assert.True(DeliveryLoopOrder.MustStayOnTheLoop(null));
        Assert.True(DeliveryLoopOrder.MustStayOnTheLoop(""));
    }

    [Fact]
    public void EveryOrderedVerb_IsAVerbTheProductActuallyHas()
    {
        // A verb renamed in its area would silently stop being ordered, and keystrokes would start
        // overtaking each other with every test still green.
        foreach (var verb in DeliveryLoopOrder.OrderedVerbs)
            Assert.NotNull(SessionCommandExecutor.AreaFor(verb));
    }

    // RETIRED, deliberately: a test here asserted that DeliveryLoopOrder.OrderedVerbs equals
    // SessionCommandExecutor.VerbsThatMustArriveInOrder. It is CONSTRUCTED from it, so the assertion could
    // not fail, and a test that cannot fail is worse than none - it reads as a guard and is a decoration.
    // (Its comment was wrong twice over as well: a union declared above Areas would throw inside the type
    // initialiser, loudly, not produce a silent empty set - and an empty set would push every verb OFF the
    // loop, not keep it on. The reviewer of the fix round caught both.)
    //
    // What really holds the list is that there is no second copy of it: each area declares its own ordered
    // verbs beside the code that types, and the theories here pin the verbs that matter. The remaining risk -
    // somebody adds a typing verb and forgets to declare it in the same file - is held by proximity, and
    // honestly by nothing else.

    [Theory]
    [InlineData("enter")]            // the phone's Enter key: a raw carriage return, written synchronously
    [InlineData("execute-action")]   // the Wingman's type / send_keys / submit
    [InlineData("queue-add")]        // not typing: a person's reordering is a sequence (#4815)
    // queue-send was here too, from the #4815 review, which was right that it TYPES. The #4816 review then
    // showed that a typing verb which WAITS belongs to the guarded send rather than to a chain; it is pinned
    // by AVerbThatWAITSForTheAgent_IsOrderedByTheGuardedSend_AndNeverChained instead.
    public void TheVerbsTheREVIEWFound_AreOrdered(string verb)
    {
        Assert.True(DeliveryLoopOrder.MustArriveInOrder(verb));
    }

    [Fact]
    public void NoVerbIsBothOrderedAndOnTheLoop()
    {
        // The two doors out of the loop are exclusive. A verb in both sets would take the first branch and
        // keep the loop, which is the defect #4816 exists to remove, silently and with every test green.
        foreach (var verb in DeliveryLoopOrder.OrderedVerbs)
            Assert.False(DeliveryLoopOrder.MustStayOnTheLoop(verb),
                $"{verb} is ordered AND on the loop, so it would hold the loop for its whole run");
    }
}

/// <summary>
/// Measuring a hold on the delivery loop. The instrument has to sit OUTSIDE the call, because the timing
/// that already exists - CommandAnswerTiming - starts measuring inside it and is held by the very verb it
/// would report.
/// </summary>
public sealed class DeliveryLoopWatchTests
{
    private static (DeliveryLoopWatch Watch, List<string> Lines, FakeTimeProvider Clock) NewWatch()
    {
        var lines = new List<string>();
        var clock = new FakeTimeProvider();
        return (new DeliveryLoopWatch(clock, lines.Add), lines, clock);
    }

    [Fact]
    public void AQuickCommand_SaysNothing()
    {
        var (watch, lines, clock) = NewWatch();

        using (watch.Enter("prompt", "cmd-1"))
            clock.Advance(TimeSpan.FromMilliseconds(40));

        Assert.Empty(lines);
    }

    [Fact]
    public void ALoopHeldPastTheBudget_IsSaidOnceWhileItIsHeld_AndAgainWithItsTotalWhenItLetsGo()
    {
        var (watch, lines, clock) = NewWatch();

        using (watch.Enter("create", "cmd-2"))
        {
            clock.Advance(TimeSpan.FromSeconds(30));
            watch.SayIfHeld();
            watch.SayIfHeld();      // the tick fires every cycle; the hold is named once, not every cycle
            Assert.Single(lines);
            Assert.Contains("DELIVERY LOOP HELD by verb=create", lines[0]);
            Assert.Contains("cmdId=cmd-2", lines[0]);
            Assert.Contains("30.0s", lines[0]);

            clock.Advance(TimeSpan.FromSeconds(400));
        }

        Assert.Equal(2, lines.Count);
        Assert.Contains("released by verb=create", lines[1]);
        Assert.Contains("430.0s", lines[1]);
    }

    [Fact]
    public void WhileTheLoopIsFree_TheTickSaysNothing()
    {
        var (watch, lines, clock) = NewWatch();

        using (watch.Enter("prompt", "cmd-3")) { }
        clock.Advance(TimeSpan.FromHours(14));
        watch.SayIfHeld();

        Assert.Empty(lines);
        Assert.Null(watch.Inside());
    }

    [Fact]
    public void WhatIsInsideTheLoop_IsAnswerableWhileItIsStillInThere()
    {
        // The question nothing could answer on 25 October: WHICH verb is holding it, right now.
        var (watch, _, clock) = NewWatch();

        using (watch.Enter("license-switch", "cmd-4"))
        {
            clock.Advance(TimeSpan.FromSeconds(9));
            var inside = watch.Inside();

            Assert.NotNull(inside);
            Assert.Equal("license-switch", inside!.Value.Verb);
            Assert.Equal(TimeSpan.FromSeconds(9), inside.Value.Held);
        }
    }
}

/// <summary>
/// The behaviour the whole change exists for: a verb that never returns does not stop the next command
/// from being answered.
/// </summary>
public sealed class DeliveryLoopDoesNotStopOnAHungVerbTests
{
    private static GatewayStreamClient Client(Func<DirectorCommand, Task<DirectorCommandResult>> dispatcher) =>
        new(new GatewayConfig { Url = "http://gateway.test", Token = "t", StreamMode = true },   // never dialled
            "dir-test", "0.0.0", () => new List<SessionDto>(), commandDispatcher: dispatcher);

    [Fact]
    public async Task AVerbThatNeverReturns_DoesNotStopTheNextCommandFromBeingAnswered()
    {
        // The hung verb blocks SYNCHRONOUSLY, which is the shape that holds the loop: a verb that blocks
        // after an await was never the problem. On the old code the first call below never came back at
        // all - the handler was invoked on the loop and the loop was the test's own thread - so this test
        // fails by timing out, which is exactly what the Director did for fourteen hours.
        using var wedged = new ManualResetEventSlim(false);
        var client = Client(cmd =>
        {
            if (cmd.Verb == "create") wedged.Wait();          // never released in this test
            return Task.FromResult(DirectorCommandResult.Success());
        });

        // Task.Run would UNWRAP the handler's task and wait for the verb itself, which is the thing being
        // measured, so the inner task is captured instead: what this asserts is that the handler HANDED IT
        // BACK, not that the verb finished.
        Task<DirectorCommandResult>? handedBack = null;
        var handedItBack = Task.Run(() => { handedBack = client.DeliverAsync(new DirectorCommand { Verb = "create", CommandId = "c1" }); });
        // A watchdog, not a verdict: on the old code this never finishes and the timeout names the hang. On the
        // new code it finishes as soon as the handler hands its task back, whatever the machine's speed.
        await handedItBack.WaitAsync(TimeSpan.FromSeconds(30));

        var next = await client.DeliverAsync(new DirectorCommand { Verb = "hold", CommandId = "c2" }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DirectorCommandStatus.Ok, next.Status);
        Assert.NotNull(handedBack);
        Assert.False(handedBack!.IsCompleted, "the wedged verb is meant to still be running");

        // Let the wedged verb go, so this test leaves nothing waiting for ever.
        wedged.Set();
        await handedBack.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnOrderedVerbThatNeverReturns_NoLongerStopsTheNextCommandEITHER()
    {
        // THE HEADLINE OF #4816. Until now a keystroke verb kept the loop, so this was the one shape the
        // #4490 change deliberately did not fix: terminal-paste paces itself for about nineteen seconds
        // for ten kilobytes, and for all of it the Director received nothing. On the old code the first
        // call below never comes back and this test fails by timing out, which is what the machine did.
        using var wedged = new ManualResetEventSlim(false);
        var client = Client(cmd =>
        {
            if (cmd.Verb == "terminal-paste") wedged.Wait();      // the paced paste, at its worst
            return Task.FromResult(DirectorCommandResult.Success());
        });

        Task<DirectorCommandResult>? handedBack = null;
        var handedItBack = Task.Run(() => { handedBack = client.DeliverAsync(
            new DirectorCommand { Verb = "terminal-paste", SessionId = "s1", CommandId = "paste" }); });
        // A watchdog, not a verdict: on correct code it finishes as soon as the handler hands its task back.
        await handedItBack.WaitAsync(TimeSpan.FromSeconds(30));

        var next = await client.DeliverAsync(new DirectorCommand { Verb = "hold", CommandId = "c2" })
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DirectorCommandStatus.Ok, next.Status);
        Assert.NotNull(handedBack);
        Assert.False(handedBack!.IsCompleted, "the wedged paste is meant to still be running");

        wedged.Set();
        await handedBack.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AnOrderedVerbHeldForOneSession_DoesNotHoldANOTHERSession()
    {
        // The chain is per session, which is the thing the loop could never be. A paste into one terminal
        // must not delay a prompt into a different one.
        using var wedged = new ManualResetEventSlim(false);
        var client = Client(cmd =>
        {
            if (cmd.SessionId == "slow") wedged.Wait();
            return Task.FromResult(DirectorCommandResult.Success());
        });

        Task<DirectorCommandResult>? held = null;
        var handedItBack = Task.Run(() => { held = client.DeliverAsync(
            new DirectorCommand { Verb = "terminal-paste", SessionId = "slow", CommandId = "paste" }); });
        await handedItBack.WaitAsync(TimeSpan.FromSeconds(30));

        var other = await client.DeliverAsync(
            new DirectorCommand { Verb = "prompt", SessionId = "quick", CommandId = "prompt" })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DirectorCommandStatus.Ok, other.Status);
        Assert.False(held!.IsCompleted);

        wedged.Set();
        await held.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task AWedgedSessionsQueue_RefusesPastItsCap_RatherThanPilingUpAndRunningItAllLater()
    {
        using var wedged = new ManualResetEventSlim(false);
        var client = Client(cmd =>
        {
            if (cmd.SessionId == "slow") wedged.Wait();
            return Task.FromResult(DirectorCommandResult.Success());
        });

        // THE FIRST CALL GOES THROUGH Task.Run WITH A WATCHDOG, like its siblings above, and the review of
        // #4816 is why: called inline, this test does not FAIL on the old routing, it HANGS - the handler
        // runs on the test's own thread and wedged.Wait() never returns, which took out a whole test host
        // for ten minutes and hid the other failures behind it. A test whose failure mode is a dead run
        // proves nothing about anything.
        var queued = new List<Task<DirectorCommandResult>>();
        Task<DirectorCommandResult>? first = null;
        var handedItBack = Task.Run(() => { first = client.DeliverAsync(new DirectorCommand
            { Verb = "terminal-input", SessionId = "slow", CommandId = "key-0" }); });
        await handedItBack.WaitAsync(TimeSpan.FromSeconds(30));
        queued.Add(first!);

        // The rest queue behind it. None of these calls blocks - that is the fix.
        for (var i = 1; i <= OrderedBySession.MostThatMayWait; i++)
            queued.Add(client.DeliverAsync(new DirectorCommand
                { Verb = "terminal-input", SessionId = "slow", CommandId = $"key-{i}" }));

        var refused = await client.DeliverAsync(new DirectorCommand
            { Verb = "terminal-input", SessionId = "slow", CommandId = "one-too-many" })
            .WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(DirectorCommandStatus.Conflict, refused.Status);
        Assert.Equal("one-too-many", refused.CommandId);
        Assert.Contains("command(s) waiting in the order they were sent", refused.Error);

        // And a DIFFERENT session is still answered while that queue is full and stuck.
        var other = await client.DeliverAsync(new DirectorCommand
            { Verb = "terminal-input", SessionId = "quick", CommandId = "elsewhere" })
            .WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(DirectorCommandStatus.Ok, other.Status);

        wedged.Set();
        Assert.True(await Task.WhenAll(queued).WaitAsync(TimeSpan.FromSeconds(30)) is not null);
    }

    [Fact]
    public async Task ThreeOrderedVerbsForONESession_ArriveInTheOrderTheyWereSent_EvenThoughNoneWasAwaited()
    {
        // The order the loop used to give, now given by the session's chain - and asserted the way that can
        // actually fail. The first verb does not finish until all three have been delivered, so if they ran
        // loose on the thread pool the second and third would overtake it and the order would be
        // second, third, first. Nothing here waits on the clock: the gate is released by this test.
        var gate = new TaskCompletionSource();
        var order = new List<string>();
        var client = Client(async cmd =>
        {
            if (cmd.CommandId == "first") await gate.Task;
            lock (order) order.Add(cmd.CommandId);
            return DirectorCommandResult.Success();
        });

        var one = client.DeliverAsync(new DirectorCommand { Verb = "terminal-input", SessionId = "s1", CommandId = "first" });
        var two = client.DeliverAsync(new DirectorCommand { Verb = "escape", SessionId = "s1", CommandId = "second" });
        var three = client.DeliverAsync(new DirectorCommand { Verb = "terminal-keys", SessionId = "s1", CommandId = "third" });

        gate.SetResult();
        await Task.WhenAll(one, two, three).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["first", "second", "third"], order);
    }
}

/// <summary>
/// The join: the periodic tick is what asks whether the loop is held. A held loop cannot report on itself,
/// so if nothing asked, the instrument would be decoration.
/// </summary>
public sealed class DeliveryLoopIsWatchedByTheTickTests
{
    [Fact]
    public async Task TheRePushTick_AsksWhetherTheLoopIsHeld_AndSaysSoWhenItIs()
    {
        using var wedged = new ManualResetEventSlim(false);
        // Raised by the verb itself once it is running. DeliverAsync enters the watch BEFORE it dispatches, so
        // when this is set the loop is held - a signal the code raises, not a poll of the clock.
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // Start() is NOT called: it would dial the Gateway and arm the real re-push timer, and either would let
        // the machine into this test. The tick is the production method, driven by its own name below.
        var client = new GatewayStreamClient(
            new GatewayConfig { Url = "http://gateway.test", Token = "t", StreamMode = true },   // never dialled
            "dir-test", "0.0.0", () => new List<SessionDto>(),
            commandDispatcher: cmd =>
            {
                if (cmd.Verb == "terminal-input") { entered.TrySetResult(); wedged.Wait(); }
                return Task.FromResult(DirectorCommandResult.Success());
            },
            rePushInterval: TimeSpan.FromSeconds(30));

        // A verb that WRITES INPUT AND RETURNS, which since #4816 wedges its SESSION'S QUEUE rather than the
        // delivery loop. Not prompt: a prompt waits for the agent, so it is deliberately not chained at all
        // (the #4816 review). It is held from another thread; this one drives the tick.
        _ = Task.Run(() => client.DeliverAsync(new DirectorCommand { Verb = "terminal-input", SessionId = "s1", CommandId = "wedged" }));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));   // a watchdog on a hang, never the verdict

        // WHERE THE WEDGE IS NOW, AND THE WHOLE POINT OF #4816: the session's queue is held and the loop is
        // free. Before that change this same verb held the loop and the Director received nothing at all.
        Assert.NotNull(client.OrderedBySession.Inside("s1"));
        Assert.Null(client.DeliveryLoop.Inside());
        Assert.Null(client.OrderedBySession.Inside("another-session"));

        // THE JOIN, AND NOTHING THE CLOCK DECIDES. What must be true is that the tick ASKS - BOTH watches,
        // because the loop can still be held by a connection-bound verb and a session queue is where every
        // long verb lives now. Whether the answer is yes depends on the budget, and that half is proved
        // against a fake clock. A test that slept past the budget to see the line would be a test the clock
        // decides (the 11 October ruling), and it would prove nothing this does not.
        Assert.Equal(0, client.DeliveryLoop.TimesItWasAsked);
        Assert.Equal(0, client.OrderedBySession.TimesItWasAsked);

        client.RePushTick();

        Assert.Equal(1, client.DeliveryLoop.TimesItWasAsked);
        Assert.Equal(1, client.OrderedBySession.TimesItWasAsked);

        wedged.Set();
        await client.StopAsync();
    }
}

/// <summary>
/// The session queues themselves: order, isolation, what they say about being held, and letting go.
///
/// Nothing here waits on the clock. Where a test needs to know that a queued verb has started, the verb
/// says so by completing a <see cref="TaskCompletionSource"/> this test owns; where it needs the budget to
/// have passed, a <see cref="FakeTimeProvider"/> is moved. The <c>WaitAsync</c> calls are watchdogs on a
/// hang, never the verdict - on correct code none of them waits.
/// </summary>
public sealed class OrderedBySessionTests
{
    [Fact]
    public async Task WorkForOneSession_RunsInTheOrderItWasQueued()
    {
        var chain = new OrderedBySession();
        var gate = new TaskCompletionSource();
        var order = new List<string>();

        async Task<string> Step(string name, bool waitFirst)
        {
            if (waitFirst) await gate.Task;
            lock (order) order.Add(name);
            return name;
        }

        // The first is still waiting when the second and third are queued, so loose scheduling would let
        // them overtake it and the order would be second, third, first.
        var one = chain.Run("s1", "prompt", "c1", () => Step("first", waitFirst: true));
        var two = chain.Run("s1", "escape", "c2", () => Step("second", waitFirst: false));
        var three = chain.Run("s1", "terminal-keys", "c3", () => Step("third", waitFirst: false));

        gate.SetResult();
        await Task.WhenAll(one, two, three).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(["first", "second", "third"], order);
    }

    [Fact]
    public async Task WorkHeldForOneSession_DoesNotHoldAnother()
    {
        var chain = new OrderedBySession();
        var held = new TaskCompletionSource<string>();
        var otherRan = new TaskCompletionSource();

        var stuck = chain.Run("slow", "terminal-paste", "c1", () => held.Task);
        var other = chain.Run("quick", "prompt", "c2", () =>
        {
            otherRan.SetResult();
            return Task.FromResult("done");
        });

        await otherRan.Task.WaitAsync(TimeSpan.FromSeconds(30));   // it ran while "slow" is still going
        Assert.Equal("done", await other);
        Assert.False(stuck.IsCompleted);

        held.SetResult("let go");
        await stuck.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task AFaultedVerb_DoesNotPoisonTheRestOfItsSessionsQueue()
    {
        var chain = new OrderedBySession();

        var broken = chain.Run<string>("s1", "prompt", "c1",
            () => Task.FromException<string>(new InvalidOperationException("the verb threw")));
        var after = chain.Run("s1", "escape", "c2", () => Task.FromResult("still ran"));

        var threw = await Assert.ThrowsAsync<InvalidOperationException>(() => broken);
        Assert.Equal("the verb threw", threw.Message);
        Assert.Equal("still ran", await after);           // the chain carried on
    }

    [Fact]
    public async Task AFinishedSessionsQueue_IsLetGo()
    {
        var chain = new OrderedBySession();

        Assert.Equal(0, chain.ChainsHeld);

        // The entry is removed by a continuation that runs AFTER the work's own task completes, so awaiting
        // the work is not enough. The chain's own tail IS that continuation: captured here and awaited
        // below, which needs no clock and no polling.
        var one = chain.Run("s1", "prompt", "c1", () => Task.FromResult("done"));
        var oneSettled = chain.Settled("s1");
        var two = chain.Run("s2", "prompt", "c2", () => Task.FromResult("done"));
        var twoSettled = chain.Settled("s2");

        await Task.WhenAll(one, two).WaitAsync(TimeSpan.FromSeconds(30));
        await Task.WhenAll(oneSettled, twoSettled).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(0, chain.ChainsHeld);
    }

    [Fact]
    public async Task AQueueHeldPastTheBudget_IsSaidOnce_WithWhatIsWaitingBehindIt()
    {
        var lines = new List<string>();
        var clock = new FakeTimeProvider();
        var chain = new OrderedBySession(clock, lines.Add);
        var held = new TaskCompletionSource<string>();
        var running = new TaskCompletionSource();

        // The work says when it is running; nothing here waits on a duration. Inside() is stamped before
        // work() is called, so once this completes the queue is recorded as held.
        var stuck = chain.Run("s1", "terminal-paste", "paste-1", () => { running.SetResult(); return held.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.NotNull(chain.Inside("s1"));
        _ = chain.Run("s1", "prompt", "prompt-2", () => Task.FromResult("queued behind it"));   // left queued

        chain.SayIfAnyQueueIsHeld();
        Assert.Empty(lines);                             // inside the budget: nothing to say

        clock.Advance(OrderedBySession.Budget);
        chain.SayIfAnyQueueIsHeld();
        chain.SayIfAnyQueueIsHeld();                     // asked twice, said once

        var line = Assert.Single(lines);
        Assert.Contains("session=s1", line);
        Assert.Contains("verb=terminal-paste", line);
        Assert.Contains("cmdId=paste-1", line);
        Assert.Contains("1 command(s) waiting behind it", line);
        Assert.Equal(1, chain.TimesItSaidAQueueWasHeld);
        Assert.Equal(3, chain.TimesItWasAsked);

        held.SetResult("let go");
        Assert.False(stuck.IsFaulted);
    }

    [Fact]
    public void AFreeQueue_SaysNothing()
    {
        var lines = new List<string>();
        var clock = new FakeTimeProvider();
        var chain = new OrderedBySession(clock, lines.Add);

        clock.Advance(TimeSpan.FromHours(1));
        chain.SayIfAnyQueueIsHeld();

        Assert.Empty(lines);
        Assert.Equal(1, chain.TimesItWasAsked);
    }

    // A QUEUE THAT IS NOT MOVING IS REFUSED, NOT JOINED. While a verb held the delivery loop, a command
    // behind it could not even be delivered, so the deafness was also the backpressure. With the loop free a
    // wedged session would accept commands for ever and then run every stale one at once - which for the
    // keystroke verbs means typing hours-old input into somebody's terminal.

    [Fact]
    public async Task AQueueAtItsCap_RefusesTheNextCommand_AndSaysWhatIsInFrontOfIt()
    {
        var clock = new FakeTimeProvider();
        var chain = new OrderedBySession(clock, _ => { });
        var held = new TaskCompletionSource<string>();
        var running = new TaskCompletionSource();

        var stuck = chain.Run("s1", "terminal-paste", "paste-1", () => { running.SetResult(); return held.Task; });
        await running.Task.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.False(chain.TooManyWaiting("s1", out _));          // one running, nothing waiting

        for (var i = 0; i < OrderedBySession.MostThatMayWait; i++)
            _ = chain.Run("s1", "terminal-input", $"key-{i}", () => Task.FromResult("typed"));   // left queued

        clock.Advance(TimeSpan.FromSeconds(90));

        Assert.True(chain.TooManyWaiting("s1", out var why));
        Assert.Contains($"{OrderedBySession.MostThatMayWait} command(s) waiting", why);
        Assert.Contains("terminal-paste", why);
        Assert.Contains("cmdId=paste-1", why);
        Assert.Contains("90.0s", why);

        // The cap is this session's, not the Director's.
        Assert.False(chain.TooManyWaiting("another-session", out _));

        held.SetResult("let go");
        Assert.False(stuck.IsFaulted);
    }

    [Fact]
    public void AnEmptyQueue_IsNeverTooFull()
    {
        var chain = new OrderedBySession();

        Assert.False(chain.TooManyWaiting("never-seen", out var why));
        Assert.Equal("", why);
    }

}
