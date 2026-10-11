using CcDirector.TestInfrastructure;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;
using Xunit.Abstractions;

namespace CcDirector.Gateway.Tests.Api;

/// <summary>
/// Every HTTP failure line the Gateway logs names its request as "VERB /route" - the verb in capitals and ONE space
/// before the leading slash (the Error Logging mission, step 1 review). That is the shape the error fingerprint keeps
/// apart by route; "POST start FAILED", "post /x" or "GET  /x" would each fold to a different problem, or merge two
/// routes into one, now that these lines reach the error store.
///
/// The sites are DERIVED from the code: every <c>FileLog.Write</c> line in the Gateway project whose text is an error
/// line (it carries FAILED, UNHANDLED, UNOBSERVED, FATAL or ERROR) and names an HTTP verb. A verb must be followed by
/// one space and then the route itself, or an interpolated route or path constant (<c>{Path}</c>, <c>{HandOverRoute}</c>).
/// The scan prints how many sites it read and fails on a run that read none.
/// </summary>
public sealed class GatewayHttpFailureLineShapeTests(ITestOutputHelper output)
{
    private static readonly Regex Marker = new(@"\b(FAILED|UNHANDLED|UNOBSERVED|FATAL|ERROR)\b", RegexOptions.CultureInvariant);
    private static readonly Regex Verb = new(@"(?<![A-Za-z_])(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)(?![A-Za-z_])", RegexOptions.CultureInvariant);
    private static readonly Regex LowerVerbRoute = new(@"(?<![A-Za-z_])(get|post|put|patch|delete|head|options) +/", RegexOptions.CultureInvariant);
    // After the verb: one space, then "/" or an interpolation hole whose name says it is a route or a path.
    private static readonly Regex Canonical = new(@"^ (/|\{[A-Za-z_.]*(Path|Route|path|route)\})", RegexOptions.CultureInvariant);

    /// <summary>The checker on one logged text: null when it is in shape, else why not.</summary>
    internal static string? Violation(string loggedText)
    {
        if (!Marker.IsMatch(loggedText)) return null;
        if (LowerVerbRoute.Match(loggedText) is { Success: true } lower)
            return $"lower-case verb '{lower.Value.Trim()}': the verb must be in capitals";
        foreach (Match verb in Verb.Matches(loggedText))
        {
            var after = loggedText[(verb.Index + verb.Length)..];
            if (!Canonical.IsMatch(after))
                return $"'{verb.Value}' is not followed by one space and the route ('{Excerpt(after)}')";
        }
        return null;
    }

    [Fact]
    public void Every_Gateway_HTTP_failure_line_names_its_request_as_VERB_space_route()
    {
        var gateway = Path.Combine(RepositorySourceIndex.Root, "src", "CcDirector.Gateway");
        var files = RepositorySourceIndex.Under(gateway, ".cs").ToList();

        var errorSites = 0;
        var httpSites = 0;
        var multiLineSites = 0;
        var bad = new List<string>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var at = lines[i].IndexOf("FileLog.Write(", StringComparison.Ordinal);
                if (at < 0) continue;
                var text = WholeCall(lines, i, at);
                if (text.Contains('\n')) multiLineSites++;
                if (!Marker.IsMatch(text)) continue;
                errorSites++;
                if (Verb.IsMatch(text) || LowerVerbRoute.IsMatch(text)) httpSites++;
                if (Violation(text) is { } why)
                    bad.Add($"{Path.GetRelativePath(gateway, file)}:{i + 1}: {why}");
            }
        }

        output.WriteLine($"read {files.Count} files, {errorSites} FileLog error lines, {httpSites} of them name an HTTP verb, "
            + $"{multiLineSites} FileLog calls read across more than one line");
        Assert.True(multiLineSites > 0, "the scan read no call that spans lines - the multi-line reading is broken, not clean");
        Assert.True(errorSites > 0, "the scan read no FileLog error lines at all - it is broken, not clean");
        Assert.True(httpSites > 0, "the scan found no HTTP failure line at all - it is broken, not clean");
        Assert.True(bad.Count == 0, "HTTP failure lines not in the \"VERB /route\" shape:\n" + string.Join("\n", bad));
    }

    [Theory]
    // The deliberately broken sites the scan must catch - each one a shape that existed before this check.
    [InlineData("FileLog.Write($\"[FleetManagerHandOverEndpoints] POST hand-over FAILED: {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[RaisedSessionEndpoints] POST {what} FAILED: {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[X] post /gateway/skills FAILED: {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[X] GET  /gateway/skills FAILED: {ex.Message}\");")]
    public void The_scan_catches_a_broken_site(string line) => Assert.NotNull(Violation(line));

    [Theory]
    [InlineData("FileLog.Write($\"[DirectorErrorEndpoints] POST {Path} FAILED ({ex.GetType().Name}): {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[GatewayEndpoints] DELETE /directors/{{id}} FORCE-KILL FAILED: id={id}\");")]
    [InlineData("FileLog.Write($\"[TeamLibraryEndpoints] GET {GroupPath}{LibraryPath} FAILED: {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[Cron] Save FAILED: {ex.Message}\");")]
    [InlineData("FileLog.Write($\"[GatewayHost] GET /sessions -> 200\");")]
    public void A_site_in_shape_passes(string line) => Assert.Null(Violation(line));

    /// <summary>
    /// The text of one <c>FileLog.Write(...)</c> call from where it starts, joined across lines until the line that
    /// closes it (step 2 review, observation 7): a string that starts on the next line is read like any other. At most
    /// eight lines, which is longer than any call in the Gateway.
    /// </summary>
    internal static string WholeCall(IReadOnlyList<string> lines, int start, int column)
    {
        var text = lines[start][column..];
        for (var i = start + 1; i < lines.Count && i < start + 8 && !text.TrimEnd().EndsWith(");", StringComparison.Ordinal); i++)
            text += "\n" + lines[i].Trim();
        return text;
    }

    [Fact]
    public void A_call_whose_string_starts_on_the_next_line_is_read_whole()
    {
        string[] lines =
        [
            "            FileLog.Write(",
            "                $\"[FleetManagerPlacementEndpoints] POST start FAILED: {ex.Message}\");",
            "            return x;",
        ];
        var text = WholeCall(lines, 0, lines[0].IndexOf("FileLog.Write(", StringComparison.Ordinal));
        Assert.DoesNotContain("return x", text);
        Assert.NotNull(Violation(text));
    }

    private static string Excerpt(string text) => text.Length > 30 ? text[..30] : text;

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.UnitTests/Api/GatewayHttpFailureLineShapeTests.cs
        var root = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(thisFile)!, "..", "..", ".."));
        var marker = Path.Combine(root, "src", "CcDirector.Gateway", "CcDirector.Gateway.csproj");
        Assert.True(File.Exists(marker), $"Resolved the repository root to {root}, but it has no {marker}. Run the suite from a checkout.");
        return root;
    }
}
