using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The verdict fold - issue #2720, Phase 1 of the restart epic.
///
/// WHAT THESE TESTS ARE FOR. A capability check that has never returned no is not a check, and a check
/// whose no cannot be told from its "cannot tell" is worse than none: it certifies a machine that was
/// merely unreachable. So every state is exercised here, and the ones that must never share a message are
/// asserted to have different messages, not merely different enum values. An enum nobody renders is not a
/// distinction the reader ever sees.
///
/// THE THREE PAIRS THAT MUST NEVER COLLAPSE, each of which has cost real time:
///   * NotConnected vs NotStreamCapable - stopped talking, versus talking perfectly and too old to be
///     told anything. One fix is to get a launcher running; the other is to update one. The wrong message
///     sends a reader to examine a network connection that is demonstrably carrying heartbeats.
///   * DeclaredNothing vs NotDeclared - a reachable launcher that is silent about itself, versus a
///     launcher we never got to ask. Treating the first as "too old" is wrong but survivable; treating
///     the SECOND as "too old" certifies an unreachable machine, which is the absence-shaped failure.
///   * a plain restart vs a GUARDED one - "can it be restarted" and "will it refuse while sessions are
///     live" are two questions, and folding them together reports either the whole fleet as
///     unrestartable or a guard that nobody has made as available.
/// </summary>
public sealed class MachineRestartCapabilityTests
{
    private const string Machine = "SOREN-NORTH";
    private static readonly DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);

    private static LauncherDto Registered(string version, TimeSpan quietFor) => new()
    {
        MachineName = Machine,
        Pid = 1234,
        Version = version,
        StartedAt = Now - TimeSpan.FromHours(3),
        LastSeenAt = Now - quietFor,
    };

    private static LauncherStreamConnection Streaming(LauncherCapabilityDeclaration? declaration = null)
        => new("conn-1", declaration);

    private static LauncherCapabilityDeclaration Declares(
        bool? signalArmed = true, bool? instanceHome = false, params string[] commands)
        => new()
        {
            Commands = commands.ToList(),
            RestartSignalArmed = signalArmed,
            ServingRootIsInstanceHome = instanceHome,
            ServingRootKey = "b1706c7af60c",
        };

    private static readonly string[] EveryVerbAModernLauncherHas =
    {
        LauncherCapabilities.DirectorStart,
        LauncherCapabilities.DirectorStop,
        LauncherCapabilities.DirectorRestart,
        LauncherCapabilities.Launch,
        LauncherCapabilities.Apps,
        LauncherCapabilities.Files,
    };

    // =====================================================================================
    // The NO cases. These come first on purpose: the phase exists because the yes was never
    // in doubt and the no had never been asked for.
    // =====================================================================================

    /// <summary>
    /// THE 2026-09-06 MACHINE, WHICH IS THE WHOLE REASON THIS CODE EXISTS. Launcher 1.9.8: registered,
    /// heartbeating twelve seconds ago, and holding no command stream. Every liveness check on that
    /// machine said yes and seventeen sessions were drained before anyone found out the answer was no.
    /// </summary>
    [Fact]
    public void A_launcher_that_heartbeats_and_holds_no_stream_cannot_restart_and_is_told_to_update()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", TimeSpan.FromSeconds(12)), connection: null, Now);

        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.Equal(LauncherReach.NotStreamCapable, answer.Reach);

        // The reason must name the version, the fix, and the thing NOT to go and check.
        Assert.Contains("1.9.8", answer.Reason);
        Assert.Contains("predates the command stream", answer.Reason);
        Assert.Contains("Update the launcher", answer.Reason);
        Assert.Contains("network connection is not the problem", answer.Reason);
        Assert.Contains("12s ago", answer.Reason);
    }

    /// <summary>
    /// The other undeliverable case, and it must NOT read like the one above. A launcher that has gone
    /// quiet needs starting, not updating - and telling its owner to check the network is right here and
    /// wrong there, which is exactly why one message for both was never acceptable.
    /// </summary>
    [Fact]
    public void A_launcher_that_has_gone_silent_cannot_restart_and_says_so_differently()
    {
        var quiet = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.FromMinutes(10)), connection: null, Now);
        var tooOld = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", TimeSpan.FromSeconds(12)), connection: null, Now);

        Assert.Equal(RestartVerdict.CannotRestart, quiet.Verdict);
        Assert.Equal(LauncherReach.NotConnected, quiet.Reach);
        Assert.Contains("stopped talking to this Gateway", quiet.Reason);
        Assert.Contains("Get that launcher running again", quiet.Reason);

        // THE ASSERTION THAT MATTERS: two different faults, two different sentences. A reader acts on
        // the sentence, so equal enums with one message would be the defect this pair exists to prevent.
        Assert.NotEqual(quiet.Reason, tooOld.Reason);
        Assert.DoesNotContain("network connection is not the problem", quiet.Reason);
        Assert.DoesNotContain("predates the command stream", quiet.Reason);
    }

    /// <summary>No launcher at all is a third fix again: install one.</summary>
    [Fact]
    public void No_launcher_registered_cannot_restart_and_says_to_install_one()
    {
        var answer = MachineRestartCapability.Judge(Machine, registered: null, connection: null, Now);

        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.Equal(LauncherReach.NoLauncher, answer.Reach);
        Assert.Contains("no launcher is registered", answer.Reason);
        Assert.Contains("Install and start cc-launcher", answer.Reason);
        Assert.Equal(0, answer.QuietForSeconds);
        Assert.Null(answer.LauncherVersion);
    }

    /// <summary>
    /// THE WRONG-ROOT MACHINE - a real state observed on 2026-09-06 and invisible from every other angle.
    /// A launcher started from a shell carrying a Director's CC_DIRECTOR_ROOT inherits it and serves that
    /// Director's instance home. It registers, heartbeats, streams, declares everything and arms its
    /// signals, all filed under a root nothing else computes. Every fact below reads healthy.
    /// </summary>
    [Fact]
    public void A_launcher_serving_an_instance_home_cannot_restart_even_though_every_other_fact_is_healthy()
    {
        var declaration = Declares(signalArmed: true, instanceHome: true, EveryVerbAModernLauncherHas);

        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.FromSeconds(3)), Streaming(declaration), Now);

        // Healthy on every other measure - which is the point of the test.
        Assert.Equal(LauncherReach.Connected, answer.Reach);
        Assert.Equal(LauncherDeclarationState.Declared, answer.Declaration);
        Assert.Equal(RestartSignalState.Listening, answer.RestartSignal);
        Assert.Contains(LauncherCapabilities.DirectorRestart, answer.DeclaredCommands);

        // And still no, with the fix named.
        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.True(answer.ServingRootIsInstanceHome);
        Assert.Contains("instance home", answer.Reason);
        Assert.Contains("no CC_DIRECTOR_ROOT", answer.Reason);
        Assert.Contains("b1706c7af60c", answer.Reason);
    }

    /// <summary>
    /// The wrong-root fault is ruled on FIRST, ahead of every route. A reader acts on the first sentence,
    /// so if this ranked below the stream route it would be reported as a machine that can be restarted -
    /// with the fatal fact sitting further down a table nobody reads twice.
    /// </summary>
    [Fact]
    public void The_wrong_root_fault_outranks_an_otherwise_available_stream_route()
    {
        var healthy = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(instanceHome: false, commands: EveryVerbAModernLauncherHas)), Now);
        var wrongRoot = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(instanceHome: true, commands: EveryVerbAModernLauncherHas)), Now);

        // The ONLY difference between the two inputs is the root classification.
        Assert.Equal(RestartVerdict.CanRestart, healthy.Verdict);
        Assert.Equal(RestartVerdict.CannotRestart, wrongRoot.Verdict);
    }

    /// <summary>A launcher that declared and left the restart out is believed. That is the one case where
    /// declaring makes the answer more negative, and it should: a build saying it cannot restart a
    /// Director is the authority on that.</summary>
    [Fact]
    public void A_launcher_that_declares_no_restart_verb_cannot_restart_on_its_own_testimony()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(signalArmed: false, commands: new[] { LauncherCapabilities.Apps, LauncherCapabilities.Files })),
            Now);

        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.Contains("declares that it does not offer a Director restart", answer.Reason);
        // It shows its evidence: what the launcher DID declare.
        Assert.Contains(LauncherCapabilities.Apps, answer.Reason);
    }

    // =====================================================================================
    // The YES cases
    // =====================================================================================

    /// <summary>The ordinary healthy machine, answering from the launcher's own word.</summary>
    [Fact]
    public void A_streaming_launcher_that_declares_the_restart_can_restart()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.FromSeconds(4)),
            Streaming(Declares(commands: EveryVerbAModernLauncherHas)), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Equal(LauncherReach.Connected, answer.Reach);
        Assert.Equal(LauncherDeclarationState.Declared, answer.Declaration);
        Assert.Contains("can be restarted", answer.Reason);
        Assert.Contains(LauncherCapabilities.DirectorRestart, answer.Reason);
    }

    /// <summary>
    /// EVERY LAUNCHER IN THE FLEET TODAY. It holds a stream and predates the capability handshake, so it
    /// declares nothing - and it can still be restarted, because the stream is the delivery path and
    /// holding one is direct evidence of it. The reason says the capability is INFERRED rather than
    /// declared, which is a weaker claim, and the reader is told so rather than left to work it out.
    ///
    /// If this returned no, the query would report the entire fleet as unrestartable on the day it
    /// shipped, and the first person to see that would correctly stop trusting it.
    /// </summary>
    [Fact]
    public void A_streaming_launcher_that_declares_nothing_can_still_restart_and_the_reason_says_it_is_inferred()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.FromSeconds(4)), Streaming(declaration: null), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Equal(LauncherDeclarationState.DeclaredNothing, answer.Declaration);
        Assert.Equal(RestartSignalState.NotDeclared, answer.RestartSignal);
        Assert.Empty(answer.DeclaredCommands);
        Assert.Contains("inferred from the stream", answer.Reason);
        Assert.Contains("predates the capability handshake", answer.Reason);
    }

    /// <summary>
    /// A launcher with no stream whose signal IS armed cannot arise, and this test pins WHY rather than
    /// leaving the combination unexplored: the signal state only ever arrives inside a declaration, and a
    /// declaration only ever arrives over a stream. So a machine with no stream reports its local route
    /// as unreportable - not as unknown - and the answer is a plain no with a fix. Calling it unknown
    /// would make the 2026-09-06 machine answer "cannot tell", which is the one answer that phase exists
    /// to eliminate.
    /// </summary>
    [Fact]
    public void With_no_stream_the_local_signal_route_is_unreportable_and_the_answer_is_still_a_plain_no()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", TimeSpan.FromSeconds(5)), connection: null, Now);

        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.Equal(RestartSignalState.NotDeclared, answer.RestartSignal);
        Assert.Contains("only over the command stream it is not holding", answer.Reason);
    }

    // =====================================================================================
    // The UNKNOWN case - carried through, never collapsed
    // =====================================================================================

    /// <summary>
    /// A launcher that answered, declares no stream restart, and reports that its platform cannot say
    /// whether anything is listening for the local signal. Off Windows a lifecycle listener leaves nothing
    /// to consult, so the honest answer is that neither route can be confirmed OR ruled out.
    ///
    /// THIS IS WHY THE VERDICT IS THREE-STATE. Collapsing it to no would tell a reader to go and fix a
    /// machine that may be perfectly capable; collapsing it to yes would let a drain start on a machine
    /// that cannot come back. Neither is available, so the answer says so.
    /// </summary>
    [Fact]
    public void An_unobservable_local_signal_with_no_declared_restart_is_Unknown_and_not_a_no()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(signalArmed: null, commands: new[] { LauncherCapabilities.Apps })), Now);

        Assert.Equal(RestartVerdict.Unknown, answer.Verdict);
        Assert.Equal(RestartSignalState.NotObservable, answer.RestartSignal);
        Assert.Contains("cannot tell whether", answer.Reason);
        Assert.Contains("cannot be observed on that", answer.Reason);
    }

    /// <summary>
    /// And an unobservable signal does NOT make a machine with a working stream route unknown. The stream
    /// is observable on every platform; a Unix launcher that streams and declares the restart can be
    /// restarted, and reporting it as unknown would be a needless refusal on the platform where nobody
    /// would notice the check had stopped meaning anything.
    /// </summary>
    [Fact]
    public void An_unobservable_local_signal_does_not_spoil_a_working_stream_route()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(signalArmed: null, commands: EveryVerbAModernLauncherHas)), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Equal(RestartSignalState.NotObservable, answer.RestartSignal);
    }

    /// <summary>
    /// The local signal alone is enough for a yes - it is the route a drained Director uses to ask its own
    /// launcher, with no network at all, and it is the route Phase 6 is built on. A build offering no
    /// stream restart but listening locally can still be restarted, and by the route that works when the
    /// Gateway does not.
    /// </summary>
    [Fact]
    public void A_listening_local_signal_is_enough_for_a_yes_even_with_no_stream_restart_declared()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(signalArmed: true, commands: new[] { LauncherCapabilities.Apps })), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Contains("listening for the local restart signal", answer.Reason);
        Assert.Contains("with no network at all", answer.Reason);
    }

    // =====================================================================================
    // The guarded restart - Phase 2's guarantee, asked BEFORE anything is sent
    // =====================================================================================

    /// <summary>
    /// THE SEAM PHASE 2 DEFERRED. Its onlyIfEmpty flag is read by the launcher, so a launcher that
    /// predates the flag ignores it, restarts a half-drained Director and reports success - which makes
    /// the answer detection rather than prevention. Prevention is this: the launcher declares nothing, so
    /// the guard is UNKNOWN, and a drain refuses to send one at all.
    /// </summary>
    [Fact]
    public void A_launcher_that_declares_nothing_has_an_Unknown_guard_and_the_reason_says_do_not_send_one()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero), Streaming(declaration: null), Now);

        // Restartable, and NOT safely restartable mid-drain. Both true; two sentences.
        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Equal(CapabilityState.Unknown, answer.GuardedRestart);
        Assert.Contains("declares no capabilities at all", answer.GuardedRestartReason);
        Assert.Contains("NOT the same as a launcher that answered and said no", answer.GuardedRestartReason);
    }

    /// <summary>
    /// Unknown-because-nothing-was-declared and Unknown-because-we-never-asked are the SAME enum and must
    /// not be the same sentence. One is reachable and needs updating; the other is unreachable, and
    /// telling its owner to update a launcher they cannot reach is the absence-shaped mistake.
    /// </summary>
    [Fact]
    public void The_two_Unknown_guards_share_an_enum_and_never_share_a_sentence()
    {
        var declaredNothing = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero), Streaming(declaration: null), Now);
        var neverAsked = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", TimeSpan.FromSeconds(5)), connection: null, Now);

        Assert.Equal(CapabilityState.Unknown, declaredNothing.GuardedRestart);
        Assert.Equal(CapabilityState.Unknown, neverAsked.GuardedRestart);
        Assert.NotEqual(declaredNothing.GuardedRestartReason, neverAsked.GuardedRestartReason);

        Assert.Equal(LauncherDeclarationState.DeclaredNothing, declaredNothing.Declaration);
        Assert.Equal(LauncherDeclarationState.NotDeclared, neverAsked.Declaration);
        Assert.Contains("it holds no command stream, so it has declared nothing", neverAsked.GuardedRestartReason);
    }

    /// <summary>Declared, and the condition is absent: a definite no, which is different again from either
    /// unknown. This launcher WILL restart a Director that still has live sessions on it.</summary>
    [Fact]
    public void A_launcher_that_declares_verbs_but_not_the_condition_has_an_Unavailable_guard()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(commands: EveryVerbAModernLauncherHas)), Now);

        Assert.Equal(CapabilityState.Unavailable, answer.GuardedRestart);
        Assert.Contains("does NOT offer a guarded restart", answer.GuardedRestartReason);
        Assert.Contains("would go ahead even with live sessions", answer.GuardedRestartReason);
    }

    /// <summary>And when a launcher does declare it, the guard is available. This is the state Phase 2's
    /// flag reaches once a launcher build honours it.</summary>
    [Fact]
    public void A_launcher_that_declares_the_condition_has_an_Available_guard()
    {
        var commands = EveryVerbAModernLauncherHas
            .Append(LauncherCapabilities.DirectorRestartOnlyIfEmpty).ToArray();

        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.1.0", TimeSpan.Zero), Streaming(Declares(commands: commands)), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        Assert.Equal(CapabilityState.Available, answer.GuardedRestart);
        Assert.Contains("will refuse a restart while its", answer.GuardedRestartReason);
        Assert.Contains("say how many", answer.GuardedRestartReason);
    }

    // =====================================================================================
    // Mechanics that would fail silently
    // =====================================================================================

    /// <summary>
    /// A LIVE STREAM OUTRANKS THE REGISTRATION ROW, and it has to. Delivery rides the stream and nothing
    /// else, so a launcher holding one is reachable even in the window before its registration lands or
    /// after the row has been swept for a missed heartbeat. Reading the row first would report a machine
    /// that can be restarted RIGHT NOW as unreachable, on the strength of presence metadata no command
    /// travels over.
    /// </summary>
    [Fact]
    public void A_stream_with_no_registration_row_is_Connected_and_can_restart()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, registered: null, Streaming(Declares(commands: EveryVerbAModernLauncherHas)), Now);

        Assert.Equal(LauncherReach.Connected, answer.Reach);
        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
        // The version is honestly unknown - it lives on the registration row, not on the stream.
        Assert.Null(answer.LauncherVersion);
        Assert.Contains("version unknown", answer.Reason);
    }

    /// <summary>
    /// The heartbeat window is the boundary between the two undeliverable refusals, so it is asserted on
    /// both sides of itself. A mutation to the comparison flips which fix a reader is sent to.
    /// </summary>
    [Fact]
    public void The_heartbeat_window_decides_which_refusal_and_is_asserted_on_both_sides()
    {
        var justInside = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", LauncherRegistry.HeartbeatTimeout - TimeSpan.FromSeconds(1)),
            connection: null, Now);
        var justOutside = MachineRestartCapability.Judge(
            Machine, Registered("1.9.8", LauncherRegistry.HeartbeatTimeout + TimeSpan.FromSeconds(1)),
            connection: null, Now);

        Assert.Equal(LauncherReach.NotStreamCapable, justInside.Reach);
        Assert.Equal(LauncherReach.NotConnected, justOutside.Reach);
    }

    /// <summary>
    /// Capability tokens are compared case-insensitively. A token is an identifier one process writes and
    /// another reads; a capability missed over letter case would report a launcher that offers a restart
    /// as one that does not, and the launcher would then be told to update to a build it is already on.
    /// </summary>
    [Fact]
    public void Declared_tokens_are_matched_regardless_of_case()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero),
            Streaming(Declares(commands: new[] { "Director/Restart" })), Now);

        Assert.Equal(RestartVerdict.CanRestart, answer.Verdict);
    }

    /// <summary>
    /// A declaration whose command list is present but EMPTY is a launcher that answered and named
    /// nothing - Declared, not DeclaredNothing. It is a real difference: this build spoke, so its silence
    /// about the restart verb is testimony, and the answer is a no rather than the inferred yes a
    /// pre-handshake launcher gets.
    /// </summary>
    [Fact]
    public void An_empty_command_list_is_Declared_and_is_believed_rather_than_inferred_around()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.Zero), Streaming(Declares(signalArmed: false)), Now);

        Assert.Equal(LauncherDeclarationState.Declared, answer.Declaration);
        Assert.Equal(RestartVerdict.CannotRestart, answer.Verdict);
        Assert.Contains("it declares: nothing", answer.Reason);
    }

    /// <summary>Clock skew reports an age of zero rather than a negative one. A heartbeat from the future
    /// is a clock problem, and "-4s ago" in a refusal reads as a broken instrument.</summary>
    [Fact]
    public void A_heartbeat_stamped_in_the_future_reports_zero_seconds_not_a_negative_age()
    {
        var answer = MachineRestartCapability.Judge(
            Machine, Registered("2.0.6", TimeSpan.FromSeconds(-30)), connection: null, Now);

        Assert.Equal(0, answer.QuietForSeconds);
    }

    /// <summary>The machine name is echoed exactly as the caller wrote it, because the caller has to be
    /// able to tell which machine an answer is about when several are asked in one pass.</summary>
    [Fact]
    public void The_machine_name_is_echoed_as_the_caller_wrote_it()
    {
        var answer = MachineRestartCapability.Judge(
            "MiXeD-CaSe-Box", Registered("2.0.6", TimeSpan.Zero), connection: null, Now);

        Assert.Equal("MiXeD-CaSe-Box", answer.Machine);
        Assert.Contains("MiXeD-CaSe-Box", answer.Reason);
    }
}
