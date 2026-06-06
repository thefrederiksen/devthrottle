using CcDirector.Core.Network;
using Xunit;

namespace CcDirector.Core.Tests.Network;

/// <summary>
/// The cross-process serve mutex (issues #179/#197/#200): every serve-MUTATING tailscale
/// invocation must be serialized so the Gateway provisioner and self-provisioning
/// Directors cannot interleave the CLI's whole-table read-modify-write. The CLI itself is
/// not shelled here - <see cref="TailscaleCli.RunSerialized"/> takes an injectable action,
/// so the serialization property is tested directly.
/// </summary>
public sealed class TailscaleCliTests
{
    [Fact]
    public void RunSerialized_ReturnsActionResult()
    {
        var result = TailscaleCli.RunSerialized(() => (true, "out", "msg"));
        Assert.True(result.ok);
        Assert.Equal("out", result.stdout);
        Assert.Equal("msg", result.message);
    }

    [Fact]
    public async Task RunSerialized_ConcurrentCallers_NeverOverlap()
    {
        var inside = 0;
        var maxInside = 0;
        var gate = new object();

        (bool ok, string stdout, string message) Action()
        {
            lock (gate) { inside++; maxInside = Math.Max(maxInside, inside); }
            Thread.Sleep(25); // hold the mutex long enough for overlap to be observable
            lock (gate) { inside--; }
            return (true, "", "");
        }

        var tasks = Enumerable.Range(0, 6)
            .Select(_ => Task.Run(() => TailscaleCli.RunSerialized(Action)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.True(r.ok));
        Assert.Equal(1, maxInside); // at most one caller ever inside the critical section
    }

    [Fact]
    public void RunSerialized_ActionThrows_MutexIsReleasedForNextCaller()
    {
        Assert.Throws<InvalidOperationException>(() =>
            TailscaleCli.RunSerialized(() => throw new InvalidOperationException("boom")));

        // A poisoned mutex would make this second call time out and report failure.
        var result = TailscaleCli.RunSerialized(() => (true, "", ""));
        Assert.True(result.ok);
    }

    [Fact]
    public void Run_WithoutCli_ReportsNotFound_InsteadOfThrowing()
    {
        // On machines without Tailscale (CI) this exercises the not-found branch; on dev
        // machines with Tailscale it exercises a harmless read. Either way: no throw.
        var (ok, _, message) = TailscaleCli.Run("version");
        if (!TailscaleCli.IsAvailable)
        {
            Assert.False(ok);
            Assert.Equal("tailscale CLI not found", message);
        }
        else
        {
            Assert.True(ok);
        }
    }
}
