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
    [InlineData("prompt")]
    [InlineData("interrupt")]
    [InlineData("escape")]
    [InlineData("terminal-input")]
    [InlineData("terminal-keys")]
    [InlineData("terminal-paste")]
    [InlineData("terminal-mouse")]
    [InlineData("clear-context")]
    [InlineData("compact-context")]
    [InlineData("history-picker")]
    public void AKeystrokeVerb_StaysOnTheLoop_BecauseItsORDERIsThePersonsIntent(string verb)
    {
        // Typing a line and then pressing Escape must reach the terminal in that order. On the thread pool
        // two of them could overtake each other, which is why "everything off the loop" is not the fix.
        Assert.True(DeliveryLoopOrder.MustStayOnTheLoop(verb));
    }

    [Theory]
    [InlineData("create")]            // the verb that held the loop for 430 seconds on 6 November 2026
    [InlineData("kill")]
    [InlineData("patch")]
    [InlineData("license-switch")]
    [InlineData("upload-image-chunk")]
    [InlineData("browser-list")]
    public void EveryOtherVerb_RunsBesideTheLoop(string verb)
    {
        // upload-image-chunk is in this list on purpose: its chunks carry a zero-based Seq, the Director
        // refuses one out of order, and the Gateway awaits each chunk's reply before sending the next, so
        // the protocol keeps their order and the loop does not have to.
        Assert.False(DeliveryLoopOrder.MustStayOnTheLoop(verb));
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

    [Fact]
    public void TheOrderedVerbs_AreExactlyWhatTheAREASDeclare_AndNothingTheConnectionLayerRemembers()
    {
        // THE DIRECTION THAT WAS MISSING, and it is the one that rots (the reviewer of pull request 4813).
        // The old test only checked that every listed verb exists; it could not notice a verb that TYPES and
        // is not listed - which happened twice in one day, with clear-context, compact-context and
        // history-picker, then "enter" and execute-action. The list now has one source, the areas
        // themselves, and this holds the connection layer to it.
        // NOT EMPTY FIRST, because two empty sets are equal and that is the failure this join can have: the
        // union is a static field initialised from Areas, and if it were ever declared ABOVE Areas in that
        // file it would read null-then-empty and every verb would silently run on the loop again. The
        // theories above would catch it too; this says so where the join is asserted.
        Assert.NotEmpty(DeliveryLoopOrder.OrderedVerbs);
        Assert.Contains("prompt", DeliveryLoopOrder.OrderedVerbs);

        Assert.Equal(
            SessionCommandExecutor.VerbsThatMustArriveInOrder.OrderBy(v => v, StringComparer.Ordinal),
            DeliveryLoopOrder.OrderedVerbs.OrderBy(v => v, StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("enter")]            // the phone's Enter key: a raw carriage return, written synchronously
    [InlineData("execute-action")]   // the Wingman's type / send_keys / submit
    [InlineData("queue-add")]        // not typing: the queue behind it is a List no lock protects (#4815)
    [InlineData("queue-send")]
    public void TheVerbsTheREVIEWFound_AreOnTheLoopNow(string verb)
    {
        Assert.True(DeliveryLoopOrder.MustStayOnTheLoop(verb));
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
        new(new GatewayConfig { Url = "http://127.0.0.1:1", Token = "t", StreamMode = true },   // never dialled
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
        Assert.True(await Finished(handedItBack, TimeSpan.FromSeconds(5)),
            "the handler never handed its task back, so the delivery loop is still held and no further command can arrive");

        var next = await client.DeliverAsync(new DirectorCommand { Verb = "hold", CommandId = "c2" }).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(DirectorCommandStatus.Ok, next.Status);
        Assert.NotNull(handedBack);
        Assert.False(handedBack!.IsCompleted, "the wedged verb is meant to still be running");

        // Let the wedged verb go, so this test leaves nothing waiting for ever.
        wedged.Set();
        await handedBack.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>True when <paramref name="task"/> finished inside <paramref name="budget"/>, with no blocking wait.</summary>
    private static async Task<bool> Finished(Task task, TimeSpan budget) =>
        await Task.WhenAny(task, Task.Delay(budget)) == task;

    [Fact]
    public async Task AKeystrokeVerb_IsStillAnsweredOnTheLoopItself_InArrivalOrder()
    {
        // The other half of the contract: the verbs that stay on the loop still run there, one after
        // another, in the order they arrived.
        var order = new List<string>();
        var client = Client(cmd =>
        {
            lock (order) order.Add(cmd.CommandId);
            return Task.FromResult(DirectorCommandResult.Success());
        });

        await client.DeliverAsync(new DirectorCommand { Verb = "prompt", CommandId = "first" });
        await client.DeliverAsync(new DirectorCommand { Verb = "escape", CommandId = "second" });
        await client.DeliverAsync(new DirectorCommand { Verb = "terminal-keys", CommandId = "third" });

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
        var jobs = new CcDirector.Core.Background.BackgroundJobs();
        using var wedged = new ManualResetEventSlim(false);
        var client = new GatewayStreamClient(
            // Start() really dials this address and is refused, then waits in the redial delay - which is
            // why the delay is replaced here. The dial decides nothing about the assertions below; what is
            // needed from Start() is the registered tick.
            new GatewayConfig { Url = "http://127.0.0.1:1", Token = "t", StreamMode = true },
            "dir-test", "0.0.0", () => new List<SessionDto>(),
            commandDispatcher: cmd => { if (cmd.Verb == "prompt") wedged.Wait(); return Task.FromResult(DirectorCommandResult.Success()); },
            rePushInterval: TimeSpan.FromSeconds(30), jobs: jobs, redialDelay: () => Task.CompletedTask);
        client.Start();

        // A KEYSTROKE verb, because those keep the loop on purpose - so one that wedges is the case the
        // watchdog has to catch. It is held from another thread; this one drives the tick.
        _ = Task.Run(() => client.DeliverAsync(new DirectorCommand { Verb = "prompt", CommandId = "wedged" }));
        var held = await Waited(() => client.DeliveryLoop.Inside() is not null, TimeSpan.FromSeconds(5));
        Assert.True(held, "the keystroke verb never reached the delivery loop");

        // THE JOIN, AND NOTHING THE CLOCK DECIDES. (The Waited helper polls the real clock as a FAILURE
        // bound; what the pass rests on is the counter, not a duration.) What must be true is that the tick ASKS - whether the answer is yes
        // depends on the budget, and that half is proved against a fake clock in DeliveryLoopWatchTests. A
        // test that slept past the budget to see the line would be a test the clock decides (the 11 October
        // ruling), and it would prove nothing this does not.
        Assert.Equal(0, client.DeliveryLoop.TimesItWasAsked);

        client.RePushTick();

        Assert.Equal(1, client.DeliveryLoop.TimesItWasAsked);

        wedged.Set();
        await client.StopAsync();
    }

    private static async Task<bool> Waited(Func<bool> until, TimeSpan budget)
    {
        var deadline = DateTime.UtcNow + budget;
        while (DateTime.UtcNow < deadline)
        {
            if (until()) return true;
            await Task.Delay(25);
        }
        return until();
    }
}
