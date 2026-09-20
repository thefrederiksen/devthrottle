using CcDirector.Gateway.Contracts;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE ONE PLACE A ROW IS STAMPED WITH ITS VOICE VERDICT - the waiting clock, the reason there is no
/// audio, and the folded display verdict every client renders verbatim.
///
/// It exists because there were TWO stamps, written months apart and already drifted: the roster route
/// and the Director push each called <see cref="Wingman.VoiceDisplayFold"/> with their own argument list,
/// and the push path's list is missing one of the facts the route's carries. Nothing detected that,
/// because nothing compared them - two answers to one question never disagree loudly, they just disagree.
/// The Wingman tab's Now view needs the same verdict on the same row, and adding a THIRD copy of this
/// call is how a session comes to be "preparing audio" on one screen and silent on another.
///
/// IT DECIDES NOTHING. Every word and every flag is <see cref="Wingman.VoiceDisplayFold"/>'s; this only
/// gathers the facts and hands them over in one order, once.
/// </summary>
internal static class VoiceRowStamp
{
    /// <summary>
    /// The voice facts for one session, each already bound to the caller's own account.
    ///
    /// Every one is OPTIONAL and a null reads as "this caller cannot see that fact", never as "false". A
    /// caller that has no voice service at all therefore stamps exactly what it stamps today - the fold's
    /// own answer for a session with nothing known about it - rather than a confident "no voice".
    /// </summary>
    /// <param name="Generating">The Wingman is producing this session's spoken summary right now.</param>
    /// <param name="AudioReady">There is fetchable, playable audio for this turn - the single truthful signal.</param>
    /// <param name="Unavailable">Why voice cannot run for this account at all (no credit, a cap, no key).</param>
    /// <param name="NothingToNarrate">The turn ended on a prompt, so there is no text answer to read.</param>
    /// <param name="DirectorCannotSendConversation">Its Director cannot send the conversation to narrate.</param>
    /// <param name="SpeechError">The reading succeeded and its audio failed: where that is on the retry schedule.</param>
    /// <param name="ServedViaFallback">This turn's ready clip came from the backup voice provider.</param>
    /// <param name="WaitingStamp">The waiting clock, which is TOLD whether this session is waiting and
    /// answers with the moment the wait began.</param>
    internal sealed record VoiceFacts(
        Func<string, bool>? Generating = null,
        Func<string, bool>? AudioReady = null,
        Func<string, Core.HostedAi.HostedAiState?>? Unavailable = null,
        Func<string, bool>? NothingToNarrate = null,
        Func<string, bool>? DirectorCannotSendConversation = null,
        Func<string, WingmanErrorDisplay?>? SpeechError = null,
        Func<string, bool>? ServedViaFallback = null,
        Func<string, bool, DateTime?>? WaitingStamp = null);

    /// <summary>
    /// Stamp one row: the unavailable reason, the waiting clock, and the folded verdict.
    ///
    /// THE CLOCK IS STAMPED FROM THE SAME FACTS THE FOLD READS, and in that order, so the elapsed time on
    /// a row and the words on that row can never disagree about whether the session is waiting at all.
    /// </summary>
    internal static void Apply(SessionDto s, VoiceFacts? facts)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (facts is null) return;

        // THE TWO READINESS BOOLEANS FIRST, because everything below reads them off the row. They are
        // Gateway-only facts - the Director never sets them - and a null delegate leaves whatever the row
        // already carries rather than overwriting it with a confident false.
        if (facts.Generating is not null) s.VoiceGenerating = facts.Generating(s.SessionId);
        if (facts.AudioReady is not null) s.VoiceAudioReady = facts.AudioReady(s.SessionId);

        var unavailable = facts.Unavailable?.Invoke(s.SessionId);
        // Null - voice is fine - leaves the field unset, which is what a client reads as "no shared
        // call to action to show".
        if (unavailable is Core.HostedAi.HostedAiState reason)
            s.VoiceUnavailable = HostedAi.HostedAiHttp.Dto(reason);

        var agentWorking = string.Equals(s.ActivityState, "Working", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(s.ActivityState, "Starting", StringComparison.OrdinalIgnoreCase);

        s.VoiceWaitingSince = facts.WaitingStamp?.Invoke(
            s.SessionId,
            Wingman.VoiceDisplayFold.IsWaitingForVoice(s.VoiceMode, s.VoiceAudioReady, agentWorking));

        s.VoiceDisplay = Wingman.VoiceDisplayFold.Fold(
            voiceMode: s.VoiceMode,
            agentWorking: agentWorking,
            hasAudio: s.VoiceAudioReady,
            generating: s.VoiceGenerating,
            unavailable: unavailable,
            nothingToNarrate: facts.NothingToNarrate?.Invoke(s.SessionId) ?? false,
            directorCannotSendConversation: facts.DirectorCannotSendConversation?.Invoke(s.SessionId) ?? false,
            speechError: facts.SpeechError?.Invoke(s.SessionId),
            servedViaFallback: facts.ServedViaFallback?.Invoke(s.SessionId) ?? false,
            waitingSince: s.VoiceWaitingSince);
    }
}
