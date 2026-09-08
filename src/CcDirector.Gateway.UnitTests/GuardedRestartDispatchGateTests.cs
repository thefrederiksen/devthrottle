using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The last gate before a guarded restart is dispatched - issue #2725, from the first review round. A
/// guarded restart goes only to a connection that declared the guard, and the same read that decides
/// this is the read that names the connection, so no other launcher can be substituted in between.
/// </summary>
public sealed class GuardedRestartDispatchGateTests
{
    private static LauncherCommand Guarded() => new() { Verb = "director/restart", OnlyIfEmpty = true };
    private static LauncherCommand Plain() => new() { Verb = "director/restart", OnlyIfEmpty = false };

    private static LauncherStreamConnection Declaring(params string[] commands)
        => new("conn-1", new LauncherCapabilityDeclaration { Commands = commands.ToList() });

    [Fact]
    public void A_connection_that_declared_the_guard_receives_the_guarded_restart()
        => Assert.Null(GuardedRestartDispatchGate.Refusal(Guarded(),
            Declaring(LauncherCapabilities.DirectorRestart, LauncherCapabilities.DirectorRestartOnlyIfEmpty)));

    [Fact]
    public void The_token_is_matched_without_regard_to_case_or_padding()
        => Assert.Null(GuardedRestartDispatchGate.Refusal(Guarded(), Declaring(" Director/Restart:Only-If-Empty ")));

    [Fact]
    public void A_connection_that_declared_nothing_is_refused_before_dispatch()
    {
        var refusal = GuardedRestartDispatchGate.Refusal(Guarded(), new LauncherStreamConnection("conn-old", null));
        Assert.NotNull(refusal);
        Assert.Contains("declared no capabilities", refusal);
        Assert.Contains("Nothing was done", refusal);
    }

    [Fact]
    public void A_connection_that_declared_a_list_without_the_guard_is_refused_naming_what_it_declared()
    {
        var refusal = GuardedRestartDispatchGate.Refusal(Guarded(), Declaring(LauncherCapabilities.DirectorRestart, LauncherCapabilities.Apps));
        Assert.NotNull(refusal);
        Assert.Contains("director/restart, apps", refusal);
        Assert.Contains(LauncherCapabilities.DirectorRestartOnlyIfEmpty, refusal);
    }

    [Fact]
    public void No_connection_is_a_refusal_too()
        => Assert.NotNull(GuardedRestartDispatchGate.Refusal(Guarded(), null));

    [Fact]
    public void An_unguarded_restart_is_not_gated_here_whatever_the_launcher_declared()
    {
        // The control: the gate is about the CONDITION. An ordinary restart to an old launcher is what it
        // has always been - the tray button and the staged update depend on it.
        Assert.Null(GuardedRestartDispatchGate.Refusal(Plain(), new LauncherStreamConnection("conn-old", null)));
        Assert.Null(GuardedRestartDispatchGate.Refusal(Plain(), null));
    }
}
