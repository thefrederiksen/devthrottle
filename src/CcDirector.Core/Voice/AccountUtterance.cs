using System.Net.Http.Headers;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Speech;

namespace CcDirector.Core.Voice;

/// <summary>
/// HOW THE DESKTOP GETS AN UTTERANCE (issue #1031).
///
/// The desktop app speaks - the briefing and the wingman's answer - and it was the last place in the product
/// that decided for itself how. It resolved its voice from the process-global <c>config.json</c> value and took
/// no account at all, so an account set to French was read aloud by whatever voice the machine held: in
/// practice the English default. The words were right. The voice was wrong. Nothing errored.
///
/// It does not decide any more. It ASKS. The Gateway already owns this decision in one place - one resolver
/// call folds the account's language and its chosen voice into a single answer, and every Gateway speech path
/// reads it - so the desktop reads that same answer over
/// <c>GET /gateway/spoken-language</c> and packages it into the SAME <see cref="SpokenUtterance"/> type the
/// Gateway builds. One contract, one decider, two runtimes: the desktop is a sink like the others.
///
/// IT PRODUCES A VOICE AND NEVER A MODEL. The desktop's engine is resolved separately, exactly as it always
/// was, with no knowledge of any language. A language selects the voice inside the one engine that already
/// serves English; a language selecting an ENGINE is what got this feature reverted
/// (devthrottle_internal#547), which is why nothing on this path can carry one.
///
/// TWO ABSENCES THAT MUST NEVER LOOK ALIKE, and collapsing them into one null was a real defect (client audit,
/// finding 1).
///
///   - NO ACCOUNT. A Director with no Gateway configured has no account, so there is no per-account language to
///     have. The machine's own configured voice is the only truth, and speaking with it is the complete and
///     correct answer for that topology.
///   - COULD NOT ASK. A Director that IS attached, whose lookup timed out, was refused, or answered partially.
///     Here the account HAS a language and we do not know what it is.
///
/// This returned null for both, so the caller could not tell them apart and fell back to the machine voice in the
/// second case too. The argument that made that look safe was that speech also needs the account KEY from the
/// same Gateway, so the two would fail together. THAT ARGUMENT IS WRONG: the key is fetched by a different route
/// and CACHED IN MEMORY (<see cref="HostedAiKeyResolver"/>). The real sequence is a French account speaking once
/// while healthy, caching its key, and then having its next sentence read aloud in the machine's English voice
/// because one lookup timed out. Silently. That is the reverted bug in a new place.
///
/// So the two states are different values and a caller must handle them differently: speak for the first, REFUSE
/// for the second. A wrong-language voice is not a degraded success - it is the failure this mission exists to
/// remove, and the desktop already has somewhere to report a failure to.
/// </summary>
public class AccountUtterance
{
    private static readonly HttpClient SharedHttp = new() { Timeout = TimeSpan.FromSeconds(5) };

    private readonly Func<GatewayConfig> _gatewayProvider;
    private readonly HttpClient _http;

    /// <param name="gatewayProvider">Supplies the current gateway configuration on demand. Read fresh on every
    ///  call rather than snapshotted, so a Director that booted standalone and later had a gateway address
    ///  written into its configuration starts speaking in the account's language without a restart - the same
    ///  live-read contract <see cref="HostedAiKeyResolver"/> follows, and for the same reason.</param>
    /// <param name="http">HTTP client for the fetch (tests inject a stub).</param>
    public AccountUtterance(Func<GatewayConfig>? gatewayProvider = null, HttpClient? http = null)
    {
        _gatewayProvider = gatewayProvider ?? GatewayConfig.Load;
        _http = http ?? SharedHttp;
    }

    /// <summary>
    /// The account's decision, packaged: <paramref name="text"/> with the language this account is spoken to in
    /// and the voice that speaks it - or WHICH KIND of absence this is, so a caller can never treat "could
    /// not ask" as "nothing to ask about" (see the two-absences note above).
    ///
    /// ASKED EVERY TIME, AND DELIBERATELY NOT CACHED. The language and voice settings are defined to take
    /// effect on the next spoken output; a cache is precisely what would make the Language tab appear to do
    /// nothing for as long as it lived, and "the setting does nothing" was reported three times on the attempt
    /// that was reverted. One small request against a Gateway that is about to be asked to synthesize a whole
    /// sentence is not the cost worth optimizing here.
    /// </summary>
    public async Task<AccountVoiceLookup> ForAsync(string text, CancellationToken ct = default)
    {
        var gateway = _gatewayProvider();
        if (!gateway.IsEnabled) return AccountVoiceLookup.NoAccount();
        if (string.IsNullOrWhiteSpace(text))
            return AccountVoiceLookup.Unavailable("there are no words to speak");

        var url = gateway.Url.TrimEnd('/') + "/gateway/spoken-language";
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrWhiteSpace(gateway.Token))
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", gateway.Token);

            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
            {
                FileLog.Write($"[AccountUtterance] GET {url} -> {(int)resp.StatusCode}");
                return AccountVoiceLookup.Unavailable($"the Gateway answered {(int)resp.StatusCode}");
            }

            var json = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(json);
            var code = doc.RootElement.TryGetProperty("language", out var l) ? l.GetString() : null;
            var voice = doc.RootElement.TryGetProperty("voice", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(voice))
            {
                FileLog.Write($"[AccountUtterance] GET {url} answered without a language or a voice");
                return AccountVoiceLookup.Unavailable("the Gateway did not say which language or voice to use");
            }

            // A CODE THIS BUILD DOES NOT KNOW IS A REFUSAL, NOT ENGLISH (re-audit). This used to resolve unknown
            // codes to English, so a Gateway answering {"language":"de"} - a newer Gateway offering a language
            // this desktop has never heard of - handed back a perfectly valid ENGLISH utterance and the desktop
            // spoke it. That is precisely the condition this whole lookup exists to refuse, arriving through a
            // 200 response instead of a timeout. The account has a language; we do not know it; we do not guess.
            var language = SpokenLanguages.TryResolve(code);
            if (language is null)
            {
                FileLog.Write($"[AccountUtterance] the Gateway named language '{code}', which this build does not speak");
                return AccountVoiceLookup.Unavailable($"the Gateway named a language this build does not speak ({code})");
            }
            return AccountVoiceLookup.Resolved(SpokenUtterance.For(language, voice, text));
        }
        catch (Exception ex)
        {
            FileLog.Write($"[AccountUtterance] fetch FAILED ({url}): {ex.Message}");
            return AccountVoiceLookup.Unavailable("the Gateway could not be reached");
        }
    }
}

/// <summary>
/// The answer to "what is this account spoken with": the utterance, or WHICH KIND of absence.
///
/// Three states rather than a nullable, because two of them are absences that must not be handled the same way
/// (client audit, finding 1). A caller holding one null cannot tell "this machine has no account" from "the
/// account has a language and I could not find out what it is" - and treating the second like the first is what
/// read a French account's sentence aloud in the machine's English voice.
/// </summary>
public sealed class AccountVoiceLookup
{
    private AccountVoiceLookup(SpokenUtterance? utterance, bool hasAccount, string? reason)
    {
        Utterance = utterance;
        HasAccount = hasAccount;
        Reason = reason;
    }

    /// <summary>The packaged utterance when it resolved; null otherwise.</summary>
    public SpokenUtterance? Utterance { get; }

    /// <summary>Whether this Director is attached to a Gateway at all - whether there IS an account whose
    ///  language could have been asked for.</summary>
    public bool HasAccount { get; }

    /// <summary>Why the lookup failed, in words a person can act on. Null unless this is a failure. ASCII, and it
    ///  never contains the spoken text.</summary>
    public string? Reason { get; }

    /// <summary>It resolved: speak this.</summary>
    public static AccountVoiceLookup Resolved(SpokenUtterance utterance)
        => new(utterance ?? throw new ArgumentNullException(nameof(utterance)), hasAccount: true, reason: null);

    /// <summary>There is no Gateway and therefore no account: the machine's own configured voice is the whole
    ///  truth here, and speaking with it is correct rather than degraded.</summary>
    public static AccountVoiceLookup NoAccount() => new(null, hasAccount: false, reason: null);

    /// <summary>There IS an account and its language could not be established. The caller must NOT speak: it does
    ///  not know which language to speak in, and guessing is the failure.</summary>
    public static AccountVoiceLookup Unavailable(string reason)
        => new(null, hasAccount: true, reason: reason);
}
