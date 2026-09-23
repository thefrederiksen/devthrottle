using System.Net.Http;
using System.Text.RegularExpressions;

namespace CcDirector.Core.HostedAi;

/// <summary>
/// What a hosted AI call is FOR, and - on the hosted Gateway - which account it is for.
///
/// Every hosted AI call (chat, speech, transcription) goes through the DevThrottle API, which records one
/// usage row per call. That row used to carry the model and the key's owner and nothing else, so a dozen
/// features served by the same model could not be told apart, and the hosted Gateway - which calls with ONE
/// key for every tenant - put every account's usage on the key's owner. This tag rides the request as two
/// headers the API records:
///
/// <list type="bullet">
/// <item><see cref="FeatureHeader"/>: the feature name (<see cref="AiFeature"/>). Any caller may send it.</item>
/// <item><see cref="AccountHeader"/>: the hosted tenant the work was done for, sent together with the
/// Gateway's service credential in <see cref="ServiceTokenHeader"/>. The API refuses an account from anyone
/// who cannot present that credential, so a user's own key is always recorded as that user.</item>
/// </list>
///
/// The credential is NEVER logged: this type has no public accessor for it and <see cref="ToString"/> leaves
/// it out (security rule DT-05).
/// </summary>
public sealed class AiCallTag
{
    /// <summary>The header naming the feature.</summary>
    public const string FeatureHeader = "X-DevThrottle-Feature";

    /// <summary>The header naming the account (a hosted tenant id). Only ever sent with the service token.</summary>
    public const string AccountHeader = "X-DevThrottle-Account";

    /// <summary>The Gateway service credential header - the same one the tenant-addressed owner email uses.</summary>
    public const string ServiceTokenHeader = Account.AccountNotifyByTenantClient.ServiceTokenHeader;

    // The API's own rule, mirrored so a bad name fails HERE, at the call site, and not as a 400 from the API.
    private static readonly Regex FeaturePattern = new("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant);

    private readonly string? _serviceToken;

    /// <param name="feature">One of the <see cref="AiFeature"/> names.</param>
    /// <param name="accountTenantId">The hosted tenant the work is for; null when the key's own account is
    /// the right one (the Director, a self-hosted Gateway).</param>
    /// <param name="serviceToken">The Gateway service credential. Required when an account is named: an
    /// account without it would be refused by the API, so it is refused here first.</param>
    public AiCallTag(string feature, string? accountTenantId = null, string? serviceToken = null)
    {
        if (feature is null || !FeaturePattern.IsMatch(feature))
            throw new ArgumentException(
                $"'{feature}' is not a valid AI feature name: lower-case letters, digits, '.', '_' or '-', at most 64 characters.",
                nameof(feature));
        if (!string.IsNullOrWhiteSpace(accountTenantId) && string.IsNullOrWhiteSpace(serviceToken))
            throw new ArgumentException(
                "An account can only be named together with the Gateway service credential.", nameof(serviceToken));
        Feature = feature;
        AccountTenantId = string.IsNullOrWhiteSpace(accountTenantId) ? null : accountTenantId.Trim();
        _serviceToken = AccountTenantId is null ? null : serviceToken!.Trim();
    }

    /// <summary>The feature name sent in <see cref="FeatureHeader"/>.</summary>
    public string Feature { get; }

    /// <summary>The tenant sent in <see cref="AccountHeader"/>, or null when none is named.</summary>
    public string? AccountTenantId { get; }

    /// <summary>The same account, a different feature.</summary>
    public AiCallTag WithFeature(string feature) => new(feature, AccountTenantId, _serviceToken);

    /// <summary>Put the tag on one outgoing request.</summary>
    public void ApplyTo(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Headers.Remove(FeatureHeader);
        request.Headers.TryAddWithoutValidation(FeatureHeader, Feature);
        if (AccountTenantId is null) return;
        request.Headers.Remove(AccountHeader);
        request.Headers.Remove(ServiceTokenHeader);
        request.Headers.TryAddWithoutValidation(AccountHeader, AccountTenantId);
        request.Headers.TryAddWithoutValidation(ServiceTokenHeader, _serviceToken);
    }

    /// <summary>Log-safe: the feature, and only WHETHER an account is named - never the credential, never the
    /// raw tenant id.</summary>
    public override string ToString() => $"feature={Feature}, account={(AccountTenantId is null ? "key-owner" : "tenant")}";
}

/// <summary>
/// The feature names every hosted AI call carries. One name per call site; the daily cost report groups by
/// them, so a new call site gets a new name here rather than borrowing one.
/// </summary>
public static class AiFeature
{
    /// <summary>Voice mode: the spoken summary of a finished turn (<c>WingmanTranslator.TranslateWithAsync</c>).</summary>
    public const string WingmanTranslate = "wingman.translate";
    /// <summary>A question put straight to the Wingman (<c>AskDirectAsync</c>).</summary>
    public const string WingmanAsk = "wingman.ask";
    /// <summary>A question about DevThrottle itself (<c>AskAboutDevThrottleAsync</c>).</summary>
    public const string WingmanAskDevThrottle = "wingman.ask-devthrottle";
    /// <summary>Reading a menu off the screen (<c>DetectMenuAsync</c>).</summary>
    public const string WingmanMenuDetect = "wingman.menu-detect";
    /// <summary>Mapping a spoken answer onto a menu choice (<c>MapChoiceAsync</c>).</summary>
    public const string WingmanMenuChoice = "wingman.menu-choice";
    /// <summary>The judge that reads every turn end (<c>TurnVerdictJudge</c>).</summary>
    public const string TurnVerdict = "turn-verdict";
    /// <summary>The minimal call that proves the turn-verdict model host is answering after a timeout.</summary>
    public const string TurnVerdictProbe = "turn-verdict.probe";
    /// <summary>Supervision.</summary>
    public const string Supervision = "supervision";
    /// <summary>Session rules.</summary>
    public const string Rules = "rules";
    /// <summary>The nightly dictionary-suggestion screening.</summary>
    public const string DictionarySuggestions = "dictionary-suggestions";
    /// <summary>The session-history summaries.</summary>
    public const string SessionHistorySummary = "session-history-summary";
    /// <summary>The Settings "test this model" round trip.</summary>
    public const string AiModelsTest = "ai-models.test";
    /// <summary>The Wingman's spoken reply, produced automatically.</summary>
    public const string VoiceSpeech = "voice.speech";
    /// <summary>The Wingman's spoken reply, produced by the generate button.</summary>
    public const string VoiceSpeechManual = "voice.speech-manual";
    /// <summary>The dictionary-correction judge on a dictation.</summary>
    public const string DictationJudge = "dictation.judge";
    /// <summary>The same judge driven by the text-in cleanup evaluation route.</summary>
    public const string DictationJudgeEvaluation = "dictation.judge-evaluation";
    /// <summary>The Director's own desktop speech playback.</summary>
    public const string DirectorSpeech = "director.speech";
    /// <summary>The Director's brief condenser.</summary>
    public const string DirectorBrief = "director.brief";

    /// <summary>Transcription, per surface: <c>transcription.dictation</c>, <c>transcription.voice</c>, ...
    /// The surface is the source the transcription service already stamps on every stored transcript;
    /// a caller that names none is <c>transcription.gateway</c>.</summary>
    public static string Transcription(string? source)
    {
        var s = string.IsNullOrWhiteSpace(source) ? "gateway" : source.Trim().ToLowerInvariant();
        return "transcription." + s;
    }
}
