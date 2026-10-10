namespace CcDirector.Core.ErrorReports;

/// <summary>
/// What the code was doing when an error line is logged, carried on the flow of execution (issue #3675). An error
/// reaches <see cref="ErrorReporter"/> as bare log text; this is the ONE way to add the optional report fields to it
/// - which command or delivery it belongs to, which session, which screen, what the user was trying to do, whether
/// the user saw it, and the Gateway answer behind it. Every error line logged while a context is open on this flow
/// of execution (this thread and the tasks it starts) is stamped with its fields.
///
/// <code>
/// using var _ = ErrorContext.Begin(correlationId: commandId, sessionId: sessionId);
/// </code>
///
/// Scopes nest: an inner scope keeps every field of the outer one it does not set itself, and disposing it puts
/// the outer one back. The same model as <see cref="OutcomeScope"/>.
///
/// Keep a scope open until the last row it is meant to stamp has been logged. A task started inside it that logs
/// after it was disposed is stamped with nothing - the scope's fields describe work that has ended.
///
/// An inner scope can override an outer field but never clear it: a field it leaves out keeps the outer value. So a
/// field that must describe one row only - userVisible, surface, action - goes on the innermost scope around that
/// row; a scope around a whole path carries only what every row in it shares, such as the correlation and session id.
///
/// An inner scope copies the outer's fields when it opens, so disposing the outer while the inner is still open
/// (possible only with scopes held by hand, never with nested using blocks) leaves the inner carrying them.
///
/// Every value here is written by our own code - ids, a screen's name, a plain-words action. Never put text a
/// person or a model wrote into it. The reporter scrubs and caps every text field all the same.
///
/// WITHHELD TEXT. Some error lines must carry what a screen showed - a refusal says what the composer held - and that
/// can be the words a person typed. The owner's rule is that a report never carries a prompt's words. So the code that
/// puts such text into a line also hands the exact same string to <see cref="Withhold"/>, and every report made inside
/// the context has each withheld string replaced by <c>&lt;withheld: N characters&gt;</c> before anything else is done
/// with it. The local log keeps the line whole. Withheld strings belong to the whole chain of nested scopes, so a line
/// logged by an outer scope after an inner one ended (the command's own failure line) is covered too. They are also
/// kept in a small process-wide list of the most recent ones, applied to EVERY report, so a caller that logs the same
/// failure message outside any context (a Wingman, a queue drain, a fleet relay) cannot carry the text out either.
/// </summary>
public sealed class ErrorContext : IDisposable
{
    /// <summary>The most strings one chain of scopes withholds. Past this the chain is marked overflowed and each of
    /// its reports keeps only the head of its line - the safe direction, never a line with text that was not withheld.</summary>
    internal const int MaxWithheld = 256;

    /// <summary>The prompt itself is withheld only from this length: a shorter one cannot be told from an ordinary word,
    /// and replacing every "go" in a line would make the report unreadable. A screen string is withheld at any length,
    /// as the exact token its line carries.</summary>
    internal const int MinWithheldPromptChars = 4;

    /// <summary>How many recently withheld strings every report is checked against, whatever its context.</summary>
    internal const int MaxRecentWithheld = 512;

    /// <summary>The shortest string the process-wide list keeps. A shorter one is withheld inside its own context only:
    /// applied to every report in the process it would blank ordinary words in unrelated lines.</summary>
    internal const int MinRecentWithheldChars = 8;

    private static readonly AsyncLocal<ErrorContext?> CurrentContext = new();
    private static readonly object RecentLock = new();
    private static readonly Queue<string> Recent = new();
    private static readonly HashSet<string> RecentSet = new(StringComparer.Ordinal);

    private readonly ErrorContext? _outer;
    private readonly WithheldTexts _withheld;
    private bool _disposed;

    /// <summary>The strings one chain of scopes withholds, shared by every scope in it.</summary>
    internal sealed class WithheldTexts
    {
        private readonly object _lock = new();
        private readonly List<string> _texts = new();
        private bool _overflowed;

        public void Add(string text)
        {
            lock (_lock)
            {
                if (_texts.Contains(text, StringComparer.Ordinal)) return;
                if (_texts.Count >= MaxWithheld) _overflowed = true;
                else _texts.Add(text);
            }
        }

        /// <summary>The strings, longest first so a string inside a longer one cannot break the longer one's match.</summary>
        public (IReadOnlyList<string> Texts, bool Overflowed) Snapshot()
        {
            lock (_lock) return (_texts.OrderByDescending(t => t.Length).ToList(), _overflowed);
        }
    }

    private ErrorContext(ErrorContext? outer, string? correlationId, string? sessionId, string? surface,
        string? action, bool? userVisible, int? httpStatus, string? errorCode)
    {
        _outer = outer;
        _withheld = outer?._withheld ?? new WithheldTexts();
        // An empty string is "not set", the same as null: a command's id defaults to "", and it must not hide the outer one.
        CorrelationId = Pick(correlationId, outer?.CorrelationId);
        SessionId = Pick(sessionId, outer?.SessionId);
        Surface = Pick(surface, outer?.Surface);
        Action = Pick(action, outer?.Action);
        UserVisible = userVisible ?? outer?.UserVisible;
        HttpStatus = httpStatus ?? outer?.HttpStatus;
        ErrorCode = Pick(errorCode, outer?.ErrorCode);
    }

    private static string? Pick(string? value, string? outer) => string.IsNullOrEmpty(value) ? outer : value;

    /// <summary>The context open on this flow of execution, with the outer scopes' fields merged in; null when none is.</summary>
    public static ErrorContext? Current
    {
        get
        {
            var context = CurrentContext.Value;
            // A scope disposed on another flow (a task that outlived its caller's using block) is skipped, never
            // stamped: its fields describe work that has ended.
            while (context is { _disposed: true }) context = context._outer;
            return context;
        }
    }

    /// <summary>The command or delivery id: every row one failure caused carries it, so they read as one incident.</summary>
    public string? CorrelationId { get; }

    /// <summary>The DevThrottle session the work concerned.</summary>
    public string? SessionId { get; }

    /// <summary>Which screen the user was on: "session terminal", "phone chat", "settings".</summary>
    public string? Surface { get; }

    /// <summary>What the user was trying to do, in plain words: "send a prompt to the session".</summary>
    public string? Action { get; }

    /// <summary>True when an error logged here is the one the user saw on a screen.</summary>
    public bool? UserVisible { get; }

    /// <summary>The Gateway answer behind the error, when there was one.</summary>
    public int? HttpStatus { get; }

    /// <summary>The machine-readable code from that answer, when there was one.</summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Open a context on this flow of execution. Every argument is optional; one left out keeps the value of the
    /// scope already open, if any. Dispose it to put the outer scope back.
    /// </summary>
    public static ErrorContext Begin(string? correlationId = null, string? sessionId = null, string? surface = null,
        string? action = null, bool? userVisible = null, int? httpStatus = null, string? errorCode = null)
    {
        var context = new ErrorContext(Current, correlationId, sessionId, surface, action, userVisible, httpStatus, errorCode);
        CurrentContext.Value = context;
        return context;
    }

    /// <summary>
    /// Withhold <paramref name="text"/> - the exact string a line is about to carry from a screen or from a prompt - from
    /// every report made inside the context open on this flow of execution, and - from
    /// <see cref="MinRecentWithheldChars"/> characters - from every report in the process while it is among the
    /// <see cref="MaxRecentWithheld"/> most recent. Empty text is ignored.
    /// </summary>
    public static void Withhold(string? text) => Withhold(text, processWide: true);

    private static void Withhold(string? text, bool processWide)
    {
        if (string.IsNullOrEmpty(text)) return;
        Current?._withheld.Add(text);
        if (!processWide || text.Length < MinRecentWithheldChars) return;
        lock (RecentLock)
        {
            if (!RecentSet.Add(text)) return;
            Recent.Enqueue(text);
            while (Recent.Count > MaxRecentWithheld) RecentSet.Remove(Recent.Dequeue());
        }
    }

    /// <summary>
    /// Withhold a prompt's own words, from <see cref="MinWithheldPromptChars"/> characters, inside the context open on
    /// this flow of execution only. A prompt is ordinary words - "delivered", "the release" - and kept process-wide it
    /// would blank those words in every unrelated report; it reaches a line elsewhere only through what a screen showed,
    /// which the code that describes the screen withholds on its own.
    /// </summary>
    public static void WithholdPrompt(string? prompt)
    {
        if (prompt is null || prompt.Length < MinWithheldPromptChars) return;
        Withhold(prompt, processWide: false);
    }

    /// <summary>
    /// <paramref name="text"/> with every string withheld in <paramref name="context"/>'s chain, and every recently
    /// withheld string, replaced. When the chain overflowed, only the head of the line is kept
    /// (<see cref="ErrorLine.Head"/>), since a string past the cap was never recorded and could be anywhere in it.
    /// </summary>
    internal static string ApplyWithheld(string text, ErrorContext? context)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var texts = new HashSet<string>(StringComparer.Ordinal);
        if (context is not null)
        {
            var (chain, overflowed) = context._withheld.Snapshot();
            if (overflowed) return ErrorLine.Head(text) + ": <withheld: this line may carry screen or prompt text>";
            texts.UnionWith(chain);
        }
        lock (RecentLock) texts.UnionWith(Recent);
        // Longest first, so a string inside a longer one cannot break the longer one's match.
        foreach (var withheld in texts.OrderByDescending(t => t.Length))
            text = text.Replace(withheld, $"<withheld: {withheld.Length} characters>", StringComparison.Ordinal);
        return text;
    }

    /// <summary>Close the scope: lines logged after this carry the outer scope's fields, or none.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(CurrentContext.Value, this)) CurrentContext.Value = _outer;
    }
}
