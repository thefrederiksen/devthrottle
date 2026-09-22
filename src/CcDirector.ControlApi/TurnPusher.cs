using System.Collections.Concurrent;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.ControlApi;

/// <summary>
/// The Director side of the turn-push mission (<c>docs/missions/turn-push-2026-09-01/brief.md</c>): keeps
/// the Gateway's stored conversation for every session current by PUSHING what it has not seen. Once this
/// runs, the Gateway never asks a Director to re-read a transcript.
///
/// Per session it remembers the generation it last pushed, how far the Gateway's watermark had got, and -
/// per SOURCE, for the life of this process - when that source was first seen. A run reads the session's
/// whole conversation once (<see cref="TurnSnapshot"/>), slices from the watermark, sends bounded batches,
/// and moves the watermark to what the Gateway answered. If the source changed (a /clear, a transcript
/// that moved into a worktree), it starts a new generation at ordinal zero.
///
/// WHY THE START STAMP IS "FIRST SEEN", NOT "NOW". The Gateway switches a session to a generation only when
/// that generation STARTED LATER than the one it is on. A Director that re-stamped every read with now
/// could take a stale read of an old source - the pointer moved between the read and the answer - and, on
/// its next run, hand the old source a newer stamp than the new one, and the Gateway would put the old
/// conversation back on the reader's screen. First-seen stamps are stable within a process, so an old
/// source keeps its old stamp however often it is re-read; only a source never seen before gets a fresh,
/// later one. After a restart the current source is stamped now, which is later than anything, and that is
/// correct: at restart the current source IS the latest.
///
/// TRIGGERS ARE DETERMINISTIC, NOT SAMPLED. The Director fires a run at its own turn-end edge (the same
/// place it pushes the session delta), on session creation, on every Hello (backfill from the watermarks
/// the Gateway hands back, and a reset for every session it does NOT hand back), and from a slow safety
/// sweep that catches anything the edges missed. Runs for one session never overlap: a trigger during a run
/// marks it pending under the session's lock, and the runner checks that flag under the same lock before
/// it leaves, so a wake-up cannot be lost. A run is bounded - so many batches per round, so many rounds per
/// call - so a session that never stops changing cannot hold the sweep hostage.
///
/// Built on delegates, not on the session manager or the stream client directly, so every branch is tested
/// with a fake conversation and a fake Gateway.
/// </summary>
public sealed class TurnPusher : IAsyncDisposable
{
    /// <summary>How often the safety sweep visits every session. A minute: late enough to cost nothing on
    /// an idle fleet, soon enough that a missed edge is corrected before anyone notices.</summary>
    public static readonly TimeSpan DefaultSweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>The most batches one round sends before it stops. A long backfill goes round again on the
    /// next trigger or sweep rather than holding one run open for minutes.</summary>
    public const int MaxBatchesPerRound = 20;

    /// <summary>The most rounds one call makes for one session before it yields, even if triggers keep
    /// arriving. What is still pending is picked up by the next trigger or the sweep.</summary>
    public const int MaxRoundsPerCall = 3;

    /// <summary>Turns per push, the Gateway's own ceiling.</summary>
    public const int BatchSize = 500;

    private sealed class SessionState
    {
        // Every field is read and written under lock (this).
        public string? Generation;
        public int Pushed;
        public string? LastHistoryState;
        public bool SentUnsupported;
        public string? RefusedGeneration;
        public bool Pending;
        public bool Running;
        /// <summary>The sweep has taken this state out of the table. A caller holding it must drop it and take
        /// a fresh one, so a run can never attach to a state nothing can find.</summary>
        public bool Retired;
        /// <summary>The latest stamp this session has issued, so the next one can be made strictly later.</summary>
        public DateTime LastStampUtc;
        /// <summary>Source -> when this process first saw it for this session. Never overwritten.</summary>
        public readonly Dictionary<string, DateTime> FirstSeen = new(StringComparer.Ordinal);
    }

    private readonly Func<IReadOnlyCollection<Guid>> _sessionIds;
    private readonly Func<Guid, TurnSnapshot?> _snapshot;
    private readonly Func<TurnPushBatch, CancellationToken, Task<TurnWatermark?>> _push;
    private readonly Func<bool> _canPush;
    private readonly Func<DateTime> _clock;
    private readonly ConcurrentDictionary<Guid, SessionState> _states = new();
    private readonly CancellationTokenSource _stopping = new();
    private readonly TimeSpan _sweepInterval;
    private readonly CcDirector.Core.Background.BackgroundJobs _jobs;
    private CcDirector.Core.Background.BackgroundJob? _sweepJob;

    /// <param name="sessionIds">The live sessions to sweep.</param>
    /// <param name="snapshot">The session's conversation now, or null when the session is gone.</param>
    /// <param name="push">Send one batch to the Gateway and return its watermark; null when the Gateway
    /// refused the batch. Throws when the tunnel is down - the run logs and stops, the sweep retries.</param>
    /// <param name="canPush">Whether the Gateway on the other end has the PushTurns hub method at all. An
    /// older Gateway does not, and pushing at it would only fill the log.</param>
    public TurnPusher(
        Func<IReadOnlyCollection<Guid>> sessionIds,
        Func<Guid, TurnSnapshot?> snapshot,
        Func<TurnPushBatch, CancellationToken, Task<TurnWatermark?>> push,
        Func<bool> canPush,
        Func<DateTime>? clock = null,
        TimeSpan? sweepInterval = null,
        CcDirector.Core.Background.BackgroundJobs? jobs = null)
    {
        _jobs = jobs ?? CcDirector.Core.Background.BackgroundJobs.Default;
        _sessionIds = sessionIds ?? throw new ArgumentNullException(nameof(sessionIds));
        _snapshot = snapshot ?? throw new ArgumentNullException(nameof(snapshot));
        _push = push ?? throw new ArgumentNullException(nameof(push));
        _canPush = canPush ?? throw new ArgumentNullException(nameof(canPush));
        _clock = clock ?? (() => DateTime.UtcNow);
        _sweepInterval = sweepInterval ?? DefaultSweepInterval;
    }

    /// <summary>
    /// The Gateway's watermarks, handed back on Hello: what it already holds per session. A FULL
    /// reconciliation: a session the Gateway lists resumes from its watermark; a session it does not list
    /// is reset so it is pushed from the start (the Gateway was replaced, or lost its rows). Then a sweep
    /// brings every live session current.
    /// </summary>
    public void SeedWatermarks(IReadOnlyList<TurnWatermark> watermarks)
    {
        ArgumentNullException.ThrowIfNull(watermarks);
        var listed = new HashSet<Guid>();
        var seeded = 0;
        foreach (var mark in watermarks)
        {
            if (!Guid.TryParse(mark.SessionId, out var sid)) continue;
            listed.Add(sid);
            var state = _states.GetOrAdd(sid, _ => new SessionState());
            lock (state)
            {
                state.Generation = mark.Generation;
                state.Pushed = mark.Count;
                state.LastHistoryState = null;     // the head's state is unknown to us; the next run refreshes it
                state.SentUnsupported = false;
                // The refusal is NOT cleared here. A refusal means the batch was malformed - a bug on this
                // side - so re-sending the same generation after a reconnect would only be refused again,
                // every sweep, forever. It clears when the source changes, or on the full reset below.
            }
            seeded++;
        }
        var reset = 0;
        foreach (var (sid, state) in _states)
        {
            if (listed.Contains(sid)) continue;
            lock (state)
            {
                if (state.Generation is null && state.Pushed == 0) continue;
                state.Generation = null;
                state.Pushed = 0;
                state.LastHistoryState = null;
                state.SentUnsupported = false;
                state.RefusedGeneration = null;
            }
            reset++;
        }
        FileLog.Write($"[TurnPusher] Hello: seeded {seeded} watermark(s) from the Gateway; reset {reset} session(s) it did not list, so they are pushed from the start");
        _ = SweepAsync(_stopping.Token);
    }

    /// <summary>Fire-and-forget: bring one session current. Coalesces with a run already in progress.</summary>
    public void Trigger(Guid sessionId) => _ = PushSessionAsync(sessionId, _stopping.Token);

    /// <summary>Start the safety sweep: a slow-and-steady job on the scheduler (docs/BackgroundWork.md),
    /// first after one interval, then every interval, never overlapping.</summary>
    public void Start()
    {
        if (_sweepJob is not null) return;
        _sweepJob = _jobs.Register(
            new CcDirector.Core.Background.BackgroundJobSpec("Turn sweep", CcDirector.Core.Background.BackgroundJobTier.SlowAndSteady, _sweepInterval, "the pusher is disposed"),
            _ => SweepAsync(_stopping.Token));
        _sweepJob.StartTimer();
    }

    /// <summary>Bring every live session current, one after another. Never throws into a timer.</summary>
    public async Task SweepAsync(CancellationToken ct = default)
    {
        if (!_canPush()) return;
        IReadOnlyCollection<Guid> ids;
        try { ids = _sessionIds(); }
        catch (Exception ex) { FileLog.Write($"[TurnPusher] sweep could not list sessions: {ex.Message}"); return; }
        foreach (var sid in ids)
        {
            if (ct.IsCancellationRequested) return;
            await PushSessionAsync(sid, ct).ConfigureAwait(false);
        }
        // Forget sessions that no longer exist, so the state table does not grow for the life of the process.
        // NEVER a session that is mid-run or has a trigger waiting: dropping its state would lose its
        // watermark, and the next run would re-push the whole conversation from ordinal zero.
        //
        // RETIRE-THEN-REMOVE, because a check followed by a removal is not atomic and a trigger can arrive
        // between the two (found in review). The flag is set under the same lock a trigger takes, so exactly
        // one of them wins: either the sweep retires the state and the trigger goes and takes a fresh one, or
        // the trigger claims it first and the sweep leaves it alone.
        foreach (var (known, state) in _states)
        {
            if (ids.Contains(known)) continue;
            var retire = false;
            lock (state)
            {
                if (!state.Running && !state.Pending && !state.Retired)
                    retire = state.Retired = true;
            }
            if (retire) _states.TryRemove(new KeyValuePair<Guid, SessionState>(known, state));
        }
    }

    /// <summary>
    /// One session's run: read, slice from the watermark, push, move the watermark, until the Gateway holds
    /// everything or the run's budget is spent. Never overlaps itself for one session; a call during a run
    /// marks it pending under the lock, and the runner re-checks under the same lock before leaving, so the
    /// wake-up is never lost. Bounded to <see cref="MaxRoundsPerCall"/> rounds. Never throws.
    /// </summary>
    public async Task PushSessionAsync(Guid sessionId, CancellationToken ct = default)
    {
        if (!_canPush()) return;
        SessionState state;
        while (true)
        {
            state = _states.GetOrAdd(sessionId, _ => new SessionState());
            var claimed = false;
            lock (state)
            {
                if (!state.Retired)
                {
                    state.Pending = true;
                    if (state.Running) return;     // the runner sees Pending before it leaves
                    state.Running = true;
                    claimed = true;
                }
            }
            if (claimed) break;
            // The sweep retired this state between the lookup and the lock. Take it out of the table if it is
            // still there, and go round for a fresh one.
            _states.TryRemove(new KeyValuePair<Guid, SessionState>(sessionId, state));
        }
        var rounds = 0;
        // Whether the last round actually got a batch into the Gateway. It gates the hand-off below, so the
        // hand-off means what its comment says: a round that returned without reaching the Gateway at all
        // (nothing to send, a refused generation, a session that has gone) does not chain another call.
        var lastRoundReachedGateway = false;
        try
        {
            while (true)
            {
                var handOff = false;
                var capped = rounds >= MaxRoundsPerCall;
                lock (state)
                {
                    if (!state.Pending)
                    {
                        state.Running = false;
                        return;
                    }
                    if (capped)
                    {
                        // This call has had its rounds but work is still outstanding. Release the session, and
                        // if the last round actually reached the Gateway hand the rest to a fresh call at
                        // once - otherwise a trigger that arrived during the final round would wait out the
                        // minute-long sweep (found in review). After a FAILED round the hand-off is skipped on
                        // purpose: a dead tunnel would otherwise be retried in a tight chain of calls, and the
                        // sweep's minute is the right pace at which to wait for it to come back. The same
                        // applies to a round that never reached the Gateway for any other reason.
                        state.Running = false;
                        handOff = lastRoundReachedGateway;
                    }
                    else state.Pending = false;
                }
                if (capped)
                {
                    if (handOff) _ = Task.Run(() => PushSessionAsync(sessionId, ct), ct);
                    return;
                }
                rounds++;
                lastRoundReachedGateway = false;
                try
                {
                    var round = await RunOnceAsync(sessionId, state, ct).ConfigureAwait(false);
                    lastRoundReachedGateway = round.ReachedGateway;
                    // The round stopped on its batch budget with more of this conversation still to send.
                    // Mark it pending so the next round carries on at once: a long backfill should not be
                    // paid out one round per minute of sweep (found in review).
                    if (round.MoreToSend) lock (state) { state.Pending = true; }
                }
                catch (OperationCanceledException) { lock (state) { state.Running = false; } return; }
                catch (Exception ex)
                {
                    // A dropped tunnel mid-push lands here. Nothing is lost: the watermark the Gateway holds is
                    // the truth, and the next round resumes from whatever it answers.
                    //
                    // It goes BACK ROUND rather than returning. A trigger that arrived while this round was
                    // failing has set Pending, and returning here would clear the run without anyone consuming
                    // it - the turn would then wait for the next trigger or the minute-long sweep (found in
                    // review). The round cap bounds this, so a persistently failing push cannot spin.
                    FileLog.Write($"[TurnPusher] push for session={sessionId} stopped: {ex.Message}; resuming from the Gateway's watermark");
                }
            }
        }
        catch (Exception ex)
        {
            FileLog.Write($"[TurnPusher] run for session={sessionId} FAILED: {ex.Message}");
            lock (state) { state.Running = false; }
        }
    }

    /// <summary>What one round did: whether it got a batch into the Gateway (which is what makes handing the
    /// rest to a fresh call worth doing), and whether it stopped with more of this conversation still to
    /// send (which is what makes the next round worth running at once).</summary>
    private readonly record struct RoundResult(bool ReachedGateway, bool MoreToSend);

    private async Task<RoundResult> RunOnceAsync(Guid sessionId, SessionState state, CancellationToken ct)
    {
        var snap = _snapshot(sessionId);
        if (snap is null) return default;

        // Take a consistent copy of the state for this round, and apply the generation change under the lock.
        int pushed; DateTime started; string? lastHistoryState; bool sentUnsupported; bool refused;
        lock (state)
        {
            if (!string.Equals(state.Generation, snap.Generation, StringComparison.Ordinal))
            {
                FileLog.Write($"[TurnPusher] session={sessionId}: generation {(state.Generation is null ? "starts" : "changed")} -> {Short(snap.Generation)}; pushing from ordinal 0");
                state.Generation = snap.Generation;
                state.Pushed = 0;
                state.SentUnsupported = false;
                state.LastHistoryState = null;
            }
            if (!state.FirstSeen.TryGetValue(snap.Generation, out started))
            {
                // STRICTLY later than every stamp this session has already issued. The Gateway switches only
                // to a LATER generation, so a clock that has not moved since the last source - or has moved
                // BACKWARDS - would mint a stamp that could never win, and the session would be stuck on the
                // old conversation (found in review). A millisecond, because that is the precision the store
                // compares at.
                started = _clock();
                if (started <= state.LastStampUtc)
                    started = state.LastStampUtc + TimeSpan.FromMilliseconds(1);
                state.LastStampUtc = started;
                state.FirstSeen[snap.Generation] = started;
            }
            pushed = state.Pushed;
            lastHistoryState = state.LastHistoryState;
            sentUnsupported = state.SentUnsupported;
            refused = string.Equals(state.RefusedGeneration, snap.Generation, StringComparison.Ordinal);
        }
        if (refused) return default;   // the Gateway refused this generation once; a re-send would be the same batch. Logged then.

        if (!snap.IsSupported)
        {
            // Nothing to push and nothing that will ever change here, but the Gateway needs the head once so
            // it can answer "unsupported" for this session instead of "nothing has arrived yet".
            if (sentUnsupported) return default;
            var answer = await _push(NewBatch(snap, started, 0, Array.Empty<PushedTurn>()), ct).ConfigureAwait(false);
            lock (state) { if (answer is not null) state.SentUnsupported = true; else state.RefusedGeneration = snap.Generation; }
            return new RoundResult(answer is not null, MoreToSend: false);
        }

        var total = snap.Turns.Count;
        if (pushed > total)
        {
            // The same source has FEWER messages than the Gateway already holds. A transcript file does not
            // shrink in the ordinary course of things; if it did, the rows the Gateway holds are the record of
            // what was said, and re-numbering them would lie about it. Say so and wait for it to grow again.
            FileLog.Write($"[TurnPusher] session={sessionId}: source {Short(snap.Generation)} now holds {total} message(s) but the Gateway holds {pushed}; not re-pushing until it grows");
            return default;
        }

        var reached = false;
        var batches = 0;
        while (pushed < total && batches < MaxBatchesPerRound)
        {
            ct.ThrowIfCancellationRequested();
            var count = Math.Min(BatchSize, total - pushed);
            var slice = new PushedTurn[count];
            for (var i = 0; i < count; i++) slice[i] = snap.Turns[pushed + i];
            var mark = await _push(NewBatch(snap, started, pushed, slice), ct).ConfigureAwait(false);
            batches++;
            if (mark is null)
            {
                FileLog.Write($"[TurnPusher] session={sessionId}: the Gateway refused a batch from ordinal {pushed} of generation {Short(snap.Generation)}; not re-sending this generation (a refusal means the batch was malformed - a bug here, not a fault there)");
                lock (state) { state.RefusedGeneration = snap.Generation; }
                return new RoundResult(reached, MoreToSend: false);
            }
            if (!string.Equals(mark.Generation, snap.Generation, StringComparison.Ordinal))
            {
                // The Gateway is on a LATER source than the one just read - this read was already stale by the
                // time it arrived. Adopt the Gateway's view and stop; the next trigger re-reads. NOT marked
                // pending: going round again at once would re-read the same source and get the same answer.
                // And because this source keeps its first-seen stamp, a later re-read of it cannot outrank the
                // generation the Gateway is on.
                FileLog.Write($"[TurnPusher] session={sessionId}: the Gateway is on generation {Short(mark.Generation)} (watermark {mark.Count}), not the one just pushed; the next trigger re-reads");
                lock (state) { state.Generation = mark.Generation; state.Pushed = mark.Count; }
                return new RoundResult(ReachedGateway: true, MoreToSend: false);
            }
            reached = true;
            var before = pushed;
            pushed = mark.Count;
            lock (state) { state.Pushed = pushed; state.LastHistoryState = snap.HistoryState; }
            lastHistoryState = snap.HistoryState;
            if (pushed <= before)
            {
                // The Gateway did not advance past where this batch started: an earlier batch was lost and it
                // answered where it wants us to resume. The loop continues from there, bounded by the round's
                // batch budget, so a Gateway that never advances cannot spin this round forever.
                FileLog.Write($"[TurnPusher] session={sessionId}: the Gateway's watermark is {pushed} after a push from {before}; resuming from there");
            }
        }

        // Nothing new to send, but the transcript-derived state moved (a background agent started or stopped
        // between turns). The head carries it, so push an empty batch to refresh it.
        if (pushed == total && !string.Equals(lastHistoryState, snap.HistoryState, StringComparison.Ordinal))
        {
            var answer = await _push(NewBatch(snap, started, total, Array.Empty<PushedTurn>()), ct).ConfigureAwait(false);
            lock (state)
            {
                if (answer is not null) state.LastHistoryState = snap.HistoryState;
                else state.RefusedGeneration = snap.Generation;   // a refusal here is a malformed head - same treatment
            }
            reached |= answer is not null;
        }

        // More to send means the batch budget ran out, not that anything is wrong: the caller runs another
        // round rather than leaving the rest to the sweep.
        return new RoundResult(reached, MoreToSend: pushed < total);
    }

    private static TurnPushBatch NewBatch(TurnSnapshot snap, DateTime started, int startOrdinal, IReadOnlyList<PushedTurn> turns) => new()
    {
        SessionId = snap.SessionId,
        Generation = snap.Generation,
        GenerationStartedUtc = started,
        Agent = snap.Agent,
        IsSupported = snap.IsSupported,
        IsRawText = snap.IsRawText,
        HistoryState = snap.HistoryState,
        StartOrdinal = startOrdinal,
        TotalCount = snap.Turns.Count,
        Turns = turns.ToList(),
    };

    private static string Short(string generation)
        => generation.Length <= 40 ? generation : "..." + generation[^40..];

    public async ValueTask DisposeAsync()
    {
        _stopping.Cancel();
        // Wait out a sweep in flight, as the loop this replaced was awaited: the host nulls its
        // stream client at the top of its own stop, and a sweep that outlived this call would push
        // at nothing and log a stopped-stream line per remaining session.
        if (_sweepJob is { } job)
        {
            _sweepJob = null;
            await job.StopAsync().ConfigureAwait(false);
        }
        _stopping.Dispose();
    }
}
