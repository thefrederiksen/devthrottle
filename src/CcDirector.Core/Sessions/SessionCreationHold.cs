namespace CcDirector.Core.Sessions;

/// <summary>
/// A hold on new sessions, from <see cref="SessionManager.HoldSessionCreation"/>. While it is undisposed no
/// session can start on this Director. Disposing it lifts the hold; disposing twice is harmless.
/// </summary>
public sealed class SessionCreationHold : IDisposable
{
    private Action? _release;

    /// <param name="sessionsAtHold">How many sessions there were when the hold took effect.</param>
    /// <param name="release">Lifts the hold; called once.</param>
    public SessionCreationHold(int sessionsAtHold, Action release)
    {
        SessionsAtHold = sessionsAtHold;
        _release = release;
    }

    /// <summary>How many sessions the Director held, or was creating, when the hold took effect.</summary>
    public int SessionsAtHold { get; }

    /// <summary>Lift the hold.</summary>
    public void Dispose()
    {
        Interlocked.Exchange(ref _release, null)?.Invoke();
    }
}
