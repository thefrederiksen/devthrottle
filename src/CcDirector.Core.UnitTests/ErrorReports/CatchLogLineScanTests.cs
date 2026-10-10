using System.Text.RegularExpressions;
using CcDirector.Core.ErrorReports;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3641: a log line written inside a <c>catch</c> records something that went wrong, and the Director and
/// the launcher send a line to the Gateway only when <see cref="ErrorLine.IsError"/> recognises it. About 147
/// such lines were not recognised - <c>[SkillToolLinker] overrides load failed</c>, <c>[RestoreSessionsDialog]
/// Restore threw exception</c> - so they reached the local log and nowhere else, and
/// <see cref="ErrorLineSourceScanTests"/> could not see them because it only checks lines that already carry a
/// marker word.
///
/// This guard reads the code itself with the C# parser, so it finds every <c>FileLog.Write</c> (and every
/// <c>EngineLog.Write</c>, whose sink is <c>FileLog.Write</c> in both processes) whose nearest enclosing body is
/// a <c>catch</c> - any exception variable name, one line or many - and fails on each one the rule would not
/// report. A site that records an EXPECTED case (a cancelled wait, a probe whose "no" is an answer) says so
/// with a named opt-out on the line above the call, and the reason is required:
/// <code>// not-an-error: the user cancelled the wait; nothing went wrong</code>
///
/// WHICH CODE. The projects the Director and the launcher executables load, read from their own project files
/// and followed through every project reference - never a hand-kept list, so a project either process starts
/// loading is scanned the day it is referenced. The Gateway's own projects are not in that set and are not
/// scanned. Core is: it is ONE assembly loaded by the Director, the launcher and the Gateway alike, so a Core
/// line cannot be known statically to run only in the Gateway, and a line recognised as an error is the right
/// answer in every process that writes it.
///
/// AND WHAT A REPORT MAY CARRY. A line that becomes reportable leaves the machine, and the scrubber removes
/// home folders and credentials, not words. So no reportable line may interpolate a value whose name says it
/// is somebody's words - a prompt, a transcript, dictation, terminal or screen text, a model's reply - except
/// by its length.
/// </summary>
public sealed class CatchLogLineScanTests
{
    /// <summary>The executables whose processes run an <see cref="ErrorReporter"/>.</summary>
    private static readonly string[] ReportingExecutables =
    {
        "src/CcDirector.Avalonia/CcDirector.Avalonia.csproj",
        "src/CcDirector.Launcher/CcDirector.Launcher.csproj",
    };

    internal const string OptOutMarker = "not-an-error:";

    private static readonly HashSet<string> LogClasses = new(StringComparer.Ordinal) { "FileLog", "EngineLog" };

    // Names of values that hold somebody's words. Matched against each identifier in an interpolated hole; a
    // hole that ends in .Length / .Count is a size, not words, and is allowed.
    private static readonly Regex WordsName = new(
        @"(?i)^(prompt|prompts|prompttext|transcript|transcripttext|dictation|dictationtext|utterance|spoken|spokentext|screentext|screen|screentail|terminaltext|buffertext|headline|reply|replytext|modeltext|modelreply|completion|narration|summarytext|raw|rawjson|rawtext|jsonline|line|output|stdout|stderr|text|content|body|chunk|typed)$",
        RegexOptions.CultureInvariant);

    private readonly ITestOutputHelper _output;

    public CatchLogLineScanTests(ITestOutputHelper output) => _output = output;

    internal sealed record Site(string File, int Line, string Rendered, bool IsError, string? OptOutReason,
        IReadOnlyList<string> WordsHoles);

    private static string RepositoryRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Core")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"could not find the repository root above {AppContext.BaseDirectory}; this test reads the source tree");
    }

    /// <summary>Every project directory the reporting executables load, followed through project references.</summary>
    internal static IReadOnlyList<string> ReportingProjects(string root)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(ReportingExecutables.Select(p => Path.GetFullPath(Path.Combine(root, p))));
        var reference = new Regex(@"<ProjectReference\s+Include=""([^""]+)""", RegexOptions.CultureInvariant);
        while (pending.Count > 0)
        {
            var project = pending.Pop();
            if (!seen.Add(project)) continue;
            if (!File.Exists(project)) throw new FileNotFoundException($"referenced project not found: {project}");
            var dir = Path.GetDirectoryName(project)!;
            foreach (Match m in reference.Matches(File.ReadAllText(project)))
                pending.Push(Path.GetFullPath(Path.Combine(dir, m.Groups[1].Value.Replace('\\', Path.DirectorySeparatorChar))));
        }
        return seen.Select(p => Path.GetDirectoryName(p)!).OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>Every log call whose nearest enclosing body is a catch clause, in one file's text.</summary>
    internal static IEnumerable<Site> SitesIn(string relativePath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            if (call.Expression is not MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Write" } access) continue;
            if (access.Expression is not IdentifierNameSyntax { Identifier.ValueText: var cls } || !LogClasses.Contains(cls)) continue;
            if (call.ArgumentList.Arguments.Count != 1) continue;
            if (!InCatch(call)) continue;

            var arg = call.ArgumentList.Arguments[0].Expression;
            var variants = Render(arg).ToList();
            var line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
            // Every way the line can come out must be recognised; the first one that is not is the one shown.
            var failing = variants.FirstOrDefault(v => !IsRecognised(v));
            var rendered = failing ?? variants[0];
            yield return new Site(relativePath, line, rendered.Split('\n')[0], failing is null, OptOutOf(call),
                WordsHolesIn(arg));
        }
    }

    private static bool IsRecognised(string line)
        => line.StartsWith(ErrorLine.ReporterTag, StringComparison.Ordinal) || ErrorLine.IsError(line);

    /// <summary>True when the nearest enclosing body is a catch clause: a lambda or local function inside a catch
    /// runs later, on its own terms, and is not the catch's record of what went wrong.</summary>
    private static bool InCatch(SyntaxNode node)
    {
        for (var n = node.Parent; n is not null; n = n.Parent)
        {
            switch (n)
            {
                case CatchClauseSyntax: return true;
                case AnonymousFunctionExpressionSyntax:
                case LocalFunctionStatementSyntax:
                case MemberDeclarationSyntax:
                    return false;
            }
        }
        return false;
    }

    /// <summary>What the line looks like at run time, as near as the source can say: every hole is a value
    /// ("x"). A conditional gives one variant per branch.</summary>
    private static IEnumerable<string> Render(ExpressionSyntax e)
    {
        switch (e)
        {
            case ParenthesizedExpressionSyntax p:
                return Render(p.Expression);
            case LiteralExpressionSyntax lit when lit.IsKind(SyntaxKind.StringLiteralExpression):
                return new[] { lit.Token.ValueText };
            case InterpolatedStringExpressionSyntax s:
                return new[] { string.Concat(s.Contents.Select(c => c switch
                {
                    InterpolatedStringTextSyntax t => t.TextToken.ValueText,
                    // The reporter's own tag is a constant, and its lines are exempt by design.
                    InterpolationSyntax h when h.Expression.ToString().EndsWith("ReporterTag", StringComparison.Ordinal) => ErrorLine.ReporterTag,
                    _ => "x",
                })) };
            case BinaryExpressionSyntax b when b.IsKind(SyntaxKind.AddExpression):
                return Render(b.Left).SelectMany(l => Render(b.Right).Select(r => l + r)).ToList();
            case ConditionalExpressionSyntax c:
                return Render(c.WhenTrue).Concat(Render(c.WhenFalse)).ToList();
            default:
                return new[] { "x" };
        }
    }

    /// <summary>The holes of the argument that name somebody's words and are not a length or a count.</summary>
    private static IReadOnlyList<string> WordsHolesIn(ExpressionSyntax arg)
    {
        var found = new List<string>();
        foreach (var hole in arg.DescendantNodesAndSelf().OfType<InterpolationSyntax>())
        {
            var text = hole.Expression.ToString();
            if (Regex.IsMatch(text, @"\.(Length|Count)\b(\(\))?\s*$", RegexOptions.CultureInvariant)) continue;
            // The name of the value PRINTED: in a.b.c that is c, so a.Index is a number however a is named.
            var names = hole.Expression.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(i => i.Parent switch
                {
                    MemberAccessExpressionSyntax access => access.Name == i,
                    ConditionalAccessExpressionSyntax conditional => conditional.Expression != i,
                    _ => true,
                })
                .Select(i => i.Identifier.ValueText);
            if (names.Any(n => WordsName.IsMatch(n))) found.Add(text);
        }
        return found;
    }

    /// <summary>The reason given by a <c>// not-an-error: reason</c> comment on the line above the call, or at the
    /// end of the call's own line, or null. Read by line rather than by syntax trivia so it works the same for a
    /// call alone in a block and for one inside a one-line <c>catch (...) { ... }</c>.</summary>
    private static string? OptOutOf(InvocationExpressionSyntax call)
    {
        var text = call.SyntaxTree.GetText();
        var line = call.SyntaxTree.GetLineSpan(call.Span).StartLinePosition.Line;
        foreach (var candidate in new[] { line - 1, line })
        {
            if (candidate < 0) continue;
            var lineText = text.Lines[candidate].ToString();
            var at = lineText.IndexOf("// " + OptOutMarker, StringComparison.Ordinal);
            if (at < 0) continue;
            // The line above must be the comment alone, not the end of some other statement's line.
            if (candidate == line - 1 && lineText[..at].Trim().Length > 0) continue;
            return lineText[(at + 3 + OptOutMarker.Length)..].Trim();
        }
        return null;
    }

    internal static List<Site> ScanRepository(string root, out int filesRead, out IReadOnlyList<string> projects)
    {
        projects = ReportingProjects(root);
        var sites = new List<Site>();
        filesRead = 0;
        var sep = Path.DirectorySeparatorChar;
        foreach (var dir in projects)
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{sep}obj{sep}") || file.Contains($"{sep}bin{sep}")) continue;
                filesRead++;
                var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
                sites.AddRange(SitesIn(rel, File.ReadAllText(file)));
            }
        }
        return sites;
    }

    [Fact]
    public void EveryLogLineInACatch_IsReportedOrSaysWhyNot()
    {
        var root = RepositoryRoot();
        var sites = ScanRepository(root, out var filesRead, out var projects);
        var optedOut = sites.Where(s => !s.IsError && s.OptOutReason is not null).ToList();
        var missed = sites.Where(s => !s.IsError && s.OptOutReason is null).ToList();
        var emptyReason = optedOut.Where(s => s.OptOutReason!.Length < 10).ToList();

        _output.WriteLine($"projects scanned: {projects.Count} ({string.Join(", ", projects.Select(p => Path.GetFileName(p)))})");
        _output.WriteLine($"files read: {filesRead}; log lines inside a catch: {sites.Count}; recognised as errors: {sites.Count(s => s.IsError)}; " +
                          $"opted out: {optedOut.Count}; not recognised: {missed.Count}");

        // A check whose pass condition is an absence: a scan that read nothing would pass it.
        Assert.True(projects.Count >= 5, $"only {projects.Count} projects found from the executables' project files - the scan is not reading them");
        Assert.True(sites.Count > 500, $"only {sites.Count} log lines inside a catch found - the scan is not reading the source");
        Assert.True(emptyReason.Count == 0,
            $"{emptyReason.Count} opt-out(s) give no real reason. Say why the case is expected:\n" +
            string.Join("\n", emptyReason.Select(s => $"{s.File}:{s.Line}: {s.Rendered}")));
        Assert.True(missed.Count == 0,
            $"{missed.Count} line(s) written inside a catch would never reach the Gateway. Use the house form " +
            "\"[Class] Method FAILED: ...\", or, for an expected case, put \"// " + OptOutMarker + " <why>\" on the line above:\n" +
            string.Join("\n", missed.Select(s => $"{s.File}:{s.Line}: {s.Rendered}")));
    }

    [Fact]
    public void NoReportableLineInACatch_CarriesSomebodysWords()
    {
        var root = RepositoryRoot();
        var sites = ScanRepository(root, out _, out _);
        var reportable = sites.Where(s => s.IsError).ToList();
        var leaking = reportable.Where(s => s.WordsHoles.Count > 0).ToList();
        _output.WriteLine($"reportable lines inside a catch: {reportable.Count}; carrying words: {leaking.Count}");
        Assert.True(reportable.Count > 300, $"only {reportable.Count} reportable lines found - the scan is not reading the source");
        Assert.True(leaking.Count == 0,
            $"{leaking.Count} reportable line(s) would send a prompt, transcript, terminal or model text to the Gateway. " +
            "Log its length instead:\n" +
            string.Join("\n", leaking.Select(s => $"{s.File}:{s.Line}: {string.Join(", ", s.WordsHoles)}")));
    }

    // ---- The guard proven against deliberately broken sites ----

    [Fact]
    public void SitesIn_ALineInACatchWithNoMarker_IsNotRecognised()
    {
        const string source = """
            class C {
                void M() {
                    try { }
                    catch (System.Exception boom)
                    {
                        FileLog.Write(
                            $"[C] M could not load the overrides: {boom.Message}");
                    }
                }
            }
            """;
        var site = Assert.Single(CatchLogLineScanTests.SitesIn("C.cs", source));
        Assert.False(site.IsError);
        Assert.Null(site.OptOutReason);
        Assert.Equal(6, site.Line);
    }

    [Fact]
    public void SitesIn_TheHouseForm_IsRecognised()
    {
        const string source = """
            class C { void M() { try { } catch (System.Exception e) { FileLog.Write($"[C] M FAILED: {e.Message}"); } } }
            """;
        Assert.True(Assert.Single(CatchLogLineScanTests.SitesIn("C.cs", source)).IsError);
    }

    [Fact]
    public void SitesIn_AnOptOutWithItsReason_IsRead()
    {
        const string source = """
            class C { void M() { try { } catch (System.OperationCanceledException) {
                // not-an-error: the wait was cancelled by shutdown; nothing went wrong
                FileLog.Write("[C] M cancelled");
            } } }
            """;
        var site = Assert.Single(CatchLogLineScanTests.SitesIn("C.cs", source));
        Assert.Equal("the wait was cancelled by shutdown; nothing went wrong", site.OptOutReason);
    }

    [Fact]
    public void SitesIn_ALambdaInsideACatch_IsNotTheCatchsLine_AndALineOutsideACatchIsNotRead()
    {
        const string source = """
            class C { void M() {
                FileLog.Write("[C] M start");
                try { } catch (System.Exception) { System.Action a = () => FileLog.Write("[C] later"); }
            } }
            """;
        Assert.Empty(CatchLogLineScanTests.SitesIn("C.cs", source));
    }

    [Fact]
    public void SitesIn_EachBranchOfAConditional_MustBeRecognised()
    {
        const string source = """
            class C { void M(bool b) { try { } catch (System.Exception e) {
                FileLog.Write(b ? $"[C] M FAILED: {e.Message}" : $"[C] M gave up: {e.Message}");
            } } }
            """;
        Assert.False(Assert.Single(CatchLogLineScanTests.SitesIn("C.cs", source)).IsError);
    }

    [Fact]
    public void SitesIn_AReportableLineCarryingAPrompt_IsCaught_ButItsLengthIsAllowed()
    {
        const string source = """
            class C { void M(string prompt, string transcript) { try { } catch (System.Exception e) {
                FileLog.Write($"[C] Send FAILED: {e.Message}, prompt={prompt}");
                FileLog.Write($"[C] Send FAILED: {e.Message}, chars={transcript.Length}");
            } } }
            """;
        var sites = CatchLogLineScanTests.SitesIn("C.cs", source).ToList();
        Assert.Equal(new[] { "prompt" }, sites[0].WordsHoles);
        Assert.Empty(sites[1].WordsHoles);
    }

    [Fact]
    public void SitesIn_AWordsValueCutShort_IsStillWords_ButAPropertyOfAWordsNamedObjectIsNot()
    {
        const string source = """
            class C { void M(string raw, string line, Chunk chunk) { try { } catch (System.Exception e) {
                FileLog.Write($"[C] Parse FAILED: {e.Message}, raw='{Truncate(raw, 200)}'");
                FileLog.Write($"[C] Parse FAILED: {e.Message}, start={line[..Math.Min(100, line.Length)]}");
                FileLog.Write($"[C] Parse FAILED: {e.Message}, chunk={chunk.Index}, length={raw?.Length ?? 0}");
            } } }
            """;
        var sites = CatchLogLineScanTests.SitesIn("C.cs", source).ToList();
        Assert.Equal(new[] { "Truncate(raw, 200)" }, sites[0].WordsHoles);
        Assert.Equal(new[] { "line[..Math.Min(100, line.Length)]" }, sites[1].WordsHoles);
        Assert.Empty(sites[2].WordsHoles);
    }
}
