namespace CcDirectorClient.Voice;

/// <summary>
/// The resilient submit + poll loop for the Gateway async voice-turn pipeline (issue #405).
///
/// The pipeline is designed to survive a signal drop: the phone submits the audio once, the
/// Gateway drives the turn server-side and caches the result for ~10 minutes, and the phone
/// just polls. This runner makes the client actually live up to that design - a single dropped
/// poll (network error, 5xx, request timeout) no longer aborts the whole turn. It retries with
/// a small backoff and keeps re-polling until either a terminal stage (reply / error) or an
/// overall deadline aligned with the Gateway job TTL. Submit is retried the same way so a drop
/// mid-upload does not lose the recorded clip.
///
/// MAUI-free and timer-free by construction: the clock and the inter-poll delay are injected
/// (<see cref="VoiceTurnRetryPolicy"/>), so the whole loop is unit tested off-device with a fake
/// <see cref="IVoiceTurnChannel"/> and instant (no real wall-clock) delays.
/// </summary>
public sealed class VoiceTurnRunner
{
    private readonly IVoiceTurnChannel _channel;
    private readonly VoiceTurnRetryPolicy _policy;

    public VoiceTurnRunner(IVoiceTurnChannel channel, VoiceTurnRetryPolicy? policy = null)
    {
        _channel = channel;
        _policy = policy ?? VoiceTurnRetryPolicy.Default;
    }

    /// <summary>
    /// Submit the utterance to the Gateway, retrying a bounded number of times with backoff on
    /// a transient failure so a drop mid-upload does not discard the clip. A terminal failure
    /// (the Gateway answered but rejected the request) surfaces immediately; the last transient
    /// failure surfaces only after the retry budget is spent.
    /// </summary>
    public async Task<VoiceTurnSubmitResult> SubmitAsync(
        string gatewayBase, string sessionId, byte[] audio, string mime, CancellationToken ct = default)
    {
        ClientLog.Write($"[VoiceTurnRunner] Submit: sid={sessionId}, bytes={audio.Length}, mime={mime}, maxAttempts={_policy.SubmitAttempts}");
        Exception? lastTransient = null;
        for (var attempt = 1; attempt <= _policy.SubmitAttempts; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await _channel.SubmitVoiceTurnAsync(gatewayBase, sessionId, audio, mime, ct);
                ClientLog.Write($"[VoiceTurnRunner] Submit OK on attempt {attempt}: turnId={result.TurnId}");
                return result;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (IsTransientSubmit(ex))
            {
                lastTransient = ex;
                ClientLog.Write($"[VoiceTurnRunner] Submit transient failure on attempt {attempt}/{_policy.SubmitAttempts}: {ex.Message}");
                if (attempt < _policy.SubmitAttempts)
                    await _policy.DelayAsync(_policy.BackoffFor(attempt), ct);
            }
        }

        ClientLog.Write($"[VoiceTurnRunner] Submit FAILED after {_policy.SubmitAttempts} attempts: {lastTransient?.Message}");
        throw new InvalidOperationException(
            $"could not send your recording to the gateway after {_policy.SubmitAttempts} attempts - please try again",
            lastTransient);
    }

    /// <summary>
    /// Poll the Gateway until the turn reaches a terminal stage. Transient poll failures
    /// (network error, 5xx, request timeout) are tolerated: the loop waits a backoff interval
    /// and re-polls rather than throwing out. It gives up only at the overall deadline
    /// (aligned with the Gateway job TTL) or on a terminal condition - the "error" stage, a
    /// 410 (session gone), or a 404 (the cached turn expired -> a clean "please resend",
    /// never an unhandled crash). <paramref name="onStage"/> fires once per distinct stage so
    /// the caller can surface progress.
    /// </summary>
    public async Task<VoiceTurnPollResult> PollToCompletionAsync(
        string gatewayBase, string sessionId, string turnId,
        Action<VoiceTurnPollResult>? onStage = null, CancellationToken ct = default)
    {
        var deadline = _policy.UtcNow() + _policy.OverallDeadline;
        ClientLog.Write($"[VoiceTurnRunner] Poll start: sid={sessionId}, turnId={turnId}, deadline={deadline:O}");

        var lastStage = "";
        var consecutiveTransient = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();

            if (_policy.UtcNow() >= deadline)
            {
                ClientLog.Write($"[VoiceTurnRunner] Poll deadline reached for turnId={turnId} (last stage='{lastStage}')");
                throw new VoiceTurnTimeoutException(
                    "the gateway did not finish this turn in time - please try again");
            }

            // The cadence: a steady interval while healthy, a growing backoff while a run of
            // polls is failing. A success resets the run so we return to the steady cadence.
            var wait = consecutiveTransient == 0
                ? _policy.PollInterval
                : _policy.BackoffFor(consecutiveTransient);
            await _policy.DelayAsync(wait, ct);

            VoiceTurnPollResult poll;
            try
            {
                poll = await _channel.PollVoiceTurnAsync(gatewayBase, sessionId, turnId, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (VoiceTurnHttpException ex) when (ex.IsExpired)
            {
                // 404 after we held a valid turn id: the cached job expired (TTL) or is
                // unknown. Terminal, but a clean "please resend" - not a crash.
                ClientLog.Write($"[VoiceTurnRunner] Poll: turn {turnId} expired (404)");
                throw new VoiceTurnExpiredException(
                    "this turn has expired on the gateway - please record and send it again");
            }
            catch (VoiceTurnHttpException ex) when (ex.IsSessionGone)
            {
                ClientLog.Write($"[VoiceTurnRunner] Poll: session gone (410) for turnId={turnId}");
                throw new InvalidOperationException("that session has exited");
            }
            catch (Exception ex) when (IsTransientPoll(ex))
            {
                consecutiveTransient++;
                ClientLog.Write($"[VoiceTurnRunner] Poll transient failure #{consecutiveTransient} for turnId={turnId}: {ex.Message}");
                continue;
            }

            consecutiveTransient = 0;

            if (!string.Equals(poll.Stage, lastStage, StringComparison.Ordinal))
            {
                lastStage = poll.Stage;
                onStage?.Invoke(poll);
            }

            if (string.Equals(poll.Stage, "error", StringComparison.Ordinal))
            {
                ClientLog.Write($"[VoiceTurnRunner] Poll terminal error for turnId={turnId}: {poll.Error}");
                throw new InvalidOperationException($"gateway voice turn failed: {poll.Error ?? "unknown error"}");
            }

            if (string.Equals(poll.Stage, "reply", StringComparison.Ordinal))
            {
                ClientLog.Write($"[VoiceTurnRunner] Poll complete for turnId={turnId}");
                return poll;
            }
        }
    }

    /// <summary>A transient submit failure is one the carrier signal caused, so a retry can
    /// succeed: a bare network error, a 5xx, or a request timeout that is NOT the caller's
    /// cancellation. A 4xx (other than the ones the runner treats terminally elsewhere) is the
    /// Gateway rejecting the request and is not retried.</summary>
    private static bool IsTransientSubmit(Exception ex) => ex switch
    {
        VoiceTurnHttpException http => http.IsServerError,
        HttpRequestException => true,
        TaskCanceledException => true, // request timeout (caller cancellation is filtered above)
        _ => false,
    };

    /// <summary>Same classification as submit for the poll path. The expired (404) and
    /// session-gone (410) cases are handled as terminal by the caller's catch clauses before
    /// this is consulted.</summary>
    private static bool IsTransientPoll(Exception ex) => ex switch
    {
        VoiceTurnHttpException http => http.IsServerError,
        HttpRequestException => true,
        TaskCanceledException => true,
        _ => false,
    };
}
