using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// The scoped "--print / -p ban" audit gate for issue #511.
///
/// `--print` / `-p` one-shot calls are moving toward expensive per-call billing, while a
/// normal session runs on the user's subscription. Issue #511 moved the four metered
/// wingman / recap / voice side-call emitters off `--print` / `-p` onto the real-session
/// mechanism <c>SessionAskRunner</c> (#509). This deterministic source scan FAILS THE
/// BUILD if a `--print` or `-p` argument reappears anywhere on that IN-SCOPE side-call
/// surface, while explicitly ALLOWING the files/projects that legitimately need
/// `-p`/`--print` and have no real-session equivalent.
///
/// SCOPE (the human-pinned Option A decision on issue #511): the ban covers ONLY the
/// metered one-shot wingman / recap / voice side-call surface. The interactive session
/// transport and the structured/one-shot clients are EXEMPT. The exemption list below is
/// the single source of truth for "where one-shot billing is banned" - to add or remove an
/// exemption, edit <see cref="ExemptFiles"/> / <see cref="ExemptProjectDirectories"/> here.
/// </summary>
public sealed class PrintBanAuditTests
{
    private readonly ITestOutputHelper _out;
    public PrintBanAuditTests(ITestOutputHelper output) => _out = output;

    /// <summary>
    /// Files that are EXEMPT from the ban - the audit allows `-p` / `--print` in these
    /// because they have no <c>SessionAskRunner</c> equivalent. Keyed by file name (the
    /// scan compares file names, which are unique across the scanned tree). Each entry
    /// records why it is exempt.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExemptFiles = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // The interactive session streaming transport (`-p --output-format stream-json
        // --verbose`) used to launch EVERY Claude Code session. The metered-billing intent
        // of #511 does not apply to it - it is not a one-shot side-call.
        ["ClaudeAgent.cs"] = "interactive session streaming transport (launches every Claude Code session)",
        ["ClaudeProcess.cs"] = "interactive session streaming transport (launches every Claude Code session)",

        // The interactive streaming transport for the Cursor agent (`-p --output-format
        // stream-json`). Same exempt category as the Claude transport above: it launches an
        // interactive session the Director parses into cards, not a one-shot side-call.
        ["CursorAgent.cs"] = "interactive session streaming transport (launches the Cursor agent session)",

        // The selectable interactive Pipe backend (`-p`). An interactive backend, not a
        // one-shot side-call.
        ["PipeBackend.cs"] = "selectable interactive Pipe backend",

        // The structured one-shot client (`-p --output-format json` / `--json-schema`) used
        // by QuickActions, which needs streaming / structured JSON / budget caps that
        // SessionAskRunner does not provide.
        ["ClaudeArgBuilder.cs"] = "structured one-shot client behind QuickActions (needs structured JSON / budget caps)",
        ["ClaudeClient.cs"] = "structured one-shot client behind QuickActions (needs structured JSON / budget caps)",
    };

    /// <summary>
    /// Whole project directories that are EXEMPT. The CliExplorer project is a diagnostic
    /// tool whose entire job is probing `--print` / `-p` flags, so the ban does not apply.
    /// Matched as a path segment so every file under the directory is skipped.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ExemptProjectDirectories = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["CcDirector.CliExplorer"] = "diagnostic tool whose job is probing --print / -p flags",
    };

    /// <summary>
    /// Matches an actual `--print` or `-p` argument literal, not prose. Three real-code
    /// shapes are covered: a quoted argument added to a ProcessStartInfo argument list
    /// (e.g. <c>ArgumentList.Add("--print")</c> or <c>"-p"</c>), and an inline
    /// <c>-p</c> / <c>--print</c> inside a quoted argument string (e.g.
    /// <c>Arguments = "-p \"...\""</c>). Word boundaries keep <c>-p</c> from matching
    /// inside identifiers like <c>-package</c>.
    /// </summary>
    private static readonly Regex PrintFlagLiteral = new(
        "\"--print\"" +                 // "--print" as a standalone quoted argument
        "|\"-p\"" +                     // "-p" as a standalone quoted argument
        "|\"-p " +                      // "-p ..." at the start of an Arguments string
        "|\"--print ",                  // "--print ..." at the start of an Arguments string
        RegexOptions.None);

    [Fact]
    public void No_in_scope_side_call_emits_print_or_p()
    {
        var coreDir = ResolveCoreSourceDir();
        var files = Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories);
        Assert.NotEmpty(files);

        var offenders = new List<string>();
        var scanned = 0;
        var exemptHit = 0;

        foreach (var file in files)
        {
            if (IsExempt(file)) { exemptHit++; continue; }

            scanned++;
            var text = File.ReadAllText(file);
            if (PrintFlagLiteral.IsMatch(text))
                offenders.Add(Path.GetFileName(file));
        }

        _out.WriteLine($"Scanned {scanned} in-scope file(s); skipped {exemptHit} exempt file(s).");

        Assert.True(offenders.Count == 0,
            "The metered one-shot --print / -p path is banned on the wingman / recap / voice " +
            "side-call surface (issue #511). Move the call to a real session via SessionAskRunner " +
            "(#509). If the file legitimately needs -p / --print and has no real-session equivalent, " +
            "add it to PrintBanAuditTests.ExemptFiles with a reason. Offending file(s):\n  " +
            string.Join("\n  ", offenders));
    }

    [Fact]
    public void Exempt_files_still_exist_so_the_allowlist_cannot_silently_rot()
    {
        // A guard on the guard: if an exempt file is renamed or deleted, the allowlist entry
        // is dead weight that would silently let a future -p slip into a file with the same
        // name. Fail loudly so the exemption list stays an accurate single source of truth.
        var coreDir = ResolveCoreSourceDir();
        var names = Directory.GetFiles(coreDir, "*.cs", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .ToHashSet(StringComparer.Ordinal);

        var missing = ExemptFiles.Keys.Where(k => !names.Contains(k)).ToList();
        Assert.True(missing.Count == 0,
            "These exempt files in PrintBanAuditTests.ExemptFiles no longer exist under " +
            "src/CcDirector.Core - update the allowlist:\n  " + string.Join("\n  ", missing));
    }

    private static bool IsExempt(string filePath)
    {
        var name = Path.GetFileName(filePath);
        if (ExemptFiles.ContainsKey(name)) return true;

        // Normalize separators so the project-directory check works on every platform.
        var normalized = filePath.Replace('\\', '/');
        foreach (var projectDir in ExemptProjectDirectories.Keys)
            if (normalized.Contains("/" + projectDir + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        return false;
    }

    private static string ResolveCoreSourceDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "CcDirector.Core");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException(
            "Could not locate src/CcDirector.Core by walking up from " + AppContext.BaseDirectory);
    }
}
