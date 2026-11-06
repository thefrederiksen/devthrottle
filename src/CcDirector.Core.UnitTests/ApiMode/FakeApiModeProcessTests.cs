using System.Text;
using CcDirector.Core.UnitTests.ApiMode;
using Xunit;

namespace CcDirector.Core.Tests.ApiMode;

/// <summary>
/// The ordering the scripted Pi relies on: with <see cref="FakeApiModeProcess.KeepsOrderAcrossStreams"/>, output pushed
/// after a standard error line is not handed out until the standard error reader has taken that line and come back for
/// the next one. Without it the two reader loops raced, and under load the recorded Pi's warning reached the dialect after
/// the turn began and landed in a clean end's reason. Every step here is a call the test makes; nothing waits on time.
/// </summary>
public sealed class FakeApiModeProcessTests
{
    [Fact]
    public async Task ReadOutputAsync_OutputPushedAfterAnErrorLine_WaitsUntilTheErrorReaderComesBack()
    {
        // Arrange
        var process = new FakeApiModeProcess { KeepsOrderAcrossStreams = true };
        process.EmitError("Warning: written first");
        process.EmitOutput("{\"type\":\"agent_start\"}\n");
        var buffer = new byte[256];

        // Act and Assert: the output waits while the warning is unread, and while it is taken but not yet stored.
        var output = process.ReadOutputAsync(buffer, CancellationToken.None).AsTask();
        Assert.False(output.IsCompleted);
        Assert.Equal("Warning: written first", await process.ReadErrorLineAsync(CancellationToken.None));
        Assert.False(output.IsCompleted);

        // The reader coming back for the next line releases it.
        var nextError = process.ReadErrorLineAsync(CancellationToken.None).AsTask();
        var count = await ScriptedPi.WithinHangGuard(output, "the output read");
        Assert.Equal("{\"type\":\"agent_start\"}\n", Encoding.UTF8.GetString(buffer, 0, count));
        Assert.False(nextError.IsCompleted);
    }

    [Fact]
    public async Task ReadOutputAsync_OutputPushedBeforeAnErrorLine_IsHandedOutAtOnce()
    {
        // Arrange
        var process = new FakeApiModeProcess { KeepsOrderAcrossStreams = true };
        process.EmitOutput("{\"type\":\"response\"}\n");
        process.EmitError("Warning: written after");
        var buffer = new byte[256];

        // Act
        var output = process.ReadOutputAsync(buffer, CancellationToken.None).AsTask();

        // Assert: nothing on standard error came before it, so it does not wait for the error reader.
        Assert.True(output.IsCompleted);
        Assert.Equal("{\"type\":\"response\"}\n", Encoding.UTF8.GetString(buffer, 0, await output));
    }
}
