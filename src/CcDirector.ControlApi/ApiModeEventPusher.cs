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
/// TRIGGERS: every publish; the FIRST Hello answered on each new connection (so after a reconnect, a Gateway restart or a
/// deploy, every event the Gateway has not acknowledged is sent again from the oldest held - which is what keeps "Working -
/// turn N" right after a Gateway restart during a turn); and a slow safety sweep. Not the Hello of the regular ten-second
/// re-push: on one connection the Gateway has already answered every batch, and a Gateway that keeps refusing would
/// otherwise get the same batch six times a minute per session (review of pull request #4090, finding 1). Runs for one
/// session never overlap: a trigger during a run marks it pending, and the runner checks that under the same lock before
/// it leaves.
///
/// ONLY WHEN THE GATEWAY HAS <c>PushApiModeEvents</c>. Against a Gateway without it the events are held, and that is
/// logged once per session; they go up when a Gateway that has it answers a Hello.
///
/// THE MEMORY BOUND (stated in the pull request): at most <see cref="DefaultMaxHeldEventsPerSession"/> events and
/// <see cref="DefaultMaxHeldCharactersPerSession"/> characters of event JSON are held per session. Past either, the
/// OLDEST held events are dropped, and every drop is logged loudly with the session, the generation and the exact
/// sequences dropped. An event too large to fit any batch is dropped the same way, by name. Nothing is ever dropped
/// without saying so.
///
/// A HOLE STARTS A NEW GENERATION (review of pull request #4090, finding 2). The Gateway stores any sequence above the
/// highest it holds without checking for a gap, so a hole in the middle of a generation would be invisible to it and to
/// every reader. The pusher therefore sends a generation's events under that generation only while it has every one of
/// them from sequence 1. At the first one it does not have - dropped by the bound, too large for a batch, or never handed
/// to it (the Gateway was turned off in the settings while the session ran) - the events after the hole go up as a NEW
/// generation, a fresh id started one millisecond after the one it continues, counted again from sequence 1. That is a
/// rule the Gateway already keeps: a later generation replaces the session's, and a reader whose cursor is of the earlier
/// one is answered "not complete" and reads again. The session's own stream on the Director keeps its one generation;
/// only what the Gateway is sent is re-labelled, and the change is logged with both ids. What it cannot do: an event in
/// the hole never reaches the Gateway, so the durable receipt of a prompt whose own event fell in it keeps the state it
/// had.
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

    /// <summary>One held event: the session's own (<see cref="Event"/>, of <see cref="SourceGeneration"/>), and what the
    /// Gateway is sent for it - the generation, start and sequence it goes up under, and its JSON.</summary>
    private sealed record HeldEvent(SessionEvent Event, string SourceGeneration, string Generation, DateTime GenerationStartedUtc,
        long Sequence, string Json);

    /// <summary>How one of the session's generations is sent: under <see cref="Label"/>, with every sequence above
    /// <see cref="Offset"/> counted again from 1. <see cref="LastSeen"/> is the last of the session's own sequences
    /// handed to the pusher, so a hole in what is handed over is seen.</summary>
    private sealed class Segment
    {
        public required string Label;
        public required DateTime StartedUtc;
        public required long Offset;
        public long LastSeen;
    }

    private sealed class SessionState
    {
        // Every field is read and written under lock (this).
        public readonly List<HeldEvent> Held = [];
        public long HeldCharacters;
        public bool Pending;
        public bool Running;
        public bool LoggedUnsupported;
        /// <summary>By the session's own generation: how its events are sent now. The same table for every state the
        /// session has had, because a state retires whenever nothing is held and what was last handed over must outlive
        /// it (or the next event would look like a hole).</summary>
        public required Dictionary<string, Segment> Segments { get; init; }
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
    // Per session, how its generations are sent (see SessionState.Segments). One small entry per API-mode session this
    // Director process has run; a session's stream has one generation for the life of the process.
    private readonly ConcurrentDictionary<string, Dictionary<string, Segment>> _segments = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _stopping = new();
    private CcDirector.Core.Background.BackgroundJob? _sweepJob;
    // 1 until the first Hello of a connection has been answered: that Hello, and only that one, resends what is held.
    private int _resendOnNextHello = 1;

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

    private SessionState NewState(string sessionId) =>
        new() { Segments = _segments.GetOrAdd(sessionId, _ => new Dictionary<string, Segment>(StringComparer.Ordinal)) };

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
        while (true)
        {
            var state = _states.GetOrAdd(batch.SessionId, NewState);
            lock (state)
            {
                if (state.Retired)
                {
                    _states.TryRemove(new KeyValuePair<string, SessionState>(batch.SessionId, state));
                    continue;
                }
                foreach (var e in batch.Events)
                {
                    var segment = SegmentForLocked(batch.SessionId, state, batch.Generation, batch.GenerationStartedUtc, e.Sequence);
                    var held = Hold(e, batch.Generation, segment);
                    state.Held.Add(held);
                    state.HeldCharacters += held.Json.Length;
                }
                EnforceBoundLocked(batch.SessionId, state);
            }
            break;
        }
        Trigger(batch.SessionId);
    }

    /// <summary>Fire-and-forget: send what is held for one session. Coalesces with a run already in progress.</summary>
    public void Trigger(string sessionId) => _ = PushSessionAsync(sessionId, _stopping.Token);

    /// <summary>A new connection to the Gateway (the first, a reconnect, a Gateway restart or a deploy): its first
    /// answered Hello resends what is held.</summary>
    public void NewConnection()
    {
        Interlocked.Exchange(ref _resendOnNextHello, 1);
        FileLog.Write("[ApiModeEventPusher] NewConnection: the next answered Hello resends every held event");
    }

    /// <summary>
    /// The Gateway answered a Hello. The first on a connection resends every held event (<see cref="ResendAllAsync"/>);
    /// the regular ten-second re-push's Hello on the same connection does nothing. Returns that work, which never throws.
    /// </summary>
    public Task HelloAnsweredAsync()
    {
        if (Interlocked.Exchange(ref _resendOnNextHello, 0) == 0)
            return Task.CompletedTask;
        return ResendAllAsync();
    }

    /// <summary>
    /// Every session with events the Gateway has not acknowledged sends them again, from the oldest held: the first Hello
    /// of a connection, and the sweep. Returns that work, which never throws; the Director does not wait for it, and a
    /// test awaits it.
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
            state = _states.GetOrAdd(sessionId, NewState);
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
                FileLog.Write($"[ApiModeEventPusher] DROPPED: session={sessionId}, generation={tooLarge.SourceGeneration}, sequence {tooLarge.Event.Sequence}: its JSON is {tooLarge.Json.Length} characters, more than a batch may carry ({_maxBatchCharacters}); what follows it goes up as a new generation");
                Remove(state, [tooLarge]);
                AfterHoleLocked(sessionId, state, tooLarge.SourceGeneration, tooLarge.Event.Sequence, "an event too large for any batch was dropped");
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
        foreach (var group in dropped.GroupBy(e => e.SourceGeneration))
        {
            FileLog.Write($"[ApiModeEventPusher] DROPPED UNACKNOWLEDGED EVENTS: session={sessionId}, generation={group.Key}, " +
                          $"sequences {group.First().Event.Sequence}..{group.Last().Event.Sequence} ({group.Count()} events): the Gateway has not acknowledged them " +
                          $"and the Director holds at most {_maxHeldEvents} events or {_maxHeldCharacters} characters per session; what follows them goes up as a new generation");
            AfterHoleLocked(sessionId, state, group.Key, group.Last().Event.Sequence, "the memory bound dropped events");
        }
    }

    // ---------------------------------------------------------------- generations as the Gateway is sent them

    /// <summary>The segment the session's event <paramref name="sequence"/> of <paramref name="generation"/> is sent
    /// under. A sequence that does not follow the last one handed over is a hole: a new generation starts at it.</summary>
    private static Segment SegmentForLocked(string sessionId, SessionState state, string generation, DateTime startedUtc, long sequence)
    {
        if (!state.Segments.TryGetValue(generation, out var segment))
        {
            segment = new Segment { Label = generation, StartedUtc = startedUtc, Offset = 0 };
            state.Segments[generation] = segment;
        }
        if (sequence != segment.LastSeen + 1)
        {
            var why = segment.LastSeen == 0
                ? $"its sequences 1..{sequence - 1} never reached the pusher"
                : $"its sequences {segment.LastSeen + 1}..{sequence - 1} never reached the pusher";
            segment = NewSegmentLocked(sessionId, state, generation, sequence - 1, why);
        }
        segment.LastSeen = sequence;
        return segment;
    }

    /// <summary>The session's events of <paramref name="generation"/> through <paramref name="holeThrough"/> will never
    /// reach the Gateway. If the generation is still sent under a segment the hole would sit inside, what follows goes
    /// up as a new one.</summary>
    private static void AfterHoleLocked(string sessionId, SessionState state, string generation, long holeThrough, string why)
    {
        if (state.Segments.TryGetValue(generation, out var segment) && holeThrough > segment.Offset)
            NewSegmentLocked(sessionId, state, generation, holeThrough, why);
    }

    /// <summary>Send the session's events of <paramref name="generation"/> above <paramref name="holeThrough"/> as a new
    /// generation from sequence 1, started a millisecond after the one it continues, and re-label those already held.</summary>
    private static Segment NewSegmentLocked(string sessionId, SessionState state, string generation, long holeThrough, string why)
    {
        var previous = state.Segments[generation];
        var segment = new Segment
        {
            Label = Guid.NewGuid().ToString(),
            StartedUtc = previous.StartedUtc.AddMilliseconds(1),
            Offset = holeThrough,
            LastSeen = previous.LastSeen,
        };
        state.Segments[generation] = segment;
        var relabelled = 0;
        for (var i = 0; i < state.Held.Count; i++)
        {
            var e = state.Held[i];
            if (e.SourceGeneration != generation || e.Event.Sequence <= holeThrough)
                continue;
            var again = Hold(e.Event, generation, segment);
            state.HeldCharacters += again.Json.Length - e.Json.Length;
            state.Held[i] = again;
            relabelled++;
        }
        FileLog.Write($"[ApiModeEventPusher] A HOLE STARTS A NEW GENERATION: session={sessionId}, generation={generation} " +
                      $"(sent as {previous.Label}): {why}, so its events through sequence {holeThrough} that the Gateway does not hold " +
                      $"never will. Its events from sequence {holeThrough + 1} go up as generation {segment.Label} " +
                      $"(started {segment.StartedUtc:O}) from sequence 1, {relabelled} of them already held; the Gateway takes the later " +
                      "generation as a fresh start and answers a reader of the earlier one that its stream is not complete. " +
                      "A receipt whose own event fell in the hole is not corrected.");
        return segment;
    }

    private static HeldEvent Hold(SessionEvent e, string sourceGeneration, Segment segment)
    {
        var sent = segment.Offset == 0 ? e : e with { Sequence = e.Sequence - segment.Offset };
        return new HeldEvent(e, sourceGeneration, segment.Label, segment.StartedUtc, sent.Sequence,
            JsonSerializer.Serialize(sent, SessionEventJson.Options));
    }
}
