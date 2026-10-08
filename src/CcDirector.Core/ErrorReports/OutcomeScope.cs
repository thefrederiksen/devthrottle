namespace CcDirector.Core.ErrorReports;

/// <summary>
/// A pass whose outcome is reported as ONE report (<see cref="ErrorReporter.BeginOutcomeScope"/>). While it is open,
/// the error lines its own flow of execution logs are held here instead of becoming reports of their own; the caller
/// carries them in the outcome's detail. Bounded: at most <see cref="MaxHeldLines"/> lines are kept, and the number
/// beyond that is counted, so a pass that logs in a loop cannot grow it.
/// </summary>
public sealed class OutcomeScope : IDisposable
{
    /// <summary>The most error lines one scope keeps.</summary>
    public const int MaxHeldLines = 20;

    private static readonly AsyncLocal<OutcomeScope?> CurrentScope = new();

    private readonly object _lock = new();
    private readonly List<string> _held = new();
    private int _notKept;
    private bool _disposed;

    private OutcomeScope(ErrorReporter owner) => Owner = owner;

    /// <summary>The scope open on this flow of execution, if any.</summary>
    internal static OutcomeScope? Current => CurrentScope.Value is { _disposed: false } scope ? scope : null;

    /// <summary>The reporter whose error lines this scope holds.</summary>
    internal ErrorReporter Owner { get; }

    internal static OutcomeScope Open(ErrorReporter owner)
    {
        var scope = new OutcomeScope(owner);
        CurrentScope.Value = scope;
        return scope;
    }

    /// <summary>The error lines held so far, oldest first; a last line says how many more were not kept.</summary>
    public IReadOnlyList<string> HeldLines
    {
        get
        {
            lock (_lock)
            {
                var lines = new List<string>(_held);
                if (_notKept > 0) lines.Add($"({_notKept} more error line(s) were logged during the pass and not kept)");
                return lines;
            }
        }
    }

    internal void Hold(string line)
    {
        lock (_lock)
        {
            if (_held.Count < MaxHeldLines) _held.Add(line);
            else _notKept++;
        }
    }

    /// <summary>Close the scope: error lines logged after this become reports of their own again.</summary>
    public void Dispose()
    {
        _disposed = true;
        if (ReferenceEquals(CurrentScope.Value, this)) CurrentScope.Value = null;
    }
}
