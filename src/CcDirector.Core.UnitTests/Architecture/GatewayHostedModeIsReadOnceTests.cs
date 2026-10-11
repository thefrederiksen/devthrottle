using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Architecture;

/// <summary>
/// THE HOSTED MODE IS READ ONCE, AND IT TRAVELS AS A VALUE.
///
/// A Gateway is hosted (many tenants, a public address, the shared machine token refused) or self-host (one
/// owner, a tailnet). For two years that decision was <c>GatewayHostedMode.IsHosted</c>: a static property
/// that read the process environment variable <c>CC_GATEWAY_HOSTED</c> every time it was asked, from more
/// than two hundred and fifty places in the Gateway and its tests. That made the mode a property of the
/// PROCESS rather than of a Gateway, and it is the first root cause of the serial Gateway test suite: two
/// hosts in one process could never be in different modes, so every test class that needed a mode set the
/// variable, and no two of them could run at the same time. It also meant a test that forgot to restore the
/// variable poisoned every test after it.
///
/// Now <c>GatewayHostOptions.FromEnvironment()</c> reads the variable exactly once, when a
/// <c>GatewayHost</c> is constructed, and every other reader takes the mode from the host as a REQUIRED
/// value: a constructor argument, a <c>Map</c> parameter, a required member, or the boundary it is handed.
/// The fail-closed discipline is kept because a required value cannot be omitted, where an optional one
/// could be forgotten and fall open.
///
/// This guard keeps it that way. It reads the Gateway SOURCE, not the compiled assembly, so it runs in the
/// default gate (a guard in a parked suite tells nobody anything at commit time) and so a comment that only
/// mentions the old reader does not count. It names the readers that are allowed, and it requires each of
/// them to be present: a check that passes on "found nothing" would certify a tree in which the files had
/// been renamed and the reads had moved somewhere it never looked.
/// </summary>
public sealed class GatewayHostedModeIsReadOnceTests
{
    /// <summary>
    /// The two sanctioned readers of <c>GatewayHostedMode.IsHosted</c>: the options factory that fixes the
    /// mode for one host at construction, and the process entry point, which picks the listen port before
    /// any host exists.
    /// </summary>
    private static readonly string[] AllowedIsHostedReaders =
    {
        "GatewayHostOptions.cs",
        "GatewayEntryPoint.cs",
    };

    /// <summary>
    /// The two sanctioned readers of the environment variable itself: the property that defines it, and the
    /// startup contract, whose whole job is to prove the hosted image booted with the variable set.
    /// </summary>
    private static readonly string[] AllowedEnvironmentVariableReaders =
    {
        "GatewayHostedMode.cs",
        "HostedStartupContract.cs",
    };

    [Fact]
    public void IsHosted_is_read_only_by_the_options_factory_and_the_entry_point()
    {
        var readers = FilesWhoseCodeMatches(new Regex(@"\bGatewayHostedMode\.IsHosted\b"));

        AssertExactly(AllowedIsHostedReaders, readers,
            "GatewayHostedMode.IsHosted is read at call time from the process environment. The mode is fixed " +
            "once, in GatewayHostOptions.FromEnvironment(), when a GatewayHost is constructed; everything else " +
            "takes it from the host as a required value (a constructor argument, a Map parameter, a required " +
            "member, or the HostedTenantBoundary it is handed). Pass the value in instead of reading the " +
            "switch - two hosts in one process must be able to run in different modes.");
    }

    [Fact]
    public void The_environment_variable_is_read_only_by_the_mode_property_and_the_startup_contract()
    {
        var readers = FilesWhoseCodeMatches(new Regex(@"GatewayHostedMode\.HostedEnvVar\b|""CC_GATEWAY_HOSTED"""));

        AssertExactly(AllowedEnvironmentVariableReaders, readers,
            "The hosted environment variable is read somewhere new. GatewayHostedMode.IsHosted is the one " +
            "property that reads it, GatewayHostOptions.FromEnvironment() is the one place that property " +
            "becomes a host's mode, and HostedStartupContract proves it was set at boot. Take the mode from " +
            "the host instead.");
    }

    private static void AssertExactly(string[] allowed, IReadOnlyCollection<string> actual, string why)
    {
        var missing = allowed.Where(a => !actual.Contains(a, StringComparer.Ordinal)).ToList();
        var extra = actual.Where(a => !allowed.Contains(a, StringComparer.Ordinal)).OrderBy(a => a, StringComparer.Ordinal).ToList();

        Assert.True(missing.Count == 0,
            "A sanctioned reader has gone missing, so this guard can no longer see what it is guarding:\n  " +
            string.Join("\n  ", missing) +
            "\n\nIf the file was renamed or the read moved, update the allow list in this test in the same " +
            "change, so the guard keeps naming the real readers.");

        Assert.True(extra.Count == 0,
            "These Gateway files read the hosted switch, and only " + string.Join(" and ", allowed) + " may:\n  " +
            string.Join("\n  ", extra) + "\n\n" + why);
    }

    /// <summary>
    /// Every Gateway source file whose CODE (comments stripped) matches <paramref name="pattern"/>, as a
    /// file name. A match inside a comment is allowed to say anything it likes about the old reader.
    /// </summary>
    private static IReadOnlyCollection<string> FilesWhoseCodeMatches(Regex pattern)
    {
        var gateway = Path.Combine(RepoRoot(), "src", "CcDirector.Gateway");
        var files = Directory.EnumerateFiles(gateway, "*.cs", SearchOption.AllDirectories)
            .Where(f => !IsBuildOutput(gateway, f))
            .ToList();
        Assert.True(files.Count > 100,
            $"Expected the Gateway source tree under {gateway}, found {files.Count} .cs files. A guard that " +
            "reads an empty tree has checked nothing.");

        var hits = new List<string>();
        foreach (var file in files)
        {
            var code = StripComments(File.ReadAllText(file));
            if (pattern.IsMatch(code))
                hits.Add(Path.GetFileName(file));
        }
        return hits;
    }

    private static bool IsBuildOutput(string root, string file)
    {
        var relative = Path.GetRelativePath(root, file);
        var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return string.Equals(first, "obj", StringComparison.OrdinalIgnoreCase)
            || string.Equals(first, "bin", StringComparison.OrdinalIgnoreCase);
    }

    private static readonly Regex BlockComment = new(@"/\*.*?\*/", RegexOptions.Singleline);
    private static readonly Regex LineComment = new(@"//[^\r\n]*");

    private static string StripComments(string source) =>
        LineComment.Replace(BlockComment.Replace(source, string.Empty), string.Empty);

    /// <summary>
    /// The repository root, walked up from the test binary. Fails loudly rather than passing over a missing
    /// tree: a guard whose pass condition is "found nothing to check" certifies a run that never happened.
    /// </summary>
    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Gateway", "GatewayHostOptions.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not find the repository root above " + AppContext.BaseDirectory +
            ". This guard reads the Gateway source in the checkout; without it there is nothing to check.");
    }
}
