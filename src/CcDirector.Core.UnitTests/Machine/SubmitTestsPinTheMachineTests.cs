using CcDirector.Core.Drivers;
using CcDirector.Core.Machine;
using CcDirector.Core.Tests.Drivers;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// EVERY TEST THAT DRIVES A SUBMIT MUST SAY WHICH MACHINE IT ASSUMES (issue #2818).
///
/// Reading the machine's memory put ambient state on a path every driver uses, so a test that does not
/// pin a reading passes or fails according to how much memory the build agent happens to have free.
/// That is not a theoretical concern and it was not caught by running the suite: the suites were run
/// three times and passed, and the gap surfaced only when a reviewer ran the gate on the same laptop a
/// few minutes later, after it had drifted from 2.6 gigabytes available to under 2. A HostedAgent test
/// then failed with an exception that said, in plain words, that the machine was short of memory.
///
/// Non-determinism of that shape is worse than a consistent failure, because it is the kind of red
/// that gets re-run rather than read. Finding it by running the tests is luck; this test removes the
/// luck. It reads the SOURCE of every test file that reaches a submit and fails if one of them has not
/// pinned a machine - so the next suite to touch this path is told at once, by name, rather than
/// failing for somebody else on a loaded afternoon.
///
/// Modelled on TerminalPromptInjectionChokepointTests, which guards a different invariant the same way.
/// </summary>
public class SubmitTestsPinTheMachineTests
{
    /// <summary>The seam every such test must use. Named here so the failure message can name it.</summary>
    private const string Pin = "PinnedMachineMemory";

    /// <summary>
    /// The file that DEFINES the pin, and the guard itself, are the only two that reach the submit path
    /// by name without needing one.
    /// </summary>
    private static readonly string[] Exempt =
    [
        "PinnedMachineMemory.cs",
        "SubmitTestsPinTheMachineTests.cs",
        // Reads the source of the submit path as TEXT to pin a chokepoint; it never executes a submit.
        "TerminalPromptInjectionChokepointTests.cs",
    ];

    [Fact]
    public void EveryTestFileThatDrivesASubmitPinsTheMachineItAssumes()
    {
        var root = RepoRoot();
        var offenders = new List<string>();

        foreach (var file in TestSourceFiles(root))
        {
            if (Exempt.Contains(Path.GetFileName(file), StringComparer.Ordinal)) continue;

            var text = File.ReadAllText(file);
            if (!DrivesASubmit(text)) continue;
            if (text.Contains(Pin, StringComparison.Ordinal)) continue;

            offenders.Add(Path.GetRelativePath(root, file));
        }

        Assert.True(offenders.Count == 0,
            "These test files reach the terminal submit path, which reads the machine's memory, but do not " +
            $"pin a machine with {Pin}. Each will pass or fail according to how much memory the build agent " +
            "happens to have free. Add `using var machine = PinnedMachineMemory.Healthy();` (or a field, or " +
            ".Starved() where that is the point) to each:\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void ThePinActuallyOverridesTheAmbientMachineAndRestoresIt()
    {
        // The guard above proves every suite ASKS for a machine. This proves asking works - that the pin
        // wins over whatever the real machine says, and puts it back afterwards. Without this, a suite
        // full of pins could still be reading the laptop.
        var before = TerminalSubmit.MemoryProbe;
        try
        {
            TerminalSubmit.MemoryProbe = new StarvedAmbientMachine();
            Assert.Equal(MemoryPressureLevel.Critical, CurrentLevel());

            using (PinnedMachineMemory.Healthy())
            {
                Assert.Equal(MemoryPressureLevel.Normal, CurrentLevel());
            }

            // Restored to the ambient probe, not to some default.
            Assert.Equal(MemoryPressureLevel.Critical, CurrentLevel());
        }
        finally
        {
            TerminalSubmit.MemoryProbe = before;
        }
    }

    [Fact]
    public void TheStarvedPinIsActuallyStarved_AndTheUnreadableOneFoldsToUnknown()
    {
        var before = TerminalSubmit.MemoryProbe;
        try
        {
            using (PinnedMachineMemory.Starved()) Assert.Equal(MemoryPressureLevel.Critical, CurrentLevel());
            using (PinnedMachineMemory.Unreadable()) Assert.Equal(MemoryPressureLevel.Unknown, CurrentLevel());
        }
        finally
        {
            TerminalSubmit.MemoryProbe = before;
        }
    }

    private static MemoryPressureLevel CurrentLevel() =>
        MemoryPressure.Level(TerminalSubmit.MemoryProbe.Read());

    private sealed class StarvedAmbientMachine : IMachineMemoryProbe
    {
        public MachineMemoryReading Read() =>
            MachineMemoryReading.Read(16UL * 1024 * 1024 * 1024, 100UL * 1024 * 1024, DateTime.UtcNow);
    }

    /// <summary>
    /// Does this file EXECUTE a submit? A file that merely names the type in a comment or reads its
    /// source as text does not, which is why the match is on a call rather than on the word.
    /// </summary>
    private static bool DrivesASubmit(string text) =>
        text.Contains("TerminalSubmit.SharedSubmitAsync(", StringComparison.Ordinal)
        || text.Contains("TerminalSubmit.EchoVerifiedSubmitAsync(", StringComparison.Ordinal)
        || text.Contains(".SubmitAsync(", StringComparison.Ordinal);

    private static IEnumerable<string> TestSourceFiles(string root)
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(root, "src")))
        {
            var name = Path.GetFileName(dir);
            if (!name.EndsWith("Tests", StringComparison.Ordinal)) continue;

            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
                yield return file;
            }
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root from " + AppContext.BaseDirectory);
    }
}
