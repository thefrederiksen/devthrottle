using CcDirector.Core.Configuration;
using CcDirector.Core.HostedAi;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.HostedAi;
using CcDirector.Gateway.Tenancy;

namespace CcDirector.Gateway.MailDesk;

/// <summary>
/// Builds the Mail Desk's model for one account the way <c>GatewayHost</c> builds the dictionary-suggestion brain:
/// the Wingman endpoint (<see cref="TranscriptionEndpointResolver.ResolveWingman"/>), its key read from the key vault,
/// the model <see cref="IncludedModelId.WingmanFast"/>, and the tag
/// <c>GatewayAiCallTags.For(tenant, AiFeature.MailDesk)</c>.
///
/// It asks the account's plan first, as the dictionary-suggestion and Mentor factories do: the Mail Desk's questions
/// are Wingman model calls (the Wingman endpoint and model), so the scope is <see cref="EntitlementScopes.Wingman"/>.
/// A self-hosted Gateway is not gated.
///
/// When the Gateway cannot ask - the plan does not allow it, no key in the vault (a self-hosted Gateway holds none for the hosted model), or a
/// hosted Gateway that cannot name the account on the call - it hands back a model whose every question fails with a
/// plain sentence saying so. The screening's rules and lists still decide; only the questions are failed, each one
/// named on its decision row, so nothing is guessed and nothing is hidden.
/// </summary>
public static class MailDeskModelFactory
{
    /// <summary>The sentence every question fails with when the vault holds no key for the hosted model.</summary>
    public const string NoKeySentence = "the Gateway holds no key for the hosted model";

    /// <summary>Build the model for <paramref name="tenant"/>.</summary>
    /// <param name="tenant">The account the calls are for, named on every call.</param>
    /// <param name="aiGate">The one gate that decides whether the hosted Gateway may make an AI call for the account.</param>
    /// <param name="readKey">Reads a key by name: <c>KeyVault.Get</c> in production. Never logged.</param>
    /// <param name="mode">The transcription mode the Wingman endpoint is resolved for.</param>
    /// <param name="http">HTTP client; tests inject one over a fake handler.</param>
    /// <param name="log">Log sink; <see cref="FileLog.Write"/> when null.</param>
    /// <param name="time">The clock the per-call deadline runs on; the system clock when null.</param>
    public static IMailDeskModel Create(
        TenantId tenant,
        TenantAiGate aiGate,
        Func<string, string?> readKey,
        TranscriptionMode mode,
        HttpClient? http = null,
        Action<string>? log = null,
        TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(aiGate);
        ArgumentNullException.ThrowIfNull(readKey);
        var write = log ?? FileLog.Write;
        try
        {
            aiGate.EnsureAllowedForTenant(tenant, EntitlementScopes.Wingman);
        }
        catch (TenantAiRefusedException ex)
        {
            var kind = ex.Decision.Verdict == TenantAiVerdict.Unknown
                ? MailDeskModelErrorKinds.PlanUnknown
                : MailDeskModelErrorKinds.PlanRefused;
            write($"[MailDeskModelFactory] Create: the AI gate did not allow the call (verdict={ex.Decision.Verdict}); every Mail Desk model question fails");
            return new UnavailableMailDeskModel(kind, ex.Message, write);
        }

        var ep = TranscriptionEndpointResolver.ResolveWingman(mode);
        var key = readKey(ep.KeyName);
        if (string.IsNullOrWhiteSpace(key))
        {
            write($"[MailDeskModelFactory] Create: no key named {ep.KeyName} in the vault; every Mail Desk model question fails");
            return new UnavailableMailDeskModel(MailDeskModelErrorKinds.NoKey, NoKeySentence, write);
        }

        AiCallTag tag;
        try
        {
            tag = GatewayAiCallTags.For(tenant, AiFeature.MailDesk);
        }
        catch (InvalidOperationException ex)
        {
            write("[MailDeskModelFactory] Create: the account cannot be named on the call; every Mail Desk model question fails");
            return new UnavailableMailDeskModel(MailDeskModelErrorKinds.NoAccountCredential, ex.Message, write);
        }

        write($"[MailDeskModelFactory] Create: model={IncludedModelId.WingmanFast.Value}, {tag}");
        return new MailDeskModel(ep.BaseUrl, key, IncludedModelId.WingmanFast, tag, http, write, time: time);
    }
}

/// <summary>The model a Gateway that cannot ask hands out: every question fails with the one sentence saying why,
/// and no call is made.</summary>
internal sealed class UnavailableMailDeskModel : IMailDeskModel
{
    private readonly string _errorKind;
    private readonly string _error;
    private readonly Action<string> _log;

    public UnavailableMailDeskModel(string errorKind, string error, Action<string> log)
    {
        _errorKind = errorKind;
        _error = error;
        _log = log;
    }

    public Task<MailDeskModelResult> AskPersonOrSpamAsync(MailDeskEmail email, CancellationToken ct = default)
        => Fail(MailDeskModel.QuestionPersonOrSpam);

    public Task<MailDeskModelResult> AskRouteAsync(MailDeskEmail email, IReadOnlyList<MailDeskRouteChoice> routes, CancellationToken ct = default)
        => Fail(MailDeskModel.QuestionRoute);

    private Task<MailDeskModelResult> Fail(string question)
    {
        _log($"[MailDeskModel] AskAsync FAILED: question={question}, errorKind={_errorKind}, no call made");
        return Task.FromResult(MailDeskModelResult.Failure(_errorKind, _error));
    }
}
