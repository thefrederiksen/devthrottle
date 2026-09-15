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

    public TurnVerdictSettings Settings(TenantId tenant) => Knobs;

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

    public async Task<TurnVerdictJudgeAnswer> AskJudgeAsync(TenantId tenant, string prompt, CancellationToken ct)
    {
        Interlocked.Increment(ref _judgeCalls);
        Prompts.Enqueue(prompt);
        var raw = await Judge(prompt, ct).ConfigureAwait(false);
        return new TurnVerdictJudgeAnswer(raw, Model, 0.2);
    }

    public TurnVerdictDto? Latest(TenantId tenant, string sessionId)
    {
        TurnVerdictDto? latest;
        lock (_gate)
            latest = _stored.TryGetValue((tenant, sessionId), out var rows)
                ? rows.OrderByDescending(r => r.JudgedAtUtc).FirstOrDefault()
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
        }
    }

    public int Invalidate(TenantId tenant, string sessionId)
    {
        Interlocked.Increment(ref _invalidations);
        lock (_gate)
        {
            if (!_stored.Remove((tenant, sessionId), out var rows)) return 0;
            return rows.Count;
        }
    }

    public int StoredCount(TenantId tenant, string sessionId)
    {
        lock (_gate) return _stored.TryGetValue((tenant, sessionId), out var rows) ? rows.Count : 0;
    }

    public bool IsVoiceSession(TenantId tenant, string sessionId) => VoiceSession(sessionId);

    public Task DelayAsync(TimeSpan delay, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Steps.Enqueue($"settle:{delay.TotalMilliseconds:F0}");
        return Task.CompletedTask;
    }

    public void Record(TurnVerdictRecord record) => Records.Enqueue(record);
    public DateTime NowUtc() => DateTime.UtcNow;

    // ------------------------------------------------------------------ canned judge answers

    private static string Json(object value) => JsonSerializer.Serialize(value);

    /// <summary>A "cannot-tell" answer: the one verdict that needs no receipt, so it validates against any
    /// package, of either kind.</summary>
    public static string CannotTell(string spoken) => Json(new
    {
        verdict = "cannot-tell",
        confidence = "ambiguous",
        evidence = "",
        label = "Cannot tell what this stop needs",
        summary = "The stop does not say enough to judge what it needs.",
        agentRecommends = (string?)null,
        answerVia = "reply",
        menu = (object?)null,
        options = Array.Empty<object>(),
        risk = "none",
        spoken,
    });

    /// <summary>A "finished" answer whose receipt is <paramref name="evidence"/>.</summary>
    public static string Finished(string evidence, string spoken, string risk = "none") => Json(new
    {
        verdict = "finished",
        confidence = "high",
        evidence,
        label = "Pushed the branch and opened the pull request",
        summary = "The branch is pushed and the pull request is open; nothing is waiting on you.",
        agentRecommends = (string?)null,
        answerVia = "reply",
        menu = (object?)null,
        options = Array.Empty<object>(),
        risk,
        spoken,
    });

    /// <summary>A "needed-you" answer on a single-select picker, receipt <paramref name="evidence"/>.</summary>
    public static string Menu(string question, string evidence, string spoken) => Json(new
    {
        verdict = "needed-you",
        confidence = "high",
        evidence,
        label = "Choose whether to proceed",
        summary = "The session is waiting on a yes or no in a picker.",
        agentRecommends = (string?)null,
        answerVia = "keys",
        menu = new { question, selectionMode = "single", submit = "" },
        options = new object[]
        {
            new { key = "Proceed", send = "1", recommended = true, note = "Carries on with the change." },
            new { key = "Stop", send = "2", recommended = false, note = "Leaves the change unmade." },
        },
        risk = "none",
        spoken,
    });
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

/// <summary>A brain that answers every ask with one fixed text and counts the asks.</summary>
internal sealed class CountingBrain : IAgentBrain
{
    private readonly Func<string> _answer;
    private int _asks;
    public CountingBrain(Func<string> answer) => _answer = answer;
    public int Asks => _asks;
    public string? SessionId => "counting-brain";

    public Task<AskResult> AskAsync(string prompt, CancellationToken ct = default)
    {
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
