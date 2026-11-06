using System.Globalization;
using CcDirector.Core.Machine;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// THE macOS PROBE'S PARSING, COVERED WITHOUT A MAC.
///
/// Every test here drives a pure function from output captured on a real Mac and committed in
/// TestData/Machine/MacOs - see that folder's README for the machine and the moment. The probe's
/// system calls cannot run on a Windows or Linux build machine, but the reading of their output is
/// where the mistakes actually are, and it needs no Mac.
///
/// TWO BUGS FOUND BY MEASURING RATHER THAN BY READING are pinned here, because both produced
/// plausible figures rather than failures. <c>struct proc_bsdinfo</c> is 136 bytes and not the 144
/// that counting its fields suggests, and <c>proc_listallpids</c> returns a count of processes and
/// not a count of bytes. The first shifted every process name by a character and dated every process
/// to 1970; the second reported 181 of the machine's 723 processes. Nothing threw in either case.
/// </summary>
public class MacOsResourceParsingTests
{
    // =============================================================================================
    // Property lists
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_RealStatistics_ReadsEveryCounter()
    {
        var values = MacOsResourceParsing.ReadPropertyListDictionary(Fixture.Text("iokit-statistics-disk0.plist"));

        // Named as literals, not constants: the probe no longer ASKS for the byte counters, so there
        // are no constants for them any more. The parser still reads every key IOKit sent, which is
        // what these two assertions prove.
        Assert.Equal("431900520448", values["Bytes (Read)"]);
        Assert.Equal("78900072448", values["Bytes (Write)"]);
        Assert.Equal("8096100", values[MacOsResourceParsing.StatisticsOperationsRead]);
        Assert.Equal("3692471", values[MacOsResourceParsing.StatisticsOperationsWritten]);
        Assert.Equal("2230432340709", values[MacOsResourceParsing.StatisticsTotalTimeRead]);
        Assert.Equal("323914863274", values[MacOsResourceParsing.StatisticsTotalTimeWritten]);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_RealCharacteristics_ReadsMediumType()
    {
        var values = MacOsResourceParsing.ReadPropertyListDictionary(
            Fixture.Text("iokit-device-characteristics-disk0.plist"));

        Assert.Equal(
            MacOsResourceParsing.MediumTypeSolidState,
            values[MacOsResourceParsing.DeviceCharacteristicsMediumType]);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_RealCapture_CarriesADocumentTypeDeclaration()
    {
        // The point of this test is the FIXTURE, not the parser: if a future capture arrives without
        // Apple's document type line then the parser is no longer being proved against the shape the
        // operating system really produces, and the test below it stops meaning anything.
        Assert.Contains("<!DOCTYPE plist", Fixture.Text("iokit-statistics-disk0.plist"), StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_DocumentTypeDeclaration_IsIgnoredWithoutGoingToTheNetwork()
    {
        // A reader left at its defaults either refuses this document or fetches the definition from
        // apple.com. On a sampler running every five seconds, going to the network would be a defect
        // far worse than the one it was trying to describe.
        var values = MacOsResourceParsing.ReadPropertyListDictionary(
            """
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
            	<key>Bytes (Read)</key>
            	<integer>17</integer>
            </dict>
            </plist>
            """);

        Assert.Equal("17", values["Bytes (Read)"]);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_NestedValue_IsLeftOutRatherThanFlattened()
    {
        // Flattening would invent key names Apple never used, and a caller asking for a key that does
        // not exist is a clean failure, whereas a caller handed an invented one is not.
        var values = MacOsResourceParsing.ReadPropertyListDictionary(
            """
            <plist version="1.0">
            <dict>
            	<key>Statistics</key>
            	<dict><key>Bytes (Read)</key><integer>5</integer></dict>
            	<key>Medium Type</key>
            	<string>Rotational</string>
            </dict>
            </plist>
            """);

        Assert.False(values.ContainsKey("Statistics"));
        Assert.Equal("Rotational", values["Medium Type"]);
        Assert.Single(values);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_BooleansAndReals_AreRead()
    {
        var values = MacOsResourceParsing.ReadPropertyListDictionary(
            """
            <plist version="1.0">
            <dict>
            	<key>Removable</key><true/>
            	<key>Ejectable</key><false/>
            	<key>Share</key><real>0.5</real>
            </dict>
            </plist>
            """);

        Assert.Equal("true", values["Removable"]);
        Assert.Equal("false", values["Ejectable"]);
        Assert.Equal("0.5", values["Share"]);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [Trait("Category", "Resources")]
    public void ReadPropertyListDictionary_NoText_IsEmptyRatherThanThrowing(string text)
    {
        Assert.Empty(MacOsResourceParsing.ReadPropertyListDictionary(text));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListString_RealBsdName_ReadsTheDeviceName()
    {
        Assert.Equal("disk0", MacOsResourceParsing.ReadPropertyListString(Fixture.Text("iokit-bsd-name-disk0.plist")));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListString_ADictionary_IsNullRatherThanSomeInnerString()
    {
        Assert.Null(MacOsResourceParsing.ReadPropertyListString(
            Fixture.Text("iokit-device-characteristics-disk0.plist")));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadPropertyListString_NoText_IsNull()
    {
        Assert.Null(MacOsResourceParsing.ReadPropertyListString(""));
    }

    // =============================================================================================
    // The IOKit disk counters
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadDiskCounters_RealStatistics_ReadsAllFour()
    {
        var values = MacOsResourceParsing.ReadPropertyListDictionary(Fixture.Text("iokit-statistics-disk0.plist"));

        Assert.True(MacOsResourceParsing.TryReadDiskCounters(values, out var counters, out var why));
        Assert.Null(why);
        Assert.Equal(8096100L, counters.OperationsRead);
        Assert.Equal(3692471L, counters.OperationsWritten);
        Assert.Equal(2230432340709L, counters.TotalTimeReadNanoseconds);
        Assert.Equal(323914863274L, counters.TotalTimeWrittenNanoseconds);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadDiskCounters_RealStatistics_TotalTimeIsNanoseconds()
    {
        // THIS IS THE EVIDENCE FOR THE UNITS, and the phase's named risk on this platform rested on
        // it. Nothing in IOKit says which clock "Total Time" is counted in, and reading Mach absolute
        // time units as nanoseconds on Apple Silicon would be wrong by a factor of about forty - which
        // would not look wrong, it would look like a slow disk. Dividing the counter by the operation
        // count gives a time per operation, and only one interpretation of the units puts that time in
        // the range a real solid-state disk can work in.
        var values = MacOsResourceParsing.ReadPropertyListDictionary(Fixture.Text("iokit-statistics-disk0.plist"));
        Assert.True(MacOsResourceParsing.TryReadDiskCounters(values, out var counters, out _));

        var millisecondsPerRead = counters.TotalTimeReadNanoseconds / 1e6 / counters.OperationsRead;
        var millisecondsPerWrite = counters.TotalTimeWrittenNanoseconds / 1e6 / counters.OperationsWritten;

        Assert.InRange(millisecondsPerRead, 0.01, 10.0);
        Assert.InRange(millisecondsPerWrite, 0.01, 10.0);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadDiskCounters_MissingKey_FailsAndNamesItRatherThanReadingZero()
    {
        // A zero here would not look like a fault. It would look like a disk that did no work, which
        // is an ordinary reading, and it would be shown to the owner as fact.
        var values = MacOsResourceParsing.ReadPropertyListDictionary(Fixture.Text("iokit-statistics-disk0.plist"))
            .Where(pair => pair.Key != MacOsResourceParsing.StatisticsTotalTimeWritten)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);

        Assert.False(MacOsResourceParsing.TryReadDiskCounters(values, out var counters, out var why));
        Assert.NotNull(why);
        Assert.Contains(MacOsResourceParsing.StatisticsTotalTimeWritten, why, StringComparison.Ordinal);
        Assert.Equal(default, counters);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadDiskCounters_ValueIsNotAWholeNumber_FailsAndSaysWhat()
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [MacOsResourceParsing.StatisticsOperationsRead] = "not a number",
        };

        Assert.False(MacOsResourceParsing.TryReadDiskCounters(values, out _, out var why));
        Assert.NotNull(why);
        Assert.Contains("not a number", why, StringComparison.Ordinal);
    }

    // =============================================================================================
    // Processor ticks
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadCoreTicks_RealCapture_SplitsIntoOneBlockPerCore()
    {
        var (coreCount, ticks) = Fixture.ProcessorInfo();

        Assert.Equal(10, coreCount);
        Assert.Equal(40, ticks.Count);
        Assert.True(MacOsResourceParsing.TryReadCoreTicks(ticks, coreCount, out var cores, out var why));
        Assert.Null(why);
        Assert.Equal(10, cores.Count);

        // The first and last core as the kernel reported them. Reading with the wrong stride does not
        // fail - it attributes one core's idle ticks to the next core's user ticks and produces a
        // processor percentage that looks plausible and is wrong.
        Assert.Equal(new MacOsResourceParsing.CoreTicks(1043169, 1149599, 13351701, 0), cores[0]);
        Assert.Equal(new MacOsResourceParsing.CoreTicks(43145, 58321, 15533853, 0), cores[9]);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadCoreTicks_RealCapture_EveryCoreHasPlausibleTotals()
    {
        var (coreCount, ticks) = Fixture.ProcessorInfo();
        Assert.True(MacOsResourceParsing.TryReadCoreTicks(ticks, coreCount, out var cores, out _));

        // Every core on one machine has been up for the same wall-clock time, so their totals must be
        // within a few percent of each other. A stride mistake breaks this badly, which makes it a
        // cheap guard against the whole family of offset errors.
        var totals = cores.Select(core => (double)core.Total).ToList();
        Assert.All(totals, total => Assert.InRange(total, totals.Min() * 0.95, totals.Max() * 1.05));
        Assert.All(cores, core => Assert.True(core.Busy < core.Total));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadCoreTicks_FewerCountersThanCoresNeed_FailsRatherThanMisattributing()
    {
        var short_ = new uint[] { 1, 2, 3, 4, 5, 6 };

        Assert.False(MacOsResourceParsing.TryReadCoreTicks(short_, coreCount: 10, out var cores, out var why));
        Assert.Empty(cores);
        Assert.NotNull(why);
        Assert.Contains("10 cores", why, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [Trait("Category", "Resources")]
    public void TryReadCoreTicks_NoCores_Fails(int coreCount)
    {
        Assert.False(MacOsResourceParsing.TryReadCoreTicks(new uint[40], coreCount, out _, out var why));
        Assert.NotNull(why);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void BusyPercent_TwoReadings_IsTheShareOfTicksSpentWorking()
    {
        var before = new MacOsResourceParsing.CoreTicks(100, 100, 800, 0);
        var after = new MacOsResourceParsing.CoreTicks(200, 150, 850, 0);

        // User went up 100 and system 50, so 150 busy ticks; idle went up 50. That is 150 busy of the
        // 200 ticks that passed.
        Assert.Equal(75.0, MacOsResourceParsing.BusyPercent(before, after).GetValueOrDefault(), precision: 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void BusyPercent_NiceCountsAsBusy()
    {
        var before = new MacOsResourceParsing.CoreTicks(0, 0, 0, 0);
        var after = new MacOsResourceParsing.CoreTicks(0, 0, 50, 50);

        Assert.Equal(50.0, MacOsResourceParsing.BusyPercent(before, after).GetValueOrDefault(), precision: 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void BusyPercent_NoTicksPassed_IsNullRatherThanZero()
    {
        var same = new MacOsResourceParsing.CoreTicks(100, 100, 800, 0);

        // Zero would read as a completely idle core, which is a reading, not a refusal to answer.
        Assert.Null(MacOsResourceParsing.BusyPercent(same, same));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void BusyPercent_CountersWentBackwards_IsNullRatherThanNegative()
    {
        var before = new MacOsResourceParsing.CoreTicks(500, 500, 500, 0);
        var after = new MacOsResourceParsing.CoreTicks(100, 100, 2000, 0);

        Assert.Null(MacOsResourceParsing.BusyPercent(before, after));
    }

    // =============================================================================================
    // Swap
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadSwapUsage_RealCapture_ReadsAMachineWithNoSwapFileAtAll()
    {
        // Every figure being zero here is the TRUTH about this machine, not a failure to read it:
        // macOS writes no swap file until it needs one, and this Mac had none, with zero swap-ins and
        // zero swap-outs. The reading is reported as successful, because it is.
        Assert.True(MacOsResourceParsing.TryReadSwapUsage(Fixture.Bytes("sysctl-vm-swapusage.base64"),
            out var usage, out var why));

        Assert.Null(why);
        Assert.Equal(0UL, usage.TotalBytes);
        Assert.Equal(0UL, usage.AvailableBytes);
        Assert.Equal(0UL, usage.UsedBytes);
        Assert.True(usage.Encrypted);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadSwapUsage_MachineWithSwapInUse_ReadsEachFieldFromItsOwnOffset()
    {
        // The real capture is all zeros, so it cannot tell a correct offset from a wrong one. This
        // builds the structure the kernel would write on a machine that IS swapping, with a different
        // number in every field, so a swapped pair of offsets cannot pass.
        var buffer = new byte[MacOsResourceParsing.SwapUsageSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 2_147_483_648UL);  // total, 2 GiB
        BitConverter.TryWriteBytes(buffer.AsSpan(8), 1_073_741_824UL);  // available, 1 GiB
        BitConverter.TryWriteBytes(buffer.AsSpan(16), 1_073_741_824UL); // used, 1 GiB
        BitConverter.TryWriteBytes(buffer.AsSpan(24), 16384U);          // page size
        BitConverter.TryWriteBytes(buffer.AsSpan(28), 0);               // not encrypted

        Assert.True(MacOsResourceParsing.TryReadSwapUsage(buffer, out var usage, out _));
        Assert.Equal(2_147_483_648UL, usage.TotalBytes);
        Assert.Equal(1_073_741_824UL, usage.AvailableBytes);
        Assert.Equal(1_073_741_824UL, usage.UsedBytes);
        Assert.False(usage.Encrypted);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadSwapUsage_TooFewBytes_FailsAndSaysHowMany()
    {
        Assert.False(MacOsResourceParsing.TryReadSwapUsage(new byte[8], out _, out var why));
        Assert.NotNull(why);
        Assert.Contains("8 bytes", why, StringComparison.Ordinal);
    }

    // =============================================================================================
    // The run queue
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadLoadAverage_RealCapture_MatchesWhatTheKernelReported()
    {
        Assert.True(MacOsResourceParsing.TryReadLoadAverage(Fixture.Bytes("sysctl-vm-loadavg.base64"),
            out var load, out var why));

        Assert.Null(why);
        Assert.Equal(2.4267578125, load.OneMinute, precision: 9);
        Assert.Equal(3.173828125, load.FiveMinutes, precision: 9);
        Assert.Equal(3.220703125, load.FifteenMinutes, precision: 9);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadLoadAverage_ScaleComesFromTheKernel_NotFromAConstant()
    {
        // The customary divisor is 2048, and hard-coding it would pass against the real capture. This
        // writes the same three fixed-point figures with a DIFFERENT scale, so a hard-coded divisor
        // reads a load of 2 as a load of 32.
        var buffer = new byte[MacOsResourceParsing.LoadAverageSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 256U);
        BitConverter.TryWriteBytes(buffer.AsSpan(4), 128U);
        BitConverter.TryWriteBytes(buffer.AsSpan(8), 64U);
        BitConverter.TryWriteBytes(buffer.AsSpan(16), 128L);

        Assert.True(MacOsResourceParsing.TryReadLoadAverage(buffer, out var load, out _));
        Assert.Equal(2.0, load.OneMinute, precision: 9);
        Assert.Equal(1.0, load.FiveMinutes, precision: 9);
        Assert.Equal(0.5, load.FifteenMinutes, precision: 9);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadLoadAverage_ScaleOfZero_FailsRatherThanDividingByIt()
    {
        var buffer = new byte[MacOsResourceParsing.LoadAverageSize];
        BitConverter.TryWriteBytes(buffer.AsSpan(0), 2048U);
        BitConverter.TryWriteBytes(buffer.AsSpan(16), 0L);

        Assert.False(MacOsResourceParsing.TryReadLoadAverage(buffer, out _, out var why));
        Assert.NotNull(why);
        Assert.Contains("scale", why, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadLoadAverage_TooFewBytes_Fails()
    {
        Assert.False(MacOsResourceParsing.TryReadLoadAverage(new byte[12], out _, out var why));
        Assert.NotNull(why);
    }

    // =============================================================================================
    // The kernel's memory-pressure level
    //
    // The mapping itself now lives in the shared MacOsKernelPressure, so the probe and the Gateway's
    // display fold read one function instead of writing the same switch twice. These tests moved with
    // it: they are the only thing pinning the ruling that the kernel's 1 means normal, and that ruling
    // was MEASURED on this machine rather than taken from documentation.
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelPressureLevel_WhatThisMachineReported_IsNormal()
    {
        // Measured together on 2026-10-15: this sysctl read 1 while kern.memorystatus_level reported
        // 77 percent of memory free, with no swap file, zero swap-ins and zero swap-outs. A machine in
        // that state is normal, which is what fixes 1 as the normal level rather than the zero that
        // the other numbering scheme in circulation would use.
        Assert.Equal(KernelMemoryPressureLevel.Normal, MacOsKernelPressure.Read(1));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelPressureLevel_TheKernelsOtherLevels_AreRead()
    {
        Assert.Equal(KernelMemoryPressureLevel.Warning, MacOsKernelPressure.Read(2));

        // THE KERNEL'S CRITICAL IS 4, NOT 3. The level is a bit flag, not a ladder, so the numbers it
        // answers in are 1, 2 and 4 with nothing at 3. Reading it as a ladder is how a machine the
        // kernel is about to start killing programs on gets reported as merely warning.
        Assert.Equal(KernelMemoryPressureLevel.Critical, MacOsKernelPressure.Read(4));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(99)]
    [InlineData(-1)]
    [InlineData(null)]
    [Trait("Category", "Resources")]
    public void ReadKernelPressureLevel_ANumberWeDoNotKnow_IsUnknownAndNotFoldedIntoNormal(int? level)
    {
        // Folding an unrecognised level into Normal would report a machine in trouble as healthy,
        // which is the one direction this reading must never fail in.
        Assert.Equal(KernelMemoryPressureLevel.Unknown, MacOsKernelPressure.Read(level));
    }

    // =============================================================================================
    // Processes
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessTaskInfo_RealCapture_ReadsProcessorTimeAndResidentMemory()
    {
        Assert.True(MacOsResourceParsing.TryReadProcessTaskInfo(
            Fixture.Bytes("proc-taskinfo.base64"), out var task));

        Assert.Equal(1043930UL, task.UserMachTime);
        Assert.Equal(478649UL, task.SystemMachTime);
        Assert.Equal(1043930UL + 478649UL, task.TotalMachTime);
        Assert.Equal(56098816UL, task.ResidentBytes);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void MachAbsoluteToNanoseconds_AppleSiliconTimebase_ScalesByFortyOneAndTwoThirds()
    {
        // The timebase this Mac reports, read from the kernel with mach_timebase_info: 125/3, so one
        // unit is 41.666... nanoseconds.
        var appleSilicon = new MacOsResourceParsing.MachTimebase(125, 3);

        Assert.True(appleSilicon.IsUsable);
        Assert.Equal(41_666UL, MacOsResourceParsing.MachAbsoluteToNanoseconds(1000, appleSilicon));
        Assert.Equal(1_250_000_000UL, MacOsResourceParsing.MachAbsoluteToNanoseconds(30_000_000, appleSilicon));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void MachAbsoluteToNanoseconds_IntelTimebase_LeavesTheFigureAlone()
    {
        // WHY THE CONVERSION IS EASY TO MISS ENTIRELY. On Intel Macs the timebase is 1 to 1, so Mach
        // absolute time units ARE nanoseconds and code that skips the conversion is correct. The same
        // code is wrong by a factor of about forty-two on Apple Silicon - two threads spinning flat
        // out reported 4.79 percent of a core instead of 200.
        var intel = new MacOsResourceParsing.MachTimebase(1, 1);

        Assert.True(intel.IsUsable);
        Assert.Equal(1_000_000_000UL, MacOsResourceParsing.MachAbsoluteToNanoseconds(1_000_000_000, intel));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void MachAbsoluteToNanoseconds_ALongLivedProcess_DoesNotOverflow()
    {
        // A process up for a hundred days has accumulated about 2.07e14 units, and multiplying that by
        // the numerator before dividing comes within reach of the top of a 64-bit number. The
        // multiplication is done in a wider type for exactly this reason.
        var appleSilicon = new MacOsResourceParsing.MachTimebase(125, 3);
        var hundredDays = (ulong)(100.0 * 86400 * 1e9 / (125.0 / 3));

        var nanoseconds = MacOsResourceParsing.MachAbsoluteToNanoseconds(hundredDays, appleSilicon);

        Assert.Equal(100.0 * 86400, nanoseconds / 1e9, precision: 0);
    }

    [Theory]
    [InlineData(0u, 1u)]
    [InlineData(125u, 0u)]
    [InlineData(0u, 0u)]
    [Trait("Category", "Resources")]
    public void MachTimebase_AFractionThatCannotBeUsed_SaysSo(uint numerator, uint denominator)
    {
        // A zero denominator cannot be divided by, and a zero numerator would turn every process's
        // processor time into nothing - which would report a flat-out machine as completely idle. The
        // caller reports the processes as unreadable rather than using either.
        Assert.False(new MacOsResourceParsing.MachTimebase(numerator, denominator).IsUsable);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessBsdInfo_RealCapture_ReadsTheParentTheNameAndTheStartTime()
    {
        Assert.True(MacOsResourceParsing.TryReadProcessBsdInfo(
            Fixture.Bytes("proc-bsdinfo.base64"), out var bsd));

        Assert.Equal(66252, bsd.ParentPid);
        Assert.Equal("macosprobeharness", bsd.Name);
        Assert.Equal(DateTimeKind.Utc, bsd.StartedAtUtc.Kind);
        Assert.Equal(
            new DateTime(2026, 10, 15, 13, 35, 35, DateTimeKind.Utc),
            new DateTime(bsd.StartedAtUtc.Ticks - bsd.StartedAtUtc.Ticks % TimeSpan.TicksPerSecond, DateTimeKind.Utc));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessBsdInfo_RealCapture_ReadsTheLongNameAndNotTheTruncatedOne()
    {
        // THE REGRESSION TEST FOR THE 136-AGAINST-144 BUG. The captured process is called
        // "macosprobeharness", seventeen characters, which does not fit the structure's short
        // sixteen-character name field at all. So this one assertion proves three things at once: the
        // long name field is the one being read, it is being read from the right offset, and the
        // structure's size is the measured 136 rather than the calculated 144 - because at the
        // calculated offsets this name came back shifted by a character.
        Assert.True(MacOsResourceParsing.TryReadProcessBsdInfo(
            Fixture.Bytes("proc-bsdinfo.base64"), out var bsd));

        Assert.Equal(17, "macosprobeharness".Length);
        Assert.Equal("macosprobeharness", bsd.Name);
        Assert.DoesNotContain("\0", bsd.Name, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessBsdInfo_RealCapture_StartTimeIsNotTheEpoch()
    {
        // At the calculated offsets the start time read as 1970, which is a plausible-looking
        // DateTime that would have put every process at the top of an "oldest first" list.
        Assert.True(MacOsResourceParsing.TryReadProcessBsdInfo(
            Fixture.Bytes("proc-bsdinfo.base64"), out var bsd));

        Assert.True(bsd.StartedAtUtc > new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessDiskUsage_RealCapture_ReadsBytesReadAndWritten()
    {
        Assert.True(MacOsResourceParsing.TryReadProcessDiskUsage(
            Fixture.Bytes("rusage-v2.base64"), out var usage));

        Assert.Equal(86016UL, usage.BytesRead);
        Assert.Equal(65536UL, usage.BytesWritten);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void TryReadProcessStructures_TooFewBytes_AllFail()
    {
        Assert.False(MacOsResourceParsing.TryReadProcessTaskInfo(new byte[32], out _));
        Assert.False(MacOsResourceParsing.TryReadProcessBsdInfo(new byte[32], out _));
        Assert.False(MacOsResourceParsing.TryReadProcessDiskUsage(new byte[32], out _));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessCorePercent_OneCoreUsedFully_IsOneHundred()
    {
        var percent = MacOsResourceParsing.ProcessCorePercent(
            beforeNanoseconds: 0,
            afterNanoseconds: 1_000_000_000,
            between: TimeSpan.FromSeconds(1));

        Assert.Equal(100.0, percent.GetValueOrDefault(), precision: 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessCorePercent_FourCoresUsedFully_IsFourHundred()
    {
        // A hundred percent means one core's worth, which is what every system monitor on this
        // platform shows. Dividing by the core count here would make the figures stop adding up to the
        // machine's own.
        var percent = MacOsResourceParsing.ProcessCorePercent(
            beforeNanoseconds: 0,
            afterNanoseconds: 4_000_000_000,
            between: TimeSpan.FromSeconds(1));

        Assert.Equal(400.0, percent.GetValueOrDefault(), precision: 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessCorePercent_NoTimePassed_IsNullRatherThanZero()
    {
        Assert.Null(MacOsResourceParsing.ProcessCorePercent(0, 1_000_000_000, TimeSpan.Zero));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessCorePercent_ProcessorTimeWentBackwards_IsNull()
    {
        // Which happens when a process identifier is reused by a new process between two samples.
        Assert.Null(MacOsResourceParsing.ProcessCorePercent(5_000_000_000, 1_000_000_000, TimeSpan.FromSeconds(1)));
    }

    // =============================================================================================
    // The kernel's process table
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelProcessTable_OnACapture_ReadsEveryRowTheOperatingSystemPrinted()
    {
        var entries = MacOsResourceParsing.ReadKernelProcessTable(Fixture.Bytes("kern-proc-all.base64"));

        Assert.Equal(3, entries.Count);

        // Every figure below is what the operating system itself printed for these exact bytes,
        // through its own headers, at the moment of capture - see the fixture's own header.
        Assert.Equal(37254, entries[0].Pid);
        Assert.Equal(37240, entries[0].ParentPid);
        Assert.Equal("dumptable", entries[0].Name);
        Assert.Equal(501u, entries[0].UserId);

        Assert.Equal(37240, entries[1].Pid);
        Assert.Equal(62661, entries[1].ParentPid);
        Assert.Equal("zsh", entries[1].Name);

        // The kernel's own child: a parent of one is launchd, which is what tells a top-level process
        // from a session's child when the slowness diagnosis walks a process tree.
        Assert.Equal(37232, entries[2].Pid);
        Assert.Equal(1, entries[2].ParentPid);
        Assert.Equal("mdworker_shared", entries[2].Name);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelProcessTable_OnACapture_ReadsTheStartTimeToTheMicrosecond()
    {
        var entries = MacOsResourceParsing.ReadKernelProcessTable(Fixture.Bytes("kern-proc-all.base64"));

        // THE START TIME IS THE KEY THAT TELLS A REUSED PROCESS IDENTIFIER FROM THE SAME PROCESS, so
        // it has to be exact. 1792091094.675943 seconds after the epoch is what the operating system
        // printed for the first row.
        Assert.Equal(
            DateTime.UnixEpoch.AddSeconds(1792091094).AddMicroseconds(675943),
            entries[0].StartedAtUtc);
        Assert.Equal(DateTimeKind.Utc, entries[0].StartedAtUtc.Kind);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelProcessTable_OnATrailingPartialRow_StopsRatherThanReadingPastIt()
    {
        var whole = Fixture.Bytes("kern-proc-all.base64");

        // The kernel can answer with fewer bytes than it promised, and a buffer cut mid-row must not
        // be read as a row: that is how a field lands at the wrong offset and nothing throws.
        var cut = whole.AsSpan(0, MacOsResourceParsing.KernelProcessEntrySize * 2 + 100).ToArray();

        var entries = MacOsResourceParsing.ReadKernelProcessTable(cut);

        Assert.Equal(2, entries.Count);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadKernelProcessTable_OnNoBytesAtAll_ReadsNothingAndDoesNotThrow()
    {
        Assert.Empty(MacOsResourceParsing.ReadKernelProcessTable(Array.Empty<byte>()));
    }

    // =============================================================================================
    // A process's share of the machine
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessMachinePercent_ForOneCoreHeldFlatOut_IsOneCoresShareOfTheMachine()
    {
        // One second of processor time in one second of wall time, on a ten core machine, is ten
        // percent of the machine - NOT a hundred. The shared contract asks for a share of the whole
        // machine, and getting this wrong made a session appear to use ten times the machine's load.
        var percent = MacOsResourceParsing.ProcessMachinePercent(
            beforeNanoseconds: 0,
            afterNanoseconds: 1_000_000_000,
            between: TimeSpan.FromSeconds(1),
            logicalCoreCount: 10);

        Assert.NotNull(percent);
        Assert.Equal(10.0, percent.Value, 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessMachinePercent_ForEveryCoreHeldFlatOut_IsTheWholeMachine()
    {
        var percent = MacOsResourceParsing.ProcessMachinePercent(
            beforeNanoseconds: 0,
            afterNanoseconds: 10_000_000_000,
            between: TimeSpan.FromSeconds(1),
            logicalCoreCount: 10);

        Assert.NotNull(percent);
        Assert.Equal(100.0, percent.Value, 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessMachinePercent_WithNoCoreCount_IsNullRatherThanDividingByNothing()
    {
        Assert.Null(MacOsResourceParsing.ProcessMachinePercent(0, 1_000, TimeSpan.FromSeconds(1), 0));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessMachinePercent_WhenTheCounterWentBackwards_IsNullRatherThanNegative()
    {
        Assert.Null(MacOsResourceParsing.ProcessMachinePercent(2_000, 1_000, TimeSpan.FromSeconds(1), 10));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessLifetimeMachinePercent_ForABrandNewProcessBurningACore_IsNotZero()
    {
        // THE DEFECT THIS EXISTS FOR. A process that started during the interval has nothing to
        // subtract from, and it used to be written down as nought percent - indistinguishable from a
        // measured idle. Measured: a child spawned mid-interval had used sixty-five milliseconds of
        // processor time and was reported as completely idle. Over its own life it is a real figure.
        var percent = MacOsResourceParsing.ProcessLifetimeMachinePercent(
            usedNanoseconds: 65_000_000,
            age: TimeSpan.FromMilliseconds(65),
            logicalCoreCount: 10);

        Assert.NotNull(percent);
        Assert.Equal(10.0, percent.Value, 6);
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ProcessLifetimeMachinePercent_ForAProcessWithNoAgeYet_IsNullRatherThanInfinite()
    {
        Assert.Null(MacOsResourceParsing.ProcessLifetimeMachinePercent(1_000, TimeSpan.Zero, 10));
    }

    // =============================================================================================
    // The device behind the volume the machine runs from
    // =============================================================================================

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadMountedFromDevice_RealCapture_IsTheDeviceTheMachineRunsFrom()
    {
        // Captured by calling statfs64("/") on this machine on 2026-11-06. The machine's own answer for
        // the same volume was f_mntfromname "/dev/disk3s1s1" on an apfs file system mounted at "/", and
        // the fixture's own header records all three. The "/dev/" is stripped, because the only caller
        // hands this to IOBSDNameMatching, which matches nothing when given the path form.
        Assert.Equal("disk3s1s1", MacOsResourceParsing.ReadMountedFromDevice(Fixture.SystemVolume()));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadMountedFromDevice_ARecordCutShort_IsNullRatherThanAGuessedName()
    {
        // A short record means the kernel did not fill the structure, so the field at offset 1112 is
        // not a device name - it is whatever happens to be in the buffer. Reading it anyway would hand
        // the diagnosis a disk that does not exist, and it would never match a real one.
        var cutShort = Fixture.SystemVolume()[..(MacOsResourceParsing.StatfsSize - 1)];

        Assert.Null(MacOsResourceParsing.ReadMountedFromDevice(cutShort));
    }

    [Fact]
    [Trait("Category", "Resources")]
    public void ReadMountedFromDevice_AVolumeThatNamesNoDevice_IsNullRatherThanAnEmptyName()
    {
        // Synthetic and system-managed mounts genuinely have no backing device, and a network mount
        // names a host rather than a device. Null says so; an empty string, or a name with no "/dev/"
        // in front of it, would be a disk that could never be matched against a real one.
        var noDevice = Fixture.SystemVolume().ToArray();
        Array.Clear(noDevice, 1112, 1024);

        Assert.Null(MacOsResourceParsing.ReadMountedFromDevice(noDevice));
    }

    // =============================================================================================
    // Loading the captures
    // =============================================================================================

    /// <summary>
    /// Reads the files in TestData/Machine/MacOs. See that folder's README for the machine they came
    /// from and how each was taken.
    /// </summary>
    private static class Fixture
    {
        internal static string Text(string name) =>
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "TestData", "Machine", "MacOs", name));

        /// <summary>
        /// The raw bytes out of a capture file, whose leading <c>#</c> lines say what it is and where it
        /// came from and are not part of the data.
        /// </summary>
        internal static byte[] Bytes(string name)
        {
            var base64 = string.Concat(Text(name)
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#')));

            return Convert.FromBase64String(base64);
        }

        internal static (int CoreCount, IReadOnlyList<uint> Ticks) ProcessorInfo()
        {
            var lines = Text("host-processor-info.txt")
                .Split('\n')
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();

            var coreCount = int.Parse(
                lines.Single(line => line.StartsWith("cores ", StringComparison.Ordinal))["cores ".Length..],
                CultureInfo.InvariantCulture);

            var ticks = lines
                .Where(line => !line.StartsWith("cores ", StringComparison.Ordinal))
                .Select(line => uint.Parse(line, CultureInfo.InvariantCulture))
                .ToList();

            return (coreCount, ticks);
        }

        internal static byte[] SystemVolume() => Bytes("statfs-root.base64");
    }
}
