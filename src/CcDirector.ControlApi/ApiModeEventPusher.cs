using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.Core.ApiMode;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Contracts.ApiMode;

namespace CcDirector.ControlApi;

/// <summary>
/// THE DIRECTOR PUSHES ITS API-MODE EVENTS (API mode mission, issue #4070, phase 4). The Director's
/// <see cref="ISessionEventSink"/> when a Gateway is configured: every event an API-mode session publishes is held here,
/// in order, until the Gateway has acknowledged it, and sent up through the hub's <c>PushApiModeEvents</c>
/// (<see cref="ApiModeEventBatch"/> is the contract).
///
/// PER SESSION, every event not yet acknowledged, oldest first, with its generation and the generation's start. A run
/// sends batches of one generation each, bounded by count (<see cref="ApiModeEventBatch.MaxEvents"/>) AND by characters
/// of event JSON (<see cref="ApiModeEventBatch.MaxCharacters"/>), each event as its own JSON text written with
/// <see cref="SessionEventJson.Options"/>. The Gateway's answer is one of four, told apart:
/// <list type="bullet">
/// <item>A watermark with a generation: stored. Every held event of that generation at or below the watermark's sequence
/// is dropped; the rest is sent next.</item>
/// <item>A watermark whose generation is EMPTY: refused for now, nothing stored. The same events stay and are sent again
/// at the next trigger.</item>
/// <item><c>null</c>: refused for good. Logged with what was refused, dropped, and never sent again.</item>
/// <item>An exception (the tunnel down, the Gateway's store unreachable): nothing kept. The events stay and are sent
/// again; storing is idempotent.</item>
/// </list>
///
/// TRIGGERS: every publish; every Hello from the Gateway (so after a reconnect, a Gateway restart or a deploy, every event
/// the Gateway has not acknowledged is sent again from the oldest held - which is what keeps "Working - turn N" right after
/// a Gateway restart during a turn); and a slow safety sweep. Runs for one session never overlap: a trigger during a run
/// marks it pending, and the runner checks that under the same lock before it leaves.
///
/// ONLY WHEN THE GATEWAY HAS <c>PushApiModeEvents</c>. Against a Gateway without it the events are held, and that is
/// logged once per session; they go up when a Gateway that has it answers a Hello.
///
/// THE MEMORY BOUND (stated in the pull request): at most <see cref="DefaultMaxHeldEventsPerSession"/> events and
/// <see cref="DefaultMaxHeldCharactersPerSession"/> characters of event JSON are held per session. Past either, the
/// OLDEST held events are dropped, and every drop is logged loudly with the session, the generation and the exact
/// sequences dropped, so a gap the Gateway will see is always explained in the Director's log. An event too large to fit
/// any batch is dropped the same way, by name. Nothing is ever dropped without saying so.
///
/// Built on delegates, not on the stream client, so every branch is tested with a fake Gateway.
/// </summary>
public sealed class ApiModeEventPusher : ISessionEventSink, IAsyncDisposable
{
    /// <summary>The hub method this pusher calls, and the capability a Gateway must advertise.</summary>
    public const string HubMethod = "PushApiModeEvents";

    /// <summary>How often the safety sweep re-sends what is still held: a minute, as the turn sweep.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The most events held for one session while the Gateway has not acknowledged them.</summary>
    public const int DefaultMaxHeldEventsPerSession = 20_000;

    /// <summary>The most characters of event JSON held for one session while the Gateway has not acknowledged them
    /// (about 100 megabytes in memory).</summary>
    public const long DefaultMaxHeldCharactersPerSession = 50_000_000;

    private sealed record HeldEvent(string Generation, DateTime GenerationStartedUtc, long Sequence, string Json);

    private sealed class SessionState
    {
        // Every field is read and written under lock (this).
        public readonly List<HeldEvent> Held = [];
        public long HeldCharacters;
        public bool Pending;
        public bool Running;
        public bool LoggedUnsupported;
        /// <summary>The run took this state out of the table (nothing held, nothing pending). A publisher holding it
        /// takes a fresh one, so an event can never land in a state nothing will send.</summary>
        public bool Retired;
    }

    private enum RunEnd { Empty, Stopped }

    private readonly Func<ApiModeEventBatch, CancellationToken, Task<ApiModeEventWatermark?>> _push;
    private readonly Func<bool> _canPush;
    private readonly int _maxBatchEvents;
    private readonly int _maxBatchCharacters;
    private readonly int _maxHeldEvents;
    private readonly long _maxHeldCharacters;
    private readonly TimeSpan _sweepInterval;
    private readonly CcDirector.Core.Background.BackgroundJobs _jobs;
    private readonly ConcurrentDictionary<string, SessionState> _states = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private CcDirector.Core.Background.BackgroundJob? _sweepJob;

    /// <param name="push">Send one batch and return the Gateway's answer. Throws when the tunnel is down.</param>
    /// <param name="canPush">Whether the Gateway on the other end advertises <see cref="HubMethod"/>.</param>
    /// <param name="maxBatchEvents">Events per batch; the contract's ceiling unless a test asks for fewer.</param>
    /// <param name="maxBatchCharacters">Characters of event JSON per batch; the contract's ceiling unless a test asks for fewer.</param>
    /// <param name="maxHeldEvents">The memory bound on events held per session.</param>
    /// <param name="maxHeldCharacters">The memory bound on characters held per session.</param>
    public ApiModeEventPusher(
        Func<ApiModeEventBatch, CancellationToken, Task<ApiModeEventWatermark?>> push,
        Func<bool> canPush,
        int maxBatchEvents = ApiModeEventBatch.MaxEvents,
        int maxBatchCharacters = ApiModeEventBatch.MaxCharacters,
        int maxHeldEvents = DefaultMaxHeldEventsPerSession,
        long maxHeldCharacters = DefaultMaxHeldCharactersPerSession,
        TimeSpan? sweepInterval = null,
        CcDirector.Core.Background.BackgroundJobs? jobs = null)
    {
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _canPush = canPush ?? throw new ArgumentNullException(nameof(canPush));
        if (maxBatchEvents < 1 || maxBatchEvents > ApiModeEventBatch.MaxEvents)
            throw new ArgumentOutOfRangeException(nameof(maxBatchEvents), maxBatchEvents, $"Between 1 and {ApiModeEventBatch.MaxEvents}.");
        if (maxBatchCharacters < 1 || maxBatchCharacters > ApiModeEventBatch.MaxCharacters)
            throw new ArgumentOutOfRangeException(nameof(maxBatchCharacters), maxBatchCharacters, $"Between 1 and {ApiModeEventBatch.MaxCharacters}.");
        if (maxHeldEvents < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHeldEvents), maxHeldEvents, "At least one event must be held.");
        if (maxHeldCharacters < 1)
            throw new ArgumentOutOfRangeException(nameof(maxHeldCharacters), maxHeldCharacters, "At least one character must be held.");
        _maxBatchEvents = maxBatchEvents;
        _maxBatchCharacters = maxBatchCharacters;
        _maxHeldEvents = maxHeldEvents;
        _maxHeldCharacters = maxHeldCharacters;
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
        _jobs = jobs ?? CcDirector.Core.Background.BackgroundJobs.Default;
        FileLog.Write($"[ApiModeEventPusher] Created: batch={maxBatchEvents} events or {maxBatchCharacters} characters, held per session={maxHeldEvents} events or {maxHeldCharacters} characters");
    }

    /// <summary>How many events are held for <paramref name="sessionId"/>, not yet acknowledged by the Gateway.</summary>
    public int HeldCount(string sessionId)
    {
        if (!_states.TryGetValue(sessionId, out var state))
            return 0;
        lock (state)
            return state.Held.Count;
    }

    /// <summary>
    /// Hold one session's events and send them. Called from the session's event loop under its lock, so it returns at
    /// once: each event is written to JSON and queued here, and the send runs on its own.
    /// </summary>
    public void Publish(SessionEventBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);
        if (batch.Events.Count == 0)
            return;
        var held = batch.Events.Select(e => new HeldEvent(batch.Generation, batch.GenerationStartedUtc, e.Sequence,
            JsonSerializer.Serialize(e, SessionEventJson.Options))).ToList();
        while (true)
        {
            var state = _states.GetOrAdd(batch.SessionId, _ => new SessionState());
            lock (state)
            {
                if (state.Retired)
                {
                    _states.TryRemove(new KeyValuePair<string, SessionState>(batch.SessionId, state));
                    continue;
                }
                foreach (var e in held)
                {
                    state.Held.Add(e);
                    state.HeldCharacters += e.Json.Length;
                }
                EnforceBoundLocked(batch.SessionId, state);
            }
            break;
        }
        Trigger(batch.SessionId);
    }

    /// <summary>Fire-and-forget: send what is held for one session. Coalesces with a run already in progress.</summary>
    public void Trigger(string sessionId) => _ = PushSessionAsync(sessionId, _stopping.Token);

    /// <summary>
    /// The Gateway answered a Hello - a new connection, a reconnect after a Gateway restart or a deploy, or the regular
    /// re-push. Every session with events the Gateway has not acknowledged sends them again, from the oldest held. Returns
    /// that work, which never throws; the Director does not wait for it, and a test awaits it.
    /// </summary>
    public Task ResendAllAsync()
    {
        var sessions = _states.Where(kv => HeldCount(kv.Key) > 0).Select(kv => kv.Key).ToList();
        if (sessions.Count > 0)
            FileLog.Write($"[ApiModeEventPusher] ResendAllAsync: {sessions.Count} session(s) hold events the Gateway has not acknowledged; sending them again");
        return Task.WhenAll(sessions.Select(sid => PushSessionAsync(sid, _stopping.Token)));
    }

    /// <summary>Start the safety sweep: a slow-and-steady job on the scheduler (docs/BackgroundWork.md).</summary>
    public void Start()
    {
        if (_sweepJob is not null)
            return;
        FileLog.Write($"[ApiModeEventPusher] Start: sweep every {_sweepInterval.TotalSeconds}s");
        _sweepJob = _jobs.Register(
            new CcDirector.Core.Background.BackgroundJobSpec("API-mode event sweep", CcDirector.Core.Background.BackgroundJobTier.SlowAndSteady,
                _sweepInterval, "the pusher is disposed"),
            _ => ResendAllAsync());
        _sweepJob.StartTimer();
    }

    /// <summary>
    /// One session's run: send what is held, a batch at a time, until nothing is held or the Gateway stops the run (a
    /// refusal for now, an exception, a watermark short of what was sent). Never overlaps itself for one session; never
    /// throws.
    /// </summary>
    public async Task PushSessionAsync(string sessionId, CancellationToken ct = default)
    {
        SessionState state;
        while (true)
        {
            state = _states.GetOrAdd(sessionId, _ => new SessionState());
            var claimed = false;
            lock (state)
            {
                if (!state.Retired)
                {
                    if (!_canPush())
                    {
                        if (!state.LoggedUnsupported && state.Held.Count > 0)
                        {
                            state.LoggedUnsupported = true;
                            FileLog.Write($"[ApiModeEventPusher] session={sessionId}: the Gateway does not advertise {HubMethod}; its {state.Held.Count} event(s) are held until one that does answers a Hello");
                        }
                        return;
                    }
                    state.Pending = true;
                    if (state.Running)
                        return;     // the runner sees Pending before it leaves
                    state.Running = true;
                    claimed = true;
                }
            }
            if (claimed)
                break;
            _states.TryRemove(new KeyValuePair<string, SessionState>(sessionId, state));
        }

        try
        {
            while (true)
            {
                lock (state)
                {
                    if (!state.Pending)
                    {
                        state.Running = false;
                        if (state.Held.Count == 0)
                        {
                            state.Retired = true;
                            _states.TryRemove(new KeyValuePair<string, SessionState>(sessionId, state));
                        }
                        return;
                    }
                    state.Pending = false;
                }

                if (await SendHeldAsync(sessionId, state, ct).ConfigureAwait(false) == RunEnd.Stopped)
                {
                    // What is held waits for the next trigger: a later publish, the next Hello or the sweep. A trigger that
                    // arrived during this run is left pending for them, so it is not lost either.
                    lock (state)
                        state.Running = false;
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            // An entry point: a failure here is a defect in this pusher. What is held stays held for the next trigger.
            FileLog.Write($"[ApiModeEventPusher] PushSessionAsync FAILED: session={sessionId}: {ex}");
            lock (state)
                state.Running = false;
        }
    }

    public async ValueTask DisposeAsync()
    {
        FileLog.Write("[ApiModeEventPusher] DisposeAsync");
        _stopping.Cancel();
        // Wait out a sweep in flight, as the turn pusher does: a sweep that outlived this call would push at a stream
        // the host has already stopped.
        if (_sweepJob is { } job)
        {
            _sweepJob = null;
            await job.StopAsync().ConfigureAwait(false);
        }
        _stopping.Dispose();
    }

    // ---------------------------------------------------------------- one run

    private async Task<RunEnd> SendHeldAsync(string sessionId, SessionState state, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var batch = TakeBatch(sessionId, state);
            if (batch is null)
                return RunEnd.Empty;
            var (request, sent) = batch.Value;
            var first = sent[0].Sequence;
            var last = sent[^1].Sequence;

            ApiModeEventWatermark? answer;
            try
            {
                answer = await _push(request, ct).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Nothing kept on the Gateway; the same events go again at the next trigger.
                FileLog.Write($"[ApiModeEventPusher] push FAILED: session={sessionId}, generation={request.Generation}, sequences {first}..{last}: {ex.Message}; held for the next trigger");
                return RunEnd.Stopped;
            }

            if (answer is null)
            {
                // Refused for good: re-sending the same batch cannot succeed. Logged with what it was, and dropped.
                FileLog.Write($"[ApiModeEventPusher] REFUSED FOR GOOD by the Gateway: session={sessionId}, generation={request.Generation}, sequences {first}..{last} ({sent.Count} events); dropped, not sent again");
                lock (state)
                    Remove(state, sent);
                continue;
            }

            if (string.IsNullOrEmpty(answer.Generation))
            {
                FileLog.Write($"[ApiModeEventPusher] refused for now by the Gateway: session={sessionId}, generation={request.Generation}, sequences {first}..{last}; held, sent again at the next trigger");
                return RunEnd.Stopped;
            }

            if (!string.Equals(answer.Generation, request.Generation, StringComparison.Ordinal))
            {
                FileLog.Write($"[ApiModeEventPusher] the Gateway answered generation {answer.Generation} for a batch of generation {request.Generation}: session={sessionId}, sequences {first}..{last}; held, sent again at the next trigger");
                return RunEnd.Stopped;
            }

            int dropped;
            lock (state)
                dropped = Acknowledge(state, answer.Generation, answer.Sequence);
            if (dropped == 0)
            {
                // A stored answer that covers none of what was sent cannot move this run forward: sending the same batch
                // again would get the same answer. Stop; the next trigger tries again.
                FileLog.Write($"[ApiModeEventPusher] the Gateway's watermark (generation {answer.Generation}, sequence {answer.Sequence}) covers none of the events sent: session={sessionId}, sequences {first}..{last}; held, sent again at the next trigger");
                return RunEnd.Stopped;
            }
            if (answer.Sequence < last)
            {
                FileLog.Write($"[ApiModeEventPusher] the Gateway holds session={sessionId} generation={answer.Generation} up to sequence {answer.Sequence}, short of the {last} sent; the rest is held and sent again at the next trigger");
                return RunEnd.Stopped;
            }
            FileLog.Write($"[ApiModeEventPusher] stored: session={sessionId}, generation={answer.Generation}, sequences {first}..{last}, watermark {answer.Sequence}, {dropped} acknowledged");
        }
        return RunEnd.Stopped;
    }

    /// <summary>The oldest held events of one generation, bounded by count and characters; null when nothing is held. An
    /// event too large for any batch is dropped by name.</summary>
    private (ApiModeEventBatch Request, List<HeldEvent> Sent)? TakeBatch(string sessionId, SessionState state)
    {
        lock (state)
        {
            while (state.Held.Count > 0 && state.Held[0].Json.Length > _maxBatchCharacters)
            {
                var tooLarge = state.Held[0];
                FileLog.Write($"[ApiModeEventPusher] DROPPED: session={sessionId}, generation={tooLarge.Generation}, sequence {tooLarge.Sequence}: its JSON is {tooLarge.Json.Length} characters, more than a batch may carry ({_maxBatchCharacters}); the Gateway will see a gap at this sequence");
                Remove(state, [tooLarge]);
            }
            if (state.Held.Count == 0)
                return null;

            var head = state.Held[0];
            var sent = new List<HeldEvent>();
            var characters = 0L;
            foreach (var e in state.Held)
            {
                if (e.Generation != head.Generation || sent.Count == _maxBatchEvents || characters + e.Json.Length > _maxBatchCharacters)
                    break;
                sent.Add(e);
                characters += e.Json.Length;
            }
            var request = new ApiModeEventBatch
            {
                SessionId = sessionId,
                Generation = head.Generation,
                GenerationStartedUtc = head.GenerationStartedUtc,
                Events = sent.Select(e => e.Json).ToList(),
            };
            return (request, sent);
        }
    }

    private static int Acknowledge(SessionState state, string generation, long sequence)
    {
        var covered = state.Held.Where(e => e.Generation == generation && e.Sequence <= sequence).ToList();
        Remove(state, covered);
        return covered.Count;
    }

    private static void Remove(SessionState state, IReadOnlyCollection<HeldEvent> events)
    {
        foreach (var e in events)
        {
            if (state.Held.Remove(e))
                state.HeldCharacters -= e.Json.Length;
        }
    }

    /// <summary>The memory bound: drop the oldest held events past it, and say exactly which.</summary>
    private void EnforceBoundLocked(string sessionId, SessionState state)
    {
        if (state.Held.Count <= _maxHeldEvents && state.HeldCharacters <= _maxHeldCharacters)
            return;
        var dropped = new List<HeldEvent>();
        while (state.Held.Count > 1 && (state.Held.Count > _maxHeldEvents || state.HeldCharacters > _maxHeldCharacters))
        {
            var oldest = state.Held[0];
            state.Held.RemoveAt(0);
            state.HeldCharacters -= oldest.Json.Length;
            dropped.Add(oldest);
        }
        foreach (var group in dropped.GroupBy(e => e.Generation))
        {
            FileLog.Write($"[ApiModeEventPusher] DROPPED UNACKNOWLEDGED EVENTS: session={sessionId}, generation={group.Key}, " +
                          $"sequences {group.First().Sequence}..{group.Last().Sequence} ({group.Count()} events): the Gateway has not acknowledged them " +
                          $"and the Director holds at most {_maxHeldEvents} events or {_maxHeldCharacters} characters per session; the Gateway will see a gap");
        }
    }
}
