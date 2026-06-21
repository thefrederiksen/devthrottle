using System.Text.RegularExpressions;
using CcDirector.Core.Wingman;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Wingman;

/// <summary>
/// The Wingman invariants audit gate (docs/wingman/WINGMAN.md, section 2). The Wingman is a
/// cross-cutting component used in many places and growing, so its invariants are
/// enforced CONTINUOUSLY here rather than fixed once: this runs in the normal test suite
/// and FAILS THE BUILD if any file under src/CcDirector.Core/Wingman/ violates them.
///
/// Deterministic by design (a static scan, no LLM call): a build gate must be fast,
/// free, and reliable. It is a floor - it catches the mechanical violations; the
/// charter's judgment-call invariants (does a prompt actually preserve fidelity?) still
/// bind humans and reviewers.
///
/// Enforced invariants:
///   1. No cheap-model literal (e.g. "haiku") in any Wingman source file - the only
///      thing that can actually invoke a cheap model.
///   2. WingmanService.Model is a known STRONG model, and the back-compat aliases
///      resolve to it (so nobody re-points an alias at a cheap model).
///   3. Every allowedTools: argument in Wingman code is a subset of the read-only
///      allow-list (Read, Grep, Glob) - never a write/execute tool.
/// </summary>
public sealed class WingmanCharterAuditTests
{
    private readonly ITestOutputHelper _out;
    public WingmanCharterAuditTests(ITestOutputHelper output) => _out = output;

    private const string CharterRef = "see docs/wingman/WINGMAN.md";

    // Quoted cheap-model names that must never appear in Wingman source. A real cheap
    // call requires the literal here, so forbidding the quoted literal is the precise gate.
    private static readonly string[] ForbiddenModelLiterals = { "haiku" };

    // The only tools a read-only Wingman session may be given.
    private static readonly HashSet<string> ReadOnlyTools =
        new(StringComparer.Ordinal) { "Read", "Grep", "Glob" };

    private static readonly HashSet<string> StrongModels =
        new(StringComparer.OrdinalIgnoreCase) { "opus", "sonnet" };

    [Fact]
    public void Model_is_strong_and_aliases_resolve_to_it()
    {
        Assert.True(StrongModels.Contains(WingmanService.Model),
            $"WingmanService.Model is '{WingmanService.Model}', not a known strong model. The Wingman runs on a strong model only ({CharterRef}).");
        foreach (var forbidden in ForbiddenModelLiterals)
            Assert.False(string.Equals(WingmanService.Model, forbidden, StringComparison.OrdinalIgnoreCase),
                $"WingmanService.Model must never be the cheap model '{forbidden}' ({CharterRef}).");

        // Back-compat aliases must point at the single strong model, not be re-pointed.
        Assert.Equal(WingmanService.Model, WingmanService.DefaultModel);
        Assert.Equal(WingmanService.Model, WingmanService.StrongModel);
    }

    [Fact]
    public void No_cheap_model_literal_in_any_wingman_source_file()
    {
        var dir = ResolveWingmanSourceDir();
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (var forbidden in ForbiddenModelLiterals)
            {
                // Match the QUOTED literal only (e.g. "haiku"), so explanatory comments
                // like "never Haiku" do not trip the gate - only an actual model arg does.
                var rx = new Regex("\"" + Regex.Escape(forbidden) + "\"", RegexOptions.IgnoreCase);
                if (rx.IsMatch(text))
                    offenders.Add($"{Path.GetFileName(file)} contains the cheap-model literal \"{forbidden}\"");
            }
        }

        Assert.True(offenders.Count == 0,
            $"Wingman source must not invoke a cheap model ({CharterRef}):\n  " + string.Join("\n  ", offenders));
    }

    [Fact]
    public void Every_allowedTools_argument_is_read_only()
    {
        var dir = ResolveWingmanSourceDir();
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);

        // Matches named-argument call sites like:  allowedTools: "Read Grep Glob"
        var rx = new Regex("allowedTools\\s*:\\s*\"([^\"]*)\"", RegexOptions.None);

        var offenders = new List<string>();
        var checkedCount = 0;
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in rx.Matches(text))
            {
                checkedCount++;
                var tools = m.Groups[1].Value.Split(new[] { ' ', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                var bad = tools.Where(t => !ReadOnlyTools.Contains(t)).ToList();
                if (bad.Count > 0)
                    offenders.Add($"{Path.GetFileName(file)}: allowedTools \"{m.Groups[1].Value}\" includes non-read-only tool(s): {string.Join(", ", bad)}");
            }
        }

        _out.WriteLine($"Checked {checkedCount} allowedTools argument(s) across {files.Length} Wingman file(s).");
        Assert.True(offenders.Count == 0,
            $"Wingman sessions must be read-only ({CharterRef}):\n  " + string.Join("\n  ", offenders));
    }

    // The only Wingman file allowed to write to a session's PTY (invariant 7:
    // one write chokepoint). Actuation must funnel through WingmanActionExecutor so the
    // audit log + self-injection guard live in exactly one place.
    private static readonly string WriteFunnelFile = "WingmanActionExecutor.cs";

    // Session write methods that count as "actuation" for the funnel check.
    private static readonly Regex SessionWriteCall =
        new(@"\.(SendInput|SendTextAsync|SendText|SendEnterAsync)\s*\(", RegexOptions.None);

    [Fact]
    public void Only_the_executor_writes_to_a_session()
    {
        var dir = ResolveWingmanSourceDir();
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        foreach (var file in files)
        {
            if (string.Equals(Path.GetFileName(file), WriteFunnelFile, StringComparison.Ordinal))
                continue; // the one permitted chokepoint

            var text = File.ReadAllText(file);
            if (SessionWriteCall.IsMatch(text))
                offenders.Add($"{Path.GetFileName(file)} calls a Session write method directly");
        }

        Assert.True(offenders.Count == 0,
            $"All Wingman actuation must funnel through {WriteFunnelFile} ({CharterRef}):\n  " + string.Join("\n  ", offenders));
    }

    // Invariant 8: actuation is request-driven, with ONE explicitly approved exception. The
    // normal caller of the executor is the ControlApi request endpoint (outside this directory).
    // If anything ELSE under src/CcDirector.Core/Wingman/ calls WingmanActionExecutor.Execute,
    // something has silently wired the Wingman to act on its own (a turn hook, timer, or
    // background loop) - which the charter forbids.
    //
    // The single sanctioned self-actuator is TransientErrorAutoResume.cs (issue #476): the
    // human-approved transient-error auto-resume loop. It is gated behind a setting that DEFAULTS
    // OFF (opt-in) and still writes through the same WingmanActionExecutor chokepoint (invariant 7
    // intact). It is allow-listed here so the gate keeps catching every OTHER self-actuator while
    // permitting this one. See WINGMAN.md invariant 8.
    private static readonly Regex ExecutorCall =
        new(@"WingmanActionExecutor\s*\.\s*Execute\s*\(", RegexOptions.None);

    private static readonly HashSet<string> ApprovedSelfActuators =
        new(StringComparer.Ordinal) { "TransientErrorAutoResume.cs" };

    [Fact]
    public void Wingman_core_never_triggers_its_own_actuation()
    {
        var dir = ResolveWingmanSourceDir();
        var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = files
            .Where(f => !ApprovedSelfActuators.Contains(Path.GetFileName(f)))
            .Where(f => ExecutorCall.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            $"Wingman actuation must be request-driven (except the approved auto-resume loop); " +
            $"nothing else in the Wingman core may invoke WingmanActionExecutor.Execute ({CharterRef}):\n  "
            + string.Join("\n  ", offenders));
    }

    private static string ResolveWingmanSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Core", "Wingman");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CcDirector.Core/Wingman by walking up from " + AppContext.BaseDirectory);
    }
}
