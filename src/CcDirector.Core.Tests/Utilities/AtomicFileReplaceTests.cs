using System.ComponentModel;
using CcDirector.Core.Utilities;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

/// <summary>
/// The write must go through while another reader holds the old file open - that is the whole reason
/// the class exists (issue #3393). The classic Windows rename behind File.Move throws here.
/// </summary>
public sealed class AtomicFileReplaceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "atomic-replace-" + Guid.NewGuid().ToString("N"));

    public AtomicFileReplaceTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void WriteAllText_WhileAReaderHoldsTheDestination_ReplacesItAndTheReaderKeepsTheOldText()
    {
        var destination = Path.Combine(_dir, "list.json");
        File.WriteAllText(destination, "old");

        using (var held = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            AtomicFileReplace.WriteAllText(destination, "new");

            using var reader = new StreamReader(held);
            Assert.Equal("old", reader.ReadToEnd());
        }

        Assert.Equal("new", File.ReadAllText(destination));
    }

    [Fact]
    public void WriteAllText_NoDestinationYet_CreatesIt()
    {
        var destination = Path.Combine(_dir, "list.json");

        AtomicFileReplace.WriteAllText(destination, "first");

        Assert.Equal("first", File.ReadAllText(destination));
    }

    /// <summary>
    /// The bytes on disk are exactly what File.WriteAllText would have written - UTF-8 and no byte order
    /// mark - so every existing reader of a file written this way keeps reading it unchanged.
    /// </summary>
    [Fact]
    public void WriteAllText_WritesTheSameBytesAsFileWriteAllText()
    {
        var destination = Path.Combine(_dir, "list.json");
        var reference = Path.Combine(_dir, "reference.json");
        const string text = "[{\"Name\":\"first\",\"Path\":\"C:\\repos\\first\"}]\nand a second line with e-acute: \u00e9";

        AtomicFileReplace.WriteAllText(destination, text);
        File.WriteAllText(reference, text);

        Assert.Equal(File.ReadAllBytes(reference), File.ReadAllBytes(destination));
    }

    /// <summary>
    /// The temporary file is gone whether the write succeeded or not: on success it IS the destination,
    /// and a failure takes it with the handle. Nothing is left beside the list for the next reader.
    /// </summary>
    [Fact]
    public void WriteAllText_LeavesNothingBesideTheDestination()
    {
        var destination = Path.Combine(_dir, "list.json");

        for (var i = 0; i < 50; i++)
            AtomicFileReplace.WriteAllText(destination, $"write {i}");

        Assert.Equal(new[] { destination }, Directory.GetFiles(_dir));
        Assert.Equal("write 49", File.ReadAllText(destination));
    }

    [Fact]
    public void RenameThroughScannerVeto_RefusedThenAllowed_RenamesWithoutFailing()
    {
        var clock = new FakeTimeProvider();
        var answers = new Queue<int>(new[] { 5, 5, 5, 0 });
        var attempts = 0;

        AtomicFileReplace.RenameThroughScannerVeto(() => { attempts++; return answers.Dequeue(); },
            "temp", "list.json", clock, clock.Advance);

        Assert.Equal(4, attempts);
        Assert.Equal(AtomicFileReplace.ScannerVetoPause * 3, clock.GetUtcNow() - FakeStart);
    }

    [Fact]
    public void RenameThroughScannerVeto_RefusedPastTheDeadline_FailsNamingTheVeto()
    {
        var clock = new FakeTimeProvider();

        var ex = Assert.Throws<Win32Exception>(() => AtomicFileReplace.RenameThroughScannerVeto(() => 5,
            "temp", "list.json", clock, clock.Advance));

        Assert.Equal(5, ex.NativeErrorCode);
        Assert.Contains("still refused after", ex.Message);
        Assert.Contains("antivirus", ex.Message);
        Assert.Equal(AtomicFileReplace.ScannerVetoDeadline, clock.GetUtcNow() - FakeStart);
    }

    [Fact]
    public void RenameThroughScannerVeto_AnyOtherError_FailsAtOnceWithoutWaiting()
    {
        var clock = new FakeTimeProvider();
        var attempts = 0;

        var ex = Assert.Throws<Win32Exception>(() => AtomicFileReplace.RenameThroughScannerVeto(
            () => { attempts++; return 1; }, "temp", "list.json", clock, clock.Advance));

        Assert.Equal(1, ex.NativeErrorCode);
        Assert.Equal(1, attempts);
        Assert.Equal(TimeSpan.Zero, clock.GetUtcNow() - FakeStart);
    }

    /// <summary>
    /// A deadline that falls between pauses is never overrun: the last pause is cut to what remains, one
    /// attempt is made AT the deadline, and the next refusal fails - at 120 ms, not 150.
    /// </summary>
    [Fact]
    public void RenameThroughScannerVeto_DeadlineBetweenPauses_LastAttemptIsAtTheDeadlineNeverPastIt()
    {
        var clock = new FakeTimeProvider();
        var attemptTimes = new List<TimeSpan>();

        Assert.Throws<Win32Exception>(() => AtomicFileReplace.RenameThroughScannerVeto(
            () => { attemptTimes.Add(clock.GetUtcNow() - FakeStart); return 5; },
            "temp", "list.json", clock, clock.Advance,
            deadline: TimeSpan.FromMilliseconds(120), pauseBetween: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(new[] { 0, 50, 100, 120 }, attemptTimes.Select(t => (int)t.TotalMilliseconds));
        Assert.Equal(TimeSpan.FromMilliseconds(120), clock.GetUtcNow() - FakeStart);
    }

    /// <summary>
    /// A pause that oversleeps never buys an attempt past the deadline: with pauses running 40 ms long, the
    /// attempts are at 0 and 90 ms, the next pause lands at 160 ms, and the write fails there without trying.
    /// </summary>
    [Fact]
    public void RenameThroughScannerVeto_PauseOversleepsTheDeadline_FailsWithoutAnotherAttempt()
    {
        var clock = new FakeTimeProvider();
        var attemptTimes = new List<TimeSpan>();

        Assert.Throws<Win32Exception>(() => AtomicFileReplace.RenameThroughScannerVeto(
            () => { attemptTimes.Add(clock.GetUtcNow() - FakeStart); return 5; },
            "temp", "list.json", clock, requested => clock.Advance(requested + TimeSpan.FromMilliseconds(40)),
            deadline: TimeSpan.FromMilliseconds(120), pauseBetween: TimeSpan.FromMilliseconds(50)));

        Assert.Equal(new[] { 0, 90 }, attemptTimes.Select(t => (int)t.TotalMilliseconds));
        Assert.All(attemptTimes, t => Assert.True(t <= TimeSpan.FromMilliseconds(120)));
    }

    /// <summary>
    /// The real Windows write, with a rename that fails: the temporary file it created through a real handle
    /// is gone afterwards - for an immediate failure and for a veto that outlasts the deadline.
    /// </summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void WriteOnWindows_RenameFails_TheTemporaryIsRemovedWithTheHandle(int renameError)
    {
        if (!OperatingSystem.IsWindows()) return;
        var destination = Path.Combine(_dir, "list.json");
        var temporary = Path.Combine(_dir, ".list.json.test.tmp");
        var clock = new FakeTimeProvider();

        var ex = Assert.Throws<Win32Exception>(() => AtomicFileReplace.WriteOnWindows(temporary, destination,
            new byte[] { 1, 2, 3 }, (_, _) => renameError, clock, clock.Advance));

        Assert.Equal(renameError, ex.NativeErrorCode);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    private static readonly DateTimeOffset FakeStart = new FakeTimeProvider().GetUtcNow();

    [Fact]
    public void WriteAllText_DestinationIsADirectory_ThrowsAtOnceAndLeavesNothingBehind()
    {
        var destination = Path.Combine(_dir, "list.json");
        Directory.CreateDirectory(destination);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        var ex = Assert.Throws<IOException>(() => AtomicFileReplace.WriteAllText(destination, "text"));

        // Windows answers a directory in the way with the same Access-denied a scanner's veto gives; it
        // must be refused up front, not waited out for the full ten seconds.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        Assert.Contains("a directory has that name", ex.Message);
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.True(Directory.Exists(destination));
    }
}
