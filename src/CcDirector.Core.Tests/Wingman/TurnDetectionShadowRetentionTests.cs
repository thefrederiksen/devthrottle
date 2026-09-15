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
