namespace CcDirector.ControlApi;

/// <summary>
/// What asking the operating system about a process identifier actually established (mission
/// "Stop a session", the Architect's ruling on inspection 1).
///
/// THERE ARE THREE ANSWERS, AND THE THIRD IS A SYNONYM FOR NEITHER OF THE OTHER TWO. Before this
/// existed the liveness check returned a plain <c>bool</c> and every failure to read collapsed into
/// <c>false</c> - so a live process whose <c>HasExited</c> threw was reported as a process that was
/// gone, the row was cleared, and the operator was told "already stopped" about something that was
/// still running. That is the inference this mission exists to remove, said inside the fix for it.
/// </summary>
internal enum ProcessLiveness
{
    /// <summary>A process with that identifier exists on this machine and has not exited.</summary>
    Alive,

    /// <summary>The operating system says there is no such process. An established absence.</summary>
    Gone,

    /// <summary>
    /// The question could not be answered. NOT an absence: nothing successfully looked, so this
    /// must never be reported as "already stopped" and must never certify that a process ended.
    /// </summary>
    Unreadable,
}

/// <summary>
/// One liveness answer, together with the machine's own words when it could not be read.
///
/// The words matter: an operator told only that "the process could not be read" cannot act, while
/// "could not read process 4242: Win32Exception: Access is denied" names the thing to go and fix.
/// They are carried all the way to the sentence the Gateway folds, so nothing invents a reason.
/// </summary>
/// <param name="State">Which of the three answers this is.</param>
/// <param name="WhatCouldNotBeRead">
/// The machine's own words, present only when <paramref name="State"/> is
/// <see cref="ProcessLiveness.Unreadable"/>, and null on the two established answers.
/// </param>
internal readonly record struct ProcessLivenessReading(ProcessLiveness State, string? WhatCouldNotBeRead)
{
    /// <summary>A process with that identifier exists and has not exited.</summary>
    internal static ProcessLivenessReading IsAlive { get; } = new(ProcessLiveness.Alive, null);

    /// <summary>The operating system says there is no such process.</summary>
    internal static ProcessLivenessReading IsGone { get; } = new(ProcessLiveness.Gone, null);

    /// <summary>The question could not be answered, and this is what the machine said about why.</summary>
    internal static ProcessLivenessReading CouldNotRead(string whatCouldNotBeRead)
        => new(ProcessLiveness.Unreadable, whatCouldNotBeRead);
}
