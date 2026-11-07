using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests.Machine;

/// <summary>
/// A TRIPWIRE FOR THE OBVIOUS SPELLING OF ONE DEFECT - NOT A GUARD (issue #4733). It reads the Resource
/// Monitor's own source and fails on a subtraction written as plainly as the one in #4686 was.
///
/// WHAT IT DOES NOT CATCH, measured by the reviewer of pull request 4739, who injected the real #4686 defect
/// three ways and watched ALL THREE walk past this file:
/// <list type="bullet">
/// <item>the subtraction split across two locals;</item>
/// <item><c>DateTime.Subtract</c> instead of the operator;</item>
/// <item>a cutoff built with <c>AddSeconds</c> and then compared with <c>&gt;=</c> - which is how membership,
/// one of #4686's own three uses, was actually written.</item>
/// </list>
/// It is also blind to a stamp passed to a helper, <c>.Ticks</c> on the left, a <c>DateTimeOffset</c>
/// conversion, stamps projected into a list, ORDERING OR COMPARING by stamp (the other two of #4686's three
/// uses), the alias <c>ResourceCrossing.AtUtc</c>, and anything outside this folder - including
/// <c>tools/harnesses/resource-load-proof/Program.cs</c>, which subtracts a sample stamp from the wall clock
/// today, in the very harness #4684 was raised against.
///
/// SO DO NOT READ A PASS HERE AS "NO STAMP SUBTRACTION EXISTS". It means the obvious spelling is absent. The
/// real answer is a Roslyn analyzer that follows the type rather than the text, and that is filed separately.
/// The diagnoser's own clock-step tests caught all three injections this file missed; THEY are the guard,
/// and this is a cheap net in front of them.
///
/// WHY THIS IS A TEST AND NOT A COMMENT. The rule was carried by prose in three places - the parameter
/// documentation on <c>SlownessDiagnoser.Diagnose</c>, the type documentation on <c>TimelineSample</c>, and a
/// comment on a test helper - and it was still broken three separate times: in the load harness (#4684), in
/// the diagnoser (#4686), and in the product's own sustained window (fixed in #4660). Three findings against
/// three prose warnings is a rule nobody can follow by reading.
///
/// WHAT IT REFUSES: any subtraction with <c>TakenAtUtc</c> on either side, anywhere under
/// <c>src/CcDirector.Core/Machine</c>. A monotonic timeline is the answer; every fold that needs a window
/// takes <see cref="CcDirector.Core.Machine.TimelineSample"/>.
///
/// THE ALLOW-LIST NAMES FILES AND GIVES REASONS, which is the only thing that stops a guard like this rotting.
/// A guard whose allow-list grows without reasons has stopped being a guard. There are no entries today.
/// </summary>
[Trait("Category", "Resources")]
public sealed class TimelineGuardTests
{
    /// <summary>Files allowed to subtract a wall-clock stamp, each with the reason it is legitimate.</summary>
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        // (empty - add a file here only with the reason a stamp subtraction is correct in it)
    };

    // "x.TakenAtUtc -" or "- x.TakenAtUtc" or "- TakenAtUtc", in any spacing. Catches both operand positions.
    private static readonly Regex Subtraction = new(
        @"TakenAtUtc\s*-(?!-)|-\s*[A-Za-z_][\w\.\[\]]*\.TakenAtUtc",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    [Fact]
    public void NoFileUnderMachine_SpellsAnIntervalAsAPlainStampSubtraction()
    {
        var folder = MachineSourceFolder();
        var offenders = new List<string>();

        foreach (var file in Directory.EnumerateFiles(folder, "*.cs", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (Allowed.ContainsKey(name)) continue;

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                // Comments EXPLAIN the rule and must be able to name the thing they forbid.
                var code = line.TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal) || code.StartsWith("///", StringComparison.Ordinal))
                    continue;
                if (Subtraction.IsMatch(line))
                    offenders.Add($"{name}:{i + 1}: {line.Trim()}");
            }
        }

        Assert.True(offenders.Count == 0,
            "An interval must come from the monotonic timeline (TimelineSample), never from subtracting "
            + "ResourceSample.TakenAtUtc stamps - see issue #4733 and the three times this has been found. "
            + "If a subtraction here is genuinely correct, add the file to Allowed with the reason.\n"
            + string.Join("\n", offenders));
    }

    /// <summary>
    /// THE GUARD ITSELF IS PROVED, not assumed. A check whose pass condition is an absence certifies a run that
    /// never happened: if the folder were wrong, or the pattern matched nothing, the test above would pass
    /// while reading no code at all. So this asserts the instrument works - it finds the offending shape in a
    /// line it is handed, and it finds real source to read.
    /// </summary>
    [Fact]
    public void TheGuardCanActuallySeeAnOffendingLine_AndHasSourceToRead()
    {
        Assert.Matches(Subtraction, "var span = newest.TakenAtUtc - window[0].TakenAtUtc;");
        // The example deliberately does NOT name a clock type: this file is itself scanned by
        // GateTestsDoNotDependOnTheMachineTests, which reads source textually and cannot tell a string
        // literal from a real call. A test that trips another guard to demonstrate its own is not clever.
        Assert.Matches(Subtraction, "var age = readAt - sample.TakenAtUtc;");
        Assert.DoesNotMatch(Subtraction, "var stamp = sample.TakenAtUtc;");
        Assert.DoesNotMatch(Subtraction, "TakenAtUtc: takenAt,");

        var files = Directory.EnumerateFiles(MachineSourceFolder(), "*.cs", SearchOption.AllDirectories).ToList();
        Assert.True(files.Count > 20, $"only {files.Count} files found - the guard is reading the wrong folder");
        Assert.Contains(files, f => Path.GetFileName(f) == "SlownessDiagnoser.cs");
    }

    private static string MachineSourceFolder()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "CcDirector.Core", "Machine")))
            directory = directory.Parent;

        Assert.NotNull(directory);
        return Path.Combine(directory!.FullName, "src", "CcDirector.Core", "Machine");
    }
}
