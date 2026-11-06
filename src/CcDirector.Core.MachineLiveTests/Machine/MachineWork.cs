namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// REAL PROCESSOR WORK, MEASURED IN WORK AND NOT IN TIME.
///
/// The resource probe's live tests need the machine to have actually DONE something between two
/// readings, or there is nothing for the counters to count. The obvious way to arrange that is to
/// sleep, and the obvious way to stop is to watch the clock - and both are refused by
/// <c>GateTestsDoNotDependOnTheMachineTests</c>, under the owner's ruling of 11 October 2026 that a
/// test must fail only for a code defect. A sleeping test measures the machine's scheduler; a
/// clock-bounded spin measures the machine's speed; and v2.18.0 took three days to ship because tests
/// like that were in the release gate.
///
/// SO THE WORK IS A FIXED NUMBER OF ITERATIONS, not a duration. A fixed amount of arithmetic costs
/// very nearly the same amount of PROCESSOR TIME on a fast machine as on a slow one - it just takes
/// less wall-clock time to get through - which is exactly the property the tests need: they assert on
/// processor time and on counter movement, never on how long anything took.
/// </summary>
internal static class MachineWork
{
    /// <summary>
    /// How many iterations one unit of work is. Chosen so that one unit is tens of milliseconds of
    /// processor time on a current machine - comfortably more than one scheduler tick, which is what
    /// the tick counters advance in - while staying far too small to slow the suite down.
    /// </summary>
    private const int IterationsPerUnit = 40_000_000;

    /// <summary>
    /// Does <paramref name="units"/> units of arithmetic on this thread and returns the result.
    ///
    /// THE RESULT IS RETURNED SO THE CALLER CAN ASSERT ON IT, which is what stops the whole loop
    /// being optimised away. A loop whose result nothing reads is a loop the compiler is entitled to
    /// delete, and then the test proves that no work moves no counters.
    /// </summary>
    internal static double Burn(int units = 1)
    {
        var total = 0.0;
        var iterations = (long)units * IterationsPerUnit;

        for (long index = 1; index <= iterations; index++)
            total += 1.0 / index;

        return total;
    }

    /// <summary>
    /// Does <paramref name="units"/> units of work on each of <paramref name="threads"/> threads at
    /// once, and returns the total. Used where a test needs more than one core busy.
    /// </summary>
    internal static double BurnOnThreads(int threads, int units = 1)
    {
        var results = new double[threads];
        var running = new List<Thread>(threads);

        for (var index = 0; index < threads; index++)
        {
            var slot = index;
            var thread = new Thread(() => results[slot] = Burn(units));
            thread.Start();
            running.Add(thread);
        }

        foreach (var thread in running) thread.Join();

        return results.Sum();
    }
}
