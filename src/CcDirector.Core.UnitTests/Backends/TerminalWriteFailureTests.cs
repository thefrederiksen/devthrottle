using CcDirector.Core.Backends;
using Xunit;

namespace CcDirector.Core.UnitTests.Backends;

/// <summary>
/// A WRITE OR RESIZE THAT DID NOT HAPPEN REACHES THE CALLER (issue #4380). The terminal backend used to return quietly
/// once it was disposed, and to swallow a resize the console refused, so a sender was told its input went when it had
/// not. Driven on a backend whose process was never started, which needs no real terminal.
/// </summary>
public sealed class TerminalWriteFailureTests
{
    [Fact]
    public void Write_ProcessNeverStarted_Throws()
    {
        using var backend = new UnixPtyBackend();

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Write([0x0D]));

        Assert.Contains("write 1 byte(s)", ex.Message);
        Assert.Contains("never started", ex.Message);
    }

    [Fact]
    public void Write_AfterDispose_Throws()
    {
        var backend = new UnixPtyBackend();
        backend.Dispose();

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Write([0x41, 0x42]));

        Assert.Contains("disposed", ex.Message);
    }

    [Fact]
    public async Task SendTextAsync_AfterDispose_Throws()
    {
        var backend = new UnixPtyBackend();
        backend.Dispose();

        await Assert.ThrowsAsync<TerminalWriteFailedException>(() => backend.SendTextAsync("hello"));
    }

    [Fact]
    public void Resize_ProcessNeverStarted_Throws()
    {
        using var backend = new UnixPtyBackend();

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Resize(120, 40));

        Assert.Contains("resize to 120x40", ex.Message);
    }
}
