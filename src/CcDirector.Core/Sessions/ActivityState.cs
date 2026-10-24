namespace CcDirector.Core.Sessions;

/// <summary>
/// Tracks what Claude is cognitively doing within a session.
/// Separate from <see cref="SessionStatus"/> which tracks process lifecycle.
/// </summary>
public enum ActivityState
{
    Starting,
    Idle,
    Working,
    WaitingForInput,
    WaitingForPerm,
    MenuOpen,
    Exited
}
