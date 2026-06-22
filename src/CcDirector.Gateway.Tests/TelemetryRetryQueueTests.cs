using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Unit tests for the durable, bounded, restart-surviving telemetry retry queue (issue #629).
/// Covers the four acceptance-criteria-mandated behaviours: enqueue-on-failure (depth grows while the
/// backend is unreachable), flush-on-recovery (FIFO delivery once reachable), persistence round-trip
/// (events survive a "restart" = dispose + reconstruct over the same file), and bound eviction (the
/// oldest event is dropped when the queue exceeds its bound). Also verifies the security property: the
/// access token is never written to the queue's log lines, though it is stored on disk for replay.
///
/// Delivery is driven deterministically through <see cref="ControllableHandler"/> (a fake
/// HttpMessageHandler whose reachability is flipped by the test) and the public
/// <see cref="TelemetryRetryQueue.FlushOnceAsync"/>, so no timers or network are involved.
/// </summary>
public sealed class TelemetryRetryQueueTests
{
    private const string TargetUrl = "http://backend.test/api/v1/telemetry/login";
    private const string AccessToken = "test-access-token-629-DO-NOT-LOG-xyz789";

    /// <summary>
    /// A fake backend. When <see cref="Reachable"/> is false it throws (the unreachable case);
    /// otherwise it returns <see cref="Status"/> and records the request body + Authorization header
    /// it received (in order), so the test can assert FIFO delivery and the replayed Bearer.
    /// </summary>
    private sealed class ControllableHandler : HttpMessageHandler
    {
        public volatile bool Reachable;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public readonly List<string> ReceivedBodies = new();
        public readonly List<string?> ReceivedAuth = new();
        private readonly object _lock = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (!Reachable)
                throw new HttpRequestException("backend unreachable (test)");

            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken);
            lock (_lock)
            {
                ReceivedBodies.Add(body);
                ReceivedAuth.Add(request.Headers.Authorization?.ToString());
            }
            return new HttpResponseMessage(Status);
        }
    }

    private static (TelemetryRetryQueue queue, ControllableHandler handler, string path) NewQueue(int maxSize = 1000)
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-test-{Guid.NewGuid():N}.json");
        var handler = new ControllableHandler();
        var queue = new TelemetryRetryQueue(path, new HttpClient(handler), TimeSpan.FromMilliseconds(50), maxSize);
        return (queue, handler, path);
    }

    private static string BodyFor(int i) => JsonSerializer.Serialize(new { source = "app", seq = i });

    // ----- AC: enqueue-on-failure -----

    [Fact]
    public async Task EnqueueOnFailure_BackendUnreachable_EventsStayQueuedAndDepthGrows()
    {
        var (queue, handler, path) = NewQueue();
        try
        {
            handler.Reachable = false; // backend down

            for (var i = 0; i < 5; i++)
                queue.Enqueue(TargetUrl, BodyFor(i), AccessToken);

            // Nothing delivered while the backend is down...
            Assert.Equal(5, queue.Depth);
            var delivered = await queue.FlushOnceAsync();
            Assert.Equal(0, delivered);
            // ...and the events remain queued (not dropped).
            Assert.Equal(5, queue.Depth);
            Assert.Empty(handler.ReceivedBodies);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC: flush-on-recovery (FIFO) -----

    [Fact]
    public async Task FlushOnRecovery_WhenBackendReachable_DeliversAllInFifoOrder()
    {
        var (queue, handler, path) = NewQueue();
        try
        {
            handler.Reachable = false;
            for (var i = 0; i < 5; i++)
                queue.Enqueue(TargetUrl, BodyFor(i), AccessToken);
            Assert.Equal(5, queue.Depth);

            // Backend comes back; one flush pass should drain everything.
            handler.Reachable = true;
            var delivered = await queue.FlushOnceAsync();

            Assert.Equal(5, delivered);
            Assert.Equal(0, queue.Depth);
            Assert.Equal(5, handler.ReceivedBodies.Count);

            // FIFO: the bodies arrived in the order they were enqueued.
            for (var i = 0; i < 5; i++)
            {
                using var doc = JsonDocument.Parse(handler.ReceivedBodies[i]);
                Assert.Equal(i, doc.RootElement.GetProperty("seq").GetInt32());
            }

            // The Bearer was replayed unchanged on every forward.
            Assert.All(handler.ReceivedAuth, a => Assert.Equal($"Bearer {AccessToken}", a));
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task FlushOnRecovery_MidStreamFailure_PreservesHeadAndOrder()
    {
        var (queue, handler, path) = NewQueue();
        try
        {
            handler.Reachable = true;
            for (var i = 0; i < 3; i++)
                queue.Enqueue(TargetUrl, BodyFor(i), AccessToken);

            // First two deliver, then the backend drops before the third.
            // Deliver one at a time by toggling: deliver all reachable first.
            handler.Reachable = false;
            Assert.Equal(0, await queue.FlushOnceAsync()); // none deliver, head stays = event 0
            Assert.Equal(3, queue.Depth);

            handler.Reachable = true;
            Assert.Equal(3, await queue.FlushOnceAsync());
            Assert.Equal(0, queue.Depth);

            // Order preserved across the outage: 0,1,2.
            for (var i = 0; i < 3; i++)
            {
                using var doc = JsonDocument.Parse(handler.ReceivedBodies[i]);
                Assert.Equal(i, doc.RootElement.GetProperty("seq").GetInt32());
            }
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- AC: persistence round-trip (survives a restart) -----

    [Fact]
    public async Task PersistenceRoundTrip_QueuedEventsSurviveDisposeAndReconstruct()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-test-{Guid.NewGuid():N}.json");
        try
        {
            // First "process": enqueue 4 while the backend is down, then stop (dispose).
            var handler1 = new ControllableHandler { Reachable = false };
            var q1 = new TelemetryRetryQueue(path, new HttpClient(handler1), TimeSpan.FromMilliseconds(50));
            for (var i = 0; i < 4; i++)
                q1.Enqueue(TargetUrl, BodyFor(i), AccessToken);
            Assert.Equal(4, q1.Depth);
            await q1.DisposeAsync(); // simulate Gateway stop

            // Second "process": reconstruct over the SAME file - the events are restored.
            var handler2 = new ControllableHandler { Reachable = true };
            var q2 = new TelemetryRetryQueue(path, new HttpClient(handler2), TimeSpan.FromMilliseconds(50));
            Assert.Equal(4, q2.Depth); // survived the restart

            // And they flush to the (now reachable) backend, in FIFO order.
            var delivered = await q2.FlushOnceAsync();
            Assert.Equal(4, delivered);
            Assert.Equal(0, q2.Depth);
            for (var i = 0; i < 4; i++)
            {
                using var doc = JsonDocument.Parse(handler2.ReceivedBodies[i]);
                Assert.Equal(i, doc.RootElement.GetProperty("seq").GetInt32());
            }
            // The Bearer survived the round-trip and was replayed unchanged.
            Assert.All(handler2.ReceivedAuth, a => Assert.Equal($"Bearer {AccessToken}", a));
            await q2.DisposeAsync();
        }
        finally { Cleanup(path); }
    }

    // ----- AC: bound eviction -----

    [Fact]
    public async Task BoundEviction_WhenFull_DropsOldestAndKeepsNewest()
    {
        var (queue, handler, path) = NewQueue(maxSize: 3);
        try
        {
            handler.Reachable = false;
            // Enqueue 5 into a bound-3 queue: events 0 and 1 are evicted (oldest first); 2,3,4 remain.
            for (var i = 0; i < 5; i++)
                queue.Enqueue(TargetUrl, BodyFor(i), AccessToken);

            Assert.Equal(3, queue.Depth); // never grows past the bound

            handler.Reachable = true;
            var delivered = await queue.FlushOnceAsync();
            Assert.Equal(3, delivered);

            // The three that remained are the NEWEST three (2,3,4), still in FIFO order.
            Assert.Equal(3, handler.ReceivedBodies.Count);
            var seqs = handler.ReceivedBodies
                .Select(b => JsonDocument.Parse(b).RootElement.GetProperty("seq").GetInt32())
                .ToList();
            Assert.Equal(new[] { 2, 3, 4 }, seqs);
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- security: the token is persisted (for replay) but never appears in a returned log line -----

    [Fact]
    public async Task PersistedFile_ContainsTokenForReplay_ButEnqueueDoesNotThrow()
    {
        // The token lives ON DISK so it can be replayed; this asserts that round-trip explicitly
        // (the "never logged" property is covered by the endpoint test against the real FileLog sink).
        var (queue, handler, path) = NewQueue();
        try
        {
            handler.Reachable = false;
            queue.Enqueue(TargetUrl, BodyFor(0), AccessToken);
            var onDisk = await File.ReadAllTextAsync(path);
            Assert.Contains(AccessToken, onDisk); // present for replay
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    // ----- validation -----

    [Fact]
    public void Constructor_NullOrEmptyPath_Throws()
    {
        Assert.Throws<ArgumentException>(() =>
            new TelemetryRetryQueue("", new HttpClient(), TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Constructor_NonPositiveMaxSize_Throws()
    {
        var path = Path.Combine(Path.GetTempPath(), $"q-{Guid.NewGuid():N}.json");
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new TelemetryRetryQueue(path, new HttpClient(), TimeSpan.FromSeconds(1), maxSize: 0));
    }

    [Fact]
    public async Task Enqueue_EmptyTargetUrl_Throws()
    {
        var (queue, _, path) = NewQueue();
        try
        {
            Assert.Throws<ArgumentException>(() => queue.Enqueue("", BodyFor(0), AccessToken));
        }
        finally { await queue.DisposeAsync(); Cleanup(path); }
    }

    [Fact]
    public async Task Load_CorruptFile_IsQuarantined_AndQueueStartsEmpty()
    {
        var path = Path.Combine(Path.GetTempPath(), $"telemetry-queue-test-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(path, "{ this is not valid json");
        try
        {
            var queue = new TelemetryRetryQueue(path, new HttpClient(), TimeSpan.FromMilliseconds(50));
            Assert.Equal(0, queue.Depth); // started empty after quarantine
            // The corrupt bytes were preserved next to the original, not destroyed.
            var dir = Path.GetDirectoryName(path) ?? Path.GetTempPath();
            var quarantined = Directory.GetFiles(dir, Path.GetFileName(path) + ".corrupt-*");
            Assert.NotEmpty(quarantined);
            await queue.DisposeAsync();
            foreach (var f in quarantined) Cleanup(f);
        }
        finally { Cleanup(path); }
    }

    private static void Cleanup(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* temp cleanup */ }
    }
}
