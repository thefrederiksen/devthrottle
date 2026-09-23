using System.Collections.Concurrent;
using System.Text.Json;
using CcDirector.AgentBrain;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.History;
using CcDirector.Gateway.Speech;
using CcDirector.Gateway.Wingman;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The turn-verdict seat's whole world, faked and recording: no clock, no tunnel, no model, no database. Every
/// test asserts on what the seat DECIDED - how many screens it read, how many questions it asked, what it
/// stored - rather than on what a real Director happened to do. The held check is the one thing this fake
/// must never be used to prove: that test runs the PRODUCTION environment over the real push store.
/// </summary>
internal sealed class FakeTurnVerdictEnvironment : ITurnVerdictEnvironment
{
    public const string Model = "devthrottle/wingman-fast";

    public TurnVerdictSettings Knobs = TurnVerdictSettings.Defaults with { JudgeEnabled = true, SettleMs = 0 };
    public Func<string, bool> Held = _ => false;
    public Func<string, SessionDto?> Facts = sid => new SessionDto
    {
        SessionId = sid,
        Name = "devthrottle - the retention sweep",
        Agent = "ClaudeCode",
        ActivityState = "WaitingForInput",
    };
    public Func<ScreenGridResponse?> Screen = () => null;
    public Func<string, StoredConversation?> Conversation = _ => null;
    public Func<string, CancellationToken, Task<string>> Judge = (_, _) => Task.FromResult(CannotTell("The session stopped."));
    public Func<string, bool> VoiceSession = _ => false;
    /// <summary>The account's plan answer for the narration. Allowed by default, as on a self-host Gateway.</summary>
    public Func<NarrationPlan> Plan = () => NarrationPlan.Allowed;
    public NarrationPlan PlanForNarration(TenantId tenant) => Plan();
    public SpokenLanguage LanguageValue = SpokenLanguages.English;
    public string? Custom { get; set; }

    /// <summary>Runs once, the first time <see cref="Latest"/> is read by the seat after being set, and is then
    /// cleared - so a test can land a Working edge in the gap between the stored-verdict read and what follows.</summary>
    public Action? AfterNextLatest;

    /// <summary>When set, the next store throws it (once), the way a database fault would.</summary>
    public Exception? NextStoreThrows;

    private int _screenReads;
    private int _judgeCalls;
    private int _invalidations;
    private int _stateReads;
    public int ScreenReads => _screenReads;
    public int JudgeCalls => _judgeCalls;
    public int Invalidations => _invalidations;
    /// <summary>How many roster snapshots the seat has taken.</summary>
    public int StateReads => _stateReads;
    public readonly ConcurrentQueue<string> Prompts = new();
    public readonly ConcurrentQueue<TurnVerdictRecord> Records = new();

    /// <summary>What the seat did, in order: "state" for a roster snapshot, "settle:{milliseconds}" for the
    /// settle wait, "screen" for a screen read.</summary>
    public readonly ConcurrentQueue<string> Steps = new();

    private readonly object _gate = new();
    private readonly Dictionary<(TenantId, string), List<TurnVerdictDto>> _stored = new();

    /// <summary>The verdict ids this fake has SUPERSEDED, by (account, session, verdict) - the real store's
    /// <c>SupersededAtUtc</c> column in the shape a fake needs. Slice G made Invalidate stamp rather than delete,
    /// and a fake that still deleted would let a test pass against behaviour the product no longer has.</summary>
    private readonly HashSet<(TenantId, string, string)> _superseded = new();

    /// <summary>When set, answers every settings read in place of <see cref="Knobs"/> - so a test can flip a switch, or
    /// make the read throw, in the middle of a flight.</summary>
    public Func<TurnVerdictSettings>? SettingsOverride;

    public TurnVerdictSettings Settings(TenantId tenant) => SettingsOverride?.Invoke() ?? Knobs;

    public TurnVerdictSessionState ReadSessionState(TenantId tenant, string sessionId)
    {
        Interlocked.Increment(ref _stateReads);
        Steps.Enqueue("state");
        return new TurnVerdictSessionState(Facts(sessionId), Held(sessionId));
    }

    public Task<ScreenGridResponse?> ReadScreenGridAsync(TenantId tenant, string directorId, string sessionId, CancellationToken ct)
    {
        Interlocked.Increment(ref _screenReads);
        Steps.Enqueue("screen");
        return Task.FromResult(Screen());
    }

    public StoredConversation? ReadConversation(TenantId tenant, string sessionId) => Conversation(sessionId);
    public SpokenLanguage Language(TenantId tenant) => LanguageValue;
    public string? CustomSpokenRules() => Custom;
    public string JudgeModel(TenantId tenant) => Model;

    /// <summary>The deadline the seat passed on each judge call, in order.</summary>
    public readonly ConcurrentQueue<TimeSpan> JudgeTimeouts = new();

    public async Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _judgeCalls);
        Prompts.Enqueue(prompt);
        JudgeTimeouts.Enqueue(timeout);
        var raw = await Judge(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(raw, Model, 0.2);
    }

    /// <summary>The minimal host-recovery call. Separate from <see cref="Judge"/> so a test can prove that a
    /// paused retry spent only its probe and did not spend the full reading.</summary>
    public Func<string, CancellationToken, Task<string>> RecoveryProbe = (_, _) => Task.FromResult("OK");

    private int _recoveryProbeCalls;
    public int RecoveryProbeCalls => _recoveryProbeCalls;
    public readonly ConcurrentQueue<string> RecoveryProbePrompts = new();
    public readonly ConcurrentQueue<TimeSpan> RecoveryProbeTimeouts = new();

    public async Task<TurnVerdictJudgeAnswer> AskRecoveryProbeAsync(
        TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _recoveryProbeCalls);
        RecoveryProbePrompts.Enqueue(prompt);
        RecoveryProbeTimeouts.Enqueue(timeout);
        var raw = await RecoveryProbe(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(raw, Model, 0.05);
    }

    /// <summary>
    /// The narration call's answer.
    ///
    /// FROM CONTRACT v3 IT IS THE ONLY SOURCE OF WORDS THERE IS. The judge no longer answers with prose at all, so
    /// a test that wants a reading with a body sets this - or, more simply, passes a <c>spoken</c> string to one of
    /// the canned judge answers below, which this default hands straight back. That keeps every test written
    /// before v3 saying what it always said: "the listener hears these words for this stop".
    ///
    /// Set it to return "" for the case where the call produced NO words, which is a failed call: the reading is
    /// then stored with no narration at all, because there is nothing else to fall back on any more.
    /// </summary>
    public Func<string, CancellationToken, Task<string>> Narrator = (_, _) => Task.FromResult(NarratedAnswer(LastCannedSpoken));

    /// <summary>
    /// The words the last canned judge answer was built with, which the default narrator answers with.
    ///
    /// STATIC, AND IT HAS TO BE. An AsyncLocal was tried here, to stop one test's canned words reaching another
    /// test's narration call, and it silently broke three tests in ClipBelongsToTheTurnTests: the write happens
    /// INSIDE the judge lambda, and an AsyncLocal written in a nested flow is invisible to the flow that started
    /// it - so the narrator answered with nothing and no clip was ever made.
    ///
    /// THE HAZARD IS REAL AND THE ANSWER IS TO NOT DEPEND ON IT: a test that asserts on the narrated WORDS sets
    /// rig.Env.Narrator itself, which is explicit and cannot be written by a sibling. This default exists only so
    /// that the many tests which assert on COUNTS need not spell a narrator out.
    /// </summary>
    private static string LastCannedSpoken = "";

    /// <summary>What the default narrator answers: the last canned judge answer's words, wrapped in the markers a
    /// real narration call answers between. A test that supplies ONE brain for both calls of a reading answers
    /// this to the narration prompt - see <see cref="CountingBrain"/>.</summary>
    public static string DefaultNarration() => NarratedAnswer(LastCannedSpoken);

    /// <summary>A narrator answer as the real model writes one: the words between the two markers.</summary>
    public static string NarratedAnswer(string spoken)
        => spoken.Length == 0
            ? ""
            : $"{Core.Drivers.SessionAskRunner.AnswerBeginMarker}\n{spoken}\n{Core.Drivers.SessionAskRunner.AnswerEndMarker}";

    private int _narratorCalls;
    /// <summary>How many narration calls were made. Never counted as judge calls.</summary>
    public int NarratorCalls => _narratorCalls;
    /// <summary>The prompt of each narration call, in order.</summary>
    public readonly ConcurrentQueue<string> NarratorPrompts = new();
    /// <summary>The deadline passed on each narration call, in order.</summary>
    public readonly ConcurrentQueue<TimeSpan> NarratorTimeouts = new();

    public async Task<TurnVerdictJudgeAnswer> AskNarratorAsync(TenantId tenant, string prompt, TimeSpan timeout, CancellationToken ct)
    {
        Interlocked.Increment(ref _narratorCalls);
        NarratorPrompts.Enqueue(prompt);
        NarratorTimeouts.Enqueue(timeout);
        var raw = await Narrator(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(raw, Model, 0.3);
    }

    public TurnVerdictDto? Latest(TenantId tenant, string sessionId)
    {
        TurnVerdictDto? latest;
        lock (_gate)
            latest = _stored.TryGetValue((tenant, sessionId), out var rows)
                ? rows.Where(r => !_superseded.Contains((tenant, sessionId, r.VerdictId)))
                      .OrderByDescending(r => r.JudgedAtUtc).FirstOrDefault()
                : null;
        Interlocked.Exchange(ref AfterNextLatest, null)?.Invoke();
        return latest;
    }

    public void Store(TenantId tenant, string sessionId, TurnVerdictDto verdict)
    {
        if (Interlocked.Exchange(ref NextStoreThrows, null) is { } fault) throw fault;
        lock (_gate)
        {
            if (!_stored.TryGetValue((tenant, sessionId), out var rows)) _stored[(tenant, sessionId)] = rows = new();
            rows.RemoveAll(r => r.JudgedAtUtc == verdict.JudgedAtUtc);
            rows.Add(verdict);
            // A new verdict id is a new statement about the screen, so it is born describing it - the store's own
            // rule when a same-moment re-judgement mints a new id.
            _superseded.Remove((tenant, sessionId, verdict.VerdictId));
        }
    }

    /// <summary>Slice G: SUPERSEDES rather than deleting, exactly as <c>TurnVerdictStore.Invalidate</c> does, and
    /// returns how many rows this call stamped. Already-stamped rows are left alone.</summary>
    public int Invalidate(TenantId tenant, string sessionId)
    {
        Interlocked.Increment(ref _invalidations);
        lock (_gate)
        {
            if (!_stored.TryGetValue((tenant, sessionId), out var rows)) return 0;
            var stamped = 0;
            foreach (var row in rows)
                if (_superseded.Add((tenant, sessionId, row.VerdictId))) stamped++;
            return stamped;
        }
    }

    /// <summary>Every record held for this session, superseded or not - the history.</summary>
    public int StoredCount(TenantId tenant, string sessionId)
    {
        lock (_gate) return _stored.TryGetValue((tenant, sessionId), out var rows) ? rows.Count : 0;
    }

    /// <summary>How many of this session's records still describe its screen.</summary>
    public int LiveCount(TenantId tenant, string sessionId)
    {
        lock (_gate)
            return _stored.TryGetValue((tenant, sessionId), out var rows)
                ? rows.Count(r => !_superseded.Contains((tenant, sessionId, r.VerdictId)))
                : 0;
    }

    public bool IsVoiceSession(TenantId tenant, string sessionId) => VoiceSession(sessionId);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Steps.Enqueue($"settle:{delay.TotalMilliseconds:F0}");
        return Task.CompletedTask;
    }

    public void Record(TurnVerdictRecord record) => Records.Enqueue(record);

    /// <summary>Every trace the seat wrote for the Wingman inspector, in order.</summary>
    public readonly ConcurrentQueue<TurnVerdictTrace> Traces = new();

    /// <summary>Runs before each trace is recorded, with the trace itself - so a test can hold the seat inside one
    /// particular trace write and act while it is held.</summary>
    public Action<TurnVerdictTrace>? BeforeRecordTrace;

    /// <summary>When set, every trace is also handed to this real writer, exactly as the production environment hands it
    /// over - so a test can assert what reached the store, and a closed writer refuses it as it would at shutdown.</summary>
    public TurnVerdictTraceWriter? TraceWriter;

    public void RecordTrace(TenantId tenant, TurnVerdictTrace trace)
    {
        BeforeRecordTrace?.Invoke(trace);
        Traces.Enqueue(trace);
        TraceWriter?.Enqueue(tenant, trace);
    }

    /// <summary>Every trace the seat could not hand in, with its cause, in order.</summary>
    public readonly ConcurrentQueue<(TurnVerdictTrace Trace, string Cause)> NotKept = new();

    public void TraceNotKept(TenantId tenant, TurnVerdictTrace trace, string cause)
    {
        NotKept.Enqueue((trace, cause));
        TraceWriter?.NotKept(trace, cause);
    }

    /// <summary>The seat's clock. Replace it to move time without waiting.</summary>
    public Func<DateTime> Clock = () => DateTime.UtcNow;
    public DateTime NowUtc() => Clock();

    private int _snapshotReads;
    public int SnapshotReads => _snapshotReads;

    /// <summary>The sessions each session owns. Null: it owns none.</summary>
    public Func<string, OwnedSessionsFacts?> Owned = _ => null;
    public OwnedSessionsFacts? OwnedSessions(TenantId tenant, string sessionId) => Owned(sessionId);

    public IReadOnlyDictionary<string, TurnVerdictDto> SnapshotLatest(TenantId tenant)
    {
        Interlocked.Increment(ref _snapshotReads);
        lock (_gate)
            return _stored
                .Where(kv => kv.Key.Item1.Equals(tenant))
                .Select(kv => (Session: kv.Key.Item2, Latest: kv.Value
                    .Where(r => !_superseded.Contains((tenant, kv.Key.Item2, r.VerdictId)))
                    .OrderByDescending(r => r.JudgedAtUtc).FirstOrDefault()))
                .Where(x => x.Latest is not null)
                .ToDictionary(x => x.Session, x => x.Latest!, StringComparer.Ordinal);
    }

    /// <summary>Every stored record for this session, newest first.</summary>
    public IReadOnlyList<TurnVerdictDto> StoredRows(TenantId tenant, string sessionId)
    {
        lock (_gate)
            return _stored.TryGetValue((tenant, sessionId), out var rows)
                ? rows.OrderByDescending(r => r.JudgedAtUtc).ToList()
                : new List<TurnVerdictDto>();
    }

    // ------------------------------------------------------------------ canned judge answers

    private static string Json(object value) => JsonSerializer.Serialize(value);

    // THESE ARE CONTRACT v3 ANSWERS: state, label, agentRecommends, menu, options, and nothing else.
    //
    // THE spoken PARAMETER IS STILL HERE, AND IT NO LONGER GOES INTO THE JUDGE'S ANSWER. It now sets what the
    // default narrator answers with, which is where a reading's words come from under v3. Every caller therefore
    // keeps saying the same thing it always said - "the listener hears these words for this stop" - without
    // knowing which of the two calls produces them, which is the point of the change being tested.
    //
    // The evidence parameter is kept on the two that had one so callers need not change, and is IGNORED: the
    // receipt was cut in v3 and nothing checks a quote any more.

    /// <summary>A "cannot-tell" answer: the state for a stop the screen does not support a judgement on, so it
    /// validates against any package, of either kind.</summary>
    public static string CannotTell(string spoken)
    {
        LastCannedSpoken = spoken;
        return Json(new
        {
            state = "cannot-tell",
            label = "Cannot tell what this stop needs",
            agentRecommends = (string?)null,
            menu = (object?)null,
            options = Array.Empty<object>(),
        });
    }

    /// <summary>A finished answer with something for the owner to read.</summary>
    public static string Finished(string evidence, string spoken, string risk = "none")
    {
        LastCannedSpoken = spoken;
        return Json(new
        {
            state = "finished-report",
            label = "Pushed the branch and opened the pull request",
            agentRecommends = (string?)null,
            menu = (object?)null,
            options = Array.Empty<object>(),
        });
    }

    /// <summary>A "carrying-on" answer: the agent said it is still working and will report back, so the stop is
    /// calm and a clock is set on it.</summary>
    public static string CarryingOn(string label, string spoken)
    {
        LastCannedSpoken = spoken;
        return Json(new
        {
            state = "carrying-on",
            label,
            agentRecommends = (string?)null,
            menu = (object?)null,
            options = Array.Empty<object>(),
        });
    }

    /// <summary>
    /// A REFUSED ANSWER THAT IS STILL READABLE, AND STILL CARRIES ITS MENU. The state word is not one of the
    /// seven, so the contract refuses it; every other field is well formed, so SalvageNarrationDecision can
    /// still read the picker out of it.
    ///
    /// This used to be an answer whose receipt was not on the screen. Contract v3 cut the receipt, so that is
    /// no longer a refusal at all - it is one of the twenty-five failures in seventy that v3 deletes - and a
    /// test built on it was quietly testing an accepted answer instead.
    /// </summary>
    public static string RefusedButReadableMenu(string question)
    {
        LastCannedSpoken = "";
        return Json(new
        {
            state = "waiting-on-you",   // not one of the seven
            label = "Choose whether to proceed",
            agentRecommends = (string?)null,
            menu = new { question, selectionMode = "single", submit = "" },
            options = new object[]
            {
                new { key = "Proceed", send = "1", recommended = true, note = "Carries on with the change." },
                new { key = "Stop", send = "2", recommended = false, note = "Leaves the change unmade." },
            },
        });
    }

    /// <summary>A "needs-you" answer on a single-select picker.</summary>
    public static string Menu(string question, string evidence, string spoken)
    {
        LastCannedSpoken = spoken;
        return Json(new
        {
            state = "needs-you",
            label = "Choose whether to proceed",
            agentRecommends = (string?)null,
            menu = new { question, selectionMode = "single", submit = "" },
            options = new object[]
            {
                new { key = "Proceed", send = "1", recommended = true, note = "Carries on with the change." },
                new { key = "Stop", send = "2", recommended = false, note = "Leaves the change unmade." },
            },
        });
    }
}

/// <summary>A tunnel caller that serves a screen and counts the reads, and a brain that counts its asks.</summary>
internal static class TurnVerdictTestDoubles
{
    public static ScreenGridResponse Screen(string sessionId, params string[] rows) => new()
    {
        SessionId = sessionId,
        Rows = rows.ToList(),
        CursorRow = rows.Length - 1,
        CursorVisible = true,
        HasGrid = true,
    };

    public static StoredConversation Reply(string ask, string reply) => new(true, new List<TurnWidgetDto>
    {
        new() { Kind = StoredConversationWidgets.UserTextKind, Content = ask },
        new() { Kind = StoredConversationWidgets.AgentTextKind, Content = reply },
    });

    public static SessionVerbClient RouteServing(string directorId, Func<ScreenGridResponse?> screen, Action? onRead = null)
        => new(new DirectorDto { DirectorId = directorId, ControlEndpoint = "http://tunnel-only" }, (_, command, _) =>
        {
            if (command.Verb == "screen-grid")
            {
                onRead?.Invoke();
                var grid = screen();
                if (grid is not null)
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(
                        JsonSerializer.Serialize(grid, new JsonSerializerOptions(JsonSerializerDefaults.Web))));
            }
            return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success());
        });

    public static async Task<bool> WaitUntil(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }
}

/// <summary>
/// A brain that answers the JUDGE with one fixed text and counts what it was asked.
///
/// IT TELLS THE TWO CALLS OF A READING APART. From contract v3 a reading is not finished until both the judge
/// and the narration call are done, and one brain serves both - but the answers are nothing like each other, the
/// judge being asked for JSON and the narrator for words between two markers. A brain that returned the judge
/// answer to both would leave every reading with no words, and <see cref="Asks"/> would count two where the
/// question a test is asking - "was this screen judged again?" - has the answer one.
/// </summary>
internal sealed class CountingBrain : IAgentBrain
{
    private readonly Func<string> _answer;
    private int _asks;
    private int _narrations;
    public CountingBrain(Func<string> answer) => _answer = answer;

    /// <summary>How many JUDGEMENTS were asked for - never the narration calls beside them.</summary>
    public int Asks => _asks;

    /// <summary>How many narration calls were made.</summary>
    public int Narrations => _narrations;

    /// <summary>What the narration call is answered with. The default is the words the last canned judge answer
    /// was built with, so a reading ends up with the text the test named.</summary>
    public Func<string> NarrationAnswer { get; set; } = FakeTurnVerdictEnvironment.DefaultNarration;

    public string? SessionId => "counting-brain";

    public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
        // The narration prompt is the one that asks for the spoken version between two markers.
        if (prompt.Contains("Output ONLY the spoken version", StringComparison.Ordinal))
        {
            Interlocked.Increment(ref _narrations);
            return Task.FromResult(new AskResult { Text = NarrationAnswer(), ReplySeconds = 0.1 });
        }
        Interlocked.Increment(ref _asks);
        return Task.FromResult(new AskResult { Text = _answer(), ReplySeconds = 0.1 });
    }

    public Task CancelAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<ClearResult> ClearAsync(CancellationToken ct = default) => Task.FromResult(new ClearResult());
    public Task RestartAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task KillAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task<BrainHealth> GetHealthAsync(CancellationToken ct = default) => Task.FromResult(new BrainHealth { IsAlive = true });
    public void Dispose() { }
}
