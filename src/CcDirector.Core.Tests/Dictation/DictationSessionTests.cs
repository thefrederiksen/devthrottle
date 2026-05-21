using System.Net.Http;
using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using Xunit;

#nullable enable

namespace CcDirector.Core.Tests.Dictation;

/// <summary>
/// Unit tests for the DictationSession facade using a fake provider that
/// returns a canned transcript. The real OpenAI provider is exercised by
/// the integration tests in TranscriptIntegrationTests.
/// </summary>
public sealed class DictationSessionTests
{
    private sealed class FakeProvider : IDictationProvider
    {
        public string CannedTranscript { get; set; } = "hello world";
        public string? LastSttPrompt { get; private set; }
        public long PushedBytes { get; private set; }
        public bool StartCalled { get; private set; }
        public bool StopCalled { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<string>? OnPartial;

        public Task StartAsync(string sttPrompt, CancellationToken ct = default)
        {
            LastSttPrompt = sttPrompt;
            StartCalled = true;
            return Task.CompletedTask;
        }

        public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            PushedBytes += chunk.Length;
            return Task.CompletedTask;
        }

        public Task<string> StopAsync(CancellationToken ct = default)
        {
            StopCalled = true;
            OnPartial?.Invoke(CannedTranscript);
            return Task.FromResult(CannedTranscript);
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }

    private static DictionaryLoader BuildLoader()
    {
        // Empty dictionary file path; loader handles missing files as empty.
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".yaml");
        return new DictionaryLoader(path, watch: false);
    }

    /// <summary>
    /// Cleanup orchestrator with a HTTP client that fails fast, so tests run
    /// offline without hitting the real OpenAI endpoint and without leaking
    /// any of the dictation pipeline's network behavior into the suite.
    /// </summary>
    private static CleanupOrchestrator NewFailingCleanup()
        => new CleanupOrchestrator(
            apiKey: "test-key-ignored",
            model: "gpt-4o-mini",
            httpClient: new HttpClient(new FailHandler()));

    private sealed class FailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("simulated failure for offline tests");
    }

    [Fact]
    public async Task StartStop_RoundTrip_ReturnsRawTranscript()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider { CannedTranscript = "hi there" };
        using var cleanup = NewFailingCleanup();

        await using var session = new DictationSession(dict, provider, cleanup);
        await session.StartAsync("default");
        await session.PushAudioAsync(new byte[] { 1, 2, 3 });
        var result = await session.StopAsync();

        Assert.True(provider.StartCalled);
        Assert.True(provider.StopCalled);
        Assert.Equal(3, provider.PushedBytes);
        Assert.Equal("hi there", result.RawTranscript);
        // Cleanup will have failed (bogus claude path) so cleaned == raw
        Assert.Equal("hi there", result.CleanedTranscript);
        Assert.False(result.CleanupApplied);
    }

    [Fact]
    public async Task Start_ForwardsSttPromptFromDictionary()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "vocabulary: [mindzie, CenCon]\n");
            using var dict = new DictionaryLoader(path, watch: false);
            var provider = new FakeProvider();
            using var cleanup = NewFailingCleanup();

            await using var session = new DictationSession(dict, provider, cleanup);
            await session.StartAsync("default");
            await session.StopAsync();

            Assert.NotNull(provider.LastSttPrompt);
            Assert.Contains("mindzie", provider.LastSttPrompt!);
            Assert.Contains("CenCon", provider.LastSttPrompt!);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task OnPartial_ForwardsFromProvider()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider { CannedTranscript = "partial text" };
        using var cleanup = NewFailingCleanup();

        await using var session = new DictationSession(dict, provider, cleanup);
        string? observed = null;
        session.OnPartial += t => observed = t;

        await session.StartAsync();
        await session.StopAsync();

        Assert.Equal("partial text", observed);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_Throws()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        using var cleanup = NewFailingCleanup();

        await using var session = new DictationSession(dict, provider, cleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
    }

    [Fact]
    public async Task StartTwice_Throws()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        using var cleanup = NewFailingCleanup();

        await using var session = new DictationSession(dict, provider, cleanup);
        await session.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
    }

    [Fact]
    public async Task PushAudio_WithoutStart_Throws()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        using var cleanup = NewFailingCleanup();

        await using var session = new DictationSession(dict, provider, cleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PushAudioAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task DisposeAsync_DisposesProvider()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        using var cleanup = NewFailingCleanup();

        var session = new DictationSession(dict, provider, cleanup);
        await session.DisposeAsync();

        Assert.True(provider.Disposed);
    }

    // ===== Phase 4: buffering, reconnect, state =============================

    /// <summary>
    /// Provider that fails on PushAudio after a configurable byte count, then
    /// can be "healed" to start succeeding again. Used to simulate a mid-
    /// stream disconnect followed by a reconnect.
    /// </summary>
    private sealed class FlakeyProvider : IDictationProvider
    {
        public long FailAfterBytes { get; set; } = long.MaxValue;
        public string CannedTranscript { get; set; } = "ok";

        public long DeliveredBytes { get; private set; }
        public int PushFailures { get; private set; }
        public bool Disposed { get; private set; }

        public event Action<string>? OnPartial;

        public void Heal() => FailAfterBytes = long.MaxValue;

        public Task StartAsync(string sttPrompt, CancellationToken ct = default) => Task.CompletedTask;

        public Task PushAudioAsync(ReadOnlyMemory<byte> chunk, CancellationToken ct = default)
        {
            if (DeliveredBytes + chunk.Length > FailAfterBytes)
            {
                PushFailures++;
                throw new HttpRequestException("simulated network failure");
            }
            DeliveredBytes += chunk.Length;
            return Task.CompletedTask;
        }

        public Task<string> StopAsync(CancellationToken ct = default)
        {
            OnPartial?.Invoke(CannedTranscript);
            return Task.FromResult(CannedTranscript);
        }

        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    [Fact]
    public async Task State_TransitionsThroughTypicalLifecycle()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        var seen = new List<ConnectionState>();
        session.OnStateChanged += s => seen.Add(s);

        Assert.Equal(ConnectionState.Idle, session.State);
        await session.StartAsync();
        Assert.Equal(ConnectionState.Connected, session.State);
        await session.PushAudioAsync(new byte[] { 1, 2 });
        await session.StopAsync();
        Assert.Equal(ConnectionState.Idle, session.State);

        Assert.Equal(new[] { ConnectionState.Connected, ConnectionState.Idle }, seen);
    }

    [Fact]
    public async Task PushAudio_OnTransientFailure_RoutesToBuffer_AndChangesState()
    {
        using var dict = BuildLoader();
        var provider = new FlakeyProvider { FailAfterBytes = 100 };
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        await session.StartAsync();

        // Two chunks of 60 bytes each. First succeeds (60 <= 100). Second
        // would push delivered to 120 > 100 so the provider throws; the
        // session should buffer the chunk and flip to Buffering.
        await session.PushAudioAsync(new byte[60]);
        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.False(session.HasBufferedAudio);

        await session.PushAudioAsync(new byte[60]);
        Assert.Equal(ConnectionState.Buffering, session.State);
        Assert.True(session.HasBufferedAudio);
        Assert.Equal(60, provider.DeliveredBytes);
        Assert.Equal(1, provider.PushFailures);

        // Further pushes stay in buffer, no extra provider calls.
        await session.PushAudioAsync(new byte[40]);
        Assert.Equal(60, provider.DeliveredBytes);
        Assert.Equal(1, provider.PushFailures);
        Assert.Equal(2, session.Buffer.ChunkCount);
    }

    [Fact]
    public async Task TryReconnect_DrainsBufferToProvider_AndReturnsToConnected()
    {
        using var dict = BuildLoader();
        var provider = new FlakeyProvider { FailAfterBytes = 100 };
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        await session.StartAsync();
        await session.PushAudioAsync(new byte[60]);    // delivered 60
        await session.PushAudioAsync(new byte[60]);    // buffer (chunk 1)
        await session.PushAudioAsync(new byte[40]);    // buffer (chunk 2)

        Assert.Equal(ConnectionState.Buffering, session.State);
        Assert.Equal(2, session.Buffer.ChunkCount);

        provider.Heal();

        var ok = await session.TryReconnectAsync();
        Assert.True(ok);
        Assert.Equal(ConnectionState.Connected, session.State);
        Assert.False(session.HasBufferedAudio);
        // 60 + 60 + 40 = 160 bytes delivered total.
        Assert.Equal(160, provider.DeliveredBytes);
    }

    [Fact]
    public async Task TryReconnect_PartialDrainFailure_PreservesRemainder()
    {
        using var dict = BuildLoader();
        // Provider accepts 100 bytes total, then fails again. We pre-buffer
        // chunks that total > 100 so the drain only partially succeeds.
        var provider = new FlakeyProvider { FailAfterBytes = 0 };  // start failing immediately
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        await session.StartAsync();
        // Buffer 3 chunks of 50 bytes
        await session.PushAudioAsync(new byte[50]);
        await session.PushAudioAsync(new byte[50]);
        await session.PushAudioAsync(new byte[50]);
        Assert.Equal(3, session.Buffer.ChunkCount);
        Assert.Equal(ConnectionState.Buffering, session.State);

        // Set healing to only allow 75 bytes through. Reconnect should
        // deliver chunk 1 (50 bytes <= 75), fail chunk 2 (would be 100 > 75),
        // and re-buffer chunks 2 and 3.
        provider.FailAfterBytes = 75;
        var ok = await session.TryReconnectAsync();

        Assert.False(ok);
        Assert.Equal(ConnectionState.Buffering, session.State);
        Assert.Equal(50, provider.DeliveredBytes);
        Assert.Equal(2, session.Buffer.ChunkCount);
    }

    [Fact]
    public async Task StopAsync_FromBufferingState_AttemptsReconnect()
    {
        using var dict = BuildLoader();
        var provider = new FlakeyProvider { FailAfterBytes = 100 };
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        await session.StartAsync();
        await session.PushAudioAsync(new byte[60]);    // delivered
        await session.PushAudioAsync(new byte[60]);    // buffer
        Assert.Equal(ConnectionState.Buffering, session.State);

        // Heal the provider before stopping; Stop should reconnect+drain
        // before issuing the provider's Stop call.
        provider.Heal();
        var result = await session.StopAsync();

        Assert.Equal(120, provider.DeliveredBytes);
        Assert.False(session.HasBufferedAudio);
        Assert.Equal(ConnectionState.Idle, session.State);
        Assert.Equal("ok", result.RawTranscript);
    }

    [Fact]
    public async Task StopAsync_FromBufferingState_ReconnectStillFails_RecordsReason()
    {
        using var dict = BuildLoader();
        var provider = new FlakeyProvider { FailAfterBytes = 100 };
        using var cleanup = NewFailingCleanup();
        await using var session = new DictationSession(dict, provider, cleanup);

        await session.StartAsync();
        await session.PushAudioAsync(new byte[60]);
        await session.PushAudioAsync(new byte[60]);
        Assert.Equal(ConnectionState.Buffering, session.State);

        // Do NOT heal the provider. Stop should attempt reconnect, fail,
        // then still call Stop on the provider to harvest whatever it has.
        var result = await session.StopAsync();

        Assert.True(session.HasBufferedAudio);
        Assert.NotNull(result.CleanupFailureReason);
        Assert.Contains("remained in offline buffer", result.CleanupFailureReason!);
    }
}
