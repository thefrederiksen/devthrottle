using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Sessions;
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
/// A supervisor's recovery "continue" and a Session Rule firing reach the Director as ordinary prompts, and unless told
/// otherwise it stamps each as the owner's turn - which the Gateway reads as the owner answering a voice session. The
/// Director may hold such a prompt queued for minutes, so nothing on the Gateway can tell its stamp from his afterwards.
/// So each is labelled at the source: agent-driven, which the Director never stamps, with framework provenance. These
/// drive both senders through a real <see cref="SessionVerbClient"/> over a fake tunnel and read the prompt as it went
/// onto the wire.
/// </summary>
public sealed class VoiceAutomaticPromptSendersTests : IDisposable
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string DirectorId = "director-1";
    private const string SessionId = "sid-1";
    private readonly GatewayDbTestHarness _data = new();

    public void Dispose() => _data.Dispose();

    /// <summary>A tunnel that keeps every prompt it is handed and answers every command Ok.</summary>
    private static Func<TenantId, string, SessionVerbClient?> Route(List<PromptRequest> sent)
    {
        DirectorCommandRouter.SendDirectorCommandAsync send = (_, cmd, _) =>
        {
            if (cmd.Verb == "prompt")
                sent.Add(JsonSerializer.Deserialize<PromptRequest>(cmd.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
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

    private static void AssertLabelledAsTheGateway(PromptRequest prompt, string text)
    {
        Assert.Equal(text, prompt.Text);
        Assert.True(prompt.AgentDriven, "a prompt the Gateway wrote must reach the Director as not the owner's, or it is stamped as his turn");
        Assert.NotNull(prompt.Provenance);
        Assert.Equal(SubmissionRoutes.Framework, prompt.Provenance!.Route);
        Assert.Equal(SubmissionIdentityKinds.Framework, prompt.Provenance.IdentityKind);
    }

    [Fact]
    public async Task TypeIntoSessionAsync_ARuleFiring_ReachesTheDirectorAsTheGatewaysOwnText()
    {
        var sent = new List<PromptRequest>();
        var env = new GatewayRuleEnvironment(new UnusedStore(), Route(sent), (_, _) => new SessionDto { SessionId = SessionId }, NoBrain);

        await env.TypeIntoSessionAsync(Tenant, DirectorId, SessionId, "yes", CancellationToken.None);

        AssertLabelledAsTheGateway(Assert.Single(sent), "yes");
    }

    [Fact]
    public async Task SendContinueAsync_ARecoveryContinue_ReachesTheDirectorAsTheGatewaysOwnText()
    {
        var sent = new List<PromptRequest>();
        var env = new GatewaySupervisorEnvironment(
            new TenantSettingsResolver(new TenantSettingsStore(_data.Open())), Route(sent), (_, _) => "WaitingForInput", NoBrain);

        await env.SendContinueAsync(Tenant, DirectorId, SessionId, CancellationToken.None);

        AssertLabelledAsTheGateway(Assert.Single(sent), SessionSupervisor.ContinueText);
    }
}
