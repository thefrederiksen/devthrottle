using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// A prompt the Gateway itself writes and types into a session - a supervisor's recovery "continue", a Session Rule
/// firing. It is labelled as what it is at the one place that knows: not the owner, and not another session, but the
/// product.
///
/// Why this matters (voice mode auto-off, review of step 2): the Director stamps a session's owner-turn time for every
/// prompt it is not told otherwise about, and that stamp is how the Gateway knows the owner answered. Unlabelled, a
/// supervisor's "continue" counted as the owner answering a voice session without listening. Sorting it out on the
/// Gateway afterwards cannot work: the Director may hold the prompt queued for minutes before it stamps, so no window
/// of time can tell its stamp from his. Marked agent-driven, the Director does not stamp it at all - the same
/// mechanism that keeps a fleet message from counting as the owner - and its provenance names it framework text.
/// </summary>
public static class GatewayAuthoredPrompt
{
    /// <summary>A prompt the Gateway authored, submitted with Enter, not waiting for idle.</summary>
    public static PromptRequest For(string text) => new()
    {
        Text = text,
        AppendEnter = true,
        WaitForIdle = false,
        AgentDriven = true,
        Provenance = new SubmissionProvenanceDto
        {
            Route = SubmissionRoutes.Framework,
            IdentityKind = SubmissionIdentityKinds.Framework,
        },
    };
}
