using CcRecorder.Recording;
using Xunit;

namespace CcRecorder.Tests;

public class RecordingShareTests
{
    private const string Folder = "/rec/abc";

    private static LocalManifest Manifest(string title, params (int Index, string File)[] chunks) => new()
    {
        RecordingId = "abc",
        Title = title,
        Chunks = chunks.Select(c => new ChunkInfo { Index = c.Index, File = c.File }).ToList(),
    };

    [Fact]
    public void Plan_SharesEverySegmentInRecordingOrder_WithReadableNames()
    {
        // Manifest order is not trusted: segments are shared by Index.
        var m = Manifest("Car pitch", (2, "0002.m4a"), (0, "0000.m4a"), (1, "0001.m4a"));

        var plan = RecordingShare.Plan(m, Folder, _ => true);

        Assert.Equal(new[] { "0000.m4a", "0001.m4a", "0002.m4a" }, plan.Select(p => Path.GetFileName(p.SourcePath)));
        Assert.Equal(new[] { "Car pitch - part 1 of 3.m4a", "Car pitch - part 2 of 3.m4a", "Car pitch - part 3 of 3.m4a" },
            plan.Select(p => p.ShareName));
    }

    [Fact]
    public void Plan_SingleSegment_HasNoPartSuffix()
    {
        var plan = RecordingShare.Plan(Manifest("Note", (0, "0000.m4a")), Folder, _ => true);

        Assert.Equal("Note.m4a", Assert.Single(plan).ShareName);
    }

    [Fact]
    public void Plan_MissingSegment_RefusesInsteadOfSharingAHole()
    {
        var m = Manifest("Car pitch", (0, "0000.m4a"), (1, "0001.m4a"));

        var ex = Assert.Throws<InvalidOperationException>(
            () => RecordingShare.Plan(m, Folder, p => !p.EndsWith("0001.m4a")));
        Assert.Contains("segment 2 of 2", ex.Message);
    }

    [Fact]
    public void Plan_NoSegments_SaysThereIsNoAudio()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => RecordingShare.Plan(Manifest("Empty"), Folder, _ => true));
        Assert.Contains("no audio", ex.Message);
    }

    [Theory]
    [InlineData("Recording 2026-10-21 11:19", "Recording 2026-10-21 11-19")]
    [InlineData("a/b\\c?", "a-b-c")]
    [InlineData("   ", "abc")]
    [InlineData("tab\there", "tab-here")]
    [InlineData("nul", "abc")]
    [InlineData("CON", "abc")]
    public void SafeName_MakesTheTitleAFileName(string title, string expected)
    {
        Assert.Equal(expected, RecordingShare.SafeName(title, "abc"));
    }

    [Fact]
    public void SafeName_CapsAVeryLongTitle()
    {
        var name = RecordingShare.SafeName(new string('x', 300), "abc");

        Assert.Equal(RecordingShare.MaxNameLength, name.Length);
    }
}
