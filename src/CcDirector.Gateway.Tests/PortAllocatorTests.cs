using CcDirector.ControlApi;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Regression tests for issue #685: PortAllocator burned a Control API port slot for every Director
/// that did not shut down gracefully. A <c>.port</c> reservation file is only removed by Release on a
/// graceful shutdown, so hard-kills, crashes, and reboots left ghost files behind. They accumulated
/// with no cleanup until the whole 7879..7898 range looked busy even when every TCP port was actually
/// free, which disabled the Control API.
///
/// The fix: a reservation counts as "used by others" only when its claimed port is genuinely NOT
/// bindable (a live Director holds its port). A reservation pointing at a free port is a ghost - it is
/// not counted AND its file is pruned so the directory cannot fill up over time.
///
/// These tests drive <see cref="PortAllocator.CollectLivePortReservations"/> directly with an injected
/// port-liveness predicate, so they do not touch real OS ports or the real user's reservation
/// directory - only an isolated temp directory of fake .port files.
/// </summary>
public sealed class PortAllocatorTests : IDisposable
{
    private readonly string _dir;

    public PortAllocatorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-director-portalloc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string WriteReservation(string directorId, int port)
    {
        var path = Path.Combine(_dir, $"{directorId}.port");
        File.WriteAllText(path, port.ToString());
        return path;
    }

    [Fact]
    public void CollectLivePortReservations_StaleReservationOnFreePort_NotCountedAndFilePruned()
    {
        // Arrange: one ghost reservation whose claimed port is free to bind.
        var file = WriteReservation("dead-director-guid", 7880);

        // Act: every port is free (predicate always true), simulating "no Director running".
        var inUse = PortAllocator.CollectLivePortReservations(_dir, except: "me", isPortFree: _ => true);

        // Assert: the ghost is not counted, and its file is pruned.
        Assert.Empty(inUse);
        Assert.False(File.Exists(file), "stale reservation file should be pruned");
    }

    [Fact]
    public void CollectLivePortReservations_LiveReservationOnBoundPort_RespectedAndFileKept()
    {
        // Arrange: a live Director is holding port 7881 (it is NOT bindable).
        var file = WriteReservation("live-director-guid", 7881);

        // Act: port 7881 is not free; all others are.
        var inUse = PortAllocator.CollectLivePortReservations(
            _dir, except: "me", isPortFree: p => p != 7881);

        // Assert: the live reservation is respected and its file is left in place.
        Assert.Contains(7881, inUse);
        Assert.True(File.Exists(file), "live reservation file must not be pruned");
    }

    [Fact]
    public void CollectLivePortReservations_NineteenStaleFilesAllPortsFree_AllPrunedNoneInUse()
    {
        // Arrange: reproduce the field failure - 19 stale reservations from dead GUIDs claim every
        // bindable port in the range, yet every OS port is actually free.
        var files = new List<string>();
        for (int port = PortAllocator.PortRangeStart; port <= PortAllocator.PortRangeStart + 18; port++)
            files.Add(WriteReservation($"dead-guid-{port}", port));

        // Act: all ports free.
        var inUse = PortAllocator.CollectLivePortReservations(_dir, except: "me", isPortFree: _ => true);

        // Assert: nothing is counted as in use, so a starting Director would find a free port. Every
        // ghost file has been pruned, so the range cannot be exhausted by ghosts going forward.
        Assert.Empty(inUse);
        foreach (var f in files)
            Assert.False(File.Exists(f), $"ghost reservation {Path.GetFileName(f)} should be pruned");
    }

    [Fact]
    public void CollectLivePortReservations_IgnoresOwnReservation()
    {
        // Arrange: the current Director's own reservation must never count against it, even when its
        // port is currently bound (it is the one binding it).
        WriteReservation("me", 7885);

        // Act
        var inUse = PortAllocator.CollectLivePortReservations(
            _dir, except: "me", isPortFree: p => p != 7885);

        // Assert: own reservation is excluded.
        Assert.DoesNotContain(7885, inUse);
    }

    [Fact]
    public void CollectLivePortReservations_MalformedReservation_PrunedAndNotCounted()
    {
        // Arrange: a reservation file with garbage contents can never identify a live Director.
        var path = Path.Combine(_dir, "garbage-director.port");
        File.WriteAllText(path, "not-a-port");

        // Act
        var inUse = PortAllocator.CollectLivePortReservations(_dir, except: "me", isPortFree: _ => false);

        // Assert: malformed file is pruned and contributes nothing.
        Assert.Empty(inUse);
        Assert.False(File.Exists(path), "malformed reservation file should be pruned");
    }

    [Fact]
    public void CollectLivePortReservations_MixOfLiveAndStale_KeepsLivePrunesStale()
    {
        // Arrange: one live reservation (port bound) and one ghost (port free) side by side.
        var live = WriteReservation("live-guid", 7890);
        var ghost = WriteReservation("ghost-guid", 7891);

        // Act: only 7890 is bound.
        var inUse = PortAllocator.CollectLivePortReservations(
            _dir, except: "me", isPortFree: p => p != 7890);

        // Assert: live kept and counted; ghost pruned and not counted.
        Assert.Contains(7890, inUse);
        Assert.DoesNotContain(7891, inUse);
        Assert.True(File.Exists(live), "live reservation must be kept");
        Assert.False(File.Exists(ghost), "ghost reservation must be pruned");
    }
}
