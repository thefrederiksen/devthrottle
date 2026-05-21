using CcDirector.Core.Dictation;
using CcDirector.Core.Dictation.Models;
using CcDirector.Core.Dictation.Providers;
using Xunit;

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

    [Fact]
    public async Task StartStop_RoundTrip_ReturnsRawTranscript()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider { CannedTranscript = "hi there" };
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

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
            var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

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
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

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
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

        await using var session = new DictationSession(dict, provider, cleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StopAsync());
    }

    [Fact]
    public async Task StartTwice_Throws()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

        await using var session = new DictationSession(dict, provider, cleanup);
        await session.StartAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.StartAsync());
    }

    [Fact]
    public async Task PushAudio_WithoutStart_Throws()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

        await using var session = new DictationSession(dict, provider, cleanup);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => session.PushAudioAsync(new byte[] { 1 }));
    }

    [Fact]
    public async Task DisposeAsync_DisposesProvider()
    {
        using var dict = BuildLoader();
        var provider = new FakeProvider();
        var cleanup = new CleanupOrchestrator(@"C:\does\not\exist.exe");

        var session = new DictationSession(dict, provider, cleanup);
        await session.DisposeAsync();

        Assert.True(provider.Disposed);
    }
}
