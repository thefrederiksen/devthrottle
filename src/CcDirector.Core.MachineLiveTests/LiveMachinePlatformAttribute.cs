using System.Reflection;
using CcDirector.Core.Tests.Machine;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace CcDirector.Core.Tests;

/// <summary>
/// The operating systems a live machine test can be written for. There is no zero member on purpose:
/// "no platform" is a null in <see cref="LiveMachinePlatformAttribute.Decide(LiveMachinePlatform?, bool, LiveMachinePlatform?)"/>,
/// never a default value that could be mistaken for one.
/// </summary>
public enum LiveMachinePlatform
{
    Linux = 1,
    Windows = 2,
    MacOs = 3,
}

/// <summary>
/// Declares the ONE operating system a live test class is written for. On any other operating system its
/// <see cref="LiveMachineFactAttribute"/> tests SKIP, with the reason - never run and fail.
///
/// Every concrete class deriving from <see cref="ResourceProbeContractLiveTests"/> MUST carry it: a
/// platform probe can only be proved on its own platform. One that does not is reported as an error by
/// <see cref="LiveMachineFactDiscoverer"/>, not run everywhere.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = false, AllowMultiple = false)]
public sealed class LiveMachinePlatformAttribute : Attribute
{
    /// <param name="platform">The one operating system the class is written for. It must be a named member.</param>
    public LiveMachinePlatformAttribute(LiveMachinePlatform platform)
    {
        // The enum deliberately has no zero member, so an undeclared value cannot pass as a platform.
        if (!Enum.IsDefined(platform))
            throw new ArgumentOutOfRangeException(nameof(platform), platform, "Name Linux, Windows or MacOs.");
        Platform = platform;
    }

    public LiveMachinePlatform Platform { get; }

    /// <summary>The operating system this process is running on, or null when it is none of the three.</summary>
    public static LiveMachinePlatform? ThisMachine =>
        OperatingSystem.IsLinux() ? LiveMachinePlatform.Linux
        : OperatingSystem.IsWindows() ? LiveMachinePlatform.Windows
        : OperatingSystem.IsMacOS() ? LiveMachinePlatform.MacOs
        : null;

    /// <summary>
    /// The platform decision for one test class, as a pure function so every branch is testable on any
    /// machine. Returns an ERROR when a shared contract class declares no platform, a SKIP reason when the
    /// class is for another platform, and neither when it may run.
    /// </summary>
    /// <param name="declaredFor">The platform the class declares, or null when it declares none.</param>
    /// <param name="isContractClass">Whether the class derives from the shared probe contract.</param>
    /// <param name="thisMachine">The platform running the test, or null when it is none of the three.</param>
    public static (string? Error, string? Skip) Decide(LiveMachinePlatform? declaredFor, bool isContractClass, LiveMachinePlatform? thisMachine)
    {
        if (declaredFor is null)
        {
            return isContractClass
                ? ("This class runs the shared probe contract but does not say which operating system its probe is "
                   + "for. Add [LiveMachinePlatform(...)] to it, or its tests would run - and fail - on every other "
                   + "platform.", null)
                : (null, null);
        }

        if (declaredFor == thisMachine) return (null, null);
        return (null, $"Written for {Name(declaredFor.Value)}; this machine is {(thisMachine is { } current ? Name(current) : "none of Linux, Windows or macOS")}.");
    }

    /// <summary>The decision for a real test class, read from its attributes and its base classes.</summary>
    public static (string? Error, string? Skip) Decide(Type testClass) =>
        Decide(
            testClass.GetCustomAttribute<LiveMachinePlatformAttribute>(inherit: false)?.Platform,
            typeof(ResourceProbeContractLiveTests).IsAssignableFrom(testClass),
            ThisMachine);

    private static string Name(LiveMachinePlatform platform) => platform switch
    {
        LiveMachinePlatform.Linux => "Linux",
        LiveMachinePlatform.Windows => "Windows",
        LiveMachinePlatform.MacOs => "macOS",
        _ => $"an unknown platform ({(int)platform})",
    };
}

/// <summary>
/// Discovers each <see cref="LiveMachineFactAttribute"/> test against the class it is ACTUALLY run on -
/// for an inherited contract test, the derived platform class - so the platform rule can be applied per
/// class. A contract class with no declared platform becomes an error test case that names the fix.
/// </summary>
public sealed class LiveMachineFactDiscoverer(IMessageSink diagnosticMessageSink) : IXunitTestCaseDiscoverer
{
    public IEnumerable<IXunitTestCase> Discover(ITestFrameworkDiscoveryOptions discoveryOptions, ITestMethod testMethod, IAttributeInfo factAttribute)
    {
        var display = discoveryOptions.MethodDisplayOrDefault();
        var displayOptions = discoveryOptions.MethodDisplayOptionsOrDefault();
        var (error, _) = LiveMachinePlatformAttribute.Decide(testMethod.TestClass.Class.ToRuntimeType());
        if (error is not null)
            return [new ExecutionErrorTestCase(diagnosticMessageSink, display, displayOptions, testMethod, error)];

        return [new LiveMachineTestCase(diagnosticMessageSink, display, displayOptions, testMethod)];
    }
}

/// <summary>
/// A live machine test case. Its skip reason is the attribute's own (the opt-in, then the core count),
/// then the platform of the class it runs on.
/// </summary>
public sealed class LiveMachineTestCase : XunitTestCase
{
    [Obsolete("Called by the de-serializer; should only be called by deriving classes for de-serialization purposes")]
    public LiveMachineTestCase()
    {
    }

    public LiveMachineTestCase(IMessageSink diagnosticMessageSink, TestMethodDisplay defaultMethodDisplay,
        TestMethodDisplayOptions defaultMethodDisplayOptions, ITestMethod testMethod)
        : base(diagnosticMessageSink, defaultMethodDisplay, defaultMethodDisplayOptions, testMethod)
    {
    }

    protected override string? GetSkipReason(IAttributeInfo factAttribute) =>
        base.GetSkipReason(factAttribute) ?? LiveMachinePlatformAttribute.Decide(TestMethod.TestClass.Class.ToRuntimeType()).Skip;
}
