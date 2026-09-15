using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Core.Wingman;
using CcDirector.Gateway.Briefing;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;

namespace CcDirector.Gateway.Wingman;

/// <summary>
/// Everything the turn-verdict seat needs from the Gateway around it, as one seam - the same shape, and for
/// the same reason, as <see cref="Supervision.ISupervisorEnvironment"/>. Production wires
/// <see cref="GatewayTurnVerdictEnvironment"/>; the tests wire a fake with no clock, no tunnel and no model,
/// and the held-check test wires the PRODUCTION environment over a real push store.
/// </summary>
public interface ITurnVerdictEnvironment
{
    /// <summary>This account's turn-verdict settings (the two switches, the ceiling, the settle, the timeout).</summary>
    TurnVerdictSettings Settings(TenantId tenant);

    /// <summary>
    /// Is a live owning session holding this session (the pushed field is still named HasLiveSupervisor)? Answered by resolving roles across the account's WHOLE fresh
    /// pushed roster - never read off the session's own row, because the push store nulls the role and the
    /// liveness answer at ingest so that only the Gateway can decide them. A check that read the row directly
    /// would answer "not held" for every session on the fleet and read every worker.
    /// </summary>
    bool HasLiveSupervisor(TenantId tenant, string sessionId);

    /// <summary>The session as the push store last saw it, or null when it is not in the fresh roster.</summary>
    SessionDto? ReadSessionFacts(TenantId tenant, string sessionId);

    /// <summary>The live screen over the tunnel, or null when it cannot be read. Unreadable is carried as no rows.</summary>
    Task<ScreenGridResponse?> ReadScreenGridAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct);

    /// <summary>The session's stored conversation, read inside the account's scope. Null: nothing stored yet.</summary>
    StoredConversation? ReadConversation(TenantId tenant, string sessionId);

    /// <summary>The account's spoken language - the "spoken" field is written in it.</summary>
    SpokenLanguage Language(TenantId tenant);

    /// <summary>The account's own narration instructions when it has replaced the shipped default, else null.</summary>
    string? CustomSpokenRules();

    /// <summary>The model id the judge runs on for this account, recorded on every verdict including a failed one.</summary>
    string JudgeModel(TenantId tenant);

    /// <summary>Ask the judge ONE question and return its raw answer. Throws on no answer, a rate limit, or an
    /// unusable provider; the service turns each into a stored failed record.</summary>
    Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, CancellationToken ct);

    /// <summary>This session's latest stored verdict in this account, or null.</summary>
    TurnVerdictDto? Latest(TenantId tenant, string sessionId);

    /// <summary>Store one verdict record, accepted or failed.</summary>
    void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict);

    /// <summary>Forget this session's stored verdicts. Returns how many rows went.</summary>
    int Invalidate(TenantId tenant, string sessionId);

    /// <summary>Whether somebody is listening to this session - a voice session is judged whatever the judge
    /// switch says, because its narration IS the verdict's spoken section.</summary>
    bool IsVoiceSession(TenantId tenant, string sessionId);

    /// <summary>Wait. The seat's only clock, so a test can run the settle delay instantly.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    /// <summary>Append one record to the activity ledger. Closed event and cause words; never screen text.</summary>
    void Record(TurnVerdictRecord record);

    /// <summary>Now, in UTC.</summary>
    DateTime NowUtc();
}

/// <summary>The judge's raw answer, which model gave it, and how long it took.</summary>
public sealed record TurnVerdictJudgeAnswer(string Raw, string Model, double ReplySeconds);

/// <summary>One line of the turn-verdict ledger. <paramref name="Detail"/> is control flow only - verdict
/// word, kind, verdict id, trigger - and never a word of a screen or a conversation.</summary>
public sealed record TurnVerdictRecord(
    TenantId Tenant,
    string DirectorId,
    string SessionId,
    string EventType,
    string Cause,
    string Detail);

/// <summary>What asked for a verdict. The free checks differ by trigger, so the trigger is carried rather
/// than guessed from the call site.</summary>
public enum TurnVerdictTrigger
{
    /// <summary>The detector observed a turn end. Every free check applies, the settle delay is waited out,
    /// the judge switch applies (except for a voice session), and the ceiling applies.</summary>
    TurnEnd,

    /// <summary>A voice session's own narration, including its booked re-attempts. Held and exited sessions
    /// are skipped; the judge switch and the ceiling do not apply, because somebody is listening.</summary>
    Voice,

    /// <summary>The idle voice sweep. Like <see cref="Voice"/>, but capped, and it never re-asks the judge
    /// about a screen whose last answer was refused - that would be a paid call every sweep pass.</summary>
    Sweep,

    /// <summary>A person pressed a button (explain, a spoken reply). Only the brand-new check applies.</summary>
    OnDemand,
}

/// <summary>How a verdict request ended.</summary>
public enum TurnVerdictOutcomeKind
{
    /// <summary>The judge was asked and the contract accepted its answer.</summary>
    Judged,
    /// <summary>The screen was unchanged, so the stored verdict was returned and nobody was asked.</summary>
    Reused,
    /// <summary>A free check stood the request down before anything was read or asked.</summary>
    Skipped,
    /// <summary>The judge was asked and no usable verdict came back. A failed record was stored.</summary>
    Failed,
    /// <summary>The session started working while the verdict was being formed. Nothing was stored.</summary>
    Cancelled,
}

/// <summary>Why a verdict request failed.</summary>
public enum TurnVerdictFailureKind
{
    None,
    /// <summary>No answer inside the timeout, or the call never arrived. Worth another attempt.</summary>
    DidNotAnswer,
    /// <summary>The provider said "not now". Worth another attempt after <see cref="TurnVerdictOutcome.RetryAfter"/>.</summary>
    RateLimited,
    /// <summary>The judge answered and the contract refused the answer.</summary>
    Refused,
    /// <summary>The judge could not be asked at all - no key, or an answered provider error.</summary>
    Unavailable,
}

/// <summary>The answer to one verdict request.</summary>
public sealed record TurnVerdictOutcome
{
    public TurnVerdictOutcomeKind Kind { get; init; }

    /// <summary>The verdict record: accepted on <see cref="TurnVerdictOutcomeKind.Judged"/> and
    /// <see cref="TurnVerdictOutcomeKind.Reused"/>, the failed record on <see cref="TurnVerdictOutcomeKind.Failed"/>.</summary>
    public TurnVerdictDto? Verdict { get; init; }

    /// <summary>The closed cause word a skip was recorded under (<see cref="ActivityCauses"/>).</summary>
    public string? SkipCause { get; init; }

    public TurnVerdictFailureKind Failure { get; init; }

    /// <summary>Plain words for the log. Never shown and never spoken.</summary>
    public string? FailureDetail { get; init; }

    /// <summary>How long the provider asked us to wait, on a rate limit that said.</summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>The full-grid hash of the screen this request read, "" when it read none.</summary>
    public string ScreenHash { get; init; } = "";

    /// <summary>The source the stop was judged from - the reply, or the failure text - as
    /// <see cref="WingmanNarrationSource.Select"/> chose it over the same screen read. Null when there was none.</summary>
    public string? SourceText { get; init; }

    /// <summary>How long the judge took, when it was asked.</summary>
    public double ReplySeconds { get; init; }

    /// <summary>True when there is an accepted verdict to act on.</summary>
    public bool HasAcceptedVerdict => Verdict is { Failed: false }
        && Kind is TurnVerdictOutcomeKind.Judged or TurnVerdictOutcomeKind.Reused;
}

/// <summary>
/// The turn-verdict seat (the Wingman-on-every-turn mission, slice C): at a stop, ask ONE model ONE
/// structured question about what the stop MEANS, validate the answer mechanically, and store it with the
/// agent's own words as the receipt. The voice path takes its spoken words from the same answer, so a voice
/// session's stop costs exactly one model call.
///
/// WHAT IT NEVER DOES. It never decides whether a stop happened (the detector does), never types into a
/// session, never snoozes or closes one, never reads a session a live owning session is holding, and never keeps
/// a verdict across a Working transition.
///
/// THE ORDER, and it is the order of cost. Everything that is free is asked before anything is read, and
/// everything read is read before anything is paid for: the per-session gate (a second stop for a session
/// already being judged is dropped, never queued); the held check against the whole roster; brand-new,
/// exited, and the judge switch; the settle delay; ONE screen read and its full-grid hash; reuse when the
/// hash is the one last judged; the account's ceiling; and only then the package, the call, the validation
/// and the store.
///
/// A VERDICT THAT SURVIVES IS NEWER THAN THE LAST WORKING TRANSITION, BY CONSTRUCTION WITHIN THIS PROCESS.
/// <see cref="OnSessionWorking"/> bumps the session's epoch and invalidates its stored verdicts under the
/// same gate every store takes, and a store only lands when the epoch it started under is still current. So
/// an answer that arrives after the session went back to work is discarded rather than written over a live
/// session.
///
/// GAP, NOT PROVEN: THE SUPERVISOR'S KEYSTROKES AND THIS SCREEN READ ARE NOT ORDERED AGAINST EACH OTHER. The
/// session supervisor and the rules launcher run their own background work off the same turn-end boundary,
/// in parallel with this seat, and nothing makes one wait for the other. The judge reads the turn-end
/// screen; a "stuck-recoverable" verdict is the judge's word for what the supervisor is handling; and a
/// Working transition caused by the supervisor's "continue" invalidates the verdict. What is not proven is
/// that the two never act on one screen at the same moment - a "continue" landing between this seat's screen
/// read and its store leaves a verdict about the pre-continue screen until the Working edge arrives.
///
/// GAP, NOT PROVEN: A SESSION WHOSE OWNING SESSION EXITS WITH NO NEW STOP IS NOT RE-JUDGED. It was skipped as held
/// at its stop; when that owning session exits nothing wakes this seat, so the session surfaces raw red (the right
/// direction) and is judged at its next stop.
/// </summary>
public sealed class TurnVerdictService : IDisposable
{
    private readonly ITurnVerdictEnvironment _env;

    private sealed class Flight
    {
        public readonly CancellationTokenSource Cts = new();
        public readonly TaskCompletionSource<TurnVerdictOutcome> Done =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    // The per-session gate. Presence means a verdict is being formed for this session right now.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), Flight> _inFlight = new();

    // How many judgements each account has in flight, against its ceiling.
    private readonly ConcurrentDictionary<TenantId, StrongBox<int>> _tenantLoad = new();

    // Bumped on every Working transition. A store lands only under the epoch it started in.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), long> _epochs = new();

    // Sessions whose verdict is being read right now - the "reading" state slice D paints.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _reading = new();

    // The moment the detector last observed a stop for each session - the join key a verdict carries.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), DateTime> _lastObserved = new();

    // Sessions known to have no stored verdict in this process, so a Working edge costs no database round
    // trip for them. Cleared the moment a verdict is stored; a session not in it is invalidated for real.
    private readonly ConcurrentDictionary<(TenantId Tenant, string SessionId), byte> _knownEmpty = new();

    private readonly object _storeGate = new();
    private long _capSkips;
    private bool _disposed;

    public TurnVerdictService(ITurnVerdictEnvironment environment)
        => _env = environment ?? throw new ArgumentNullException(nameof(environment));

    /// <summary>How many stops have been stood down by an account's in-flight ceiling since start.</summary>
    public long CapSkips => Interlocked.Read(ref _capSkips);

    /// <summary>True while a verdict is being formed for this session.</summary>
    public bool IsReading(TenantId tenant, string sessionId) => _reading.ContainsKey((tenant, sessionId));

    /// <summary>Is a live owning session holding this session? The one held check every narration caller asks,
    /// resolved against the whole roster.</summary>
    public bool IsHeld(TenantId tenant, string sessionId) => _env.HasLiveSupervisor(tenant, sessionId);

    /// <summary>This session's latest stored verdict, or null.</summary>
    public TurnVerdictDto? Latest(TenantId tenant, string sessionId) => _env.Latest(tenant, sessionId);

    /// <summary>
    /// A stop was observed. Fire-and-forget: the gate is taken synchronously, before this returns, so a voice
    /// narration started right after this call joins the same judgement instead of asking a second time.
    /// </summary>
    public void OnTurnEnd(TurnEndSignal signal) => _ = StartTurnEnd(signal);

    /// <summary>
    /// The awaited form of <see cref="OnTurnEnd"/>, for tests and for callers that want the outcome. A stop
    /// for a session already being judged comes back skipped with <see cref="ActivityCauses.AlreadyJudging"/>.
    /// </summary>
    public Task<TurnVerdictOutcome> StartTurnEnd(TurnEndSignal signal)
    {
        ArgumentNullException.ThrowIfNull(signal);
        if (_disposed || !signal.Tenant.IsValid || string.IsNullOrEmpty(signal.SessionId))
            return Task.FromResult(new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = ActivityCauses.Unknown });

        var key = (signal.Tenant, signal.SessionId);
        _lastObserved[key] = signal.ObservedAtUtc;

        var flight = new Flight();
        if (!_inFlight.TryAdd(key, flight))
        {
            flight.Cts.Dispose();
            return Task.FromResult(Skip(signal.Tenant, signal.DirectorId, signal.SessionId,
                TurnVerdictTrigger.TurnEnd, ActivityCauses.AlreadyJudging));
        }

        FileLog.Write($"[TurnVerdictService] OnTurnEnd: sid={signal.SessionId} tenant={signal.Tenant.ToLogString()} newTurn={signal.IsNewTurn}");
        return Task.Run(() => RunFlightAsync(key, flight, signal.DirectorId, signal.ObservedAtUtc,
            TurnVerdictTrigger.TurnEnd, screenReader: null, providerHold: null));
    }

    /// <summary>
    /// The verdict for this session's CURRENT screen, for a caller that needs its words now - a voice session's
    /// narration, the idle sweep, a person pressing explain.
    ///
    /// ONE MODEL CALL PER STOP. A judgement already in flight for this session is JOINED, not repeated: its
    /// screen was read after the settle delay, which is the read this stop is judged on, so reading again here
    /// first would risk seeing an earlier repaint and asking twice. With nothing in flight the screen is read
    /// once; an unchanged screen returns the stored verdict; only a changed one asks the judge.
    /// </summary>
    /// <param name="screenReader">The caller's own screen read, when it already holds the route (the voice
    /// path) or has already read the screen (the sweep). Null uses the environment's tunnel read.</param>
    /// <param name="providerHold">Given the screen hash and the source text of THIS stop, how long the provider
    /// asked this caller to wait before asking again, or null. Consulted after the screen and the source are
    /// known and before the judge is asked, so a new stop is never held back by an earlier stop's wait.</param>
    public async Task<TurnVerdictOutcome> VerdictForCurrentScreenAsync(
        TenantId tenant,
        string directorId,
        string sessionId,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader = null,
        CancellationToken ct = default,
        Func<string, string?, TimeSpan?>? providerHold = null)
    {
        if (!tenant.IsValid) throw new ArgumentException("A verdict needs a valid tenant.", nameof(tenant));
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        ObjectDisposedException.ThrowIf(_disposed, this);

        var key = (tenant, sessionId);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (_inFlight.TryGetValue(key, out var existing))
            {
                var joined = await existing.Done.Task.WaitAsync(ct).ConfigureAwait(false);
                // A turn-end judgement that stood down for a reason that binds only the turn-end path - this
                // account's ceiling, or its judge switch - is not an answer for somebody who is listening.
                if (!(joined.Kind == TurnVerdictOutcomeKind.Skipped
                      && joined.SkipCause is ActivityCauses.InFlightCap or ActivityCauses.JudgeSwitchOff))
                    return joined;
                continue;
            }

            var flight = new Flight();
            if (!_inFlight.TryAdd(key, flight))
            {
                flight.Cts.Dispose();
                continue;   // somebody else took the gate between the two lines above - join them next pass
            }

            var observedAt = _lastObserved.TryGetValue(key, out var seen) ? seen : _env.NowUtc();
            return await RunFlightAsync(key, flight, directorId, observedAt, trigger, screenReader, providerHold).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            $"[TurnVerdictService] could not take or join the verdict gate for sid={sessionId} after three tries; "
            + "something is holding and releasing it in a tight loop.");
    }

    /// <summary>
    /// The session is working again. Whatever verdict it carried described a screen that is gone: the
    /// in-flight judgement is cancelled, the reading state cleared, and the stored verdict invalidated - so
    /// the row goes back to exactly what the detector says.
    /// </summary>
    public void OnSessionWorking(TenantId tenant, string sessionId)
    {
        if (_disposed || !tenant.IsValid || string.IsNullOrEmpty(sessionId)) return;
        var key = (tenant, sessionId);

        lock (_storeGate)
        {
            _epochs.AddOrUpdate(key, 1, (_, epoch) => epoch + 1);
            if (!_knownEmpty.ContainsKey(key))
            {
                var removed = _env.Invalidate(tenant, sessionId);
                _knownEmpty[key] = 1;
                if (removed > 0)
                    FileLog.Write($"[TurnVerdictService] OnSessionWorking: sid={sessionId} tenant={tenant.ToLogString()} invalidated {removed} stored verdict(s)");
            }
        }

        _reading.TryRemove(key, out _);
        // The last observed stop is over too. A verdict request that starts before the detector observes the NEXT
        // stop carries the moment it started, and takes the observed moment when the detector catches up.
        _lastObserved.TryRemove(key, out _);
        if (_inFlight.TryGetValue(key, out var flight))
        {
            try { flight.Cts.Cancel(); }
            catch (ObjectDisposedException) { /* the flight already finished and disposed it */ }
        }
    }

    private async Task<TurnVerdictOutcome> RunFlightAsync(
        (TenantId Tenant, string SessionId) key,
        Flight flight,
        string directorId,
        DateTime observedAt,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader,
        Func<string, string?, TimeSpan?>? providerHold)
    {
        TurnVerdictOutcome outcome;
        try
        {
            outcome = await JudgeAsync(key, flight.Cts.Token, directorId, observedAt, trigger, screenReader, providerHold).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (flight.Cts.IsCancellationRequested)
        {
            outcome = Cancelled(key.Tenant, directorId, key.SessionId, trigger, "the session started working while its verdict was being formed");
        }
        catch (Exception ex)
        {
            // The flight's boundary: it runs on the thread pool for a turn-end callback that has already
            // returned, so nothing above it can catch. Logged loud and answered as a failure, never as calm.
            FileLog.Write($"[TurnVerdictService] verdict FAILED: sid={key.SessionId} tenant={key.Tenant.ToLogString()} trigger={trigger}: {ex.Message}");
            outcome = new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Failed,
                Failure = TurnVerdictFailureKind.Unavailable,
                FailureDetail = ex.Message,
            };
        }
        finally
        {
            _inFlight.TryRemove(new KeyValuePair<(TenantId, string), Flight>(key, flight));
        }

        flight.Done.TrySetResult(outcome);
        flight.Cts.Dispose();
        return outcome;
    }

    private async Task<TurnVerdictOutcome> JudgeAsync(
        (TenantId Tenant, string SessionId) key,
        CancellationToken ct,
        string directorId,
        DateTime observedAt,
        TurnVerdictTrigger trigger,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader,
        Func<string, string?, TimeSpan?>? providerHold)
    {
        var (tenant, sid) = key;
        var settings = _env.Settings(tenant);
        var automatic = trigger != TurnVerdictTrigger.OnDemand;

        // ---- the free checks: nothing is read and nothing is paid for until every one of them passes ----
        // HELD FIRST. A session a live owning session holds is not the owner's to be read, so its screen is never
        // read and no model is asked about it.
        if (automatic && _env.HasLiveSupervisor(tenant, sid))
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.Held);

        var facts = _env.ReadSessionFacts(tenant, sid);
        if (facts is null && automatic)
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.SessionNotLive);
        if (facts is { IsBrandNew: true })
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.BrandNew);
        if (automatic && facts is not null && IsExited(facts))
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.SessionExit);
        if (trigger == TurnVerdictTrigger.TurnEnd && !settings.JudgeEnabled && !_env.IsVoiceSession(tenant, sid))
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.JudgeSwitchOff);

        if (trigger == TurnVerdictTrigger.TurnEnd && settings.SettleMs > 0)
            await _env.DelayAsync(TimeSpan.FromMilliseconds(settings.SettleMs), ct).ConfigureAwait(false);

        // ---- ONE screen read, and its full-grid hash ----
        var grid = await ReadScreenAsync(tenant, directorId, sid, screenReader, ct).ConfigureAwait(false);
        var rows = grid is { HasGrid: true, Rows.Count: > 0 } ? (IReadOnlyList<string>)grid.Rows : Array.Empty<string>();
        var hash = rows.Count == 0 ? "" : WingmanScreenVerdictCache.HashRows(rows);

        // ---- reuse: the screen the stored verdict was formed on ----
        var latest = _env.Latest(tenant, sid);
        // AN UNREADABLE SCREEN IS NEVER REUSED, except by the sweep. Every unreadable screen hashes to "", so two
        // different stops on a Director that cannot be reached would look like one screen, and the second would
        // be played the first one's words. The sweep is the exception because it comes past every pass, and
        // re-asking the judge about an unreachable session each time would be a paid call on a loop.
        if (latest is not null
            && string.Equals(latest.ScreenHash, hash, StringComparison.Ordinal)
            && (hash.Length > 0 || trigger == TurnVerdictTrigger.Sweep)
            && (!latest.Failed || trigger == TurnVerdictTrigger.Sweep))
            return Reuse(key, directorId, trigger, observedAt, latest, hash, rows);

        // The source this stop is judged from, chosen ONCE, over this one screen read.
        var conversation = _env.ReadConversation(tenant, sid)
                           ?? new StoredConversation(false, Array.Empty<TurnWidgetDto>());
        var source = WingmanNarrationSource.Select(conversation.Widgets, rows);

        // THE PROVIDER'S OWN DEADLINE. A caller the provider told to wait - the voice path, after a rate limit it
        // would not hold a promise across - is not asked about again for this stop until the wait has passed.
        if (providerHold?.Invoke(hash, source?.Content) is { } wait)
        {
            FileLog.Write($"[TurnVerdictService] sid={sid}: not asking the judge for {wait.TotalSeconds:F0}s more - the provider asked this caller to wait");
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.RateLimited);
        }

        // ---- the account's ceiling ----
        var capped = trigger is TurnVerdictTrigger.TurnEnd or TurnVerdictTrigger.Sweep;
        var load = _tenantLoad.GetOrAdd(tenant, _ => new StrongBox<int>());
        if (capped && Interlocked.Increment(ref load.Value) > settings.MaxInFlight)
        {
            Interlocked.Decrement(ref load.Value);
            Interlocked.Increment(ref _capSkips);
            return Skip(tenant, directorId, sid, trigger, ActivityCauses.InFlightCap);
        }

        var epoch = _epochs.GetOrAdd(key, 0);
        _reading[key] = 1;
        try
        {
            var signal = new TurnEndSignal(sid, directorId, tenant, observedAt, IsNewTurn: trigger == TurnVerdictTrigger.TurnEnd);
            var package = TurnVerdictPackageBuilder.Build(
                signal,
                facts ?? new SessionDto { SessionId = sid },
                conversation,
                grid,
                latest is { Failed: false } ? latest.Label : null);
            var prompt = TurnVerdictPrompt.BuildVerdictPrompt(_env.Language(tenant), package, _env.CustomSpokenRules());

            TurnVerdictDto record;
            var failure = TurnVerdictFailureKind.None;
            TimeSpan? retryAfter = null;
            string? detail = null;
            double replySeconds = 0;
            try
            {
                var answer = await _env.AskJudgeAsync(tenant, prompt, ct).ConfigureAwait(false);
                replySeconds = answer.ReplySeconds;
                record = TurnVerdictContract.ParseAndValidate(answer.Raw, package, answer.Model, observedAt);
                if (record.Failed)
                {
                    failure = TurnVerdictFailureKind.Refused;
                    detail = record.FailureReason;
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (WingmanModelRateLimitedException rl)
            {
                failure = TurnVerdictFailureKind.RateLimited;
                retryAfter = rl.RetryAfter;
                detail = rl.Message;
                record = FailedRecord(package, tenant, observedAt, "the judge was rate limited: " + rl.Message);
            }
            catch (Exception ex) when (ex is TimeoutException or HttpRequestException or OperationCanceledException)
            {
                failure = TurnVerdictFailureKind.DidNotAnswer;
                detail = ex.Message;
                record = FailedRecord(package, tenant, observedAt, "the judge did not answer: " + ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                failure = TurnVerdictFailureKind.Unavailable;
                detail = ex.Message;
                record = FailedRecord(package, tenant, observedAt, "the judge could not be asked: " + ex.Message);
            }

            ct.ThrowIfCancellationRequested();

            // THE JOIN KEY IS THE LATEST OBSERVED MOMENT, found on the rig. The idle sweep began judging a stop ten
            // seconds before the detector's reconcile tick observed it; the tick's turn end was dropped by the gate
            // (rightly - one call per stop), and the stored verdict carried the PREVIOUS stop's moment, so the
            // turn-log record of this stop paired with nothing. A stop observed while this flight was running is
            // the stop this screen belongs to - a Working edge in between would have cancelled the flight - so
            // the verdict carries that later moment.
            if (_lastObserved.TryGetValue(key, out var seenSince) && seenSince > record.TurnEndObservedAtUtc)
                record.TurnEndObservedAtUtc = seenSince;

            if (!StoreIfCurrent(key, epoch, record))
                return Cancelled(tenant, directorId, sid, trigger, "the session worked between the read and the store; the answer describes a screen that is gone");

            if (record.Failed)
            {
                _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictFailed,
                    FailureCause(failure),
                    $"trigger={TriggerWord(trigger)} kind={record.PackageKind} id={record.VerdictId} model={record.Model}"));
                return new TurnVerdictOutcome
                {
                    Kind = TurnVerdictOutcomeKind.Failed,
                    Verdict = record,
                    Failure = failure,
                    FailureDetail = detail,
                    RetryAfter = retryAfter,
                    ScreenHash = hash,
                    SourceText = source?.Content,
                    ReplySeconds = replySeconds,
                };
            }

            // The menu cache the send-time guards read (WaitingScreenReader.ConfirmedMenuAsync) is fed from the
            // verdict, under the same full-grid hash, so an unchanged screen is answered without a second call.
            if (rows.Count > 0)
                WingmanScreenVerdictCache.Store($"{tenant}/{sid}", hash, ScreenNeeds(record));

            _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictJudged,
                ActivityCauses.JudgeAnswered,
                $"trigger={TriggerWord(trigger)} verdict={record.Verdict} confidence={record.Confidence} kind={record.PackageKind} id={record.VerdictId} model={record.Model}"));
            return new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Judged,
                Verdict = record,
                ScreenHash = hash,
                SourceText = source?.Content,
                ReplySeconds = replySeconds,
            };
        }
        finally
        {
            _reading.TryRemove(key, out _);
            if (capped) Interlocked.Decrement(ref load.Value);
        }
    }

    private TurnVerdictOutcome Reuse(
        (TenantId Tenant, string SessionId) key,
        string directorId,
        TurnVerdictTrigger trigger,
        DateTime observedAt,
        TurnVerdictDto latest,
        string hash,
        IReadOnlyList<string> rows)
    {
        var (tenant, sid) = key;
        var verdict = latest;

        // A new stop on an unchanged screen is the same verdict about a later moment: refresh the join key so
        // the turn-log record of THIS stop still pairs with a verdict row.
        if (trigger == TurnVerdictTrigger.TurnEnd && !latest.Failed && latest.TurnEndObservedAtUtc != observedAt)
        {
            var refreshed = Copy(latest);
            refreshed.TurnEndObservedAtUtc = observedAt;
            if (StoreIfCurrent(key, _epochs.GetOrAdd(key, 0), refreshed)) verdict = refreshed;
        }

        var conversation = _env.ReadConversation(tenant, sid);
        var source = WingmanNarrationSource.Select(conversation?.Widgets, rows);

        _env.Record(new TurnVerdictRecord(tenant, directorId, sid, ActivityEventTypes.TurnVerdictReused,
            ActivityCauses.ScreenUnchanged,
            $"trigger={TriggerWord(trigger)} id={verdict.VerdictId} failed={verdict.Failed}"));

        return verdict.Failed
            ? new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Failed,
                Verdict = verdict,
                Failure = TurnVerdictFailureKind.Refused,
                FailureDetail = "the last answer about this unchanged screen was refused; the sweep does not ask again",
                ScreenHash = hash,
                SourceText = source?.Content,
            }
            : new TurnVerdictOutcome
            {
                Kind = TurnVerdictOutcomeKind.Reused,
                Verdict = verdict,
                ScreenHash = hash,
                SourceText = source?.Content,
            };
    }

    private bool StoreIfCurrent((TenantId Tenant, string SessionId) key, long epoch, TurnVerdictDto record)
    {
        lock (_storeGate)
        {
            if (_epochs.GetOrAdd(key, 0) != epoch) return false;
            _env.Store(key.Tenant, key.SessionId, record);
            _knownEmpty.TryRemove(key, out _);
            return true;
        }
    }

    private async Task<ScreenGridResponse?> ReadScreenAsync(
        TenantId tenant, string directorId, string sid,
        Func<CancellationToken, Task<ScreenGridResponse?>>? screenReader, CancellationToken ct)
    {
        if (screenReader is null)
            return await _env.ReadScreenGridAsync(tenant, directorId, sid, ct).ConfigureAwait(false);
        try
        {
            return await screenReader(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            // Unreadable is not evidence of anything: carried as no rows, which the contract binds to cannot-tell.
            FileLog.Write($"[TurnVerdictService] screen read FAILED sid={sid}: {ex.Message} - judging without a screen");
            return null;
        }
    }

    private TurnVerdictOutcome Skip(TenantId tenant, string directorId, string sid, TurnVerdictTrigger trigger, string cause)
    {
        _env.Record(new TurnVerdictRecord(tenant, directorId ?? "", sid, ActivityEventTypes.TurnVerdictSkipped,
            cause, $"trigger={TriggerWord(trigger)}"));
        return new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Skipped, SkipCause = cause };
    }

    private TurnVerdictOutcome Cancelled(TenantId tenant, string directorId, string sid, TurnVerdictTrigger trigger, string why)
    {
        FileLog.Write($"[TurnVerdictService] cancelled sid={sid} tenant={tenant.ToLogString()}: {why}");
        _env.Record(new TurnVerdictRecord(tenant, directorId ?? "", sid, ActivityEventTypes.TurnVerdictCancelled,
            ActivityCauses.WorkingObservation, $"trigger={TriggerWord(trigger)}"));
        return new TurnVerdictOutcome { Kind = TurnVerdictOutcomeKind.Cancelled };
    }

    private TurnVerdictDto FailedRecord(TurnVerdictPackage package, TenantId tenant, DateTime observedAt, string reason)
        => new()
        {
            VerdictId = Guid.NewGuid().ToString("N"),
            JudgedAtUtc = _env.NowUtc(),
            TurnEndObservedAtUtc = observedAt,
            ScreenHash = package.ScreenHash,
            Model = _env.JudgeModel(tenant),
            ContractVersion = TurnVerdictContract.Version,
            PackageKind = TurnVerdictPackage.WireName(package.Kind),
            Failed = true,
            FailureReason = reason,
        };

    private static TurnVerdictDto Copy(TurnVerdictDto v) => new()
    {
        VerdictId = v.VerdictId,
        JudgedAtUtc = v.JudgedAtUtc,
        TurnEndObservedAtUtc = v.TurnEndObservedAtUtc,
        ScreenHash = v.ScreenHash,
        Model = v.Model,
        ContractVersion = v.ContractVersion,
        PackageKind = v.PackageKind,
        Failed = v.Failed,
        FailureReason = v.FailureReason,
        Verdict = v.Verdict,
        Confidence = v.Confidence,
        Evidence = v.Evidence,
        Label = v.Label,
        Summary = v.Summary,
        AgentRecommends = v.AgentRecommends,
        AnswerVia = v.AnswerVia,
        Menu = v.Menu,
        Options = v.Options,
        Risk = v.Risk,
        Spoken = v.Spoken,
    };

    /// <summary>The three-word screen verdict the menu cache has always held, read off the verdict: a picker
    /// the answer selects from is a menu; a stop that needs a person is waiting on an answer; anything else
    /// needs nothing.</summary>
    internal static string ScreenNeeds(TurnVerdictDto verdict)
    {
        if (string.Equals(verdict.AnswerVia, "keys", StringComparison.Ordinal) && verdict.Menu is not null) return "menu";
        return string.Equals(verdict.Verdict, TurnVerdictVocabulary.NeededYou, StringComparison.Ordinal) ? "answer" : "nothing";
    }

    private static bool IsExited(SessionDto s)
        => s.Crashed || string.Equals(s.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase);

    private static string FailureCause(TurnVerdictFailureKind failure) => failure switch
    {
        TurnVerdictFailureKind.DidNotAnswer => ActivityCauses.JudgeDidNotAnswer,
        TurnVerdictFailureKind.RateLimited => ActivityCauses.RateLimited,
        TurnVerdictFailureKind.Refused => ActivityCauses.JudgeRefused,
        _ => ActivityCauses.JudgeUnavailable,
    };

    private static string TriggerWord(TurnVerdictTrigger trigger) => trigger switch
    {
        TurnVerdictTrigger.TurnEnd => "turn-end",
        TurnVerdictTrigger.Voice => "voice",
        TurnVerdictTrigger.Sweep => "sweep",
        _ => "on-demand",
    };

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var flight in _inFlight.Values)
        {
            try { flight.Cts.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
}
