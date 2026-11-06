using Xunit;

namespace CcDirector.Core.Tests;

/// <summary>
/// THE LOCK THAT KEEPS EVERY OTHER TEST IN THIS PROJECT FROM READING A MACHINE BY ACCIDENT.
///
/// Everything else here is gated by <see cref="LiveMachineFactAttribute"/>, so if that gate did not
/// work, a project full of real waits and real machine readings would run wherever it was pointed. These
/// are the tests here that always run, and they read nothing but the gate's own decision - which is a
/// pure function, so both the opt-in branch and the core-count branch are exercised on every machine
/// rather than only on one that happens to be configured for them.
///
/// It is also why <c>dotnet test</c> on this project is not empty before the first platform probe lands:
/// an empty run and a run that discovered nothing look identical, and the second is a broken instrument.
/// </summary>
public sealed class LiveMachineFactTests
{
    [Fact]
    public void WithoutTheOptIn_TheTestIsSkipped_AndTheReasonSaysWhatItReadsAndHowToRunIt()
    {
        var reason = LiveMachineFactAttribute.SkipReason(
            "the processor and the process list", minimumLogicalCores: 0, enabled: false, logicalCores: 10);

        Assert.NotNull(reason);
        Assert.Contains("the processor and the process list", reason, StringComparison.Ordinal);
        Assert.Contains(LiveMachineFactAttribute.Variable, reason, StringComparison.Ordinal);
    }

    [Fact]
    public void WithoutTheOptIn_TooFewCoresIsNotTheReasonGiven_BecauseItCannotRunAtAll()
    {
        var reason = LiveMachineFactAttribute.SkipReason(
            "the processor", minimumLogicalCores: 4, enabled: false, logicalCores: 2);

        Assert.NotNull(reason);
        Assert.Contains(LiveMachineFactAttribute.Variable, reason, StringComparison.Ordinal);
        Assert.DoesNotContain("logical cores", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AskedFor_OnAMachineWithTooFewCoresToJudge_SkipsAndNamesBothCounts()
    {
        var reason = LiveMachineFactAttribute.SkipReason(
            "the processor", minimumLogicalCores: 4, enabled: true, logicalCores: 2);

        Assert.NotNull(reason);
        Assert.Contains("at least 4 logical cores", reason, StringComparison.Ordinal);
        Assert.Contains("This machine has 2", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AskedFor_OnAMachineWithEnoughCores_Runs()
    {
        Assert.Null(LiveMachineFactAttribute.SkipReason(
            "the processor", minimumLogicalCores: 4, enabled: true, logicalCores: 10));
    }

    [Fact]
    public void AskedFor_WithNoCoreRequirement_Runs_EvenOnOneCore()
    {
        Assert.Null(LiveMachineFactAttribute.SkipReason(
            "the whole machine", minimumLogicalCores: 0, enabled: true, logicalCores: 1));
    }

    [Fact]
    public void TheAttributeAppliesTheSameDecision_ForWhateverThisMachineAndEnvironmentAre()
    {
        // The attribute is only a carrier for the function above. Pinned so a future edit cannot let the
        // two disagree, which is how a gate quietly opens.
        var fact = new LiveMachineFactAttribute("the processor") { MinimumLogicalCores = 4 };

        var expected = LiveMachineFactAttribute.SkipReason(
            "the processor", 4, LiveMachineFactAttribute.Enabled, Environment.ProcessorCount);

        Assert.Equal(expected, fact.Skip);
    }
    // ----- the platform a class is written for -------------------------------------------------------

    [Fact]
    public void Platform_ContractClassForThisMachine_Runs()
    {
        Assert.Equal((null, null), LiveMachinePlatformAttribute.Decide(
            LiveMachinePlatform.Linux, isContractClass: true, thisMachine: LiveMachinePlatform.Linux));
    }

    [Fact]
    public void Platform_ContractClassForAnotherPlatform_SkipsAndNamesBoth()
    {
        var (error, skip) = LiveMachinePlatformAttribute.Decide(
            LiveMachinePlatform.Linux, isContractClass: true, thisMachine: LiveMachinePlatform.MacOs);

        Assert.Null(error);
        Assert.NotNull(skip);
        Assert.Contains("Linux", skip, StringComparison.Ordinal);
        Assert.Contains("macOS", skip, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Platform_AClassForAnotherPlatform_Skips_WhetherOrNotItRunsTheSharedContract(bool isContractClass)
    {
        // THE BRANCH THAT PROTECTS THE 36 macOS TESTS THAT ARE *NOT* CONTRACT CLASSES.
        // MacOsPlatformReaderLiveTests and MacOsResourceProbeLiveTests declare a platform and derive
        // nothing; today Decide consults isContractClass only when no platform is declared, so both
        // shapes share this branch. Nothing said so. A plausible future simplification - "only the
        // shared contract classes need a platform check", which the error branch above already keys on
        // - would pass every other test in this file, and on a Linux machine with the opt-in set those
        // 36 tests would run and fail rather than skip. So the ordinary shape is pinned here beside the
        // contract one, and the two are asserted to decide alike.
        var (error, skip) = LiveMachinePlatformAttribute.Decide(
            LiveMachinePlatform.MacOs, isContractClass, thisMachine: LiveMachinePlatform.Linux);

        Assert.Null(error);
        Assert.NotNull(skip);
        Assert.Contains("macOS", skip, StringComparison.Ordinal);
        Assert.Contains("Linux", skip, StringComparison.Ordinal);
    }

    [Fact]
    public void Platform_ContractClassThatDeclaresNone_IsAnErrorNotARunEverywhere()
    {
        var (error, skip) = LiveMachinePlatformAttribute.Decide(
            declaredFor: null, isContractClass: true, thisMachine: LiveMachinePlatform.Windows);

        Assert.NotNull(error);
        Assert.Contains("[LiveMachinePlatform", error, StringComparison.Ordinal);
        Assert.Null(skip);
    }

    [Fact]
    public void Platform_AnOrdinaryLiveTestThatDeclaresNone_RunsAnywhere()
    {
        Assert.Equal((null, null), LiveMachinePlatformAttribute.Decide(
            declaredFor: null, isContractClass: false, thisMachine: LiveMachinePlatform.Windows));
    }

    [Fact]
    public void Platform_OnAnUnknownOperatingSystem_APlatformClassSkips()
    {
        var (_, skip) = LiveMachinePlatformAttribute.Decide(
            LiveMachinePlatform.Windows, isContractClass: true, thisMachine: null);

        Assert.NotNull(skip);
    }

    [Fact]
    public void Platform_EveryConcreteContractClassInThisProject_DeclaresItsPlatform()
    {
        // Stated as a presence: there is at least one contract class, and each one declares a platform.
        var contractClasses = typeof(LiveMachineFactTests).Assembly.GetTypes()
            .Where(t => !t.IsAbstract && typeof(Machine.ResourceProbeContractLiveTests).IsAssignableFrom(t))
            .ToList();

        Assert.NotEmpty(contractClasses);
        Assert.All(contractClasses, t => Assert.Null(LiveMachinePlatformAttribute.Decide(t).Error));
    }

    [Fact]
    public void TheDiscovererNamedOnTheAttribute_IsTheOneThatExists()
    {
        // The discoverer is named by STRINGS. Were either to drift from the real type, xunit would
        // discover no live machine test at all, and an empty run reads as a green one. This turns that
        // silent absence into a failure.
        var named = typeof(LiveMachineFactAttribute).GetCustomAttributesData()
            .Single(a => a.AttributeType == typeof(Xunit.Sdk.XunitTestCaseDiscovererAttribute));
        var typeName = named.ConstructorArguments[0].Value as string;
        var assemblyName = named.ConstructorArguments[1].Value as string;

        Assert.NotNull(typeName);
        Assert.NotNull(assemblyName);
        Assert.Equal(typeof(LiveMachineFactDiscoverer), Type.GetType($"{typeName}, {assemblyName}"));
    }

    [Fact]
    public void Platform_AValueThatIsNotANamedPlatform_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new LiveMachinePlatformAttribute(default));
    }
}
