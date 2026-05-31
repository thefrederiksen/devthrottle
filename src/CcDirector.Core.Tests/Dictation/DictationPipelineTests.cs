using System.Net.Http;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using Xunit;

#nullable enable

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Offline, deterministic tests for <see cref="DictationPipeline"/> - the
/// capture-first orchestration that fixed the "lost the first sentence" bug.
///
/// The bug: the desktop flow connected to OpenAI BEFORE starting the mic, so
/// everything spoken during the (slow) connect was never captured. These tests
/// hold the connection open on purpose, feed audio during that window, and
/// prove every captured byte is delivered to the provider in capture order.
///
/// No mic, no network: a fake audio source emits known PCM chunks on demand and
/// a gated fake provider lets the test control exactly when "connection"
/// completes. That is what makes the no-loss invariant provable in CI.
/// </summary>
public sealed class DictationPipelineTests
{
    // ===== test doubles =====================================================

    /// <summary>Mic stand-in. The test calls <see cref="Emit"/> to simulate a captured buffer.</summary>
    private sealed class FakeAudioSource : IAudioSource
    {
        public event Action<byte[]>? OnAudioChunk;
        public bool Started { get; private set; }
        public bool Stopped { get; private set; }
        public string Description => "Fake Test Microphone";

        public void Start() => Started = true;
        public void Stop() => Stopped = true;

        /// <summary>Simulate the driver delivering a captured buffer.</summary>
        public void Emit(byte[] chunk) => OnAudioChunk?.Invoke(chunk);
    }

    /// <summary>
    /// Provider whose StartAsync blocks until the test releases it, modelling a
    /// slow TLS handshake. Records every pushed chunk in arrival order.
    /// </summary>
    private sealed class GatedProvider : IDictationProvider
    {
        private readonly TaskCompletionSource _connectGate =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public List<byte[]> Received { get; } = new();
        public bool BlockConnect { get; init; }
        public bool StopCalled { get; private set; }
        public string Canned { get; set; } = "ok";

        public event Action<string>? OnPartial;

        public void ReleaseConnect() => _connectGate.TrySetResult();

        public async Task StartAsync(string sttPrompt, CancellationToken ct = default)
        {
            if (BlockConnect) await _connectGate.Task.WaitAsync(ct);
        }

        public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            lock (Received) Received.Add(chunk.ToArray());
            return Task.CompletedTask;
        }

        public Task<string> StopAsync(CancellationToken ct = default)
        {
            StopCalled = true;
            OnPartial?.Invoke(Canned);
            return Task.FromResult(Canned);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        /// <summary>Concatenate everything received, in arrival order.</summary>
        public byte[] AllReceivedBytes()
        {
            lock (Received) return Received.SelectMany(b => b).ToArray();
        }
    }

    /// <summary>Provider whose connect always fails, to test the error path.</summary>
    private sealed class FailingConnectProvider : IDictationProvider
    {
        public event Action<string>? OnPartial { add { } remove { } }
        public Task StartAsync(string sttPrompt, CancellationToken ct = default)
            => throw new InvalidOperationException("simulated connect failure");
        public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> StopAsync(CancellationToken ct = default) => Task.FromResult("");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // ===== helpers ==========================================================

    private static DictionaryLoader BuildLoader()
        => new DictionaryLoader(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".yaml"), watch: false);

    private static CleanupOrchestrator NewOfflineCleanup()
        => new CleanupOrchestrator("test-key-ignored", "gpt-4o-mini", new HttpClient(new FailHandler()));

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("offline");
    }

    // ===== the core guarantee ===============================================

    [Fact]
    public async Task SlowConnection_DeliversEveryCapturedByteInCaptureOrder()
    {
        // This is THE regression test. Audio spoken while the connection is
        // still being established must not be lost - it must be buffered and
        // delivered, in order, the moment the link is up.
        using var dict = BuildLoader();
        var provider = new GatedProvider { BlockConnect = true };
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        // Start, but do not await: the connect is gated, yet capture must
        // already be running (capture-first).
        var startTask = pipeline.StartAsync("default");
        Assert.True(src.Started);

        // The user talks DURING the connect window. The old code dropped this.
        src.Emit(new byte[] { 1, 2, 3 });
        src.Emit(new byte[] { 4, 5, 6 });

        // Nothing is delivered yet (we are still "connecting"), but it is all
        // captured and counted as primed.
        Assert.Empty(provider.Received);
        Assert.Equal(2, pipeline.PrimedChunkCount);

        // Connection completes; buffered audio drains.
        provider.ReleaseConnect();
        await startTask;

        // The user keeps talking on the live link.
        src.Emit(new byte[] { 7, 8, 9 });

        var result = await pipeline.StopAsync();

        // Every captured byte reached the provider, in the exact capture order.
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9 }, provider.AllReceivedBytes());
        Assert.Equal(9, pipeline.CapturedBytes);
        Assert.Equal(pipeline.CapturedBytes, pipeline.DeliveredBytes);
        Assert.Equal("ok", result.RawTranscript);
    }

    [Fact]
    public async Task NoAudioCaptured_Throws_NamingTheDevice_WithoutCommitting()
    {
        // When the mic produces zero bytes (silent/wrong/dead device), the
        // pipeline must NOT commit an empty buffer to the provider - that would
        // surface the provider's opaque "buffer too small / 0.00ms" error. It
        // must instead throw a clear, device-named failure.
        using var dict = BuildLoader();
        var provider = new GatedProvider(); // connects immediately
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        await pipeline.StartAsync("default");
        // The user "talked" but the device delivered nothing: no Emit() calls.

        var ex = await Assert.ThrowsAsync<NoAudioCapturedException>(() => pipeline.StopAsync());

        Assert.Equal("Fake Test Microphone", ex.DeviceDescription);
        Assert.Contains("Fake Test Microphone", ex.Message);
        Assert.Equal(0, pipeline.CapturedBytes);
        // The provider was never committed - no opaque empty-buffer error leaks out.
        Assert.False(provider.StopCalled);
    }

    [Fact]
    public async Task CaptureStarts_BeforeConnectionCompletes()
    {
        using var dict = BuildLoader();
        var provider = new GatedProvider { BlockConnect = true };
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        var captureStartedFired = false;
        pipeline.OnCaptureStarted += () => captureStartedFired = true;

        var startTask = pipeline.StartAsync("default");

        // Connection is still blocked, yet the source is started and the
        // capture-started signal has already fired.
        Assert.True(src.Started);
        Assert.True(captureStartedFired);
        Assert.False(startTask.IsCompleted);

        provider.ReleaseConnect();
        await startTask;
        // Emit one chunk so Stop has real audio to commit; this test is about the
        // capture-before-connect ordering, not the empty-buffer guard.
        src.Emit(new byte[] { 1 });
        await pipeline.StopAsync();
    }

    [Fact]
    public async Task ManyChunksAcrossConnectBoundary_PreserveExactOrder()
    {
        // Stress the ordering guarantee: a long burst that straddles the moment
        // the connection opens. Each chunk is tagged with its sequence number so
        // any reordering or loss is caught.
        using var dict = BuildLoader();
        var provider = new GatedProvider { BlockConnect = true };
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        const int total = 200;
        var startTask = pipeline.StartAsync("default");

        // Emit the first half while "connecting".
        for (int i = 0; i < total / 2; i++)
            src.Emit(new byte[] { (byte)(i % 256), (byte)i });

        provider.ReleaseConnect();
        await startTask;

        // Emit the second half live.
        for (int i = total / 2; i < total; i++)
            src.Emit(new byte[] { (byte)(i % 256), (byte)i });

        await pipeline.StopAsync();

        lock (provider.Received)
        {
            Assert.Equal(total, provider.Received.Count);
            for (int i = 0; i < total; i++)
            {
                Assert.Equal((byte)(i % 256), provider.Received[i][0]);
                Assert.Equal((byte)i, provider.Received[i][1]);
            }
        }
        Assert.True(pipeline.PrimedChunkCount >= total / 2);
        Assert.Equal(pipeline.CapturedBytes, pipeline.DeliveredBytes);
    }

    [Fact]
    public async Task Stop_DrainsAllCapturedAudio_BeforeReturning()
    {
        using var dict = BuildLoader();
        var provider = new GatedProvider();           // connects instantly
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        await pipeline.StartAsync("default");
        for (int i = 0; i < 50; i++) src.Emit(new byte[] { (byte)i });

        await pipeline.StopAsync();

        // The pipeline does not commit until the pump has delivered everything.
        Assert.Equal(50, pipeline.CapturedBytes);
        Assert.Equal(50, pipeline.DeliveredBytes);
        Assert.True(provider.StopCalled);
    }

    [Fact]
    public async Task ForwardsPartialsAndStateChanges()
    {
        using var dict = BuildLoader();
        var provider = new GatedProvider { Canned = "hello there" };
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        string? partial = null;
        var states = new List<ConnectionState>();
        pipeline.OnPartial += p => partial = p;
        pipeline.OnStateChanged += s => states.Add(s);

        await pipeline.StartAsync("default");
        src.Emit(new byte[] { 1 });
        await pipeline.StopAsync();

        Assert.Equal("hello there", partial);
        Assert.Contains(ConnectionState.Connected, states);
    }

    [Fact]
    public async Task ConnectionFails_StopsCapture_AndThrows()
    {
        using var dict = BuildLoader();
        var provider = new FailingConnectProvider();
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        await Assert.ThrowsAsync<InvalidOperationException>(() => pipeline.StartAsync("default"));

        // Capture must be torn down on a failed connect - no orphaned mic.
        Assert.True(src.Started);
        Assert.True(src.Stopped);
    }

    [Fact]
    public async Task Dispose_StopsAudioAndPump()
    {
        using var dict = BuildLoader();
        var provider = new GatedProvider();
        using var cleanup = NewOfflineCleanup();
        var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        var pipeline = new DictationPipeline(src, session);

        await pipeline.StartAsync("default");
        await pipeline.DisposeAsync();

        Assert.True(src.Stopped);
        await session.DisposeAsync();
    }

    [Fact]
    public async Task FastConnection_NothingPrimed_StillDeliversAll()
    {
        // Sanity: when the connection is instant there is no priming window, but
        // the delivery guarantee still holds.
        using var dict = BuildLoader();
        var provider = new GatedProvider();
        using var cleanup = NewOfflineCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);
        var src = new FakeAudioSource();
        await using var pipeline = new DictationPipeline(src, session);

        await pipeline.StartAsync("default");
        src.Emit(new byte[] { 10, 20, 30 });
        await pipeline.StopAsync();

        Assert.Equal(new byte[] { 10, 20, 30 }, provider.AllReceivedBytes());
        Assert.Equal(pipeline.CapturedBytes, pipeline.DeliveredBytes);
    }
}
