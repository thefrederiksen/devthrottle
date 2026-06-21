using System.Security.Cryptography;
using System.Text;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

/// <summary>
/// Tests for the audio completeness gate (issue #586) in
/// <see cref="VoiceUtteranceService"/>. These exercise only the gate paths
/// (empty capture, missing/zero-byte segment) which return BEFORE any
/// transcription, so they run without OpenAI or a live session.
///
/// The service stores chunks under a per-user temp root keyed by the utterance
/// id; each test uses a fresh GUID id and deletes that directory afterward.
/// </summary>
public sealed class VoiceUtteranceServiceGateTests : IDisposable
{
    private readonly VoiceUtteranceService _svc;
    private readonly string _id = Guid.NewGuid().ToString("N");
    private readonly string _dir;

    public VoiceUtteranceServiceGateTests()
    {
        var options = new AgentOptions();
        _svc = new VoiceUtteranceService(new SessionManager(options), options);
        _dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "cc-director", "voice-utterances", _id);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    [Fact]
    public async Task Complete_EmptyCapture_FailsLoud()
    {
        // Acceptance criterion 5: an empty capture (zero chunks declared) fails
        // with a named error and never produces an empty transcript.
        _svc.Register(_id);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => _svc.CompleteAsync(_id, totalChunks: 0, mime: "audio/webm", repoPath: ""));
        Assert.Contains("empty capture", ex.Message);
    }

    [Fact]
    public async Task Complete_MissingSegment_RefusedAsIncomplete_NamesIndex()
    {
        // Acceptance criterion 1: a complete call missing a declared segment is
        // refused as "incomplete", naming the missing index, with no transcript.
        _svc.Register(_id);
        var c0 = Encoding.UTF8.GetBytes("voice-chunk-0");
        await _svc.StoreChunkAsync(_id, 0, c0, Sha(c0));
        // Declare two chunks but only store index 0; index 1 is missing.

        var resp = await _svc.CompleteAsync(_id, totalChunks: 2, mime: "audio/webm", repoPath: "");

        Assert.Equal("incomplete", resp.Status);
        Assert.NotNull(resp.Error);
        Assert.Contains("1", resp.Error);
    }
}
