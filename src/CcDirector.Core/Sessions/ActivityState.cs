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

/// <summary>Shared activity-state categories used by every turn-end listener.</summary>
internal static class ActivityStateFacts
{
    internal static bool IsTurnEnd(ActivityState state) =>
        state is ActivityState.WaitingForInput or ActivityState.WaitingForPerm or ActivityState.MenuOpen;
}
