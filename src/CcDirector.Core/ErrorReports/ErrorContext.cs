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
/// Every value here is written by our own code - ids, a screen's name, a plain-words action. Never put text a
/// person or a model wrote into it. The reporter scrubs and caps every text field all the same.
/// </summary>
public sealed class ErrorContext : IDisposable
{
    private static readonly AsyncLocal<ErrorContext?> CurrentContext = new();

    private readonly ErrorContext? _outer;
    private bool _disposed;

    private ErrorContext(ErrorContext? outer, string? correlationId, string? sessionId, string? surface,
        string? action, bool? userVisible, int? httpStatus, string? errorCode)
    {
        _outer = outer;
        CorrelationId = correlationId ?? outer?.CorrelationId;
        SessionId = sessionId ?? outer?.SessionId;
        Surface = surface ?? outer?.Surface;
        Action = action ?? outer?.Action;
        UserVisible = userVisible ?? outer?.UserVisible;
        HttpStatus = httpStatus ?? outer?.HttpStatus;
        ErrorCode = errorCode ?? outer?.ErrorCode;
    }

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

    /// <summary>Close the scope: lines logged after this carry the outer scope's fields, or none.</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (ReferenceEquals(CurrentContext.Value, this)) CurrentContext.Value = _outer;
    }
}
