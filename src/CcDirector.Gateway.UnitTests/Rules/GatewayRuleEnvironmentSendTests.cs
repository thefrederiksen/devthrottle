using CcDirector.AgentBrain;
using CcDirector.Core.Configuration;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Rules;
using Xunit;

namespace CcDirector.Gateway.Tests.Rules;

/// <summary>
/// THE SEND SEAM AGAINST THE PRODUCTION WIRING, not against a fake of it.
///
/// The evaluator's own tests set a <see cref="RuleSendResult"/> by hand and prove what the RECORD says
/// about it. That leaves the more important question untested: does the production wiring produce the
/// right one? It did not. Everything below the seam collapsed three different events into one boolean -
/// a Director that is not connected, a Director that answered a failure, and a command nothing answered
/// at all - and the wiring then read every false as "it went out and nobody confirmed it", which the
/// record printed as text that had been typed into a session.
///
/// So these drive <see cref="GatewayRuleEnvironment"/> through a REAL <see cref="SessionVerbClient"/>
/// over a fake tunnel, one case per distinguishable event, and require the seam to keep them apart:
///
///   * no route at all - the machine is not connected. NOTHING WAS TYPED.
///   * a route, but the tunnel produced no result - the command never left this Gateway. NOTHING WAS TYPED.
///   * a route, and the Director answered a failure - the command went out and what became of it is not
///     known. The record must not claim either way.
///   * a route, and the Director answered Ok - confirmed.
/// </summary>
public sealed class GatewayRuleEnvironmentSendTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";

    /// <summary>A rule store seam that is never reached by these tests - the send is what is under test.</summary>
    private sealed class UnusedStore : IRuleReading
    {
        public IReadOnlyList<SessionRule> All() => Array.Empty<SessionRule>();

        public IReadOnlyList<SessionRuleFiring> FiringsFor(Guid ruleId) => Array.Empty<SessionRuleFiring>();

        public SessionRuleFiring RecordFiring(
            Guid ruleId, string sessionId, string screenText, string understanding, string decision,
            string reason, IEnumerable<RulePrimitiveRun> primitiveRuns, string typedText, string outcome,
            string grounding, DateTime nowUtc) =>
            throw new NotSupportedException("these tests are about the send seam, not the record.");

        public SessionRuleFiring CompleteFiring(Guid firingId, string typedText, string outcome, DateTime nowUtc) =>
            throw new NotSupportedException("these tests are about the send seam, not the record.");
    }

    private static GatewayRuleEnvironment EnvironmentWhoseTunnelAnswers(
        DirectorCommandResult? answer, bool directorIsConnected = true)
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, _, _) => Task.FromResult(answer);

        var route = directorIsConnected
            ? new Func<TenantId, string, SessionVerbClient?>(
                (_, directorId) => new SessionVerbClient(new DirectorDto { DirectorId = directorId }, send))
            : (_, _) => null;

        return new GatewayRuleEnvironment(
            new UnusedStore(),
            route,
            (_, _) => new SessionDto { SessionId = SessionId },
            (_, _, _) => Task.FromException<IAgentBrain>(
                new NotSupportedException("these tests never ask the model.")));
    }

    private static Task<RuleSendResult> Send(GatewayRuleEnvironment env) =>
        env.TypeIntoSessionAsync(Tenant, DirectorId, SessionId, "/usage-credits", CancellationToken.None);

    [Fact]
    public async Task A_director_that_is_not_connected_means_nothing_was_typed()
    {
        var result = await Send(EnvironmentWhoseTunnelAnswers(null, directorIsConnected: false));

        Assert.Equal(RuleSendOutcomes.NotSent, result.What);
        Assert.Contains("not connected", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_command_that_produced_no_result_at_all_means_nothing_was_typed()
    {
        // THE CASE THE LOWER LAYER HID. A null tunnel result means the command never left this Gateway -
        // the Director is not on the tunnel, or the Gateway refused the send before it went. The layer
        // below mapped that to the SAME false tuple as a Director that answered a refusal, so the wiring
        // above could not tell "nothing was typed" from "nobody answered", and reported the wrong one.
        var result = await Send(EnvironmentWhoseTunnelAnswers(null));

        Assert.Equal(RuleSendOutcomes.NotSent, result.What);
        Assert.NotEqual("", result.Detail);
    }

    [Fact]
    public async Task A_director_that_answered_a_failure_is_unknown_and_never_reported_as_typed()
    {
        var refused = DirectorCommandResult.Fail(DirectorCommandStatus.NotFound, "no such session on this Director");

        var result = await Send(EnvironmentWhoseTunnelAnswers(refused));

        Assert.Equal(RuleSendOutcomes.Unknown, result.What);
        Assert.Contains("no such session", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_director_that_refused_an_exited_session_is_still_unknown_for_rules()
    {
        // The send kinds now tell a definite Director refusal apart (dev reports review, High 4). Session Rules
        // deliberately record it exactly as before: unknown.
        var refused = DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "session has exited");

        var result = await Send(EnvironmentWhoseTunnelAnswers(refused));

        Assert.Equal(RuleSendOutcomes.Unknown, result.What);
        Assert.Contains("session has exited", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_director_that_answered_ok_is_confirmed()
    {
        // THE PRESENCE. A seam that answered "not sent" or "unknown" to everything would pass all three
        // assertions above while making the feature incapable of ever typing anything.
        var result = await Send(EnvironmentWhoseTunnelAnswers(DirectorCommandResult.Success()));

        Assert.Equal(RuleSendOutcomes.Confirmed, result.What);
    }
}
