using System.Runtime.InteropServices;
using CcDirector.Core.Backends;
using Xunit;

namespace CcDirector.Core.UnitTests.Backends;

/// <summary>
/// A WRITE OR RESIZE THAT DID NOT HAPPEN REACHES THE CALLER (issue #4380). The terminal backend used to return quietly
/// once it was disposed, and to swallow a resize the console refused, so a sender was told its input went when it had
/// not. Driven on a backend whose process was never started, which needs no real terminal.
/// Each test runs on every backend this platform can build: the Windows terminal backend on every platform (it touches
/// no Windows interface until it is started), and the Unix one everywhere but Windows, whose constructor refuses there.
/// </summary>
public sealed class TerminalWriteFailureTests
{
    public static TheoryData<string> Backends()
    {
        var names = new TheoryData<string> { nameof(ConPtyBackend) };
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            names.Add(nameof(UnixPtyBackend));
        return names;
    }

    private static ISessionBackend Create(string name) => name switch
    {
        nameof(ConPtyBackend) => new ConPtyBackend(),
        nameof(UnixPtyBackend) => new UnixPtyBackend(),
        _ => throw new ArgumentException($"Unknown backend '{name}'", nameof(name)),
    };

    [Theory]
    [MemberData(nameof(Backends))]
    public void Write_ProcessNeverStarted_Throws(string backendName)
    {
        using var backend = Create(backendName);

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Write([0x0D]));

        Assert.Contains("write 1 byte(s)", ex.Message);
        Assert.Contains("never started", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Write_AfterDispose_Throws(string backendName)
    {
        var backend = Create(backendName);
        backend.Dispose();

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Write([0x41, 0x42]));

        Assert.Contains("disposed", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SendTextAsync_AfterDispose_Throws(string backendName)
    {
        var backend = Create(backendName);
        backend.Dispose();

        await Assert.ThrowsAsync<TerminalWriteFailedException>(() => backend.SendTextAsync("hello"));
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public async Task SendEnterAsync_ProcessNeverStarted_Throws(string backendName)
    {
        using var backend = Create(backendName);

        var ex = await Assert.ThrowsAsync<TerminalWriteFailedException>(() => backend.SendEnterAsync());

        Assert.Contains("press Enter", ex.Message);
    }

    [Theory]
    [MemberData(nameof(Backends))]
    public void Resize_ProcessNeverStarted_Throws(string backendName)
    {
        using var backend = Create(backendName);

        var ex = Assert.Throws<TerminalWriteFailedException>(() => backend.Resize(120, 40));

        Assert.Contains("resize to 120x40", ex.Message);
    }
}
