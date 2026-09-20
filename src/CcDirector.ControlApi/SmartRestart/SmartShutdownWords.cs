namespace CcDirector.ControlApi.SmartRestart;

/// <summary>
/// THE WORDS OF A SMART SHUTDOWN, IN ONE PLACE. A screen shows these as they are and never words a state
/// or a phase itself, so a new state is one edit here and no new branch in any window.
/// </summary>
public static class SmartShutdownWords
{
    /// <summary>What a phase is called on the progress screen.</summary>
    /// <param name="phase">The phase.</param>
    public static string PhaseLabel(SmartShutdownPhase phase) => phase switch
    {
        SmartShutdownPhase.Starting => "Writing the record of what is running",
        SmartShutdownPhase.Asking => "Asking each session to hand over",
        SmartShutdownPhase.Collecting => "Waiting for handovers and closing sessions as they finish",
        SmartShutdownPhase.Interrupting => "Interrupting sessions that are still working and asking again",
        SmartShutdownPhase.EndingAtLimit => "Shutting down every session that is still running",
        SmartShutdownPhase.Cancelling => "Cancelling: telling sessions the restart is off and bringing closed ones back",
        SmartShutdownPhase.Restarting => "Every session is shut down - restarting the Director",
        SmartShutdownPhase.Finished => "Finished",
        _ => throw new ArgumentOutOfRangeException(nameof(phase), phase, "A phase with no words. Add them here."),
    };

    /// <summary>What a session's state is called on the progress screen.</summary>
    /// <param name="state">The state.</param>
    public static string StateLabel(SmartShutdownSessionState state) => state switch
    {
        SmartShutdownSessionState.Pending => "Waiting to be asked by its lead",
        SmartShutdownSessionState.Asked => "Asked to hand over",
        SmartShutdownSessionState.NotDelivered => "The request did not reach it",
        SmartShutdownSessionState.Writing => "Writing its handover",
        SmartShutdownSessionState.HandedOver => "Handed over",
        SmartShutdownSessionState.Interrupted => "Interrupted and asked to hand over now",
        SmartShutdownSessionState.ShutDown => "Shut down",
        SmartShutdownSessionState.EndedAtLimit => "Ended when time was up",
        SmartShutdownSessionState.KeptRunning => "Still running - the restart is off",
        SmartShutdownSessionState.BroughtBack => "Brought back from its handover",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "A state with no words. Add them here."),
    };

    /// <summary>"4 of 9 shut down".</summary>
    /// <param name="gone">How many sessions are verified gone.</param>
    /// <param name="total">How many sessions the run covers.</param>
    public static string CountLabel(int gone, int total) => $"{gone} of {total} shut down";
}
