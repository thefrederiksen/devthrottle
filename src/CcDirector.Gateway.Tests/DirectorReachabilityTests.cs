using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Reachability circuit-breaker on <see cref="DirectorRegistry"/>: a Director that fails repeated
/// fleet probes stops being probed (so it no longer costs a timeout per poll), and a fresh
/// registration clears the breaker. See DIRECTOR_LIVENESS_PLAN.md.
/// </summary>
public sealed class DirectorReachabilityTests
{
    [Fact]
    public void ShouldProbe_UnknownDirector_ReturnsTrue()
    {
        var reg = new DirectorRegistry();
        Assert.True(reg.ShouldProbe("never-seen"));
    }

    [Fact]
    public void Breaker_OpensAtThreshold_AndResetsOnReachable()
    {
        var reg = new DirectorRegistry();
        const string id = "dir-1";

        // Below the threshold the Director is still probed.
        for (var i = 0; i < DirectorRegistry.MaxConsecutiveFailures - 1; i++)
            reg.RecordUnreachable(id, "timeout");
        Assert.True(reg.ShouldProbe(id));

        // The failure that hits the threshold opens the circuit: skip it from then on.
        reg.RecordUnreachable(id, "timeout");
        Assert.False(reg.ShouldProbe(id));
        Assert.Equal("timeout", reg.LastUnreachableError(id));

        // A successful probe clears the breaker.
        reg.RecordReachable(id);
        Assert.True(reg.ShouldProbe(id));
    }

    [Fact]
    public void Upsert_ClearsBreaker()
    {
        var reg = new DirectorRegistry();
        const string id = "dir-2";

        for (var i = 0; i < DirectorRegistry.MaxConsecutiveFailures; i++)
            reg.RecordUnreachable(id, "timeout");
        Assert.False(reg.ShouldProbe(id));

        // Re-registering (possibly with a corrected endpoint) gives a clean slate.
        reg.Upsert(new DirectorRegistrationRequest { DirectorId = id, TailnetEndpoint = "https://host:7879" });
        Assert.True(reg.ShouldProbe(id));
    }
}
