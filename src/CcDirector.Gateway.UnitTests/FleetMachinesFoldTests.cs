using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The Machines view fold (fleet maintenance, devthrottle_internal#2026). The point of folding on the Gateway is
/// that the Cockpit never offers a button the launcher would refuse, so every "not offered" case here asserts the
/// REASON a person reads, not only the boolean.
/// </summary>
public sealed class FleetMachinesFoldTests
{
    private static readonly DateTime Now = new(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc);
    private static readonly NewestReleaseSnapshot Newest = new("2.1.4", Now, null);

    private static readonly string[] ModernCommands =
    {
        LauncherCapabilities.DirectorStart, LauncherCapabilities.DirectorStop, LauncherCapabilities.DirectorRestart,
        LauncherCapabilities.DirectorRestartOnlyIfEmpty, LauncherCapabilities.DirectorUpdate,
        LauncherCapabilities.DirectorUpdateStatus,
    };

    private static LauncherDto Launcher(string machine, string version = "2.1.4", TimeSpan? quiet = null) => new()
    {
        MachineName = machine,
        Pid = 10,
        Version = version,
        StartedAt = Now.AddHours(-1),
        RegisteredAt = Now.AddHours(-1),
        LastSeenAt = Now - (quiet ?? TimeSpan.FromSeconds(5)),
    };

    private static LauncherStreamConnection Stream(params string[] commands) => new("conn", new LauncherCapabilityDeclaration
    {
        Commands = commands.ToList(),
        RestartSignalArmed = true,
        ServingRootIsInstanceHome = false,
    });

    private static DirectorDto Director(string id, string machine, string version, bool stopped = false) => new()
    {
        DirectorId = id,
        MachineName = machine,
        Version = version,
        LastSeen = Now.AddSeconds(-3),
        StoppedAtUtc = stopped ? Now.AddMinutes(-10) : null,
    };

    private static FleetMachinesDto Fold(
        LauncherDto[] launchers,
        Dictionary<string, LauncherStreamConnection> streams,
        DirectorDto[] directors,
        Dictionary<string, int>? sessions = null,
        NewestReleaseSnapshot? newest = null)
        => FleetMachinesFold.Fold(launchers, m => streams.TryGetValue(m, out var s) ? s : null, directors,
            sessions ?? new Dictionary<string, int>(), newest ?? Newest, Now);

    [Fact]
    public void Fold_IdleMachineBehindNewest_OffersUpdateNow()
    {
        var view = Fold(new[] { Launcher("LAPTOP") },
            new() { ["LAPTOP"] = Stream(ModernCommands) },
            new[] { Director("d1", "LAPTOP", "2.1.3") });

        var machine = Assert.Single(view.Machines);
        Assert.True(machine.Update.Offered);
        Assert.Equal("Update now", machine.Update.Label);
        Assert.True(machine.CanReportUpdateStatus);
        Assert.True(machine.Restart.Offered);
        Assert.False(machine.Start.Offered);
        var director = Assert.Single(machine.Directors);
        Assert.True(director.VersionState.Behind);
        Assert.Equal("behind", director.VersionState.Label);
        Assert.Contains(view.Highlights, h => h.Text == "1 of 1 Director behind");
    }

    [Fact]
    public void Fold_BusyMachine_DoesNotOfferUpdateAndSaysHowManySessions()
    {
        var view = Fold(new[] { Launcher("NORTH") },
            new() { ["NORTH"] = Stream(ModernCommands) },
            new[] { Director("d1", "NORTH", "2.1.3") },
            new() { ["D1"] = 3 });

        var machine = Assert.Single(view.Machines);
        Assert.False(machine.Update.Offered);
        Assert.Equal("Update when empty", machine.Update.Label);
        Assert.Contains("3 sessions running", machine.Update.Reason);
        Assert.False(machine.Restart.Offered);
        Assert.Equal(3, machine.Directors[0].Sessions);
    }

    [Fact]
    public void Fold_NewestReleaseUnknown_CallsNothingBehindAndSaysWhy()
    {
        var view = Fold(new[] { Launcher("LAPTOP") },
            new() { ["LAPTOP"] = Stream(ModernCommands) },
            new[] { Director("d1", "LAPTOP", "2.1.3") },
            newest: new NewestReleaseSnapshot(null, Now, "GitHub answered 503"));

        var machine = Assert.Single(view.Machines);
        Assert.False(machine.Directors[0].VersionState.Behind);
        Assert.Equal("", machine.Directors[0].VersionState.Label);
        Assert.False(machine.Update.Offered);
        Assert.Contains("not known", machine.Update.Reason);
        Assert.Equal("unknown", view.NewestRelease.Label);
        Assert.Contains("GitHub answered 503", view.NewestRelease.Detail);
    }

    [Fact]
    public void Fold_LauncherWithoutTheUpdateCommand_SaysItIsOlderThanRemoteUpdates()
    {
        var older = ModernCommands.Where(c => c != LauncherCapabilities.DirectorUpdate && c != LauncherCapabilities.DirectorUpdateStatus).ToArray();
        var view = Fold(new[] { Launcher("LAPTOP", "2.1.3") },
            new() { ["LAPTOP"] = Stream(older) },
            new[] { Director("d1", "LAPTOP", "2.1.3") });

        var machine = Assert.Single(view.Machines);
        Assert.False(machine.Update.Offered);
        Assert.Contains("older than remote updates", machine.Update.Reason);
        Assert.False(machine.CanReportUpdateStatus);
        Assert.True(machine.Restart.Offered);
        Assert.True(machine.LauncherVersionState.Behind);
    }

    [Fact]
    public void Fold_LauncherTooOldForCommands_IsNamedAndOffersNothing()
    {
        var view = Fold(new[] { Launcher("DEMO", "1.9.8") }, new(), new[] { Director("d1", "DEMO", "2.0.9", stopped: true) });

        var machine = Assert.Single(view.Machines);
        Assert.Equal(LauncherReach.NotStreamCapable, machine.Reach);
        Assert.Equal("Too old for commands", machine.ReachLabel);
        Assert.Equal(FleetTone.Bad, machine.ReachTone);
        Assert.False(machine.Update.Offered);
        Assert.False(machine.Start.Offered);
        Assert.Equal(machine.ReachDetail, machine.Update.Reason);
        Assert.Contains(view.Highlights, h => h.Text == "1 launcher too old for commands");
    }

    [Fact]
    public void Fold_StoppedDirectorBehindAConnectedLauncher_OffersStartNotUpdate()
    {
        var view = Fold(new[] { Launcher("LAPTOP") },
            new() { ["LAPTOP"] = Stream(ModernCommands) },
            new[] { Director("d1", "LAPTOP", "2.1.3", stopped: true) });

        var machine = Assert.Single(view.Machines);
        Assert.True(machine.Start.Offered);
        Assert.False(machine.Update.Offered);
        Assert.Contains("next time it starts", machine.Update.Reason);
        Assert.False(machine.Restart.Offered);
        Assert.Equal("Stopped", machine.Directors[0].StateLabel);
    }

    [Fact]
    public void Fold_LauncherWithNoDirector_StillAppearsAsAMachine()
    {
        var view = Fold(new[] { Launcher("SPARE") }, new() { ["SPARE"] = Stream(ModernCommands) }, Array.Empty<DirectorDto>());

        var machine = Assert.Single(view.Machines);
        Assert.Equal("SPARE", machine.Machine);
        Assert.Empty(machine.Directors);
        Assert.True(machine.Start.Offered);
    }

    [Fact]
    public void Fold_DirectorWithNoLauncher_AppearsUnderItsMachineWithNoLauncher()
    {
        var view = Fold(Array.Empty<LauncherDto>(), new(), new[] { Director("d1", "BUILD-BOX", "2.1.4") });

        var machine = Assert.Single(view.Machines);
        Assert.Equal("BUILD-BOX", machine.Machine);
        Assert.Equal(LauncherReach.NoLauncher, machine.Reach);
        Assert.Equal("No launcher", machine.ReachLabel);
        Assert.Null(machine.LauncherVersion);
        Assert.False(machine.Update.Offered);
    }

    [Fact]
    public void Fold_EveryDirectorCurrent_UpdateSaysUpToDateWithNoReason()
    {
        var view = Fold(new[] { Launcher("LAPTOP") },
            new() { ["LAPTOP"] = Stream(ModernCommands) },
            new[] { Director("d1", "laptop", "2.1.4") });

        var machine = Assert.Single(view.Machines);
        Assert.Single(machine.Directors);
        Assert.False(machine.Update.Offered);
        Assert.Equal("Up to date", machine.Update.Label);
        Assert.Null(machine.Update.Reason);
        Assert.Contains(view.Highlights, h => h.Text == "The Director is current");
    }

    [Theory]
    [InlineData("2.1.3", "2.1.4", "behind", true)]
    [InlineData("2.1.4", "2.1.4", "current", false)]
    [InlineData("2.1.4+8e80e9c64", "2.1.4", "current", false)]
    [InlineData("v2.1.4", "2.1.4", "current", false)]
    [InlineData("2.2.0", "2.1.4", "newer than release", false)]
    [InlineData("not-a-version", "2.1.4", "unreadable version", false)]
    [InlineData("", "2.1.4", "unreadable version", false)]
    public void CompareToNewest_ReadsTheVersionTheWayTheUpdaterDoes(string running, string newest, string label, bool behind)
    {
        var state = FleetMachinesFold.CompareToNewest(running, newest);

        Assert.Equal(label, state.Label);
        Assert.Equal(behind, state.Behind);
    }

    // Issue #4423: a launcher restarting into a new version heartbeats with no stream for a moment. It used to be
    // labelled "Too old for commands" in that moment; this Gateway saw its stream close, so it is reconnecting.
    [Fact]
    public void Fold_HeartbeatingWithNoStreamAfterAStreamClosed_IsReconnectingNotTooOld()
    {
        var launcher = Launcher("STUDIO_MAC");
        launcher.StreamClosedAt = Now.AddSeconds(-20);

        var view = Fold(new[] { launcher }, new(), new[] { Director("d1", "STUDIO_MAC", "2.1.4") });

        var machine = Assert.Single(view.Machines);
        Assert.Equal(LauncherReach.Reconnecting, machine.Reach);
        Assert.Equal("Launcher reconnecting", machine.ReachLabel);
        Assert.DoesNotContain(view.Highlights, h => h.Text.Contains("too old"));
    }

    // Review finding 2: "reconnecting" has a limit. Past it the launcher is not coming back by itself.
    [Fact]
    public void Fold_StreamDownLongerThanARestartTakes_IsNotTakingCommandsNotReconnecting()
    {
        var launcher = Launcher("STUDIO_MAC");
        launcher.StreamClosedAt = Now - LauncherReachability.ReconnectWindow - TimeSpan.FromSeconds(1);

        var view = Fold(new[] { launcher }, new(), new[] { Director("d1", "STUDIO_MAC", "2.1.4") });

        var machine = Assert.Single(view.Machines);
        Assert.Equal(LauncherReach.StreamDown, machine.Reach);
        Assert.Equal("Not taking commands", machine.ReachLabel);
        Assert.Contains("Restart the launcher", machine.ReachDetail);
    }

    [Fact]
    public void Fold_StreamClosedJustInsideTheWindow_IsReconnecting()
    {
        var launcher = Launcher("STUDIO_MAC");
        launcher.StreamClosedAt = Now - LauncherReachability.ReconnectWindow;

        var view = Fold(new[] { launcher }, new(), new[] { Director("d1", "STUDIO_MAC", "2.1.4") });

        Assert.Equal(LauncherReach.Reconnecting, Assert.Single(view.Machines).Reach);
    }

    [Fact]
    public void Fold_HeartbeatingWithNoStreamEverSeen_IsStillTooOld()
    {
        var view = Fold(new[] { Launcher("OLD_BOX", "1.9.8") }, new(), new[] { Director("d1", "OLD_BOX", "2.1.4") });

        Assert.Equal(LauncherReach.NotStreamCapable, Assert.Single(view.Machines).Reach);
    }

    // Issue #4423: the Cockpit drops an update's stale answer once the machine is up to date - that verdict is folded here.
    [Fact]
    public void Fold_LauncherAndRunningDirectorCurrent_IsUpToDate()
    {
        var view = Fold(new[] { Launcher("STUDIO_MAC", "2.1.4") }, new() { ["STUDIO_MAC"] = Stream(ModernCommands) },
            new[] { Director("d1", "STUDIO_MAC", "2.1.4") });

        Assert.True(Assert.Single(view.Machines).UpToDate);
    }

    [Theory]
    [InlineData("2.1.3", "2.1.4")] // launcher behind
    [InlineData("2.1.4", "2.1.3")] // Director behind
    public void Fold_LauncherOrDirectorBehind_IsNotUpToDate(string launcherVersion, string directorVersion)
    {
        var view = Fold(new[] { Launcher("STUDIO_MAC", launcherVersion) }, new() { ["STUDIO_MAC"] = Stream(ModernCommands) },
            new[] { Director("d1", "STUDIO_MAC", directorVersion) });

        Assert.False(Assert.Single(view.Machines).UpToDate);
    }

    [Fact]
    public void Fold_NewestReleaseNotKnown_IsNotUpToDate()
    {
        var view = Fold(new[] { Launcher("STUDIO_MAC", "2.1.4") }, new() { ["STUDIO_MAC"] = Stream(ModernCommands) },
            new[] { Director("d1", "STUDIO_MAC", "2.1.4") }, newest: new NewestReleaseSnapshot(null, null, null));

        Assert.False(Assert.Single(view.Machines).UpToDate);
    }
}
