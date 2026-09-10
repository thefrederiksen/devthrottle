using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core.Transcription;
using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// The dictation corpus: a clip kept together with the transcript it produced, so a transcript carrying
/// a word nobody said can be checked against the audio afterwards.
///
/// The properties that matter: it writes NOTHING until the user turns it on; a kept pair carries the RAW
/// transcript as well as the corrected one (the verbatim rule needs both halves); both bounds prune
/// whole pairs; and nothing here may ever throw into the dictation path.
/// </summary>
public sealed class DictationCorpusStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "cc-director-tests", Guid.NewGuid().ToString("N"));

    private static DictationCorpusConfig On(int maxClips = 100, int maxAgeDays = 90)
        => new(Enabled: true, MaxClips: maxClips, MaxAgeDays: maxAgeDays);

    private static DictationTranscript Words(string raw, string? cleaned = null, int corrected = 0)
        => new(raw, cleaned ?? raw, corrected);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* scratch dir; best effort */ }
    }

    [Fact]
    public void Disabled_WritesNothingAndDoesNotCreateTheDirectory()
    {
        var config = new DictationCorpusConfig(Enabled: false, MaxClips: 100, MaxAgeDays: 90);

        var path = DictationCorpusStore.TryKeep(new byte[] { 1, 2, 3 }, Words("hello"), config, _dir);

        Assert.Null(path);
        Assert.False(Directory.Exists(_dir), "the corpus directory must not be created until the user turns the corpus on");
    }

    [Fact]
    public void Enabled_KeepsTheClipAndItsTranscriptAsAPair()
    {
        var wav = new byte[] { 9, 8, 7, 6 };

        var path = DictationCorpusStore.TryKeep(wav, Words("raw words", "clean words", 2), On(), _dir);

        Assert.NotNull(path);
        Assert.EndsWith(".wav", path);
        Assert.Equal(wav, File.ReadAllBytes(path!));

        var jsonPath = Path.ChangeExtension(path!, ".json");
        Assert.True(File.Exists(jsonPath), "a kept clip must have its transcript beside it");

        using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
        var root = doc.RootElement;
        Assert.Equal(Path.GetFileName(path!), root.GetProperty("clip").GetString());
        Assert.Equal("raw words", root.GetProperty("rawTranscript").GetString());
        Assert.Equal("clean words", root.GetProperty("cleanedTranscript").GetString());
        Assert.Equal(2, root.GetProperty("dictionaryWordsCorrected").GetInt32());
        Assert.Equal(wav.Length, root.GetProperty("audioBytes").GetInt32());
    }

    [Fact]
    public void RawTranscriptIsKeptSeparately_SoADictionaryEditCanBeSeen()
    {
        // The verbatim rule is provable only when BOTH halves survive: what the speech model wrote,
        // and what the dictionary then changed. Keeping only the corrected text would erase the edit.
        var path = DictationCorpusStore.TryKeep(new byte[] { 1 }, Words("mind zee studio", "mindzieStudio", 1), On(), _dir);

        using var doc = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(path!, ".json")));
        Assert.NotEqual(
            doc.RootElement.GetProperty("rawTranscript").GetString(),
            doc.RootElement.GetProperty("cleanedTranscript").GetString());
    }

    [Fact]
    public void EachKeepGetsItsOwnPair()
    {
        var first = DictationCorpusStore.TryKeep(new byte[] { 1 }, Words("one"), On(), _dir);
        var second = DictationCorpusStore.TryKeep(new byte[] { 2 }, Words("two"), On(), _dir);

        Assert.NotEqual(first, second);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.wav").Length);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public void CountBound_PrunesTheOldestWholePairs()
    {
        var config = On(maxClips: 2);

        var oldest = DictationCorpusStore.TryKeep(new byte[] { 1 }, Words("one"), config, _dir);
        Backdate(oldest!, DateTime.UtcNow.AddMinutes(-30));
        var middle = DictationCorpusStore.TryKeep(new byte[] { 2 }, Words("two"), config, _dir);
        Backdate(middle!, DateTime.UtcNow.AddMinutes(-20));
        DictationCorpusStore.TryKeep(new byte[] { 3 }, Words("three"), config, _dir);

        Assert.False(File.Exists(oldest!), "the oldest clip must be pruned past the count bound");
        Assert.False(File.Exists(Path.ChangeExtension(oldest!, ".json")), "its transcript must go with it");
        Assert.Equal(2, Directory.GetFiles(_dir, "*.wav").Length);
        Assert.Equal(2, Directory.GetFiles(_dir, "*.json").Length);
    }

    [Fact]
    public void AgeBound_PrunesPairsPastTheMaxAge()
    {
        var config = On(maxAgeDays: 7);

        var stale = DictationCorpusStore.TryKeep(new byte[] { 1 }, Words("old"), config, _dir);
        Backdate(stale!, DateTime.UtcNow.AddDays(-8));

        DictationCorpusStore.TryKeep(new byte[] { 2 }, Words("new"), config, _dir);

        Assert.False(File.Exists(stale!), "a clip past the age bound must be pruned");
        Assert.False(File.Exists(Path.ChangeExtension(stale!, ".json")));
        Assert.Single(Directory.GetFiles(_dir, "*.wav"));
    }

    [Fact]
    public void NoAudio_KeepsNothingAndDoesNotThrow()
    {
        Assert.Null(DictationCorpusStore.TryKeep(Array.Empty<byte>(), Words("hello"), On(), _dir));
        Assert.Null(DictationCorpusStore.TryKeep(null!, Words("hello"), On(), _dir));
    }

    [Fact]
    public void NoTranscript_KeepsNothingAndDoesNotThrow()
    {
        Assert.Null(DictationCorpusStore.TryKeep(new byte[] { 1 }, null!, On(), _dir));
    }

    [Fact]
    public void AnUnwritableDirectoryIsSwallowed_KeepingIsNeverAGate()
    {
        // A file where the directory should be: creating the directory must fail. The dictation must
        // still be unaffected, so TryKeep reports null rather than throwing into the send path.
        var blocked = Path.Combine(_dir, "blocked");
        Directory.CreateDirectory(_dir);
        File.WriteAllText(blocked, "not a directory");

        var path = DictationCorpusStore.TryKeep(new byte[] { 1 }, Words("hello"), On(), blocked);

        Assert.Null(path);
    }

    private static void Backdate(string clipPath, DateTime whenUtc)
    {
        File.SetLastWriteTimeUtc(clipPath, whenUtc);
        var json = Path.ChangeExtension(clipPath, ".json");
        if (File.Exists(json)) File.SetLastWriteTimeUtc(json, whenUtc);
    }
}
