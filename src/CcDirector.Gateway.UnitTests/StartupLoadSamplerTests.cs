using System.Text;
using CcDirector.Gateway.Diagnostics;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The deploy outage measurement: a load line every two seconds for the first ten minutes after start, then
/// nothing. The loop is driven by a fake clock that the fake wait advances, so these tests take no real time
/// and do not depend on the machine. None of them listens to the real Npgsql meter: it is process-wide, so a pool
/// another test created would show up here. The real meter is proved against real PostgreSQL in
/// CcDirector.Gateway.DatabaseTests (StartupLoadSamplerPostgresTests).
/// </summary>
public sealed class StartupLoadSamplerTests
{
    private static readonly DateTime Start = new(2026, 11, 9, 2, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Run_TenMinuteWindow_WritesThreeHundredSamplesThenStops()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        var clock = TimeSpan.Zero;
        var lines = new List<string>();
        var waits = 0;

        var samples = sampler.Run(
            elapsed: () => clock,
            wait: d => { waits++; clock += d; return true; },
            write: Bounded(lines),
            utcNow: () => Start + clock);

        // 0, 2, 4 ... 598 seconds: 300 samples, and the loop returned by itself at the window.
        Assert.Equal(300, samples);
        Assert.Equal(StartupLoadSampler.Window, clock);
        Assert.Equal(300, lines.Count(l => l.Contains(" utc=")));
        Assert.StartsWith($"{StartupLoadSampler.LinePrefix} started", lines[0]);
        Assert.StartsWith($"{StartupLoadSampler.LinePrefix} finished: 300 samples", lines[^1]);
        Assert.Equal(300, waits);
    }

    [Fact]
    public void Run_SampleLinesCarryUtcTimestampsTwoSecondsApart()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        var clock = TimeSpan.Zero;
        var lines = new List<string>();

        sampler.Run(() => clock, d => { clock += d; return true; }, Bounded(lines), () => Start + clock);

        var samples = lines.Where(l => l.Contains(" utc=")).ToList();
        Assert.Contains(" utc=2026-11-09T02:00:00.000Z t=0.0s ", samples[0]);
        Assert.Contains(" utc=2026-11-09T02:00:02.000Z t=2.0s ", samples[1]);
        Assert.Contains(" utc=2026-11-09T02:09:58.000Z t=598.0s ", samples[^1]);
    }

    [Fact]
    public void Run_SlowSample_DoesNotPushTheScheduleBack()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        var clock = TimeSpan.Zero;
        var waited = new List<TimeSpan>();
        var lines = new List<string>();

        sampler.Run(
            () => clock,
            d => { waited.Add(d); clock += d; return true; },
            line => { Bounded(lines)(line); if (line.Contains(" t=0.0s ")) clock += TimeSpan.FromMilliseconds(500); },
            () => Start + clock);

        // The first sample took half a second, so the first wait is the remaining 1.5 seconds, not 2.
        Assert.Equal(TimeSpan.FromMilliseconds(1500), waited[0]);
        Assert.Equal(TimeSpan.FromSeconds(2), waited[1]);
    }

    [Fact]
    public void Run_StartedPastTheWindow_WritesNoSample()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        var lines = new List<string>();

        var samples = sampler.Run(() => StartupLoadSampler.Window, _ => true, Bounded(lines), () => Start);

        Assert.Equal(0, samples);
        Assert.Equal(2, lines.Count);
    }

    [Fact]
    public void Run_WaitRefuses_StopsEarly()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        var lines = new List<string>();

        var samples = sampler.Run(() => TimeSpan.Zero, _ => false, Bounded(lines), () => Start);

        Assert.Equal(1, samples);
    }

    [Fact]
    public void FormatLine_NoDatabaseYet_SaysSoAndCarriesTheThreadPool()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);

        var line = sampler.FormatLine(TimeSpan.FromSeconds(4), Start);

        Assert.StartsWith(StartupLoadSampler.LinePrefix, line);
        Assert.Contains(" threads=", line);
        ThreadPool.GetMinThreads(out var floor, out _);
        Assert.Contains($" floor={floor} ", line);
        Assert.Contains(" queued=", line);
        Assert.Contains(" hub=", line);
        Assert.Contains(" db=none-yet", line);
    }

    [Fact]
    public void Record_PoolMeasurements_AppearPerPoolUnderAHashedLabel()
    {
        using var sampler = new StartupLoadSampler(listenToNpgsql: false);
        const string pool = "Host=db.example.internal;Username=gateway;Database=gw";

        sampler.Record("db.client.connections.usage", observable: true, 15, Tags(pool, "used"));
        sampler.Record("db.client.connections.usage", observable: true, 0, Tags(pool, "idle"));
        sampler.Record("db.client.connections.max", observable: true, 15, Tags(pool, null));
        // Up-down counters arrive as adds: three waiters joined, one left.
        sampler.Record("db.client.connections.pending_requests", observable: false, 1, Tags(pool, null));
        sampler.Record("db.client.connections.pending_requests", observable: false, 1, Tags(pool, null));
        sampler.Record("db.client.connections.pending_requests", observable: false, 1, Tags(pool, null));
        sampler.Record("db.client.connections.pending_requests", observable: false, -1, Tags(pool, null));
        sampler.Record("db.client.connections.timeouts", observable: false, 1, Tags(pool, null));
        // A duration histogram is not a count and is left out.
        sampler.Record("db.client.commands.duration", observable: false, 1234, Tags(pool, null));

        var sb = new StringBuilder();
        sampler.AppendDatabasePools(sb);
        var text = sb.ToString();

        var label = StartupLoadSampler.PoolLabel(pool);
        Assert.Equal(
            $" db[{label}] used=15 idle=0 max=15 waiting=2 timeouts=1 executing=-",
            text);
        Assert.DoesNotContain("db.example.internal", text);
        Assert.DoesNotContain("1234", text);
    }

    [Fact]
    public void RunThread_FirstStepThrows_WritesOneLineAndDoesNotThrow()
    {
        // An exception escaping the sampler's dedicated thread would end the Gateway process.
        var lines = new List<string>();

        StartupLoadSampler.RunThread(
            new StartupLoadSampler(listenToNpgsql: false),
            elapsed: () => TimeSpan.Zero,
            Bounded(lines),
            firstOnThread: () => throw new InvalidOperationException("share went away"));

        var line = Assert.Single(lines);
        Assert.Contains("FAILED and stopped: InvalidOperationException: share went away", line);
    }

    [Fact]
    public void PoolLabel_IsStableAndShort()
    {
        Assert.Equal(StartupLoadSampler.PoolLabel("a"), StartupLoadSampler.PoolLabel("a"));
        Assert.NotEqual(StartupLoadSampler.PoolLabel("a"), StartupLoadSampler.PoolLabel("b"));
        Assert.Equal(6, StartupLoadSampler.PoolLabel("a").Length);
    }

    // A sampler that never stops would hang the whole gate rather than fail one test, so every fake writer
    // refuses to go far past the 302 lines a correct ten-minute run writes.
    private static Action<string> Bounded(List<string> lines) => line =>
    {
        if (lines.Count >= 1000)
            throw new InvalidOperationException("The sampler wrote over 1000 lines; it did not stop at its window.");
        lines.Add(line);
    };

    private static KeyValuePair<string, object?>[] Tags(string pool, string? state)
    {
        var tags = new List<KeyValuePair<string, object?>> { new("pool.name", pool) };
        if (state is not null) tags.Add(new("state", state));
        return tags.ToArray();
    }
}
