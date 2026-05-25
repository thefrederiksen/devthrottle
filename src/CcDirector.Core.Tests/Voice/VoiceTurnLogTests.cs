using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Voice;
using Xunit;

namespace CcDirector.Core.Tests.Voice;

// Serializes execution with other tests that mutate the CC_DIRECTOR_ROOT env var.
[Collection("CcStorageRoot")]
public class VoiceTurnLogTests : IDisposable
{
    private readonly string _tempDir;

    public VoiceTurnLogTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"cc-voiceturnlog-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _tempDir);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", null);
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, true);
    }

    private static string Root => CcStorage.VoiceTurnLogs();

    [Fact]
    public void WriteInbound_WritesAudioAndTranscripts()
    {
        var sid = Guid.NewGuid().ToString();
        var dir = VoiceTurnLog.WriteInbound(
            sid, "myrepo", new byte[] { 1, 2, 3, 4 }, "utterance.webm",
            rawTranscript: "who is the ceo", cleanedTranscript: "who is the CEO", cleanupReason: "fixed casing");

        Assert.True(Directory.Exists(dir));
        Assert.True(File.Exists(Path.Combine(dir, "audio.webm")));
        Assert.Equal(4, new FileInfo(Path.Combine(dir, "audio.webm")).Length);

        var inbound = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(dir, "inbound.json")));
        Assert.Equal("who is the ceo", inbound.GetProperty("RawTranscript").GetString());
        Assert.Equal("who is the CEO", inbound.GetProperty("CleanedTranscript").GetString());
        Assert.Equal(sid, inbound.GetProperty("SessionId").GetString());
    }

    [Fact]
    public void AttachOutbound_AttachesToNewestPendingInboundForSession()
    {
        var sid = Guid.NewGuid().ToString();

        var first = VoiceTurnLog.WriteInbound(sid, "r", new byte[] { 1 }, "a.webm", "q1", "q1", "");
        Thread.Sleep(10);
        var second = VoiceTurnLog.WriteInbound(sid, "r", new byte[] { 2 }, "a.webm", "q2", "q2", "");

        VoiceTurnLog.AttachOutbound(sid, agentReply: "Full agent answer", wingmanSpoken: "Short spoken", summarizerModel: "haiku", status: "ok");

        // The newest pending inbound gets the outbound; the older one is untouched.
        Assert.True(File.Exists(Path.Combine(second, "outbound.json")));
        Assert.False(File.Exists(Path.Combine(first, "outbound.json")));

        var outbound = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(second, "outbound.json")));
        Assert.Equal("Full agent answer", outbound.GetProperty("AgentReply").GetString());
        Assert.Equal("Short spoken", outbound.GetProperty("WingmanSpoken").GetString());
    }

    [Fact]
    public void AttachOutbound_NoPendingInbound_WritesStandalone()
    {
        var sid = Guid.NewGuid().ToString();

        VoiceTurnLog.AttachOutbound(sid, "agent reply", "spoken reply", "haiku", "ok");

        var dirs = Directory.GetDirectories(Root);
        Assert.Single(dirs);
        Assert.True(File.Exists(Path.Combine(dirs[0], "outbound.json")));
        Assert.True(File.Exists(Path.Combine(dirs[0], "inbound.json"))); // standalone stub
    }

    [Fact]
    public void AttachOutbound_DoesNotCrossSessions()
    {
        var sidA = Guid.NewGuid().ToString();
        var sidB = Guid.NewGuid().ToString();

        var inboundA = VoiceTurnLog.WriteInbound(sidA, "a", new byte[] { 1 }, "a.webm", "qa", "qa", "");

        // Outbound for a DIFFERENT session must not attach to A's pending inbound.
        VoiceTurnLog.AttachOutbound(sidB, "b reply", "b spoken", "haiku", "ok");

        Assert.False(File.Exists(Path.Combine(inboundA, "outbound.json")));
        Assert.Equal(2, Directory.GetDirectories(Root).Length); // A inbound + B standalone
    }

    [Fact]
    public void WriteInbound_PurgesDirectoriesOlderThanRetention()
    {
        // Create an aged turn dir, then a WriteInbound triggers the purge.
        var stale = Path.Combine(Root, "20200101-000000000_deadbeef");
        Directory.CreateDirectory(stale);
        File.WriteAllText(Path.Combine(stale, "inbound.json"), "{}");
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddDays(-(VoiceTurnLog.RetentionDays + 1)));

        var fresh = VoiceTurnLog.WriteInbound(Guid.NewGuid().ToString(), "r", new byte[] { 1 }, "a.webm", "q", "q", "");

        Assert.False(Directory.Exists(stale));
        Assert.True(Directory.Exists(fresh));
    }
}
