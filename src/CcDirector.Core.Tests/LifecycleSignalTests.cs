using CcDirector.Core.Lifecycle;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The cross-process request channel that lifecycle runs on now that it is off the network.
///
/// WHAT THESE HAVE TO PROVE, and what they deliberately do NOT. They prove the mechanism: a raise
/// reaches a listener, a raise with nobody listening is REPORTED as undelivered rather than silently
/// swallowed, a disposed listener stops being reachable, and two names never cross. They do NOT prove
/// that a Director shuts down or that a launcher restarts one - that is two real processes and it is
/// proved in the phase's end-to-end rig, because a test that signals itself cannot tell you the
/// operating system delivered anything between two processes.
///
/// The "nobody is listening" case is the one worth having. It is what the launcher uses to tell a
/// Director that CAN be asked to stop from one that has to be killed, and if it answered true when
/// nothing was there, every stop would look graceful and every Director would be force-killed after a
/// timeout - which is precisely the silent degradation this mission keeps finding.
/// </summary>
public sealed class LifecycleSignalTests
{
    /// <summary>A name no other test or process could be using.</summary>
    private static string UniqueName() => "cc-director-test-" + Guid.NewGuid().ToString("N");

    [Fact]
    public void ARaisedSignal_ReachesItsListener()
    {
        var name = UniqueName();
        using var arrived = new ManualResetEventSlim(false);
        using var listener = LifecycleSignal.Listen(name, () => arrived.Set());

        Assert.True(LifecycleSignal.Raise(name));

        Assert.True(arrived.Wait(TimeSpan.FromSeconds(10)),
            "the signal was raised but the listener never ran");
    }

    [Fact]
    public void HasListener_AnswersWithoutRaisingAnything()
    {
        // The question a capability check has to be able to ask. Raising the signal to find out whether
        // anybody is there would restart a Director or quit a launcher as a side effect of asking, so
        // this must answer AND leave the listener unfired.
        if (!OperatingSystem.IsWindows()) return;

        var name = UniqueName();
        Assert.False(LifecycleSignal.HasListener(name));

        var fired = 0;
        using (var listener = LifecycleSignal.Listen(name, () => Interlocked.Increment(ref fired)))
        {
            Assert.True(LifecycleSignal.HasListener(name));
            Thread.Sleep(200);
            Assert.Equal(0, Volatile.Read(ref fired));
        }

        Assert.False(LifecycleSignal.HasListener(name));
    }

    [Fact]
    public void HasListener_WhereItCannotBeObserved_SaysSoRatherThanInventingANo()
    {
        // Unix has no named event: the mechanism is a request file a listener polls, and nothing on disk
        // says whether anybody is polling. A false there would be an invented answer, so it is null.
        if (OperatingSystem.IsWindows()) return;

        var name = UniqueName();
        using var listener = LifecycleSignal.Listen(name, () => { });

        Assert.Null(LifecycleSignal.HasListener(name));
    }

    [Fact]
    public void RaisingASignalNobodyListensFor_ReportsItWasNotDelivered()
    {
        // Windows can say this outright - no kernel object of that name exists. The Unix arm writes a
        // request file it cannot know anyone will read, so it reports delivery; the callers verify the
        // EFFECT either way, which is why the contract is "handed over", not "carried out".
        if (!OperatingSystem.IsWindows()) return;

        Assert.False(LifecycleSignal.Raise(UniqueName()));
    }

    [Fact]
    public void ADisposedListener_IsNoLongerReachable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var name = UniqueName();
        var listener = LifecycleSignal.Listen(name, () => { });
        Assert.True(LifecycleSignal.Raise(name));

        listener.Dispose();

        Assert.False(LifecycleSignal.Raise(name),
            "a disposed listener still answered - a stopped process would keep absorbing shutdown requests");
    }

    /// <summary>
    /// The scoping property the whole design rests on: a request aimed at one Director must not reach
    /// another. This machine routinely runs several.
    /// </summary>
    [Fact]
    public void ASignalRaisedForOneName_DoesNotReachAnother()
    {
        var mine = UniqueName();
        var theirs = UniqueName();
        using var wrongOneRan = new ManualResetEventSlim(false);
        using var rightOneRan = new ManualResetEventSlim(false);

        using var a = LifecycleSignal.Listen(mine, () => rightOneRan.Set());
        using var b = LifecycleSignal.Listen(theirs, () => wrongOneRan.Set());

        LifecycleSignal.Raise(mine);

        Assert.True(rightOneRan.Wait(TimeSpan.FromSeconds(10)));
        Assert.False(wrongOneRan.Wait(TimeSpan.FromSeconds(1)),
            "a signal named for one Director reached another");
    }

    [Fact]
    public void EachRaise_RunsTheHandlerExactlyOnce()
    {
        var name = UniqueName();
        var count = 0;
        using var second = new ManualResetEventSlim(false);
        using var listener = LifecycleSignal.Listen(name, () =>
        {
            if (Interlocked.Increment(ref count) == 2) second.Set();
        });

        LifecycleSignal.Raise(name);
        LifecycleSignal.Raise(name);

        Assert.True(second.Wait(TimeSpan.FromSeconds(10)));
        Thread.Sleep(200);
        Assert.Equal(2, Volatile.Read(ref count));
    }

    /// <summary>
    /// A handler that throws must not kill the listener: a Director that stopped answering its shutdown
    /// signal because one request went wrong could never be stopped again.
    /// </summary>
    [Fact]
    public void AHandlerThatThrows_DoesNotStopTheListener()
    {
        var name = UniqueName();
        using var secondArrived = new ManualResetEventSlim(false);
        var calls = 0;
        using var listener = LifecycleSignal.Listen(name, () =>
        {
            if (Interlocked.Increment(ref calls) == 1) throw new InvalidOperationException("on purpose");
            secondArrived.Set();
        });

        LifecycleSignal.Raise(name);
        Thread.Sleep(200);
        LifecycleSignal.Raise(name);

        Assert.True(secondArrived.Wait(TimeSpan.FromSeconds(10)),
            "the listener stopped answering after its handler threw");
    }

    [Fact]
    public void ASignalName_IsRequired()
    {
        Assert.Throws<ArgumentException>(() => LifecycleSignal.Raise(""));
        Assert.Throws<ArgumentException>(() => LifecycleSignal.Listen("  ", () => { }));
    }
}

/// <summary>
/// The names themselves. Every one is scoped, and these pin the scoping rather than the spelling:
/// a name that stopped varying by Director would make one shutdown request reach every Director on the
/// machine, and nothing else in the design would notice.
/// </summary>
public sealed class LifecycleSignalNamesTests
{
    [Fact]
    public void TwoDirectors_GetDifferentShutdownNames()
    {
        Assert.NotEqual(
            LifecycleSignalNames.DirectorShutdown("11111111-0000-0000-0000-000000000001"),
            LifecycleSignalNames.DirectorShutdown("22222222-0000-0000-0000-000000000002"));
    }

    [Fact]
    public void ShutdownAndUpdateCheck_AreDifferentSignalsForTheSameDirector()
    {
        const string id = "11111111-0000-0000-0000-000000000001";
        Assert.NotEqual(LifecycleSignalNames.DirectorShutdown(id), LifecycleSignalNames.DirectorUpdateCheck(id));
    }

    [Fact]
    public void ADirectorIdentifier_IsRequired()
    {
        Assert.Throws<ArgumentException>(() => LifecycleSignalNames.DirectorShutdown(""));
        Assert.Throws<ArgumentException>(() => LifecycleSignalNames.DirectorUpdateCheck("   "));
    }

    /// <summary>
    /// A test rig with its own storage root and the installed launcher must not hear each other. This
    /// is what makes it safe to run a launcher under test on the same machine as the real one.
    /// </summary>
    [Fact]
    public void TwoStorageRoots_GetDifferentLauncherNames()
    {
        Assert.NotEqual(
            LifecycleSignalNames.LauncherShutdown(@"C:\Users\someone\AppData\Local\cc-director"),
            LifecycleSignalNames.LauncherShutdown(@"D:\rig\cc-director"));
    }

    [Fact]
    public void TheSameRoot_AlwaysGetsTheSameName()
    {
        Assert.Equal(
            LifecycleSignalNames.LauncherShutdown(@"D:\rig\cc-director"),
            LifecycleSignalNames.LauncherShutdown(@"D:\rig\cc-director\"));
    }

    [Fact]
    public void QuittingTheLauncherAndRestartingTheDirector_AreDifferentSignals()
    {
        Assert.NotEqual(
            LifecycleSignalNames.LauncherShutdown(@"D:\rig\cc-director"),
            LifecycleSignalNames.LauncherRestartDirector(@"D:\rig\cc-director"));
    }
}
