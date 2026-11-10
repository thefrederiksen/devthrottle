using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.UnitTests.Utilities;

/// <summary>
/// ISSUE #4853. The Director's own log folder had never had anything removed from it - 409 MB in 85
/// files on devthrottle-mac-mini, the oldest three weeks old, one file per Director process.
///
/// <para>These tests pin the BOUNDARY rather than the plumbing, because the boundary is the whole of
/// the safety: the sweep is destructive, so it must act only on what it can positively prove is a
/// spent Director process log. Every test here that names a file the sweep must KEEP is a test that
/// the allow-list has not become a deny-list.</para>
///
/// <para>Time is passed in; nothing reads a clock.</para>
/// </summary>
public sealed class DirectorLogSweepTests
{
    private static readonly DateTime Now = new(2026, 11, 9, 12, 0, 0, DateTimeKind.Utc);
    private const string Prefix = FileLog.DirectorFilePrefix;
    /// <summary>
    /// An ARBITRARY window, passed in explicitly, and deliberately not the product's own
    /// <see cref="DirectorLogSweep.DefaultKeepWindow"/>. The boundary rule must hold for whatever
    /// window it is handed, so these tests must not agree with the policy by accident. What the
    /// shipped Director actually keeps is pinned separately, by
    /// <see cref="Run_WithNoWindowGiven_KeepsSevenDays_ToWithinAnHour"/>.
    /// </summary>
    private static readonly TimeSpan Window = TimeSpan.FromDays(14);

    private static LogFileFact Aged(string name, int daysOld, long bytes = 1024) =>
        new(name, Now.AddDays(-daysOld), bytes);

    // ---- what the sweep may delete ----

    [Fact]
    public void AProcessLogOlderThanTheWindow_IsSweptAndItsBytesReported()
    {
        var plan = DirectorLogSweep.Plan(
            new[] { Aged("director-2026-10-20-93523.log", 20, bytes: 15 * 1024 * 1024) },
            Prefix, liveFileName: null, Now, Window);

        Assert.Equal(new[] { "director-2026-10-20-93523.log" }, plan.Delete);
        Assert.Equal(15 * 1024 * 1024, plan.BytesToReclaim);
        Assert.Equal(0, plan.Kept);
    }

    [Fact]
    public void AContainerStyleHexInstanceId_IsStillAProcessLog()
    {
        // UseUniqueInstanceId gives a 12-character hex token instead of a process id.
        var plan = DirectorLogSweep.Plan(
            new[] { Aged("director-2026-10-20-a1b2c3d4e5f6.log", 20) },
            Prefix, liveFileName: null, Now, Window);
        Assert.Single(plan.Delete);
    }

    // ---- what the sweep must KEEP: the allow-list, held to its word ----

    [Fact]
    public void TheFileThisProcessIsWriting_IsNeverSwept_HoweverOldItLooks()
    {
        // The live file carries an old DATE in its name but is being written now. Even if its
        // timestamp were somehow stale, naming it live must be enough on its own.
        const string live = "director-2026-10-01-20104.log";
        var plan = DirectorLogSweep.Plan(new[] { Aged(live, 40) }, Prefix, liveFileName: live, Now, Window);

        Assert.Empty(plan.Delete);
        Assert.Equal(1, plan.Kept);
    }

    [Fact]
    public void ALogWrittenInsideTheWindow_IsKept()
    {
        var plan = DirectorLogSweep.Plan(new[] { Aged("director-2026-11-05-10502.log", 4) }, Prefix, null, Now, Window);
        Assert.Empty(plan.Delete);
    }

    [Fact]
    public void ALogExactlyAtTheWindowEdge_IsKept()
    {
        // The boundary leans to KEEP: strictly older than the horizon is swept, equal is not.
        var plan = DirectorLogSweep.Plan(
            new[] { new LogFileFact("director-2026-10-26-111.log", Now - Window, 10) },
            Prefix, null, Now, Window);
        Assert.Empty(plan.Delete);
    }

    [Theory]
    // Another tool's log in the same folder is not ours to remove.
    [InlineData("launcher-2026-10-20-93523.log")]
    // A name that is not a process log at all.
    [InlineData("director.log")]
    [InlineData("crash-journal.json")]
    [InlineData("notes.txt")]
    // A half-written or hand-renamed name: the shape does not parse, so it cannot be proven spent.
    [InlineData("director-2026-10-20-93523.log.tmp")]
    [InlineData("director-2026-10-20.log")]
    [InlineData("director-2026-10-20-93523-copy.log")]
    [InlineData("director--93523.log")]
    [InlineData("director-2026-10-20-.log")]
    // A date that does not exist was not written by us.
    [InlineData("director-2026-13-01-5.log")]
    [InlineData("director-2026-02-30-5.log")]
    [InlineData("director-20x6-10-20-5.log")]
    // A non-alphanumeric discriminator is not a process id or a hex token.
    [InlineData("director-2026-10-20-9 3.log")]
    public void ANameTheRuleCannotProveIsASpentProcessLog_SurvivesHoweverOldItIs(string name)
    {
        var plan = DirectorLogSweep.Plan(new[] { Aged(name, 400) }, Prefix, null, Now, Window);

        Assert.Empty(plan.Delete);
        Assert.Equal(1, plan.Kept);
        Assert.False(DirectorLogSweep.IsProcessLogName(name, Prefix), $"'{name}' must not be classified as a sweepable process log");
    }

    [Fact]
    public void AMixedFolder_SweepsOnlyTheSpentProcessLogs()
    {
        const string live = "director-2026-11-09-20104.log";
        var plan = DirectorLogSweep.Plan(
            new[]
            {
                Aged(live, 0),                                   // live
                Aged("director-2026-11-07-777.log", 2),          // inside the window
                Aged("director-2026-10-20-93523.log", 20),       // spent
                Aged("director-2026-10-21-93524.log", 19),       // spent
                Aged("launcher-2026-10-20-1.log", 20),           // another tool
                Aged("director-2026-10-20-93523.log.tmp", 20),   // malformed
            },
            Prefix, live, Now, Window);

        Assert.Equal(
            new[] { "director-2026-10-20-93523.log", "director-2026-10-21-93524.log" },
            plan.Delete.OrderBy(n => n, StringComparer.Ordinal).ToArray());
        Assert.Equal(4, plan.Kept);
    }

    [Fact]
    public void AnEmptyFolder_SweepsNothing()
    {
        var plan = DirectorLogSweep.Plan(Array.Empty<LogFileFact>(), Prefix, null, Now, Window);
        Assert.Empty(plan.Delete);
        Assert.Equal(0, plan.Kept);
    }

    // ---- the real directory, end to end ----

    [Fact]
    public void Run_RemovesOnlyTheSpentLogs_FromARealFolder()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-log-sweep-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            // Timestamps are SET, never taken from the clock.
            void Write(string name, int daysOld)
            {
                var p = Path.Combine(dir, name);
                File.WriteAllText(p, new string('x', 2048));
                File.SetLastWriteTimeUtc(p, Now.AddDays(-daysOld));
            }

            const string live = "director-2026-11-09-20104.log";
            Write(live, 0);
            Write("director-2026-11-07-777.log", 2);
            Write("director-2026-10-20-93523.log", 20);
            Write("launcher-2026-10-20-1.log", 20);
            Write("director-2026-10-20-93523.log.tmp", 20);
            Directory.CreateDirectory(Path.Combine(dir, "director-2026-10-20-999.log")); // a DIRECTORY with a log's name

            var plan = DirectorLogSweep.Run(dir, Prefix, Path.Combine(dir, live), Now, Window);

            Assert.Equal(new[] { "director-2026-10-20-93523.log" }, plan.Delete);
            Assert.False(File.Exists(Path.Combine(dir, "director-2026-10-20-93523.log")));

            // Everything else is still here, including the directory that is named like a log.
            Assert.True(File.Exists(Path.Combine(dir, live)));
            Assert.True(File.Exists(Path.Combine(dir, "director-2026-11-07-777.log")));
            Assert.True(File.Exists(Path.Combine(dir, "launcher-2026-10-20-1.log")));
            Assert.True(File.Exists(Path.Combine(dir, "director-2026-10-20-93523.log.tmp")));
            Assert.True(Directory.Exists(Path.Combine(dir, "director-2026-10-20-999.log")));
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }

    [Fact]
    public void Run_OnAFolderThatDoesNotExist_DoesNothingAndDoesNotThrow()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cc-director-absent-" + Guid.NewGuid().ToString("N")[..12]);
        var plan = DirectorLogSweep.Run(missing, Prefix, null, Now, Window);
        Assert.Empty(plan.Delete);
    }

    /// <summary>
    /// THE SHIPPED WINDOW IS SEVEN DAYS, TO WITHIN AN HOUR (the owner's ruling, 10 November 2026; it
    /// was fourteen days when the sweep first merged).
    ///
    /// <para>This drives the path the Director really takes - start-up calls <c>Run</c> with no window
    /// at all - rather than reading <see cref="DirectorLogSweep.DefaultKeepWindow"/>, because a test
    /// that asserts a field equals itself would still pass if the application stopped using the field.
    /// </para>
    ///
    /// <para>The two files straddle the horizon BY AN HOUR, which is what makes this the only test
    /// needed. <c>Plan</c> deletes when <c>LastWriteUtc &lt; nowUtc - keepWindow</c>, so a file of age
    /// <c>d</c> goes when <c>d</c> is STRICTLY greater than the window. The file an hour inside the
    /// week must stay, which requires a window of at least six days twenty-three hours; the file an
    /// hour outside it must go, which requires a window under seven days one hour. Nothing outside
    /// that one-hour-either-side band passes, so this pins the policy on its own.</para>
    ///
    /// <para>An earlier version used ten days and six days, and an earlier version of this comment
    /// then described that range with both fenceposts inverted. Ten and six also left the test
    /// passing at six, seven, eight or nine days, so it took a second test reading the constant to
    /// pin seven at all - and that second test could not distinguish a changed policy from an
    /// abandoned one. One hour either side removes the need for it.</para>
    /// </summary>
    [Fact]
    public void Run_WithNoWindowGiven_KeepsSevenDays_ToWithinAnHour()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-director-log-window-" + Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        try
        {
            // Timestamps are SET, never taken from the clock.
            void Write(string name, TimeSpan age)
            {
                var path = Path.Combine(dir, name);
                File.WriteAllText(path, new string('x', 1024));
                File.SetLastWriteTimeUtc(path, Now - age);
            }

            const string anHourOutside = "director-2026-11-02-111.log";
            const string anHourInside = "director-2026-11-02-222.log";
            Write(anHourOutside, TimeSpan.FromDays(7) + TimeSpan.FromHours(1));
            Write(anHourInside, TimeSpan.FromDays(7) - TimeSpan.FromHours(1));

            // No keepWindow argument: this is exactly how App.axaml.cs calls it.
            var plan = DirectorLogSweep.Run(dir, Prefix, liveFilePath: null, Now);

            Assert.Equal(new[] { anHourOutside }, plan.Delete);
            Assert.False(File.Exists(Path.Combine(dir, anHourOutside)));
            Assert.True(File.Exists(Path.Combine(dir, anHourInside)));
            Assert.Equal(1, plan.Kept);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
