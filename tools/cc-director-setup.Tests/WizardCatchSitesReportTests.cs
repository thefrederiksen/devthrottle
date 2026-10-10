using System.IO;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Issue #3640: every place the Windows setup wizard catches an exception either reports it to DevThrottle
/// (<c>WizardProgressReport</c>), hands it on (<c>throw;</c>), or says in a comment at the site why it is not a
/// failure worth a report ("Not reported: ..."). The list of sites is read from the wizard's own source, never
/// kept by hand, so a new catch that only writes the setup log fails here.
///
/// Proved once against a deliberately broken site: removing the report from the catch in
/// <c>CompleteStep.OpenDirector</c> made this test fail and name that file and line.
/// </summary>
public sealed class WizardCatchSitesReportTests(ITestOutputHelper output)
{
    // A catch of everything: "catch (Exception ...)" with or without a filter, or a bare "catch". A catch of one
    // narrow type (IOException in the log writer, OperationCanceledException in a delay) is a decision about that
    // type, not a swallowed failure, and is not in scope.
    private static readonly Regex CatchAll = new(@"\bcatch\s*(?:\(\s*(?:System\.)?Exception\b[^)]*\))?\s*(?:when\s*\((?>[^()]+|\((?<d>)|\)(?<-d>))*\)\s*)?\{",
        RegexOptions.CultureInvariant);

    [Fact]
    public void EveryCatchAllInTheWindowsWizard_ReportsRethrowsOrSaysWhyNot()
    {
        var root = Path.Combine(RepoRoot(), "tools", "cc-director-setup");
        var files = Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}"))
            .ToList();

        var sites = 0;
        var unreported = new List<string>();
        foreach (var file in files)
        {
            var text = File.ReadAllText(file);
            foreach (Match m in CatchAll.Matches(text))
            {
                sites++;
                var body = Block(text, m.Index + m.Length - 1);
                if (body.Contains("WizardProgressReport.", StringComparison.Ordinal)
                    || body.Contains("throw;", StringComparison.Ordinal)
                    || body.Contains("Not reported:", StringComparison.Ordinal))
                    continue;
                var line = text[..m.Index].Count(c => c == '\n') + 1;
                unreported.Add($"{Path.GetRelativePath(root, file)}:{line}");
            }
        }

        output.WriteLine($"catch sites read: {sites} in {files.Count} files");
        Assert.True(sites > 0, "the scan read no catch sites at all, so it is broken, not clean");
        Assert.True(unreported.Count == 0,
            "these catches only log; report them with WizardProgressReport, rethrow, or write \"Not reported: <why>\" at the site:\n"
            + string.Join("\n", unreported));
    }

    /// <summary>The text from the opening brace at <paramref name="open"/> to its matching closing brace.</summary>
    private static string Block(string text, int open)
    {
        var depth = 0;
        for (var i = open; i < text.Length; i++)
        {
            if (text[i] == '{') depth++;
            else if (text[i] == '}' && --depth == 0) return text[open..(i + 1)];
        }
        throw new InvalidOperationException($"no closing brace for the block at {open}");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "tools", "cc-director-setup", "CcDirectorSetup.csproj")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
