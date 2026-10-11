using System.Text;
using System.Text.RegularExpressions;
using CcDirector.TestInfrastructure;
using Xunit;

namespace CcDirector.Core.UnitTests.Architecture;

/// <summary>
/// A GATE TEST DECIDES ON THE CODE, NEVER ON THE MACHINE.
///
/// The owner's ruling of 11 October 2026 (the Reliable Tests mission): a test failure must mean a code defect,
/// like a car sensor that only lights when something is really wrong. v2.18.0 took three days to ship because
/// the release gate was full of tests that measured the machine instead - how busy the processor was, how fast
/// the disk answered, whether a real process started in time, how long a wall-clock sleep really slept - and any
/// one of them failing at random threw away a run of hours.
///
/// So test code in a gate suite may not, for a new reason, use any of these:
/// <list type="bullet">
/// <item>a wall-clock wait: <c>Thread.Sleep</c>, <c>Task.Delay</c> with a finite wait, <c>SpinWait.SpinUntil</c>,
/// or a timer of the test's own (await the event, or advance a fake clock - <c>FakeTimeProvider</c>,
/// <c>VirtualTime</c>);</item>
/// <item>the real clock: <c>DateTime.Now</c>/<c>UtcNow</c>/<c>Today</c>, <c>DateTimeOffset.Now</c>/<c>UtcNow</c>,
/// <c>TimeProvider.System</c>, <c>Environment.TickCount</c>, or a <c>Stopwatch</c> (inject a <c>TimeProvider</c>
/// into the code under test and hand it a fake);</item>
/// <item>a real process: <c>Process.Start</c>, <c>new Process</c>, <c>new ProcessStartInfo</c>, or either one
/// built with a target-typed <c>new(...)</c>;</item>
/// <item>Docker or a real database server: Docker, Testcontainers, Npgsql (database tests live in
/// <c>CcDirector.Gateway.DatabaseTests</c>, which the gate never runs);</item>
/// <item>a fixed network port: <c>localhost:5000</c>, <c>http://+:5000</c>,
/// <c>new IPEndPoint(IPAddress.Loopback, 5000)</c> and the like (port 0, or an in-memory host).</item>
/// </list>
///
/// A <c>.WaitAsync(timeout)</c> watchdog is NOT refused: it only turns a hang into a named failure and never decides
/// a pass. WHAT A TEXT SCAN CANNOT SEE: it counts where a sleep or a process start is WRITTEN, not where it is
/// called from, so a new test calling an existing helper that sleeps adds nothing here. Do not write such tests;
/// the helpers are on the baseline so they can be found and removed, and any new helper anywhere is a new use.
///
/// THE BASELINE. Today's offenders are listed in <c>GateTestsDoNotDependOnTheMachine.baseline.txt</c>, one line
/// per file and rule with the count found. The list may only SHRINK: a file whose count rises, or a file that is
/// not listed, fails this test; and a file whose count falls also fails until its line is lowered or removed, so
/// a fix is banked the moment it is made and cannot quietly be spent again by the next test written in that file.
///
/// WHICH SUITES. The gate suites are read out of <c>scripts/test-local.ps1</c> itself - every project it names,
/// default, installer and parked - plus <c>src/TestInfrastructure</c>, which every test project compiles in. A
/// suite added to the gate is therefore guarded the day it is added. The scan reads source, not assemblies, so it
/// runs here in the default gate and costs a fraction of a second; comments are stripped first, so a comment may
/// say anything it likes about a sleep.
/// </summary>
public sealed class GateTestsDoNotDependOnTheMachineTests
{
    private const string BaselineFileName = "GateTestsDoNotDependOnTheMachine.baseline.txt";

    private static readonly (string Name, Regex Pattern)[] Rules =
    {
        ("sleep", new Regex(@"\bThread\.Sleep\s*\(", RegexOptions.Compiled)),
        // A never-ending wait (Timeout.Infinite, Timeout.InfiniteTimeSpan, -1) reads no clock; every finite one does.
        ("delay", new Regex(@"\bTask\.Delay\s*\((?!\s*(Timeout\.Infinite(TimeSpan)?\b|-1\s*[,)]))", RegexOptions.Compiled)),
        // A polling wait, or a timer of the test's own, on the real clock.
        ("timer-wait", new Regex(@"\bSpinWait\.SpinUntil\s*\(|\bnew\s+(System\.Threading\.)?Timer\s*\(|\bnew\s+PeriodicTimer\s*\(", RegexOptions.Compiled)),
        ("real-clock", new Regex(@"\bDateTime(Offset)?\.((Utc)?Now|Today)\b|\bTimeProvider\.System\b|\bEnvironment\.TickCount(64)?\b", RegexOptions.Compiled)),
        ("stopwatch", new Regex(@"\bStopwatch\b", RegexOptions.Compiled)),
        ("process", new Regex(@"\bProcess\.Start\s*\(|\bnew\s+Process\s*[({]|\bnew\s+ProcessStartInfo\b|\bProcess(StartInfo)?\s+\w+\s*=\s*new\s*\(", RegexOptions.Compiled)),
        ("docker", new Regex(@"\b(docker|testcontainers)\b", RegexOptions.Compiled | RegexOptions.IgnoreCase)),
        ("npgsql", new Regex(@"\bNpgsql", RegexOptions.Compiled)),
        ("fixed-port", new Regex(@"\b(localhost|127\.0\.0\.1|0\.0\.0\.0|\[::1\]):(?!0\b)\d{1,5}\b|//[+*]:[1-9]\d{0,4}\b|\bIPEndPoint\s*\(\s*IPAddress\.\w+\s*,\s*[1-9]", RegexOptions.Compiled)),
    };

    /// <summary>The three parked suites hold most of the offenders; a scan that lost one of them would pass.</summary>
    private static readonly string[] SuitesThatMustBeFound =
    {
        "src/CcDirector.Gateway.Tests",
        "src/CcDirector.Gateway.UnitTests",
        "src/CcDirector.Core.Tests",
        "src/CcDirector.Core.UnitTests",
    };

    [Fact]
    public void No_gate_test_adds_a_sleep_a_real_clock_a_real_process_docker_or_a_fixed_port()
    {
        var actual = ScanGateSuites();
        var baseline = ReadBaseline();

        var risen = new List<string>();
        var fallen = new List<string>();
        foreach (var key in actual.Keys.Union(baseline.Keys).OrderBy(k => k.Path, StringComparer.Ordinal).ThenBy(k => k.Rule, StringComparer.Ordinal))
        {
            actual.TryGetValue(key, out var now);
            baseline.TryGetValue(key, out var allowed);
            if (now > allowed) risen.Add(Line(key, now) + (allowed == 0 ? "   (new)" : $"   (was {allowed})"));
            else if (now < allowed) fallen.Add(now == 0 ? $"remove: {Line(key, allowed)}" : $"lower:  {Line(key, now)}   (was {allowed})");
        }

        Assert.True(risen.Count == 0,
            "These gate test files add a use of something that makes a test depend on the machine:\n  " +
            string.Join("\n  ", risen) + "\n\n" +
            "A gate test decides pass or fail on the code alone. Await the event instead of sleeping; give the code " +
            "under test a TimeProvider and advance a FakeTimeProvider instead of reading the clock or a Stopwatch; " +
            "use a fake instead of a real process; keep database tests in CcDirector.Gateway.DatabaseTests; bind " +
            "port 0 or use an in-memory host. If the use is genuinely not a wait or a clock read (a timestamp " +
            "stored as data, say), take it from a fixed value or a fake clock anyway. Do NOT raise the baseline: it " +
            "only ever shrinks.");

        Assert.True(fallen.Count == 0,
            "Fewer machine-dependent uses than the baseline allows - well done. Bank it: edit " + BaselineFileName +
            " in the same change:\n  " + string.Join("\n  ", fallen));
    }

    [Fact]
    public void The_scan_reads_every_gate_suite_named_by_the_gate_script()
    {
        var suites = GateSuiteDirectories();

        var missing = SuitesThatMustBeFound.Where(s => !suites.Contains(s, StringComparer.OrdinalIgnoreCase)).ToList();
        Assert.True(missing.Count == 0,
            "scripts/test-local.ps1 no longer names these suites, or the way it names them changed, so this guard " +
            "is no longer reading them:\n  " + string.Join("\n  ", missing));
        Assert.True(suites.Count >= 10, $"Expected at least ten gate suites in scripts/test-local.ps1, read {suites.Count}.");
    }

    private static string Line((string Rule, string Path) key, int count) => $"{key.Rule} | {key.Path} | {count}";

    /// <summary>The gate suites' project directories, repository-relative with forward slashes, plus the shared test infrastructure.</summary>
    private static List<string> GateSuiteDirectories()
    {
        var script = File.ReadAllText(Path.Combine(RepositorySourceIndex.Root, "scripts", "test-local.ps1"));
        var suites = Regex.Matches(script, @"""((?:src|tools)\\[^""]+?)\\[^""\\]+\.csproj""")
            .Select(m => m.Groups[1].Value.Replace('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // Every project path the script quotes must be one this guard read: a change in how the script spells a
        // path would otherwise drop that suite out of the scan without a word.
        var quoted = Regex.Matches(script, @"""[^""\r\n]*\.csproj""").Select(m => m.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        Assert.True(quoted == suites.Count,
            $"scripts/test-local.ps1 quotes {quoted} test project paths and this guard could read the folder of only " +
            $"{suites.Count} of them. Teach GateSuiteDirectories the new spelling, so every gate suite is scanned.");
        suites.Add("src/TestInfrastructure");
        return suites;
    }

    private static Dictionary<(string Rule, string Path), int> ScanGateSuites()
    {
        var self = "src/CcDirector.Core.UnitTests/Architecture/" + nameof(GateTestsDoNotDependOnTheMachineTests) + ".cs";
        var counts = new Dictionary<(string, string), int>();
        var scanned = 0;
        foreach (var suite in GateSuiteDirectories())
        {
            foreach (var file in RepositorySourceIndex.Under(suite, ".cs"))
            {
                var relative = RepositorySourceIndex.Relative(file);
                if (relative.Equals(self, StringComparison.OrdinalIgnoreCase)) continue;
                scanned++;
                var code = StripComments(File.ReadAllText(file));
                foreach (var (name, pattern) in Rules)
                {
                    var hits = pattern.Matches(code).Count;
                    if (hits > 0) counts[(name, relative)] = hits;
                }
            }
        }
        Assert.True(scanned > 1000, $"Read only {scanned} test source files across the gate suites. A guard that reads an empty tree has checked nothing.");
        return counts;
    }

    private static Dictionary<(string Rule, string Path), int> ReadBaseline()
    {
        var path = Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.Core.UnitTests", "Architecture", BaselineFileName);
        var baseline = new Dictionary<(string, string), int>();
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var parts = line.Split('|', StringSplitOptions.TrimEntries);
            if (parts.Length != 3 || !int.TryParse(parts[2], out var count) || count <= 0)
                throw new InvalidDataException($"{BaselineFileName}: '{raw}' is not 'rule | path | count'.");
            if (!baseline.TryAdd((parts[0], parts[1]), count))
                throw new InvalidDataException($"{BaselineFileName}: '{parts[0]} | {parts[1]}' is listed twice.");
        }
        Assert.True(baseline.Count > 0, $"{BaselineFileName} is empty; it is read from {path}.");
        return baseline;
    }

    /// <summary>
    /// Remove every comment and keep every string literal, by walking the source once, so a <c>//</c> inside an
    /// <c>https://</c> literal does not cut the rest of the line away. It knows ordinary, verbatim, interpolated and
    /// raw literals: a raw JSON fixture full of <c>//</c> and lone quotes once threw a simpler walker out of step
    /// and hid real code after it.
    /// </summary>
    private static string StripComments(string source)
    {
        var output = new StringBuilder(source.Length);
        var i = 0;
        while (i < source.Length)
        {
            var c = source[i];
            var next = i + 1 < source.Length ? source[i + 1] : '\0';

            if (c == '/' && next == '/')
            {
                while (i < source.Length && source[i] != '\n') i++;
                continue;
            }
            if (c == '/' && next == '*')
            {
                var end = source.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? source.Length : end + 2;
                continue;
            }
            if (c == '"' && next == '"' && i + 2 < source.Length && source[i + 2] == '"')
            {
                // Raw literal ("""...""", with or without a $ prefix): it ends at the first run of as many quotes as
                // opened it, and nothing inside it - a // in a JSON fixture, a lone quote - means anything.
                var run = 0;
                while (i + run < source.Length && source[i + run] == '"') run++;
                var fence = new string('"', run);
                var close = source.IndexOf(fence, i + run, StringComparison.Ordinal);
                var end = close < 0 ? source.Length : close + run;
                output.Append(source, i, end - i);
                i = end;
                continue;
            }
            if ((c == '@' && next == '"') || (c == '@' && next == '$' && i + 2 < source.Length && source[i + 2] == '"'))
            {
                // Verbatim literal, interpolated or not: only a doubled quote continues it.
                if (next == '$') { output.Append(c); i++; }
                output.Append(source[i]).Append(source[i + 1]);
                i += 2;
                while (i < source.Length)
                {
                    output.Append(source[i]);
                    if (source[i] == '"')
                    {
                        if (i + 1 < source.Length && source[i + 1] == '"') { output.Append('"'); i += 2; continue; }
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }
            if (c == '"' || c == '\'')
            {
                var quote = c;
                output.Append(c);
                i++;
                while (i < source.Length)
                {
                    output.Append(source[i]);
                    if (source[i] == '\\' && i + 1 < source.Length) { output.Append(source[i + 1]); i += 2; continue; }
                    if (source[i] == quote) { i++; break; }
                    i++;
                }
                continue;
            }

            output.Append(c);
            i++;
        }
        return output.ToString();
    }
}
