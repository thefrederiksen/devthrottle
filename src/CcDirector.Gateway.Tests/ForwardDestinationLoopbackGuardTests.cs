using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #457/#460: the Gateway must never dial a loopback endpoint to reach a Director on a
/// DIFFERENT machine (that would hit the Gateway itself - the "reconnecting to 127.0.0.1" bug).
/// <see cref="SessionWsProxyEndpoints.ForwardDestination"/> encodes that policy: a routable
/// endpoint is always usable; a loopback endpoint is usable ONLY for a same-machine Director.
/// </summary>
public sealed class ForwardDestinationLoopbackGuardTests
{
    private static string ThisMachine => Environment.MachineName;
    private const string OtherMachine = "some-other-box-9e7f";

    [Fact]
    public void Routable_tailnet_endpoint_is_always_used()
    {
        var d = new DirectorDto
        {
            DirectorId = "d1",
            MachineName = OtherMachine,
            TailnetEndpoint = "https://laptop.tail0123.ts.net:7879",
            ControlEndpoint = "https://laptop.tail0123.ts.net:7879",
        };
        Assert.Equal("https://laptop.tail0123.ts.net:7879", SessionWsProxyEndpoints.ForwardDestination(d));
    }

    [Fact]
    public void Routable_lan_endpoint_is_always_used()
    {
        var d = new DirectorDto
        {
            DirectorId = "d1",
            MachineName = OtherMachine,
            TailnetEndpoint = "http://192.168.1.42:7879",
            ControlEndpoint = "http://192.168.1.42:7879",
        };
        Assert.Equal("http://192.168.1.42:7879", SessionWsProxyEndpoints.ForwardDestination(d));
    }

    [Fact]
    public void Loopback_endpoint_for_a_remote_director_is_refused()
    {
        // The exact bug: a remote Director whose only endpoint is loopback. Dialing it from the
        // Gateway hits the Gateway itself, never the Director. -> null = "no reachable endpoint".
        var d = new DirectorDto
        {
            DirectorId = "d1",
            MachineName = OtherMachine,
            TailnetEndpoint = "",
            ControlEndpoint = "http://127.0.0.1:7878",
        };
        Assert.Null(SessionWsProxyEndpoints.ForwardDestination(d));
    }

    [Fact]
    public void Loopback_endpoint_for_a_same_machine_director_is_allowed()
    {
        // Same-machine FSW-discovered Director: loopback is the correct, fast path.
        var d = new DirectorDto
        {
            DirectorId = "d1",
            MachineName = ThisMachine,
            ControlEndpoint = "http://127.0.0.1:7884",
        };
        Assert.Equal("http://127.0.0.1:7884", SessionWsProxyEndpoints.ForwardDestination(d));
    }

    [Fact]
    public void Loopback_endpoint_with_unknown_machine_is_allowed_for_back_compat()
    {
        // Historically only same-machine FSW discovery produced a loopback endpoint, and it left
        // MachineName set; an empty name is treated as same-machine so old entries do not regress.
        var d = new DirectorDto
        {
            DirectorId = "d1",
            MachineName = "",
            ControlEndpoint = "http://127.0.0.1:7884",
        };
        Assert.Equal("http://127.0.0.1:7884", SessionWsProxyEndpoints.ForwardDestination(d));
    }

    [Fact]
    public void No_endpoint_at_all_is_null()
    {
        var d = new DirectorDto { DirectorId = "d1", MachineName = OtherMachine };
        Assert.Null(SessionWsProxyEndpoints.ForwardDestination(d));
    }
}
