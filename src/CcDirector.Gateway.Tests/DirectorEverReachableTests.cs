using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The was-ever-reachable bit on <see cref="DirectorRegistry"/> (issue #197): it is what
/// turns the generic "unreachable (timeout; cooling down)" into the actionable "endpoint
/// never answered since registration - check Tailscale Serve on that machine". It must
/// SURVIVE the evict/410/re-register flap cycle (same live process, same structural
/// problem) but reset when the process is genuinely gone.
/// </summary>
public sealed class DirectorEverReachableTests
{
    [Fact]
    public void NeverProbed_IsNotEverReachable()
    {
        var reg = new DirectorRegistry();
        Assert.False(reg.WasEverReachable("unknown"));
    }

    [Fact]
    public void RecordReachable_SetsTheBit()
    {
        var reg = new DirectorRegistry();
        reg.RecordReachable("dir-1");
        Assert.True(reg.WasEverReachable("dir-1"));
    }

    [Fact]
    public void Bit_SurvivesUpsert()
    {
        // The unreachable-evict cycle re-registers the SAME live process every ~3.5 min;
        // Upsert clears the probe breaker (clean slate for a corrected endpoint) but must
        // NOT erase the fact that this Director once answered.
        var reg = new DirectorRegistry();
        const string id = "dir-2";
        reg.Upsert(new DirectorRegistrationRequest { DirectorId = id, TailnetEndpoint = "https://host:7879" });
        reg.RecordReachable(id);

        reg.Upsert(new DirectorRegistrationRequest { DirectorId = id, TailnetEndpoint = "https://host:7879" });

        Assert.True(reg.WasEverReachable(id));
    }

    [Fact]
    public void Bit_ClearedByGracefulRemove()
    {
        var reg = new DirectorRegistry();
        const string id = "dir-3";
        reg.Upsert(new DirectorRegistrationRequest { DirectorId = id, TailnetEndpoint = "https://host:7879" });
        reg.RecordReachable(id);

        Assert.True(reg.Remove(id));

        // A future registration under the same id is a new process: blank slate.
        Assert.False(reg.WasEverReachable(id));
    }
}
