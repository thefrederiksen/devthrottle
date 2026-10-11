using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests.Transcription;

/// <summary>
/// MTR-10 Gap A. The transcription-audio archive has ONE directory with no tenant in its path or API and
/// a GLOBAL age/count prune, so on a multi-tenant hosted Gateway it would mix every account's raw speech
/// at rest and let one tenant's traffic prune another's diagnostic clips. It is a LOCAL self-host
/// diagnostic aid - write-only, no read method, no public archive-read route - so the fix is the same one
/// the local history beside it already takes on hosted (#1897): stop the write. These tests pin BOTH
/// directions of that gate, because a guard has two failure directions: under-refusing on hosted leaks
/// cross-tenant audio at rest, and over-refusing on self-host silently deletes the diagnostic the archive
/// exists to provide.
///
/// In the <c>GatewayHostedMode</c> collection because it sets the process-wide <c>CC_GATEWAY_HOSTED</c>
/// variable (that collection runs alone, so no other test reads the mode this one is flipping).
/// </summary>
[Collection("GatewayHostedMode")]
public sealed class TranscriptionAudioArchiveHostedTests : IDisposable
{
    private readonly string? _prevHosted;
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cc-audio-archive-hosted-tests", Guid.NewGuid().ToString("N"));

    public TranscriptionAudioArchiveHostedTests()
    {
        _prevHosted = Environment.GetEnvironmentVariable(GatewayHostedMode.HostedEnvVar);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, _prevHosted);
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* scratch cleanup is best-effort */ }
    }

    private static byte[] Clip(byte fill = 0x42) => Enumerable.Repeat(fill, 64).ToArray();

    [Fact]
    public void TrySave_OnHosted_TwoTenantsWriteNothingAndCannotPruneEachOther()
    {
        // The Gap A property: on hosted, no raw speech lands at rest, so two tenants can neither SHARE the
        // one archive directory nor let one tenant's clip prune the other's. Both saves are refused and the
        // directory stays empty - there is nothing to mix and nothing to prune.
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "1");
        var archive = new TranscriptionAudioArchive(true, _dir);

        var tenantAClip = archive.TrySave("tenant-a-turn", Clip(0x0A), "audio/wav");
        var tenantBClip = archive.TrySave("tenant-b-turn", Clip(0x0B), "audio/wav");

        Assert.Null(tenantAClip);
        Assert.Null(tenantBClip);
        // No file for either tenant - the shared, unpartitioned archive is never populated on hosted, so
        // neither tenant's traffic can consume the global clip cap against the other.
        Assert.False(Directory.Exists(_dir) && Directory.GetFiles(_dir, "turn-*").Length > 0);
    }

    [Fact]
    public void TrySave_OnSelfHost_StillWritesTheClip()
    {
        // The OTHER failure direction: the hosted gate must not become a blanket break. Self-host is
        // single-tenant, the archive is the diagnostic that catches a transcription that silently drops
        // half the speech, and it must keep working exactly as before. CC_GATEWAY_HOSTED explicitly not "1".
        Environment.SetEnvironmentVariable(GatewayHostedMode.HostedEnvVar, "0");
        var archive = new TranscriptionAudioArchive(false, _dir);

        var saved = archive.TrySave("self-host-turn", Clip(), "audio/wav");

        Assert.NotNull(saved);
        Assert.True(File.Exists(saved));
    }
}
