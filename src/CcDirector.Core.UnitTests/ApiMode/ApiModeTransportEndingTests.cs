using System.Text.Json.Nodes;
using CcDirector.Core.ApiMode;
using CcDirector.TestInfrastructure;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

/// <summary>
/// How the transport behaves at the edges of a process's life: a start that fails, the window between the exit and
/// the end being reported, a shutdown against an agent that stopped reading, and failures of the read loops. Each
/// case comes from the phase 1 review (docs/missions/api-mode-pi-2026-10-16/reviews/phase-1-transport-review.md).
/// </summary>
public sealed class ApiModeTransportEndingTests
{
    private static ApiModeProcessStartInfo PiStart() => new()
    {
        Executable = "pi",
        Arguments = ["--mode", "rpc"],
        WorkingDirectory = "/work",
    };

    private static JsonObject Prompt() => new() { ["type"] = "prompt", ["message"] = "Reply with exactly the word PONG" };

    /// <summary>
    /// Wait until the transport has armed its drain-grace timer, then move the clock past it in one step. The timer is
    /// armed on a pool thread once the transport sees the exit, so the test waits for the clock's own signal that it
    /// was armed (<paramref name="graceArmed"/>, taken before the exit) rather than counting turns: a counted loop runs
    /// out first when the pool thread is late, and the test then waits for ever on a timer nobody moves.
    /// </summary>
    internal static async Task RunPastTheDrainGraceAsync(VirtualClock clock, Task graceArmed)
    {
        await graceArmed;
        clock.Advance(new ApiModeTransportOptions().DrainGrace);
    }

    [Fact]
    public async Task DisposeAsync_AfterAFailedStart_CompletesAndTheCallerSeesTheStartFailure()
    {
        // Arrange: the ordinary caller shape, await using, around a start that fails.
        var process = new FakeApiModeProcess { StartFailure = new ApiModeStartException("pi", "Cannot start 'pi': it was not found on the search path (PATH).") };

        async Task StartAndDispose()
        {
            await using var transport = new ApiModeTransport(process);
            transport.Start(PiStart());
        }

        // Act
        var ex = await Assert.ThrowsAsync<ApiModeStartException>(StartAndDispose);

        // Assert
        Assert.Equal("pi", ex.Executable);
        Assert.Equal(0, process.KillCount);
    }

    [Fact]
    public async Task DisposeAsync_AfterTheRealProcessFailedToStart_TheCallerSeesTheStartFailure()
    {
        // Arrange: a working folder that cannot exist, so nothing is spawned.
        var folder = Path.Combine(Path.GetTempPath(), $"no-such-folder-{Guid.NewGuid():N}");

        async Task StartAndDispose()
        {
            await using var transport = new ApiModeTransport(new SystemApiModeProcess());
            transport.Start(new ApiModeProcessStartInfo { Executable = "pi", WorkingDirectory = folder });
        }

        // Act
        var ex = await Assert.ThrowsAsync<ApiModeStartException>(StartAndDispose);

        // Assert
        Assert.Contains(folder, ex.Message);
    }

    [Fact]
    public async Task SendRequestAsync_AfterAFailedStart_ThrowsSayingTheStartFailed()
    {
        // Arrange
        var transport = new ApiModeTransport(new FakeApiModeProcess { StartFailure = new ApiModeStartException("pi", "no pi") });
        Assert.Throws<ApiModeStartException>(() => transport.Start(PiStart()));

        // Act
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => transport.SendRequestAsync(Prompt(), CancellationToken.None));

        // Assert
        Assert.Contains("start failed", ex.Message);
        await transport.DisposeAsync();
    }

    [Fact]
    public async Task SendRequestAsync_ProcessExitedButTheEndIsNotReportedYet_FailsWithConnectionEndedAndTheExitCode()
    {
        // Arrange: a grandchild holds the pipes, so the end waits for the drain grace after the exit.
        var clock = new VirtualClock(DateTimeOffset.UnixEpoch);
        var process = new FakeApiModeProcess { ExitEndsStreams = false };
        await using var transport = new ApiModeTransport(process, clock);
        transport.Start(PiStart());
        var graceArmed = clock.NextWaitStarted;
        process.Exit(1);

        // Act
        var send = transport.SendRequestAsync(Prompt(), CancellationToken.None);
        await RunPastTheDrainGraceAsync(clock, graceArmed);
        var ex = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(() => send);

        // Assert
        Assert.Contains("exit code 1", ex.Message);
        Assert.Contains("prompt", ex.Message);
        Assert.Equal(1, ex.Ended.ExitCode);
        Assert.Empty(process.WrittenLines);
    }

    [Fact]
    public async Task SendRequestAsync_TheWriteRacesTheExit_FailsWithConnectionEndedCarryingThePipeError()
    {
        // Arrange
        var process = new FakeApiModeProcess { ExitDuringWrite = true };
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());

        // Act
        var ex = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(() => transport.SendRequestAsync(Prompt(), CancellationToken.None));

        // Assert
        Assert.Contains("exit code 0", ex.Message);
        Assert.IsType<IOException>(ex.InnerException);
    }

    [Fact]
    public async Task WriteRecordAsync_TheWriteRacesTheExit_FailsWithConnectionEnded()
    {
        // Arrange
        var process = new FakeApiModeProcess { ExitDuringWrite = true };
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());

        // Act
        var ex = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(() =>
            transport.WriteRecordAsync(new JsonObject { ["type"] = "extension_ui_response", ["id"] = "ui-1", ["cancelled"] = true }, CancellationToken.None));

        // Assert
        Assert.Contains("extension_ui_response", ex.Message);
        Assert.Contains("exit code 0", ex.Message);
    }

    [Fact]
    public async Task ShutdownAsync_AWriteIsStuckOnAFullPipe_KillsWhenTheGraceRunsOut()
    {
        // Arrange: the agent stopped reading, and a write holds the write lock forever.
        var time = new FakeTimeProvider();
        var process = new FakeApiModeProcess { BlockWrites = true };
        await using var transport = new ApiModeTransport(process, time);
        transport.Start(PiStart());
        var stuck = transport.WriteRecordAsync(Prompt(), CancellationToken.None);
        Assert.False(stuck.IsCompleted);

        // Act: the grace timer is armed before ShutdownAsync first yields, even though the lock is held.
        var shutdown = transport.ShutdownAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        var ended = await shutdown;

        // Assert
        Assert.False(process.InputClosed);
        Assert.Equal(1, process.KillCount);
        Assert.True(ended.KilledByTransport);
        Assert.Equal(137, ended.ExitCode);
        var writeFailure = await Assert.ThrowsAsync<ApiModeConnectionEndedException>(() => stuck);
        Assert.Contains("killed by the transport", writeFailure.Message);
    }

    [Fact]
    public async Task Ended_KilledOnWindows_MinusOneIsAnOrdinaryExitCodeWithNoFault()
    {
        // Arrange: on Windows .NET terminates a killed process with exit code -1.
        var time = new FakeTimeProvider();
        var process = new FakeApiModeProcess { ExitsWhenInputCloses = false, KillExitCode = -1 };
        await using var transport = new ApiModeTransport(process, time);
        transport.Start(PiStart());

        // Act
        var shutdown = transport.ShutdownAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(10));
        var ended = await shutdown;

        // Assert
        Assert.Equal(-1, ended.ExitCode);
        Assert.True(ended.KilledByTransport);
        Assert.Null(ended.TransportFault);
        Assert.Equal("exit code -1, killed by the transport", ended.Describe());
    }

    [Fact]
    public async Task Ended_ProcessExitsOnItsOwn_IsNotKilledByTheTransport()
    {
        // Arrange
        var process = new FakeApiModeProcess();
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());

        // Act
        process.Exit(0);
        var ended = await transport.Ended;

        // Assert
        Assert.False(ended.KilledByTransport);
        Assert.Equal("exit code 0", ended.Describe());
    }

    [Fact]
    public async Task DisposeAsync_AfterTheDrainGraceReadsFailOnTheirClosedPipe_NoFaultAndNoKill()
    {
        // Arrange: after the drain grace the reads are stopped; a stopped read on a closed Windows handle fails with an
        // error rather than a cancellation, and that must not be taken for a transport failure.
        var clock = new VirtualClock(DateTimeOffset.UnixEpoch);
        var process = new FakeApiModeProcess { ExitEndsStreams = false, CancelledReadFailure = new ObjectDisposedException("pipe") };
        var transport = new ApiModeTransport(process, clock);
        transport.Start(PiStart());
        var graceArmed = clock.NextWaitStarted;
        process.Exit(0);
        await RunPastTheDrainGraceAsync(clock, graceArmed);
        var ended = await transport.Ended;

        // Act
        await transport.DisposeAsync();

        // Assert
        Assert.Null(ended.TransportFault);
        Assert.Equal(0, process.KillCount);
    }

    [Fact]
    public async Task Ended_ReadingStandardErrorFails_TheFaultSaysTheLinesMayBeIncomplete()
    {
        // Arrange
        var process = new FakeApiModeProcess { ErrorReadFailure = new IOException("The handle is invalid.") };
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());

        // Act
        process.Exit(1);
        var ended = await transport.Ended;

        // Assert
        Assert.NotNull(ended.TransportFault);
        Assert.Contains("standard error", ended.TransportFault);
        Assert.Contains("The handle is invalid.", ended.TransportFault);
    }

    [Fact]
    public async Task SendRequestAsync_BooleanId_ThrowsBecauseNoResponseCouldEverMatchIt()
    {
        // Arrange
        var process = new FakeApiModeProcess();
        await using var transport = new ApiModeTransport(process);
        transport.Start(PiStart());
        var command = new JsonObject { ["type"] = "get_state", ["id"] = true };

        // Act and assert
        await Assert.ThrowsAsync<ArgumentException>(() => transport.SendRequestAsync(command, CancellationToken.None));
        Assert.Empty(process.WrittenLines);
    }
}
