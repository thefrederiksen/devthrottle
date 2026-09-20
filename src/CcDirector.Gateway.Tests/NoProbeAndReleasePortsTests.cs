using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// No test in the two Gateway test projects may obtain a port by opening a listener, reading the assigned
/// number, and then CLOSING it before the port is used (issue #1156).
///
/// WHY THIS IS PINNED AS SOURCE TEXT. The defect is not a wrong value - the port returned by a
/// probe-and-release helper is perfectly valid at the instant it is read. The defect is the WINDOW after it
/// is released, during which any other process on the machine can bind it. No assertion on the returned port
/// can detect that, because the port is correct until somebody else takes it, and whether anybody does is a
/// race. So the pattern itself is what has to be forbidden, and the only place to forbid it is the source.
///
/// This mattered enough to be worth a guard: probe-and-release was the mechanism behind the historical
/// cross-run collisions in this suite, and it is an easy pattern to reintroduce because it looks careful -
/// it asks the operating system for a free port rather than hardcoding one. The safe replacement is
/// <see cref="DeadPortReservation"/>, which HOLDS the port for as long as the address must stay dead.
///
/// The single permitted exception is the explicit-port Kestrel contract test, which must pass a real number
/// to a real listener to prove the explicit-port path works at all. It is named here so the exemption is a
/// deliberate, visible decision rather than a hole anyone can widen.
///
/// TWO DIRECTORIES, NOT ONE. HostedImagePublishedArtifactTests moved to its own project
/// (CcDirector.Gateway.HostedImage.Tests) on 19 September 2026 so the hosted deploy could run it instead of
/// continuous integration, and it is one of the DeadPortReservation callers. A guard that scans only the
/// directory it happens to live in stops covering whatever moves out of it, silently - so both directories
/// are named, and a named directory that is missing or that yields no files fails this test rather than
/// quietly shrinking what it looked at.
/// </summary>
public sealed class NoProbeAndReleasePortsTests
{
    /// <summary>
    /// The one file allowed to hand a probed port to a real bind: it exists to prove that GatewayHost
    /// honours an explicitly chosen port, which cannot be demonstrated without one.
    /// </summary>
    private static readonly string[] AllowedFiles = ["GatewayHostAssignedPortTests.cs"];

    /// <summary>Every directory whose tests may reach for a port. Both hold DeadPortReservation callers.</summary>
    private static readonly string[] ScannedProjectDirectories =
    [
        "CcDirector.Gateway.Tests",
        "CcDirector.Gateway.HostedImage.Tests",
    ];

    [Fact]
    public void No_test_probes_a_port_and_releases_it_before_use()
    {
        var offenders = new List<string>();
        var scanned = 0;

        foreach (var projectDirectory in ScannedProjectDirectories)
        {
            var testsDir = Path.Combine(RepoRoot(), "src", projectDirectory);

            // An absent directory is a BROKEN INSTRUMENT, not a clean result: EnumerateFiles on a missing
            // path would throw, and catching it would turn "I could not look" into "I found nothing".
            Assert.True(Directory.Exists(testsDir),
                $"{projectDirectory} does not exist, so this guard cannot scan it. If the project was "
                + "renamed or merged away, change the list above - do not leave a name here that reads as "
                + "covered while nothing is read.");

            var filesHere = 0;
            foreach (var file in Directory.EnumerateFiles(testsDir, "*.cs", SearchOption.AllDirectories))
            {
                filesHere++;
                var name = Path.GetFileName(file);
                if (AllowedFiles.Contains(name)) continue;
                if (name == nameof(NoProbeAndReleasePortsTests) + ".cs") continue;
                if (name == nameof(DeadPortReservation) + ".cs") continue;

                var text = File.ReadAllText(file);

                // The shape: a TcpListener is created, then Stop()/Dispose() is called on it. A test that
                // needs a listener for real work keeps it running; one that stops it is releasing the port
                // to get a number, which is the pattern being banned.
                if (!text.Contains("new TcpListener(", StringComparison.Ordinal)) continue;
                if (Regex.IsMatch(text, @"\.Stop\(\)\s*;", RegexOptions.None, TimeSpan.FromSeconds(5))
                    || text.Contains("using var l = new TcpListener(", StringComparison.Ordinal))
                {
                    offenders.Add(name);
                }
            }

            // A directory that yields no source files at all was not really read either.
            Assert.True(filesHere > 0,
                $"{projectDirectory} yielded no C# files, so nothing in it was checked. An empty sweep "
                + "reads exactly like a clean one, which is why it fails here instead.");
            scanned += filesHere;
        }

        // The count is quoted so the record shows how far the sweep reached, not just that it ran.
        Assert.True(scanned > 0, "No source file was scanned at all.");

        Assert.True(offenders.Count == 0,
            "These files obtain a port from a TcpListener and then release it before the port is used. "
            + "That port is unowned from the moment it is released, so another process - notably a second "
            + "run of this suite - can bind it and turn an 'unreachable' assertion into a conversation with "
            + "somebody else's listener. Use DeadPortReservation, which holds the port for as long as the "
            + "address must stay dead: " + string.Join(", ", offenders));
    }

    /// <summary>The repository root, located from this source file's own path - the tests always run
    /// from a checkout, and bin-relative paths would break under different runners.</summary>
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string thisFile = "")
    {
        // this file: <repo>/src/CcDirector.Gateway.Tests/NoProbeAndReleasePortsTests.cs
        var dir = Path.GetDirectoryName(thisFile)!;
        return Path.GetFullPath(Path.Combine(dir, "..", ".."));
    }
}
