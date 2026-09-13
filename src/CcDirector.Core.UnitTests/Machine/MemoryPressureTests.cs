using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// The fold from a memory reading to a pressure level (issue #2818).
///
/// These belong in the PARALLEL half of the Core suite, which forbids wall-clock dependence, and they
/// hold to it: <see cref="MemoryPressure"/> is a pure function of a reading the test hands it, so
/// nothing here sleeps, measures elapsed time, or reads the machine it runs on. That is deliberate -
/// a test for "what does the Director do when memory is short" must not need a machine that is
/// actually short of memory.
/// </summary>
public class MemoryPressureTests
{
    private static readonly DateTime At = new(2026, 9, 13, 12, 0, 0, DateTimeKind.Utc);

    private const ulong Gigabyte = 1024UL * 1024 * 1024;

    private static MachineMemoryReading Reading(ulong totalGigabytes, double availableGigabytes) =>
        MachineMemoryReading.Read(totalGigabytes * Gigabyte, (ulong)(availableGigabytes * Gigabyte), At);

    // -------------------------------------------------------------------------------------------
    // The reading could not be taken
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Level_UnreadableReading_IsUnknownAndNeverNormal()
    {
        var level = MemoryPressure.Level(MachineMemoryReading.Unreadable("no /proc/meminfo", At));

        Assert.Equal(MemoryPressureLevel.Unknown, level);
        Assert.NotEqual(MemoryPressureLevel.Normal, level);
    }

    [Fact]
    public void Level_ZeroTotal_IsUnknown_NotCritical()
    {
        // A total of zero is not a machine with no memory, it is a platform that answered with
        // nothing. Folding it to Critical would widen every deadline on the fleet off a bad read.
        var level = MemoryPressure.Level(MachineMemoryReading.Read(totalBytes: 0, availableBytes: 0, At));

        Assert.Equal(MemoryPressureLevel.Unknown, level);
    }

    [Fact]
    public void Level_AvailableAboveTotal_IsUnknown()
    {
        var level = MemoryPressure.Level(MachineMemoryReading.Read(8 * Gigabyte, 9 * Gigabyte, At));

        Assert.Equal(MemoryPressureLevel.Unknown, level);
    }

    [Fact]
    public void Unknown_DoesNotCountAsUnderPressure()
    {
        // The question every caller asks. Unknown must not widen a deadline any more than Normal does:
        // nothing is known about the machine, so nothing changes.
        Assert.False(MemoryPressure.IsUnderPressure(MemoryPressureLevel.Unknown));
        Assert.False(MemoryPressure.IsUnderPressure(MemoryPressureLevel.Normal));
        Assert.True(MemoryPressure.IsUnderPressure(MemoryPressureLevel.Tight));
        Assert.True(MemoryPressure.IsUnderPressure(MemoryPressureLevel.Critical));
    }

    // -------------------------------------------------------------------------------------------
    // A healthy machine
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(16, 8.0)]    // half free
    [InlineData(32, 20.0)]   // a workstation with room
    [InlineData(8, 4.0)]     // a small laptop, still half free
    public void Level_PlentyAvailable_IsNormal(ulong totalGigabytes, double availableGigabytes)
    {
        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressure.Level(Reading(totalGigabytes, availableGigabytes)));
    }

    // -------------------------------------------------------------------------------------------
    // The absolute floor - the reason the fold is not a fraction alone
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Level_LargeMachineWithSmallFraction_IsNormalWhileManyGigabytesRemain()
    {
        // THE DESIGN REVIEW'S COUNTEREXAMPLE, kept as a permanent guard (issue #2818).
        //
        // A 128 gigabyte machine with 8 gigabytes available is 6.25 percent free. The first version of
        // this fold tested "fraction below 7 percent OR bytes below 800 megabytes" and called this
        // machine Critical, because a branch of an OR can only make a verdict more severe - the
        // absolute floor could never protect anything. Eight gigabytes available is a comfortable
        // machine. If this test ever fails, the fraction has crept back into the verdict.
        var reading = MachineMemoryReading.Read(128 * Gigabyte, 8 * Gigabyte, At);

        Assert.Equal(0.0625, reading.AvailableFraction);
        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Level_SmallMachineNearlyFull_IsCritical()
    {
        // The machine issue #2811 was filed about: an older laptop where everything went slow and the
        // Director said nothing. 600 megabytes free of 16 gigabytes.
        var level = MemoryPressure.Level(MachineMemoryReading.Read(16 * Gigabyte, 600UL * 1024 * 1024, At));

        Assert.Equal(MemoryPressureLevel.Critical, level);
    }

    [Fact]
    public void Level_SameAvailableBytes_GivesSameVerdictOnEverySizeOfMachine()
    {
        // The verdict is on available bytes and nothing else, so one and a half gigabytes available is
        // the same answer on a laptop and on a workstation. This is the property that replaced the
        // fraction, stated directly.
        var laptop = MemoryPressure.Level(MachineMemoryReading.Read(8 * Gigabyte, 1536UL * 1024 * 1024, At));
        var workstation = MemoryPressure.Level(MachineMemoryReading.Read(128 * Gigabyte, 1536UL * 1024 * 1024, At));

        Assert.Equal(MemoryPressureLevel.Tight, laptop);
        Assert.Equal(laptop, workstation);
    }

    // -------------------------------------------------------------------------------------------
    // Threshold boundaries - both are exclusive
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Level_ExactlyAtTightFloor_IsNormal_ThresholdIsExclusive()
    {
        var reading = MachineMemoryReading.Read(16 * Gigabyte, MemoryPressure.TightAbsoluteBytes, At);

        Assert.Equal(MemoryPressureLevel.Normal, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Level_JustBelowTightFloor_IsTight()
    {
        var reading = MachineMemoryReading.Read(16 * Gigabyte, MemoryPressure.TightAbsoluteBytes - 1, At);

        Assert.Equal(MemoryPressureLevel.Tight, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Level_ExactlyAtCriticalFloor_IsTightNotCritical()
    {
        var reading = MachineMemoryReading.Read(8 * Gigabyte, MemoryPressure.CriticalAbsoluteBytes, At);

        Assert.Equal(MemoryPressureLevel.Tight, MemoryPressure.Level(reading));
    }

    [Fact]
    public void Level_JustBelowCriticalAbsoluteFloor_IsCritical()
    {
        var reading = MachineMemoryReading.Read(8 * Gigabyte, MemoryPressure.CriticalAbsoluteBytes - 1, At);

        Assert.Equal(MemoryPressureLevel.Critical, MemoryPressure.Level(reading));
    }

    // -------------------------------------------------------------------------------------------
    // The multiplier - what actually changes a deadline
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void DeadlineMultiplier_HealthyOrUnknown_ChangesNothing()
    {
        // THE IMPORTANT ONE. On a machine with room, and on a machine we could not read, every deadline
        // in the Director must stay exactly where it was before issue #2818 touched it.
        Assert.Equal(1.0, MemoryPressure.DeadlineMultiplier(MemoryPressureLevel.Normal));
        Assert.Equal(1.0, MemoryPressure.DeadlineMultiplier(MemoryPressureLevel.Unknown));
    }

    [Fact]
    public void DeadlineMultiplier_RisesWithPressure()
    {
        var tight = MemoryPressure.DeadlineMultiplier(MemoryPressureLevel.Tight);
        var critical = MemoryPressure.DeadlineMultiplier(MemoryPressureLevel.Critical);

        Assert.True(tight > 1.0);
        Assert.True(critical > tight);
    }

    // -------------------------------------------------------------------------------------------
    // Reporting
    // -------------------------------------------------------------------------------------------

    [Fact]
    public void Describe_EveryLevel_SaysSomethingAPersonCanRead()
    {
        foreach (var level in Enum.GetValues<MemoryPressureLevel>())
            Assert.False(string.IsNullOrWhiteSpace(MemoryPressure.Describe(level)));
    }

    [Fact]
    public void ToString_UnreadableReading_CarriesTheReason()
    {
        var text = MachineMemoryReading.Unreadable("no /proc/meminfo", At).ToString();

        Assert.Contains("no /proc/meminfo", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AvailableFraction_UnreadableReading_IsNull_SoNobodyDividesByAnUnmeasuredNumber()
    {
        Assert.Null(MachineMemoryReading.Unreadable("nope", At).AvailableFraction);
        Assert.Null(MachineMemoryReading.Read(totalBytes: 0, availableBytes: 0, At).AvailableFraction);
    }
}
