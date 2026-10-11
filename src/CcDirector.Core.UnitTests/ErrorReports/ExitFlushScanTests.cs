using CcDirector.TestInfrastructure;
using CcDirector.Core.ErrorReports;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Core.UnitTests.ErrorReports;

/// <summary>
/// Issue #3352: the reporter sends every twenty seconds, so an error logged just before a deliberate exit left with
/// the process unless the exit called <see cref="ErrorReporter.FlushBeforeExit"/> first. The flush itself is
/// proven in <see cref="ErrorReporterTests"/>; this guard proves the EXITS call it, so deleting one of those calls
/// fails a test instead of passing every suite.
///
/// It reads the two reporting executables' own source (CcDirector.Avalonia and CcDirector.Launcher, from
/// <see cref="CatchLogLineScanTests.ReportingExecutables"/>) with the C# parser. A deliberate exit is a call to
/// <c>FileLog.Stop()</c> - the log is closed, so the process is leaving - or to <c>Environment.Exit(...)</c>. Each
/// one must have, BEFORE it in its own block or in a block around it, a statement that calls
/// <c>ErrorReporter.FlushBeforeExit</c> or waits on <c>ErrorReporter.ExitFlushBudget</c>. An exit that cannot
/// flush says why, on the line above:
/// <code>// exits-without-flush: the reporter is not started on this path, so nothing can be pending</code>
///
/// What it cannot see: an exit that is a plain <c>return</c> from Main with no FileLog.Stop() before it.
/// </summary>
public sealed class ExitFlushScanTests
{
    internal const string OptOutMarker = "exits-without-flush:";

    private readonly ITestOutputHelper _output;

    public ExitFlushScanTests(ITestOutputHelper output) => _output = output;

    internal sealed record ExitSite(string File, int Line, string Call, bool Flushed, string? OptOutReason);

    internal static IEnumerable<ExitSite> ExitSitesIn(string relativePath, string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        foreach (var call in tree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var name = CallName(call);
            if (name is not ("FileLog.Stop" or "Environment.Exit")) continue;
            var line = tree.GetLineSpan(call.Span).StartLinePosition.Line + 1;
            yield return new ExitSite(relativePath, line, name, FlushedBefore(call), OptOutOf(call));
        }
    }

    private static string? CallName(InvocationExpressionSyntax call)
        => call.Expression is MemberAccessExpressionSyntax access ? LastTwo(access) : null;

    // "System.Environment.Exit" and "Environment.Exit" are both Environment.Exit.
    private static string LastTwo(MemberAccessExpressionSyntax access)
    {
        var left = access.Expression switch
        {
            MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
            IdentifierNameSyntax i => i.Identifier.ValueText,
            _ => "",
        };
        return left + "." + access.Name.Identifier.ValueText;
    }

    /// <summary>True when a statement before the exit - in its own block, or in any block around it up to the
    /// member - flushes the reporter.</summary>
    private static bool FlushedBefore(SyntaxNode exit)
    {
        SyntaxNode node = exit;
        for (var parent = node.Parent; parent is not null; node = parent, parent = parent.Parent)
        {
            if (parent is BlockSyntax block)
            {
                foreach (var statement in block.Statements)
                {
                    if (statement.Span.Start >= node.Span.Start) break;
                    if (Flushes(statement)) return true;
                }
            }
            if (parent is MemberDeclarationSyntax or AnonymousFunctionExpressionSyntax or LocalFunctionStatementSyntax) return false;
        }
        return false;
    }

    // A flush inside a lambda is only declared, not run - unless the statement also waits on the budget.
    private static bool Flushes(SyntaxNode statement)
        => statement.DescendantNodesAndSelf(n => n is not AnonymousFunctionExpressionSyntax).Any(n => n switch
        {
            InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax a } => LastTwo(a) == "ErrorReporter.FlushBeforeExit",
            MemberAccessExpressionSyntax a => LastTwo(a) == "ErrorReporter.ExitFlushBudget",
            _ => false,
        });

    private static string? OptOutOf(InvocationExpressionSyntax call)
    {
        var text = call.SyntaxTree.GetText();
        var line = call.SyntaxTree.GetLineSpan(call.Span).StartLinePosition.Line;
        if (line == 0) return null;
        var above = text.Lines[line - 1].ToString().Trim();
        return above.StartsWith("// " + OptOutMarker, StringComparison.Ordinal)
            ? above[(3 + OptOutMarker.Length)..].Trim()
            : null;
    }

    [Fact]
    public void EveryDeliberateExit_FlushesTheErrorReporterFirst_OrSaysWhyNot()
    {
        var root = RepositorySourceIndex.Root;
        var sites = new List<ExitSite>();
        var filesRead = 0;
        foreach (var project in CatchLogLineScanTests.ReportingExecutables)
        {
            var dir = Path.GetDirectoryName(Path.GetFullPath(Path.Combine(root, project)))!;
            foreach (var file in RepositorySourceIndex.Under(dir, ".cs"))
            {
                filesRead++;
                sites.AddRange(ExitSitesIn(Path.GetRelativePath(root, file).Replace('\\', '/'), File.ReadAllText(file)));
            }
        }
        var optedOut = sites.Where(s => !s.Flushed && s.OptOutReason is not null).ToList();
        var missed = sites.Where(s => !s.Flushed && s.OptOutReason is null).ToList();

        _output.WriteLine($"files read: {filesRead}; exit sites: {sites.Count} (FileLog.Stop {sites.Count(s => s.Call == "FileLog.Stop")}, " +
                          $"Environment.Exit {sites.Count(s => s.Call == "Environment.Exit")}); flushed first: {sites.Count(s => s.Flushed)}; " +
                          $"opted out: {optedOut.Count}; missing a flush: {missed.Count}");
        foreach (var s in optedOut) _output.WriteLine($"  opted out: {s.File}:{s.Line}: {s.OptOutReason}");

        // A check whose pass condition is an absence: a scan that read nothing would pass it.
        Assert.True(sites.Count(s => s.Call == "FileLog.Stop") >= 5, $"only {sites.Count(s => s.Call == "FileLog.Stop")} FileLog.Stop() calls found - the scan is not reading the source");
        Assert.True(sites.Any(s => s.Call == "Environment.Exit"), "no Environment.Exit call found - the scan is not reading the source");
        Assert.True(optedOut.All(s => s.OptOutReason!.Length >= 10), "an exits-without-flush opt-out gives no real reason");
        Assert.True(missed.Count == 0,
            $"{missed.Count} exit(s) leave without flushing the error reporter, so an error logged just before them is lost. " +
            "Call ErrorReporter.FlushBeforeExit(ErrorReporter.ExitFlushBudget) before it, or put \"// " + OptOutMarker + " <why>\" on the line above:\n" +
            string.Join("\n", missed.Select(s => $"{s.File}:{s.Line}: {s.Call}")));
    }

    // ---- The guard proven against hand-built sources ----

    [Fact]
    public void ExitSitesIn_AnExitWithNoFlush_IsCaught()
    {
        const string source = """
            class P { static int Main() {
                FileLog.Write("[Program] leaving");
                FileLog.Stop();
                System.Environment.Exit(1);
                return 0;
            } }
            """;
        var sites = ExitSitesIn("P.cs", source).ToList();
        Assert.Equal(new[] { "FileLog.Stop", "Environment.Exit" }, sites.Select(s => s.Call));
        Assert.All(sites, s => Assert.False(s.Flushed));
    }

    [Fact]
    public void ExitSitesIn_AFlushBeforeTheExit_InTheSameBlockOrABlockAroundIt_Counts()
    {
        const string source = """
            class P { static int Main(bool update) {
                ErrorReporter.FlushBeforeExit(ErrorReporter.ExitFlushBudget);
                if (update) { FileLog.Stop(); return 1; }
                try { } finally {
                    var flush = Task.Run(() => ErrorReporter.FlushBeforeExit(ErrorReporter.ExitFlushBudget));
                    flush.Wait(ErrorReporter.ExitFlushBudget + TimeSpan.FromSeconds(2));
                    Environment.Exit(0);
                }
                return 0;
            } }
            """;
        Assert.All(ExitSitesIn("P.cs", source), s => Assert.True(s.Flushed, $"line {s.Line}"));
    }

    [Fact]
    public void ExitSitesIn_AFlushAfterTheExit_OrInsideALambda_DoesNotCount()
    {
        const string source = """
            class P { static void Main() {
                System.Action later = () => ErrorReporter.FlushBeforeExit(ErrorReporter.ExitFlushBudget);
                FileLog.Stop();
                ErrorReporter.FlushBeforeExit(ErrorReporter.ExitFlushBudget);
            } }
            """;
        // The lambda's statement is before the exit, but declaring a flush is not running one.
        Assert.False(Assert.Single(ExitSitesIn("P.cs", source)).Flushed);
    }

    [Fact]
    public void ExitSitesIn_AnOptOutWithItsReason_IsRead()
    {
        const string source = """
            class P { static int Main() {
                // exits-without-flush: the reporter is not started on this path
                FileLog.Stop();
                return 0;
            } }
            """;
        Assert.Equal("the reporter is not started on this path", Assert.Single(ExitSitesIn("P.cs", source)).OptOutReason);
    }
}
