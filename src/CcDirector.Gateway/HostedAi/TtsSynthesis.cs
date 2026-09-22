using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using System.Threading.Tasks;
using CcDirector.Core.Utilities;

namespace CcDirector.Gateway.HostedAi;

/// <summary>
/// Forwards a text-to-speech request to the configured provider-compatible <c>/audio/speech</c>
/// endpoint with a per-attempt deadline DERIVED FROM THE TEXT, and a single retry.
///
/// The upstream speech model (the DevThrottle proxy forwarding to DeepInfra's Kokoro) is bimodal: a
/// healthy call returns in about a second, but an intermittently cold or overloaded worker never
/// answers. A flat 60-second client timeout with no retry turned one such stall into a full
/// 60-second freeze on the phone (every wingman turn hung for a minute). A per-attempt cap fails
/// fast on the stalled worker, and one retry almost always lands on a warm worker.
///
/// A deadline must exist: the provider does not queue (200 concurrent per model, 429 immediately
/// when busy), so a long silence is a genuine hang, not work in progress.
///
/// WHY THE DEADLINE IS COMPUTED AND NOT A CONSTANT (issue #1612). It used to be a flat 15 seconds.
/// That number was only ever right for a 4000-character world, because synthesis scales LINEARLY
/// with the text - about 1.7 ms per character, measured. The 4000 cap and the 15 s deadline were one
/// decision pretending to be two, so raising the cap alone would have converted silent truncation
/// into silent FAILURE: 12,000 characters needs ~21 s and would have blown a 15 s deadline every
/// time. A constant here is the same disease that produced the 4000 in the first place - correct
/// once, never re-derived when the world moved. Derive it from the work and it cannot rot when the
/// cap moves again.
///
/// This is the ONLY deadline on the speech path. The server-side proxy had its own, nested wrongly
/// inside this one, and it was deleted (devthrottle_internal#360) - a proxy must not invent policy.
/// Do NOT reintroduce a second one anywhere: that nesting was the original bug.
///
/// This is NOT a fallback that hides a failure: a genuine provider response (success, 402, any 4xx or
/// 5xx) is returned immediately with no retry, and when BOTH attempts exceed the deadline the
/// caller is told the synthesis failed (a thrown <see cref="TimeoutException"/>). Only the transient
/// "the worker never answered in time" case is retried.
/// </summary>
internal static class TtsSynthesis
{
    /// <summary>
    /// Fixed cost allowed for everything that is NOT synthesis: TLS, auth, the credit pre-flight, the
    /// network both ways, and - the part that actually sets this number - the provider's COLD START.
    /// It does not scale with the text, and it is neither small nor steady, which is why this is 30
    /// and not 5, and no longer the 15 that still was not enough.
    ///
    /// It shipped as 5s and that was a REGRESSION (fixed same day). 5s + 4ms/char is TIGHTER than the
    /// flat 15s it replaced for anything under ~2,500 characters - which is almost every real
    /// narration. It failed immediately on the owner's Gateway:
    ///   [TtsSynthesis] attempt 1/2 timed out after 5s (20 chars); retrying
    ///   [TtsSynthesis] attempt 2/2 timed out after 5s (20 chars); giving up
    /// Twenty characters. Roughly 30ms of GPU. Dead in 10 seconds.
    ///
    /// The mistake was believing latency scales with length. Synthesis does; the CALL does not. Three
    /// same-day measurements of the SAME 47-character call: 0.7s, 1.3s, and 13.3s - with only 72ms of
    /// GPU in all three. That ~13s of variance is network and provider overhead, and it lands on a
    /// 20-character call exactly as hard as on a 4,000-character one. A base of 5 gave it nowhere to
    /// go. The base must absorb the fixed overhead INCLUDING its outliers; the slope absorbs synthesis.
    ///
    /// THAT VARIANCE HAS A NAME, AND 15 WAS STILL TOO SMALL FOR IT. It is a COLD START. The provider
    /// scales the speech model down when nobody is calling, and the first call after an idle period
    /// pays the model load. The "0.7s, 1.3s, 13.3s" spread above is not noise to be averaged - it is
    /// warm, warm, cold. Measured direct to the provider on 2026-07-15, from a fleet that had been
    /// silent, 720-character call repeated:
    ///
    ///     COLD:  16.9s   12.4s   11.3s      <- first calls after idle; all returned HTTP 200 + real audio
    ///     WARM:   1.8s    1.9s    3.8s      <- same call, same length, moments later
    ///
    /// 16.9s against the 17.9s this formula allowed a 720-char call. One second of margin. At 47
    /// characters the deadline was 15.2s and the cold start was LONGER than the entire deadline - a
    /// short narration on a cold provider could not succeed at all.
    ///
    /// This is what made it a trap rather than a slow day. On 2026-07-15 a timeout armed a fleet-wide
    /// speech cooldown for 120 seconds, and 120 seconds of nobody calling the provider is exactly how the
    /// provider goes cold - so the cooldown MANUFACTURED the cold start that caused the next timeout that
    /// re-armed it. The fleet went 0/8 sessions with audio, every one reporting ServiceDown, and could
    /// not climb out on its own - the service was answering perfectly the whole time; three warm-up calls
    /// by hand took it to 6/8 with no code change. That shared cooldown gate was removed entirely on
    /// 2026-07-17: each session now calls the provider on its own, so one slow call no longer starves the
    /// rest and the cold-start feedback loop is gone. This per-attempt deadline is still the thing that
    /// bounds a single stalled call, which is why getting it right still matters.
    ///
    /// 30 was the first attempt at this and it was STILL too tight - measured against 16.9s, when the
    /// same provider was later seen taking 39.9s for a SIXTEEN character call. Hours later the live log
    /// read "attempt 1/2 timed out after 31s (168 chars)": a 168-character narration, dead, because the
    /// deadline was a guess dressed as arithmetic. Twice now this number has been set to just above the
    /// worst thing seen so far, and twice the provider has gone slower than that.
    ///
    /// 60 stops chasing it. It clears the worst observed cold start (39.9s) with real headroom rather
    /// than a shave, and the cost of being generous is now nearly nothing: a timeout no longer touches
    /// any other session (see WingmanVoiceService.TtsAsync), so an over-long deadline costs ONE session
    /// a slow turn, while an under-long one costs that session its voice entirely and buys nothing.
    ///
    /// If you are tempted to lower this: a deadline is not a performance target, and this one is not
    /// protecting the user from waiting - the narration is already made or not made by then. It exists
    /// only to stop a truly hung call from holding a slot forever. Being generous costs a slot; being
    /// tight costs the feature. The failure is not symmetric, so neither is the number.
    /// </summary>
    private static readonly TimeSpan DeadlineBase = TimeSpan.FromSeconds(60);

    /// <summary>Allowance per character of input. Synthesis measures ~1.7 ms/char, so 4 ms/char is
    /// roughly 2.4x headroom at every length - enough for a slow-but-working worker, short enough
    /// that a genuine hang is still caught quickly.</summary>
    private const double DeadlineMsPerChar = 4.0;

    /// <summary>
    /// The per-attempt deadline for synthesising <paramref name="inputChars"/> characters:
    /// <see cref="DeadlineBase"/> + <see cref="DeadlineMsPerChar"/> per character.
    ///
    /// READ THIS BEFORE CHANGING THE NUMBERS. The table below used to list ONLY healthy runs, and
    /// that is exactly how a regression shipped: every row cleared its deadline comfortably, the
    /// arithmetic looked sound, and a 20-character narration then died in 5 seconds on a real
    /// machine. A headroom claim measured against best-case runs is not a headroom claim. Any
    /// deadline for a network call must be justified against its WORST observed behaviour, because
    /// the worst case is the only one a deadline exists for.
    ///
    /// Measured direct to the provider, 2026-07-15, production key:
    ///
    ///   SYNTHESIS (scales with length - this is what the slope pays for):
    ///     4,000 chars -> 7.3 s     8,000 chars -> 14.0 s
    ///     5,000 chars -> 11.6 s   12,000 chars -> 21.1 s
    ///
    ///   FIXED OVERHEAD (does NOT scale - this is what the base pays for, and it is the part that
    ///   bites). The SAME 47-character call, three times in one day: 0.7 s, 1.3 s, 13.3 s - with
    ///   72 ms of GPU in all three. So ~13 s of the wall time had nothing to do with the text, and a
    ///   short call is exposed to it just as much as a long one.
    ///
    ///   COLD START - the same overhead, named. Re-measured 2026-07-15 against a provider left idle
    ///   by the very cooldown a timeout arms. 720-char call, four runs, cold then warm:
    ///     COLD: 16.9 s  12.4 s  11.3 s     WARM: 1.8 s  1.9 s  3.8 s
    ///   All six returned HTTP 200 with real audio. 16.9 s is the number the base must clear; the old
    ///   base of 15 did not, and at 47 chars the whole deadline (15.2 s) was shorter than the cold
    ///   start. Warm latency by length, same session: 47 -> 1.8 s, 469 -> 2.8 s, 720 -> 3.8 s,
    ///   1,292 -> 9.6 s (worst of four each).
    ///
    /// Deadlines that follow, against the worst case at each length:
    ///     20 chars    -> 30.1 s   (vs the 16.9 s cold start that used to beat the old 15.1 s outright)
    ///     1,292 chars -> 35.2 s   (today's median narration; 9.6 s observed warm, 16.9 s cold)
    ///     4,000 chars -> 46.0 s   (7.3 s synthesis + room for the cold start)
    ///     12,000 chars-> 78.0 s   (21.1 s synthesis + the same)
    ///
    /// Never tighter than the flat 15 s this replaced, at ANY length - that is the floor, and
    /// <c>NarrationLengthTests</c> pins it so the short end cannot be regressed again.
    /// </summary>
    public static TimeSpan DeadlineFor(int inputChars)
        => DeadlineBase + TimeSpan.FromMilliseconds(DeadlineMsPerChar * Math.Max(0, inputChars));

    /// <summary>
    /// Total attempts. ONE - there is no retry, and removing it is a fix, not a regression.
    ///
    /// It was 2, "targeting the transient stalled-worker case", on the theory that attempt 1 eats a
    /// cold start and attempt 2 lands on a warm worker. Production disagreed, repeatedly and in plain
    /// text:
    ///
    ///   [TtsSynthesis] attempt 1/2 timed out after 33s (709 chars); retrying
    ///   [TtsSynthesis] attempt 2/2 timed out after 33s (709 chars); giving up
    ///
    /// The retry never landed warm. That is not bad luck, it is mechanism: cancelling attempt 1 very
    /// plausibly cancels the provider-side work that was loading the model, so attempt 2 starts the
    /// same cold start over rather than arriving after it. Two attempts bought a doubled wait, a
    /// doubled load on an already-struggling provider, and the same failure.
    ///
    /// So: one attempt, with a deadline long enough for the cold start to actually finish (see
    /// DeadlineBase). Waiting through a slow call is what gets the audio; racing it and starting again
    /// is what loses it. Recovery is per-session retry at a higher level, seconds later, not a second
    /// attempt seconds into the same stall.
    ///
    /// This also halves the worst-case wait on the INTERACTIVE path (/wingman/tts, where a human is
    /// listening for it): a never-answering upstream now costs one deadline, not two.
    /// </summary>
    public const int Attempts = 1;

    /// <summary>
    /// POST <c>{ model, voice, input, response_format }</c> to <paramref name="url"/> with bearer
    /// <paramref name="key"/>. Returns the provider's response (success or error) for the caller to
    /// read and dispose. Throws <see cref="TimeoutException"/> when every attempt exceeds the
    /// deadline for <paramref name="inputChars"/> (see <see cref="DeadlineFor"/>). Honors
    /// <paramref name="ct"/>: caller cancellation propagates immediately and is never mistaken for a
    /// per-attempt timeout.
    /// </summary>
    /// <summary>The out-of-band routing hint the Gateway sends the cloud speech proxy after it has
    /// watched the primary voice provider go SILENT on a session (issue devthrottle_internal#405). A silent hang gives the
    /// proxy's own failover no error to react to, so the Gateway - the layer that owns this deadline and
    /// therefore actually observes the hang - asks the proxy to skip the stalling provider and serve
    /// from the backup. It is a ROUTING hint, not a deadline: the proxy keys on the header's presence and
    /// ignores its value. Kept as a stable literal so both repos agree without coordination.</summary>
    public const string PreferBackupHeaderName = "X-DevThrottle-TTS-Prefer-Backup";

    /// <param name="inputChars">Length of the text being synthesised. The deadline is derived from
    /// it, so pass the length of the text actually in <paramref name="payload"/>.</param>
    /// <param name="preferBackup">When true, send <see cref="PreferBackupHeaderName"/> so the cloud
    /// proxy routes straight to the backup provider (issue devthrottle_internal#405). The Gateway sets this after it has
    /// seen the primary go silent on this session; it is a routing hint only and changes no deadline.</param>
    /// <param name="deadlineOverride">Replaces the length-derived deadline. Exists so a test can prove the
    /// stall-to-504 path without waiting through the production sixty-second base (issue #1156): the test that
    /// covered it used to spend 60s of every suite run sitting on a clock. Passed as an OPTION rather than
    /// exposed as a static test hook on purpose - a process-global switch would be shared by every host in the
    /// process and is exactly the kind of seam that blocks running these tests in parallel. Null in production,
    /// where the length-derived deadline is the only one.</param>
    /// <param name="tag">What the speech is for and which account (devthrottle_internal #2213), sent on every
    /// attempt so each one the API records carries it. Required: every speech call is a cost someone owns.</param>
    public static async Task<HttpResponseMessage> PostAsync(HttpClient http, string url, string key, object payload, int inputChars, bool preferBackup, CancellationToken ct, Core.HostedAi.AiCallTag tag, TimeSpan? deadlineOverride = null)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var deadline = deadlineOverride ?? DeadlineFor(inputChars);
        TimeoutException? lastTimeout = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            using var perAttempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
            perAttempt.CancelAfter(deadline);
            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = JsonContent.Create(payload),
            };
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            tag.ApplyTo(req);
            if (preferBackup)
                req.Headers.TryAddWithoutValidation(PreferBackupHeaderName, "1");
            try
            {
                return await http.SendAsync(req, perAttempt.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Our per-attempt timer fired (the caller did not cancel): the upstream worker stalled.
                lastTimeout = new TimeoutException(
                    $"text-to-speech attempt {attempt} exceeded {deadline.TotalSeconds:0}s for {inputChars} characters");
                FileLog.Write($"[TtsSynthesis] attempt {attempt}/{Attempts} timed out after " +
                    $"{deadline.TotalSeconds:0}s ({inputChars} chars)" +
                    $"{(attempt < Attempts ? "; retrying" : "; giving up")}");
            }
        }
        throw lastTimeout!;
    }
}
