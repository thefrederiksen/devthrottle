namespace CcDirector.Core.Machine;

/// <summary>
/// Everything the 24-hour local recording keeps about one five-second sample: the sample itself, what
/// each session cost at that moment, and the verdict and diagnosis the Director had ALREADY worked out
/// from it.
///
/// THE VERDICT AND THE DIAGNOSIS ARE HANDED IN, NEVER WORKED OUT HERE. They are computed once, by
/// <c>ResourcePressure</c> and <c>SlownessDiagnoser</c> in Core, and the recording stores what they
/// said. That is what lets <c>cc-devthrottle machine diagnose</c> print "Slow because of the disk" without
/// a second copy of the rules in another language (CLAUDE.md rule 7: whoever reads the recording renders
/// it, and rules nothing). Null means the Director computed none for this sample, and the command says
/// exactly that rather than guessing one from the figures.
/// </summary>
/// <param name="Sample">The probe's reading of the whole machine.</param>
/// <param name="Sessions">What each running session's process tree cost, with its name. Empty when there are none.</param>
/// <param name="Verdict">Normal, Tight or Critical per resource, as the Director decided it. Null when none was computed.</param>
/// <param name="Diagnosis">The answer to "why is it slow", as the Director decided it. Null when none was computed.</param>
public sealed record ResourceMoment(
    ResourceSample Sample,
    IReadOnlyList<RecordedSession> Sessions,
    ResourcePressureVerdict? Verdict,
    SlownessDiagnosis? Diagnosis);

/// <summary>
/// One session's cost at one moment, with the name a person or an agent recognises it by.
/// </summary>
/// <param name="Usage">The session's whole process tree, summed, with its counts of unknown figures.</param>
/// <param name="Name">The session's name. Null when the Director did not know it.</param>
public sealed record RecordedSession(SessionUsage Usage, string? Name);
