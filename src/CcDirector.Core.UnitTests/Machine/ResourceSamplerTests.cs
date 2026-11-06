using CcDirector.Core.Machine;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace CcDirector.Core.UnitTests.Machine;

/// <summary>
/// The resource sampler, driven by a virtual clock and a fake probe. No test sleeps and none reads the real
/// clock. The verdict is handed in, so each test chooses the levels it needs and watches what the sampler
/// does with them.
/// </summary>
[Trait("Category", "Resources")]
public sealed class ResourceSamplerTests
{
    private static readonly TimeSpan Step = ResourceSampling.Interval;

    private static ResourcePressureVerdict Verdict(ResourceLevel cpu, ResourceLevel memory = ResourceLevel.Normal) =>
        new([
            new ResourceVerdict(ResourceKind.Cpu, cpu, $"processor {cpu}"),
            new ResourceVerdict(ResourceKind.Memory, memory, $"memory {memory}"),
            new ResourceVerdict(ResourceKind.Swap, ResourceLevel.Normal, "swap normal"),
        ]);

    private static (ResourceSampler Sampler, FakeResourceProbe Probe, FakeTimeProvider Time) Create(
        Func<IReadOnlyList<TimelineSample>, ResourcePressureVerdict> evaluate)
    {
        var time = new FakeTimeProvider(ResourceSampleBuilder.DefaultTakenAtUtc);
        var probe = new FakeResourceProbe();
        return (new ResourceSampler(probe, evaluate, time), probe, time);
    }

    [Fact]
    public void Current_BeforeStart_IsNotYetSampled_AndEveryResourceUnknown()
    {
        var (sampler, probe, _) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            Assert.Same(ResourceSamplerState.NotYetSampled, sampler.Current);
            Assert.All(sampler.Current.Verdict.Resources, v => Assert.Equal(ResourceLevel.Unknown, v.Level));
            Assert.Empty(sampler.History);
            Assert.Null(sampler.LatestAge);
            Assert.Equal(0, probe.ReadCount);
        }
    }

    [Fact]
    public void Start_ReadsAtOnce_ThenOncePerInterval()
    {
        var (sampler, probe, time) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            sampler.Start();
            Assert.Equal(1, probe.ReadCount);

            time.Advance(Step - TimeSpan.FromMilliseconds(1));
            Assert.Equal(1, probe.ReadCount);

            time.Advance(TimeSpan.FromMilliseconds(1));
            Assert.Equal(2, probe.ReadCount);

            time.Advance(Step * 10);
            Assert.Equal(12, probe.ReadCount);
        }
    }

    [Fact]
    public void Start_Twice_Throws()
    {
        var (sampler, _, _) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            sampler.Start();
            Assert.Throws<InvalidOperationException>(sampler.Start);
        }
    }

    [Fact]
    public void History_PastOneHour_KeepsTheNewest720_OldestFirst()
    {
        var (sampler, probe, _) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            for (var i = 0; i < ResourceSampling.HistoryCapacity + 80; i++)
            {
                probe.Set(new ResourceSampleBuilder().Cpu(i % 100).At(ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(i)).Build());
                sampler.Tick();
            }

            var history = sampler.History;
            Assert.Equal(ResourceSampling.HistoryCapacity, history.Count);
            Assert.Equal(ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(80), history[0].TakenAtUtc);
            Assert.Equal(ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(ResourceSampling.HistoryCapacity + 79), history[^1].TakenAtUtc);
        }
    }

    [Fact]
    public void History_IsBoundedByCount_WhenTheWallClockStampsJumpBackwards()
    {
        var (sampler, probe, _) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            for (var i = 0; i < ResourceSampling.HistoryCapacity + 10; i++)
            {
                // Every other stamp is a day earlier: a clock that is stepped back and forth.
                var stamp = ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(i).AddDays(i % 2 == 0 ? 0 : -1);
                probe.Set(new ResourceSampleBuilder().At(stamp).Build());
                sampler.Tick();
            }

            Assert.Equal(ResourceSampling.HistoryCapacity, sampler.History.Count);
        }
    }

    [Fact]
    public void Evaluate_IsHandedTheWholeHistory_OldestFirst_EndingInTheNewestSample()
    {
        var seen = new List<int>();
        var (sampler, probe, _) = Create(history =>
        {
            seen.Add(history.Count);
            return Verdict(ResourceLevel.Normal);
        });
        using (sampler)
        {
            var first = new ResourceSampleBuilder().At(ResourceSampleBuilder.DefaultTakenAtUtc).Build();
            var second = new ResourceSampleBuilder().At(ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(5)).Build();
            probe.Set(first);
            sampler.Tick();
            probe.Set(second);
            sampler.Tick();

            Assert.Equal([1, 2], seen);
            Assert.Same(second, sampler.Current.Latest);
        }
    }

    [Fact]
    public void Evaluate_IsHandedTheMonotonicTimeline_NotTheSamplesWallClockStamps()
    {
        IReadOnlyList<TimelineSample>? last = null;
        var (sampler, probe, time) = Create(history =>
        {
            last = history;
            return Verdict(ResourceLevel.Normal);
        });
        using (sampler)
        {
            // Stamps that jump an hour forward and then a day back; the monotonic clock moves five seconds a read.
            DateTime[] stamps = [At(0), At(3600), At(-86400)];
            foreach (var stamp in stamps)
            {
                probe.Set(new ResourceSampleBuilder().At(stamp).Build());
                sampler.Tick();
                time.Advance(Step);
            }

            Assert.Equal([TimeSpan.Zero, Step, Step * 2], last!.Select(s => s.Timeline));
        }

        static DateTime At(int seconds) => ResourceSampleBuilder.DefaultTakenAtUtc.AddSeconds(seconds);
    }

    [Fact]
    public void Crossed_AResourceHeldInAmber_RaisesOneBanner_NotOnePerSample()
    {
        var level = ResourceLevel.Normal;
        var (sampler, _, _) = Create(_ => Verdict(level));
        var crossings = new List<ResourceCrossing>();
        sampler.Crossed += (_, e) => crossings.Add(e.Crossing);
        using (sampler)
        {
            sampler.Tick();
            level = ResourceLevel.Tight;
            for (var i = 0; i < 20; i++) sampler.Tick();

            var crossing = Assert.Single(crossings);
            Assert.Equal(ResourceKind.Cpu, crossing.Kind);
            Assert.Equal(ResourceCrossingChange.Raised, crossing.Change);
            Assert.True(crossing.ShowsBanner);
        }
    }

    [Fact]
    public void Crossed_WhenTheLoadGoesAway_ClearsTheWarningExactlyOnce()
    {
        var level = ResourceLevel.Critical;
        var (sampler, _, _) = Create(_ => Verdict(level));
        var crossings = new List<ResourceCrossing>();
        sampler.Crossed += (_, e) => crossings.Add(e.Crossing);
        using (sampler)
        {
            sampler.Tick();
            level = ResourceLevel.Normal;
            for (var i = 0; i < 10; i++) sampler.Tick();

            Assert.Equal(2, crossings.Count);
            Assert.True(crossings[0].ShowsBanner);
            Assert.Equal(ResourceCrossingChange.Cleared, crossings[1].Change);
            Assert.True(crossings[1].EndsWarning);
            Assert.False(crossings[1].ShowsBanner);
        }
    }

    [Fact]
    public void Crossed_OneBannerPerResource_TwoResourcesCrossingTogetherGiveTwo()
    {
        var (sampler, _, _) = Create(_ => Verdict(ResourceLevel.Tight, ResourceLevel.Critical));
        var crossings = new List<ResourceCrossing>();
        sampler.Crossed += (_, e) => crossings.Add(e.Crossing);
        using (sampler)
        {
            for (var i = 0; i < 5; i++) sampler.Tick();

            Assert.Equal(2, crossings.Count);
            Assert.Contains(crossings, c => c.Kind == ResourceKind.Cpu && c.ShowsBanner);
            Assert.Contains(crossings, c => c.Kind == ResourceKind.Memory && c.ShowsBanner);
        }
    }

    [Fact]
    public void Crossed_IsRaisedAfterSampled_WithCurrentAlreadyUpdated()
    {
        var (sampler, _, _) = Create(_ => Verdict(ResourceLevel.Critical));
        var order = new List<string>();
        ResourceSamplerState? currentInCrossed = null;
        sampler.Sampled += (_, _) => order.Add("sampled");
        sampler.Crossed += (_, e) =>
        {
            order.Add("crossed");
            currentInCrossed = sampler.Current;
            Assert.Same(e.State, currentInCrossed);
        };
        using (sampler)
        {
            sampler.Tick();

            Assert.Equal(["sampled", "crossed"], order);
            Assert.NotNull(currentInCrossed!.Latest);
        }
    }

    [Fact]
    public void Tick_WhenTheProbeThrows_SkipsThatRead_AndKeepsSampling()
    {
        var time = new FakeTimeProvider(ResourceSampleBuilder.DefaultTakenAtUtc);
        var probe = new ThrowOnceProbe();
        using var sampler = new ResourceSampler(probe, _ => Verdict(ResourceLevel.Normal), time);

        sampler.Start();
        Assert.Null(sampler.Current.Latest);

        time.Advance(Step);
        Assert.NotNull(sampler.Current.Latest);
        Assert.Single(sampler.History);
    }

    [Fact]
    public void Sampled_AHandlerThatThrows_DoesNotStopTheNextHandlerOrTheSampler()
    {
        var (sampler, probe, time) = Create(_ => Verdict(ResourceLevel.Normal));
        var calls = 0;
        sampler.Sampled += (_, _) => throw new InvalidOperationException("a broken subscriber");
        sampler.Sampled += (_, _) => calls++;
        using (sampler)
        {
            sampler.Start();
            time.Advance(Step);

            Assert.Equal(2, calls);
            Assert.Equal(2, probe.ReadCount);
        }
    }

    [Fact]
    public void LatestAge_IsMeasuredOnTheMonotonicClock_NotFromTheSamplesWallClockStamp()
    {
        var (sampler, probe, time) = Create(_ => Verdict(ResourceLevel.Normal));
        using (sampler)
        {
            // A stamp forty minutes in the past, as a stepped clock would produce.
            probe.Set(new ResourceSampleBuilder().At(ResourceSampleBuilder.DefaultTakenAtUtc.AddMinutes(-40)).Build());
            sampler.Tick();

            time.Advance(TimeSpan.FromSeconds(3));

            Assert.Equal(TimeSpan.FromSeconds(3), sampler.LatestAge);
        }
    }

    [Fact]
    public void Dispose_StopsTheTimer()
    {
        var (sampler, probe, time) = Create(_ => Verdict(ResourceLevel.Normal));
        sampler.Start();
        sampler.Dispose();

        time.Advance(Step * 5);

        Assert.Equal(1, probe.ReadCount);
    }

    private sealed class ThrowOnceProbe : IResourceProbe
    {
        private bool _thrown;

        public ResourceSample Read()
        {
            if (_thrown) return new ResourceSampleBuilder().Build();
            _thrown = true;
            throw new InvalidOperationException("a probe defect");
        }
    }
}
