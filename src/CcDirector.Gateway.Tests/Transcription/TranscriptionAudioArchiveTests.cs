using CcDirector.Gateway.Transcription;
using Xunit;

namespace CcDirector.Gateway.Tests.Transcription;

/// <summary>
/// The archive exists so a transcription that silently drops half the speech can be PROVEN against the
/// audio that produced it. These tests pin the two properties that failure needs: the clip survives a
/// successful turn (the old net deleted exactly then), and it is findable from the turn id in the
/// local history. The rest pin the bounds that keep it from filling the disk.
///
/// Every test writes to its own scratch directory - never the real user's archive.
/// </summary>
[Collection("DirectorRoot")] // serializes classes that mutate the process-wide CC_DIRECTOR_ROOT
public sealed class TranscriptionAudioArchiveTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), "cc-audio-archive-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* scratch cleanup is best-effort */ }
    }

    private TranscriptionAudioArchive NewArchive() => new(false, _dir);

    private static byte[] Clip(byte fill = 0x42) => Enumerable.Repeat(fill, 64).ToArray();

    [Fact]
    public void TrySave_WritesTheExactBytesSent()
    {
        var archive = NewArchive();
        var audio = Clip();

        var path = archive.TrySave("turn1", audio, "audio/wav");

        Assert.NotNull(path);
        Assert.True(File.Exists(path));
        Assert.Equal(audio, File.ReadAllBytes(path!));
    }

    [Fact]
    public void TrySave_FileIsFoundFromTheHistoryTurnId()
    {
        // The whole point of the key: a suspicious line in transcription-history names a turnId, and that
        // turnId must lead to the clip with no other index in between.
        var archive = NewArchive();
        const string turnId = "0b7e368634d1467fa063ec90218d71f9";

        var saved = archive.TrySave(turnId, Clip(), "audio/wav");

        Assert.Equal(archive.FileFor(turnId, ".wav"), saved);
        Assert.True(File.Exists(archive.FileFor(turnId, ".wav")));
    }

    [Theory]
    [InlineData("audio/wav", ".wav")]
    [InlineData("audio/mpeg", ".mp3")]
    [InlineData("audio/webm", ".webm")]
    [InlineData("audio/ogg", ".ogg")]
    [InlineData("audio/mp4", ".m4a")]
    // What the browser and phone ACTUALLY send. A private exact-match copy of this mapping missed the
    // ";codecs=" parameter and wrote real clips as unplayable .bin - caught only by watching the live
    // archive, never by a test, because no test used the real MIME string.
    [InlineData("audio/webm;codecs=opus", ".webm")]
    [InlineData("audio/ogg; codecs=vorbis", ".ogg")]
    public void TrySave_NamesTheFileSoAPlayerCanOpenIt(string contentType, string expectedExtension)
    {
        var path = NewArchive().TrySave("turn1", Clip(), contentType);

        Assert.Equal(expectedExtension, Path.GetExtension(path));
    }

    [Fact]
    public void TrySave_EmptyAudio_SavesNothing()
    {
        var archive = NewArchive();

        Assert.Null(archive.TrySave("turn1", Array.Empty<byte>(), "audio/wav"));
        Assert.Null(archive.TrySave("turn1", null!, "audio/wav"));
    }

    [Fact]
    public void TrySave_BlankTurnId_SavesNothing()
    {
        Assert.Null(NewArchive().TrySave("  ", Clip(), "audio/wav"));
    }

    [Fact]
    public void Clear_RemovesEveryRetainedClip()
    {
        var archive = NewArchive();
        archive.TrySave("turn1", Clip(), "audio/wav");
        archive.TrySave("turn2", Clip(), "audio/webm");

        Assert.Equal(2, archive.Clear());
        Assert.Empty(Directory.GetFiles(_dir, "turn-*"));
        Assert.Equal(0, archive.Clear());
    }

    [Fact]
    public void TrySave_UnwritableDirectory_ReturnsNullAndNeverThrows()
    {
        // Fail-open contract: a broken archive degrades diagnostics, never the transcription. The
        // directory path is occupied by a FILE, so creating it cannot succeed.
        var occupied = Path.Combine(_dir, "occupied");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(occupied, "not a directory");
        var archive = new TranscriptionAudioArchive(false, occupied);

        var path = archive.TrySave("turn1", Clip(), "audio/wav");

        Assert.Null(path);
    }

    [Fact]
    public void TrySave_PrunesClipsOlderThanMaxAge()
    {
        var archive = NewArchive();
        archive.TrySave("stale", Clip(), "audio/wav");
        var stalePath = archive.FileFor("stale", ".wav");
        File.SetLastWriteTimeUtc(stalePath, DateTime.UtcNow - TranscriptionAudioArchive.MaxAge - TimeSpan.FromMinutes(1));

        archive.TrySave("fresh", Clip(), "audio/wav");

        Assert.False(File.Exists(stalePath));
        Assert.True(File.Exists(archive.FileFor("fresh", ".wav")));
    }

    [Fact]
    public void TrySave_KeepsClipsInsideMaxAge()
    {
        var archive = NewArchive();
        archive.TrySave("recent", Clip(), "audio/wav");
        var recentPath = archive.FileFor("recent", ".wav");
        File.SetLastWriteTimeUtc(recentPath, DateTime.UtcNow - TranscriptionAudioArchive.MaxAge + TimeSpan.FromMinutes(5));

        archive.TrySave("fresh", Clip(), "audio/wav");

        Assert.True(File.Exists(recentPath));
    }

    [Fact]
    public void TrySave_PrunesOldestOnceOverMaxClips()
    {
        var archive = NewArchive();
        var baseTime = DateTime.UtcNow - TimeSpan.FromHours(1);

        // One clip over the ceiling, each a minute newer than the last so "oldest" is unambiguous.
        for (var i = 0; i <= TranscriptionAudioArchive.MaxClips; i++)
        {
            var id = $"turn{i:D4}";
            archive.TrySave(id, Clip(), "audio/wav");
            File.SetLastWriteTimeUtc(archive.FileFor(id, ".wav"), baseTime.AddMinutes(i));
        }

        var remaining = Directory.GetFiles(_dir, "turn-*");
        Assert.Equal(TranscriptionAudioArchive.MaxClips, remaining.Length);
        Assert.False(File.Exists(archive.FileFor("turn0000", ".wav")));
        Assert.True(File.Exists(archive.FileFor($"turn{TranscriptionAudioArchive.MaxClips:D4}", ".wav")));
    }

    [Fact]
    public void DefaultDirectory_FollowsCcDirectorRoot_ResolvedPerAccess()
    {
        // THE regression. Shared is a static field, so a path captured in the constructor is baked at
        // TYPE LOAD - once, before any test sets CC_DIRECTOR_ROOT, and no test can undo it. That shipped:
        // isolated tests that fell back to Shared wrote 3-byte clips into the REAL user's archive,
        // because Shared's path had already been fixed to the real location. The tests were isolated
        // correctly; the static defeated them. Resolving per access is what makes the override real.
        var prev = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        try
        {
            // An archive constructed with NO override, BEFORE the root is redirected - exactly Shared's
            // situation. It must still follow a root set afterwards.
            var archive = new TranscriptionAudioArchive(hosted: false);

            var rootA = Path.Combine(_dir, "rootA");
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", rootA);
            var underA = archive.FileFor("turn1", ".wav");

            var rootB = Path.Combine(_dir, "rootB");
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", rootB);
            var underB = archive.FileFor("turn1", ".wav");

            Assert.StartsWith(rootA, underA);
            Assert.StartsWith(rootB, underB);
            Assert.NotEqual(underA, underB);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", prev);
        }
    }

    [Fact]
    public void MaxAge_IsAtLeastADay()
    {
        // A problem reported "yesterday" must still have its audio; a shorter window silently
        // reintroduces the failure this archive exists to end.
        Assert.True(TranscriptionAudioArchive.MaxAge >= TimeSpan.FromHours(24));
    }
}
