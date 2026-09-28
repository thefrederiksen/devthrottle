using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Rules;
using CcDirector.Gateway.Settings;
using CcDirector.Gateway.Supervision;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// Voice mode auto-off, step 2 (review finding): the Gateway's OWN prompts must not be counted as the owner answering.
///
/// A supervisor's recovery "continue" and a Session Rule firing both reach the Director as ordinary prompts, and it
/// stamps each as the owner's turn. So each sender says so, before it types, through the hook the host wires to the
/// listening ledger. These drive both through a real <see cref="SessionVerbClient"/> over a fake tunnel and pin that
/// the notice is given, for the right session, before the prompt leaves.
/// </summary>
public sealed class VoiceAutomaticPromptSendersTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";
    private readonly GatewayDbTestHarness _data = new();

    public void Dispose() => _data.Dispose();

    /// <summary>A tunnel that records the order of events and answers every command Ok.</summary>
    private static Func<TenantId, string, SessionVerbClient?> Route(List<string> events)
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, cmd, _) =>
        {
            events.Add($"sent {cmd.Verb}");
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
        };
        return (_, directorId) => new SessionVerbClient(new DirectorDto { DirectorId = directorId }, send);
    }

    private static Task<IAgentBrain> NoBrain(TenantId t, Core.Configuration.WingmanModelRole r, string f, CancellationToken c) =>
        Task.FromException<IAgentBrain>(new NotSupportedException("these tests never ask the model."));

    private sealed class UnusedStore : IRuleReading
    {
        public IReadOnlyList<SessionRule> All() => Array.Empty<SessionRule>();
        public IReadOnlyList<SessionRuleFiring> FiringsFor(Guid ruleId) => Array.Empty<SessionRuleFiring>();
        public SessionRuleFiring RecordFiring(Guid ruleId, string sessionId, string screenText, string understanding, string decision,
            string reason, IEnumerable<RulePrimitiveRun> primitiveRuns, string typedText, string outcome, string grounding, DateTime nowUtc) =>
            throw new NotSupportedException();
        public SessionRuleFiring CompleteFiring(Guid firingId, string typedText, string outcome, DateTime nowUtc) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task TypeIntoSessionAsync_ARuleFiring_SaysItIsTheGatewayTypingBeforeItTypes()
    {
        var events = new List<string>();
        var env = new GatewayRuleEnvironment(new UnusedStore(), Route(events), (_, _) => new SessionDto { SessionId = SessionId }, NoBrain,
            onAutomaticPrompt: (t, sid) => events.Add($"automatic {t.Value} {sid}"));

        await env.TypeIntoSessionAsync(Tenant, DirectorId, SessionId, "yes", CancellationToken.None);

        Assert.Equal($"automatic {Tenant.Value} {SessionId}", events[0]);
        Assert.Contains(events, e => e.StartsWith("sent ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task SendContinueAsync_ARecoveryContinue_SaysItIsTheGatewayTypingBeforeItTypes()
    {
        var events = new List<string>();
        var env = new GatewaySupervisorEnvironment(
            new TenantSettingsResolver(new TenantSettingsStore(_data.Open())), Route(events), (_, _) => "WaitingForInput", NoBrain,
            onAutomaticPrompt: (t, sid) => events.Add($"automatic {t.Value} {sid}"));

        await env.SendContinueAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        Assert.Equal($"automatic {Tenant.Value} {SessionId}", events[0]);
        Assert.Contains(events, e => e.StartsWith("sent ", StringComparison.Ordinal));
    }
}
