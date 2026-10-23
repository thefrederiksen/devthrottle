using System.Reflection;
using System.Text.RegularExpressions;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using Xunit;

namespace CcDirector.Core.UnitTests.Drivers;

/// <summary>
/// DRIVERS SAY WHAT TO TYPE; THE SESSION ALONE TYPES IT (issue #4380). A driver that wrote to the terminal itself skipped
/// every check the Session's send path makes - the one-send-at-a-time gate, the look before typing, the proof after the
/// Enter - and that is how /clear and /compact could be typed into an open menu. These fail the moment a driver writes to
/// a backend again, outside the explicit list below.
/// </summary>
public sealed class DriverWritesNothingTests
{
    /// <summary>
    /// THE ALLOWLIST: the driver members that may take a terminal, and why. Only <c>SubmitAsync</c> - the prompt submit
    /// of a host that has no Session (the hosted agent and the ask runner), which goes through the shared echo-verified
    /// <see cref="TerminalSubmit"/> rather than writing to the backend itself. Adding to this list is a design decision,
    /// not a way to make the test pass.
    /// </summary>
    private static readonly Dictionary<string, string> MayTakeATerminal = new()
    {
        ["SubmitAsync"] = "a Session-less host's prompt submit, through TerminalSubmit",
    };

    /// <summary>A direct write to a terminal: a call of Write, SendTextAsync or SendEnterAsync on anything but the
    /// log.</summary>
    private static readonly Regex DirectWrite = new(@"(?<!FileLog)\.(Write|SendTextAsync|SendEnterAsync)\(", RegexOptions.CultureInvariant);

    private static IEnumerable<Type> DriverTypes() =>
        typeof(IAgentDriver).Assembly.GetTypes()
            .Where(t => typeof(IAgentDriver).IsAssignableFrom(t) && t is { IsClass: true, IsAbstract: false });

    [Fact]
    public void Drivers_ExistToBeChecked()
    {
        // A check whose pass is an absence must first prove it looked at something.
        Assert.Contains(typeof(ClaudeDriver), DriverTypes());
        Assert.Contains(typeof(CodexDriver), DriverTypes());
        Assert.Contains(typeof(PiDriver), DriverTypes());
        Assert.True(DriverTypes().Count() >= 6, $"found only {DriverTypes().Count()} driver types");
    }

    [Fact]
    public void DriverMembers_TakeNoTerminal_OutsideTheAllowlist()
    {
        var offenders = DriverTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.GetParameters().Any(p => typeof(ISessionBackend).IsAssignableFrom(p.ParameterType)))
                .Where(m => !MayTakeATerminal.ContainsKey(m.Name))
                .Select(m => $"{t.Name}.{m.Name}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "A driver says what to type and the Session types it (issue #4380); these driver members take a terminal: " +
            string.Join(", ", offenders));
    }

    [Fact]
    public void DriverSources_WriteToNoBackendDirectly()
    {
        var folder = Path.Combine(RepoRoot(), "src", "CcDirector.Core", "Drivers");
        var driverFiles = DriverTypes().Select(t => Path.Combine(folder, t.Name + ".cs")).ToList();
        Assert.All(driverFiles, f => Assert.True(File.Exists(f), $"no source file for a driver at {f}"));

        var offenders = new List<string>();
        foreach (var file in driverFiles)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var code = lines[i].TrimStart();
                if (code.StartsWith("//", StringComparison.Ordinal)) continue;
                if (DirectWrite.IsMatch(code))
                    offenders.Add($"{Path.GetFileName(file)}:{i + 1}: {code}");
            }
        }

        Assert.True(offenders.Count == 0,
            "A driver writes to a terminal directly; it must return a DriverInput for the Session to type (issue #4380):" +
            Environment.NewLine + string.Join(Environment.NewLine, offenders));
    }

    [Fact]
    public void DirectWrite_RecognisesTheWritesItForbids()
    {
        // The instrument is checked too: a pattern that matched nothing would pass every driver.
        Assert.Matches(DirectWrite, "backend.Write(EscapeByte);");
        Assert.Matches(DirectWrite, "return backend.SendTextAsync(\"/clear\");");
        Assert.Matches(DirectWrite, "await backend.SendEnterAsync();");
        Assert.DoesNotMatch(DirectWrite, "FileLog.Write(\"[ClaudeDriver] Cancel\");");
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find the repository root from " + AppContext.BaseDirectory);
    }
}
