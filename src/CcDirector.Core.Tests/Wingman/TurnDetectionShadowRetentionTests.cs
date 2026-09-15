using System.Text;
using CcDirector.Core.Storage;
using CcDirector.Core.Wingman;
using Xunit;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The shadow log is bounded on disk. It was not, and on a machine running a fleet all day an
/// unbounded append-only log is a disk leak - the more so because the one driver that repaints for
/// ever used to force a check, and therefore a row, every few seconds while it sat idle.
///
/// TWO BOUNDS, BOTH IN ONE NAMED PLACE. A size bound per session, so one long-lived session cannot
/// grow without limit, and an age bound, so the files of sessions that ended are not kept for ever.
/// Each is pinned here in both directions: the bound is enforced, and a file inside it is left
/// alone. The second half matters more than it looks - a sweep that deleted everything would pass
/// any test that only checked that something was deleted.
///
/// These write real files, so they live in the serialised half of the Core tests with
/// CC_DIRECTOR_ROOT redirected to a throwaway directory.
/// </summary>
[Collection("CcStorageRoot")]
public sealed class TurnDetectionShadowRetentionTests : IDisposable
{
    private readonly string _root;
    private readonly string? _previousRoot;
    private readonly bool _shadowWasEnabled;
    private readonly long _maxBytesWas;
    private readonly int _maxAgeWas;
    private readonly TimeSpan _sweepWas;

    public TurnDetectionShadowRetentionTests()
    {
        _previousRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _root = Path.Combine(Path.GetTempPath(), "ccd-shadow-retention-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _root);

        _shadowWasEnabled = TurnDetectionShadowLog.Enabled;
        _maxBytesWas = TurnDetectionShadowLog.MaxFileBytes;
        _maxAgeWas = TurnDetectionShadowLog.MaxFileAgeDays;
        _sweepWas = TurnDetectionShadowLog.SweepInterval;
        TurnDetectionShadowLog.Enabled = true;
    }

    public void Dispose()
    {
        TurnDetectionShadowLog.Enabled = _shadowWasEnabled;
        TurnDetectionShadowLog.MaxFileBytes = _maxBytesWas;
        TurnDetectionShadowLog.MaxFileAgeDays = _maxAgeWas;
        TurnDetectionShadowLog.SweepInterval = _sweepWas;
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _previousRoot);
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { /* best effort */ }
    }

    [Fact]
    public void A_session_file_that_reaches_the_size_bound_is_rolled_over()
    {
        TurnDetectionShadowLog.MaxFileBytes = 2_000;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.FromDays(1); // the age bound is not what is under test
        var session = Guid.NewGuid();

        for (int i = 0; i < 40; i++)
            TurnDetectionShadowLog.Append(session, RowFor(i));

        var live = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl");
        var rolled = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".1.jsonl");

        Assert.True(File.Exists(rolled), "the file passed the size bound and was never rolled over");
        Assert.True(new FileInfo(live).Length < TurnDetectionShadowLog.MaxFileBytes,
            "the live file is over the bound, so the rollover happened too late to bound anything");

        // Exactly two files: the live one and ONE predecessor. A rollover that kept every
        // generation would bound nothing at all.
        var files = Directory.GetFiles(CcStorage.TurnDetectionShadow(), session.ToString("N") + "*.jsonl");
        Assert.Equal(2, files.Length);
    }

    [Fact]
    public void A_session_file_inside_the_size_bound_is_left_whole()
    {
        // The other direction. Without it a rollover on every append would pass the test above.
        TurnDetectionShadowLog.MaxFileBytes = 1_000_000;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.FromDays(1);
        var session = Guid.NewGuid();

        for (int i = 0; i < 40; i++)
            TurnDetectionShadowLog.Append(session, RowFor(i));

        var rolled = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".1.jsonl");
        Assert.False(File.Exists(rolled), "a file well inside the size bound was rolled over anyway");
        Assert.Equal(40, File.ReadAllLines(
            Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl")).Length);
    }

    [Fact]
    public void A_file_older_than_the_age_bound_is_deleted_and_a_recent_one_is_not()
    {
        TurnDetectionShadowLog.MaxFileAgeDays = 14;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.Zero; // sweep on this append
        Directory.CreateDirectory(CcStorage.TurnDetectionShadow());

        var aged = Path.Combine(CcStorage.TurnDetectionShadow(), Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(aged, "{}\n");
        File.SetLastWriteTimeUtc(aged, DateTime.UtcNow.AddDays(-(TurnDetectionShadowLog.MaxFileAgeDays + 1)));

        var recent = Path.Combine(CcStorage.TurnDetectionShadow(), Guid.NewGuid().ToString("N") + ".jsonl");
        File.WriteAllText(recent, "{}\n");
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-(TurnDetectionShadowLog.MaxFileAgeDays - 1)));

        TurnDetectionShadowLog.Append(Guid.NewGuid(), RowFor(1));

        Assert.False(File.Exists(aged), "a file past the age bound was kept, so the log grows for ever");
        Assert.True(File.Exists(recent), "a file INSIDE the age bound was deleted");
    }

    [Fact]
    public void The_bounds_are_the_numbers_this_log_ships_with()
    {
        // Pinned outright, because they are the whole of the bound: a reader who wants to know how
        // much disk this can cost multiplies these two by the session count, and a silent change to
        // either makes that answer wrong. Four megabytes and one predecessor per session; fourteen
        // days for a session that has ended.
        Assert.Equal(4L * 1024 * 1024, TurnDetectionShadowLog.DefaultMaxFileBytes);
        Assert.Equal(14, TurnDetectionShadowLog.DefaultMaxFileAgeDays);
        Assert.Equal(TimeSpan.FromHours(1), TurnDetectionShadowLog.DefaultSweepInterval);

        // AND THE VALUES THE PRODUCT ACTUALLY CONSUMES. The three constants above were pinned and
        // the LIVE fields initialised from them were not, so an inspection left every constant
        // alone, put five megabytes, fifteen days and two hours into the live properties instead,
        // and every focused test stayed green - the behaviour tests all overwrite these before they
        // run. These assertions read them where nothing has moved them: the values below are
        // whatever the type initialiser produced, restored by this class's Dispose after every test
        // that changes them, and this class's constructor does not touch them.
        Assert.Equal(TurnDetectionShadowLog.DefaultMaxFileBytes, TurnDetectionShadowLog.MaxFileBytes);
        Assert.Equal(TurnDetectionShadowLog.DefaultMaxFileAgeDays, TurnDetectionShadowLog.MaxFileAgeDays);
        Assert.Equal(TurnDetectionShadowLog.DefaultSweepInterval, TurnDetectionShadowLog.SweepInterval);
    }

    [Fact]
    public void The_shadow_log_ships_ON()
    {
        // The pairing the whole phase turns on - the rule off, the log on - and until now nothing
        // asserted the second half. Enabled itself is mutable and tests move it, so what is pinned
        // is the resolver and the value this process started with.
        Assert.True(TurnDetectionShadowLog.ResolveEnabled(null), "unset must mean ON");
        Assert.True(TurnDetectionShadowLog.ResolveEnabled(""), "empty must mean ON");
        Assert.True(TurnDetectionShadowLog.ResolveEnabled("1"));
        Assert.False(TurnDetectionShadowLog.ResolveEnabled("0"), "only an explicit 0 turns it off");

        Assert.Equal(
            TurnDetectionShadowLog.ResolveEnabled(
                Environment.GetEnvironmentVariable(TurnDetectionShadowLog.EnabledVariable)),
            TurnDetectionShadowLog.InitialEnabled);

        // AND THE SHIPPED PROPERTY REALLY STARTS FROM THAT RESOLVER. The resolver was pinned and
        // the field initialised from it was not, so an inspection changed Enabled's initialiser to
        // a bare false, left ResolveEnabled and InitialEnabled alone, and every focused test stayed
        // green - because every test that cares sets Enabled itself. _shadowWasEnabled is read in
        // this class's constructor BEFORE it turns the log on, and every test in this collection
        // restores what it found, so what is compared here is the value the type initialiser
        // produced.
        Assert.Equal(TurnDetectionShadowLog.InitialEnabled, _shadowWasEnabled);
    }

    // ------------------------------------------------------------------------------------------
    // The sweep deletes only what it can prove it wrote
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void The_sweep_leaves_alone_an_aged_file_this_log_did_not_write()
    {
        // THE DESTRUCTIVE BOUNDARY, which nothing tested. The sweep enumerated every *.jsonl in the
        // directory and deleted on age alone, and an inspection demonstrated the consequence: it
        // dropped an aged owner-notes.jsonl into the shadow directory and the very next append
        // deleted it. An extension rules some things out; it is not proof of ownership.
        //
        // Both directions in one test on purpose. A sweep that deleted nothing at all would pass
        // the first assertion, so the owned file next to it has to still go.
        TurnDetectionShadowLog.MaxFileAgeDays = 14;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.Zero; // sweep on this append
        Directory.CreateDirectory(CcStorage.TurnDetectionShadow());

        var stranger = Path.Combine(CcStorage.TurnDetectionShadow(), "owner-notes.jsonl");
        Aged(stranger);

        var ours = Path.Combine(CcStorage.TurnDetectionShadow(), Guid.NewGuid().ToString("N") + ".jsonl");
        Aged(ours);

        var oursRolled = Path.Combine(
            CcStorage.TurnDetectionShadow(), Guid.NewGuid().ToString("N") + ".1.jsonl");
        Aged(oursRolled);

        TurnDetectionShadowLog.Append(Guid.NewGuid(), RowFor(1));

        Assert.True(File.Exists(stranger),
            "the sweep deleted a file this log never wrote; an extension is a deny boundary, not ownership");
        Assert.False(File.Exists(ours), "an aged file this log DID write was kept, so the bound does nothing");
        Assert.False(File.Exists(oursRolled), "the rolled predecessor shape must age out as well");
    }

    [Theory]
    // The two shapes Append and RotateIfOversized compose, and nothing else.
    [InlineData("0123456789abcdef0123456789abcdef.jsonl", true)]
    [InlineData("0123456789abcdef0123456789abcdef.1.jsonl", true)]
    [InlineData("owner-notes.jsonl", false)]
    [InlineData("0123456789abcdef0123456789abcdef.jsonl.bak", false)]
    [InlineData("0123456789abcdef0123456789abcde.jsonl", false)]            // thirty-one characters
    [InlineData("0123456789abcdef0123456789abcdefa.jsonl", false)]          // thirty-three
    [InlineData("0123456789ABCDEF0123456789ABCDEF.jsonl", false)]           // this writer emits lower case
    [InlineData("0123456789abcdef0123456789abcdef.2.jsonl", false)]         // only ONE predecessor is kept
    [InlineData("notes-0123456789abcdef0123456789abcdef.jsonl", false)]     // anchored at the start
    public void The_owned_name_shapes_are_exactly_the_two_this_log_writes(string name, bool owned)
    {
        Assert.Equal(owned, TurnDetectionShadowLog.IsOwnedFileName(name));
    }

    // ------------------------------------------------------------------------------------------
    // The rollover: a failure must not cost the row, and the bound is stated honestly
    // ------------------------------------------------------------------------------------------

    [Fact]
    public void A_rollover_that_cannot_happen_still_writes_the_row()
    {
        // Rotation used to share the append's one broad try, so a File.Move that threw skipped the
        // append entirely: the observation was gone, and only a healthy FileLog sink showed it. An
        // oversized file is a far smaller harm than a missing observation.
        //
        // The failure is made real rather than mocked - a DIRECTORY sits where the rolled
        // predecessor would go, so File.Move cannot succeed, while the live file stays perfectly
        // writable.
        TurnDetectionShadowLog.MaxFileBytes = 200;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.FromDays(1);
        var session = Guid.NewGuid();
        Directory.CreateDirectory(CcStorage.TurnDetectionShadow());

        var live = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl");
        var rolled = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".1.jsonl");

        TurnDetectionShadowLog.Append(session, RowFor(0));
        Assert.True(new FileInfo(live).Length >= TurnDetectionShadowLog.MaxFileBytes,
            "the live file must be past the size bound, or no rollover would be attempted");
        int before = File.ReadAllLines(live).Length;

        Directory.CreateDirectory(rolled); // File.Move onto a directory cannot succeed

        TurnDetectionShadowLog.Append(session, RowFor(1));

        Assert.Equal(before + 1, File.ReadAllLines(live).Length);
    }

    [Fact]
    public void A_live_file_can_pass_the_size_bound_by_at_most_one_record()
    {
        // THE BOUND STATED HONESTLY RATHER THAN THE ALGORITHM TIGHTENED. The size is tested BEFORE
        // the next record is appended, so a file that was inside the bound can end an append
        // outside it. The forty-row test happens to finish under its own small bound and therefore
        // proves nothing about this; an inspection pointed that out, and the answer is to say what
        // the algorithm really guarantees instead of pretending it guarantees more.
        TurnDetectionShadowLog.MaxFileBytes = 1_000;
        TurnDetectionShadowLog.SweepInterval = TimeSpan.FromDays(1);
        var session = Guid.NewGuid();

        var live = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl");
        var rolled = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".1.jsonl");

        long longestRecord = 0;
        long biggestBeforeAnyRollover = 0;
        for (int i = 0; i < 40 && !File.Exists(rolled); i++)
        {
            long was = File.Exists(live) ? new FileInfo(live).Length : 0;
            TurnDetectionShadowLog.Append(session, RowFor(i));
            if (File.Exists(rolled)) break;

            long now = new FileInfo(live).Length;
            longestRecord = Math.Max(longestRecord, now - was);
            biggestBeforeAnyRollover = Math.Max(biggestBeforeAnyRollover, now);
        }

        Assert.True(biggestBeforeAnyRollover > TurnDetectionShadowLog.MaxFileBytes,
            $"the live file never passed the bound ({biggestBeforeAnyRollover} bytes against "
            + $"{TurnDetectionShadowLog.MaxFileBytes}), so this test is not measuring the overshoot it claims to");

        Assert.True(biggestBeforeAnyRollover <= TurnDetectionShadowLog.MaxFileBytes + longestRecord,
            $"the overshoot was {biggestBeforeAnyRollover - TurnDetectionShadowLog.MaxFileBytes} bytes against a "
            + $"longest record of {longestRecord}; the documented bound is the maximum plus AT MOST ONE record");
    }

    // ------------------------------------------------------------------------------------------
    // Reading the log must not cost a row, and writing it must not shut a reader out
    // ------------------------------------------------------------------------------------------

    [Fact]
    public async Task A_reader_holding_the_file_does_not_cost_a_row()
    {
        // THE DEFECT THAT FAILED THE FULL CORE SUITE. These files exist to be read, and every
        // ordinary way of reading a file on Windows - File.ReadAllLines, Get-Content, a scoring
        // script, a scanner - opens it permitting other readers and DENYING writers. The append then
        // failed, and that failure is swallowed, so the row was gone for good and nothing said so.
        // The check that produced it had already been taken off the books, so no retry existed.
        //
        // The reader here opens EXACTLY as File.ReadAllLines does, which is what makes this the
        // reported failure rather than a similar-looking one.
        var session = Guid.NewGuid();
        TurnDetectionShadowLog.Append(session, RowFor(0));
        var path = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl");

        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var letGo = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(120));
            reader.Dispose();
        });

        TurnDetectionShadowLog.Append(session, RowFor(1));
        await letGo.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(2, ReadRows(path).Length);
    }

    [Fact]
    public void An_append_past_the_contention_budget_loses_the_row_and_says_so()
    {
        // THE BOUND IS REAL AND IS NOT PRETENDED AWAY. The retry is bounded because it runs under
        // the process-wide lock, so a handle nobody releases must not stall every other session's
        // append behind it. Past the bound the row IS lost - that is the honest residual, and a test
        // that only proved the happy direction would leave the reader believing otherwise.
        var was = TurnDetectionShadowLog.AppendContentionBudget;
        try
        {
            TurnDetectionShadowLog.AppendContentionBudget = TimeSpan.FromMilliseconds(60);
            var session = Guid.NewGuid();
            TurnDetectionShadowLog.Append(session, RowFor(0));
            var path = Path.Combine(CcStorage.TurnDetectionShadow(), session.ToString("N") + ".jsonl");

            using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                TurnDetectionShadowLog.Append(session, RowFor(1));
            }

            Assert.Single(ReadRows(path));
        }
        finally
        {
            TurnDetectionShadowLog.AppendContentionBudget = was;
        }
    }

    /// <summary>Read the rows without denying the writer - see ContentTurnRuleTests.ShadowRows.</summary>
    private static string[] ReadRows(string path)
    {
        using var stream = new FileStream(
            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Write a file and stamp it past the age bound.</summary>
    private static void Aged(string path)
    {
        File.WriteAllText(path, "{}\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-(TurnDetectionShadowLog.MaxFileAgeDays + 1)));
    }

    private static TurnDetectionShadowLog.Record RowFor(int i) => new(
        T: DateTime.UtcNow.ToString("o"),
        Agent: "ClaudeCode",
        Mode: "byte",
        Rule: "off",
        ByteRuleOpens: true,
        RowRuleOpens: false,
        RowEvidence: null,
        SizeRuleOpens: false,
        ChangedCharacters: i,
        SizeThreshold: 200,
        SettledHash: "aaaaaaaa",
        CurrentHash: "bbbbbbbb",
        Bytes: 64,
        Submitted: false);
}
