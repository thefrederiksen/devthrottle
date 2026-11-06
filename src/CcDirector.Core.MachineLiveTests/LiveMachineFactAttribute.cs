using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// A test that READS THE REAL MACHINE, and which therefore SKIPS - visibly, with a stated reason -
/// unless it is asked for by name.
///
/// WHY IT IS OPT-IN. A test in a gate suite never lets a real disk, a real clock, a real port or a real
/// process decide whether it passes; a sensor that lights at random is worse than no sensor. But the one
/// thing a resource probe exists to do is read a real machine, and that cannot be proved against a fake.
/// So these tests exist, they are run deliberately - by a Developer on the platform in question and by
/// the cross-platform quality assurance run - and the gate never runs them.
///
/// WHY A SKIP AND NOT AN EARLY RETURN. The idiom this replaces was
/// <c>if (!Enabled) return;</c> at the top of the test body, which reports a PASS for a test that ran no
/// assertion at all. That is a check that fails open: the green count silently includes tests that did
/// nothing, and no reader can tell which readings were actually taken. A skip appears in the run as
/// skipped, with its reason, so the gated tests are counted in the open.
///
/// THE DECISION IS A PURE FUNCTION - <see cref="SkipReason"/> - and the attribute only applies it. The
/// first version decided inside the constructor and the property setter, reading the environment and the
/// core count as it went, which left the core-count branch unreachable unless the environment variable
/// happened to be set: the lock that protects every other test here could not itself be tested in an
/// ordinary run. Taking both facts as arguments makes every branch testable always.
///
/// HOW TO RUN THEM: set <c>CC_DIRECTOR_LIVE_MACHINE_TESTS=1</c>.
/// </summary>
public sealed class LiveMachineFactAttribute : FactAttribute
{
    /// <summary>The one variable that turns every machine-reading test on.</summary>
    public const string Variable = "CC_DIRECTOR_LIVE_MACHINE_TESTS";

    /// <summary>True when the caller has asked for the machine to be read.</summary>
    public static bool Enabled => Environment.GetEnvironmentVariable(Variable) == "1";

    private readonly string _reads;
    private int _minimumLogicalCores;

    /// <param name="reads">What this test reads from the machine, in plain English, for the skip reason.</param>
    public LiveMachineFactAttribute(string reads)
    {
        _reads = reads;
        Apply();
    }

    /// <summary>
    /// How many logical cores this machine must have for the test to be able to judge anything. Below
    /// it the test SKIPS, with the count in the reason, rather than passing on a machine that cannot
    /// tell the right answer from the wrong one.
    ///
    /// It exists for the per-process processor check: on one or two cores, a share of the whole machine
    /// and a share of one core are too close to tell apart, so a pass there would be meaningless and a
    /// failure would be the test's fault rather than the probe's. Said out loud in the run rather than
    /// hidden in an early return, which would report a pass for a test that proved nothing.
    /// </summary>
    public int MinimumLogicalCores
    {
        get => _minimumLogicalCores;
        set
        {
            _minimumLogicalCores = value;
            Apply();
        }
    }

    private void Apply() => Skip = SkipReason(_reads, _minimumLogicalCores, Enabled, Environment.ProcessorCount);

    /// <summary>
    /// The reason this test should be skipped, or null when it should run. Pure: it reads nothing, so
    /// every branch can be exercised from a test without setting an environment variable or finding a
    /// machine with the right number of cores.
    ///
    /// NOT BEING ASKED FOR COMES FIRST. When the opt-in is absent the test cannot run at all, so that is
    /// the reason the reader needs; the core count only matters once it has been asked for.
    /// </summary>
    /// <param name="reads">What the test reads from the machine, in plain English.</param>
    /// <param name="minimumLogicalCores">The core count the test needs to judge its result. Zero means any.</param>
    /// <param name="enabled">Whether the caller asked for machine-reading tests.</param>
    /// <param name="logicalCores">How many logical cores this machine has.</param>
    public static string? SkipReason(string reads, int minimumLogicalCores, bool enabled, int logicalCores)
    {
        if (!enabled)
            return $"Reads the real machine ({reads}). Set {Variable}=1 to run it.";

        if (logicalCores < minimumLogicalCores)
            return $"Reads the real machine ({reads}), and needs at least {minimumLogicalCores} logical "
                   + $"cores to judge the result. This machine has {logicalCores}.";

        return null;
    }
}
