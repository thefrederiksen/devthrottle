using CcDirector.Core.Configuration;
using CcDirector.Core.Dictation;
using CcDirector.Core;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.Transcription;

/// <summary>
/// The one place a dictation judge is built, so every path that corrects a transcript gets the same
/// judge or the same honest null - and so a new correction path cannot quietly ship without one.
///
/// Three callers need it: live dictation, the batch recording pipeline, and the text-in/text-out
/// evaluation endpoint. When the judge was built inline in only the first of those, the other two
/// silently became unable to apply any unlisted correction while still answering success. That is the
/// failure this class exists to prevent.
/// </summary>
public static class DictationJudgeFactory
{
    /// <summary>
    /// A judge from the deployment credential, or null when this install has none.
    ///
    /// Null is a safe answer rather than a degraded one: with no judge the orchestrator applies no
    /// unlisted correction at all, which is the correct behaviour for a self-host install with no
    /// DevThrottle key. The wrong forms the user listed by hand keep working either way.
    /// </summary>
    public static ICandidateJudge? FromVault(KeyVault vault, CcDirector.Core.HostedAi.AiCallTag tag, Action<string>? log = null)
    {
        var write = log ?? FileLog.Write;
        try
        {
            var endpoint = TranscriptionEndpointResolver.ResolveDictationCleanup(TranscriptionModeConfig.Get());
            var key = vault.Get(endpoint.KeyName);
            return FromKey(endpoint.BaseUrl, key, tag, write);
        }
        catch (Exception ex)
        {
            // Never fail a turn over the judge. No judge means no unlisted correction, which is safe.
            write($"[DictationJudgeFactory] judge unavailable ({ex.GetType().Name}); unlisted corrections stay off");
            return null;
        }
    }

    /// <summary>A judge from an already-resolved base URL and credential - the batch pipeline path,
    /// which has its routing in hand and no vault.</summary>
    /// <param name="tag">The judge's tag (the dictation-judge feature and the account the dictation belongs
    /// to), sent on every ruling request.</param>
    public static ICandidateJudge? FromKey(string? baseUrl, string? apiKey, CcDirector.Core.HostedAi.AiCallTag tag, Action<string>? log = null)
    {
        var write = log ?? FileLog.Write;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(apiKey))
        {
            write("[DictationJudgeFactory] no dictation-judge credential; unlisted corrections stay off "
                  + "and the wrong forms the user listed still apply");
            return null;
        }

        return new HostedCandidateJudge(
            baseUrl!, apiKey!, IncludedModelId.DictationCleanup, log: write, tag: tag);
    }
}
