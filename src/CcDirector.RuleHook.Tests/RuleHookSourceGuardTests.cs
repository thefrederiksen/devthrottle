using System.Xml.Linq;
using Xunit;

namespace CcDirector.RuleHook.Tests;

/// <summary>
/// Guards on what the hook is ALLOWED to be, read from its source. Each one is a ruling that a later
/// change could quietly undo, and none of them would fail a behaviour test if it were undone:
/// - P10: anything on standard output from a PreToolUse hook is read by Claude Code, so the hook never
///   writes there.
/// - P11: no project references, so it never drags the Director in and stays fast to start.
/// - The remove-the-network-port mission: no network call in a hook, ever.
/// </summary>
public sealed class RuleHookSourceGuardTests
{
    private static string ProjectDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return Path.Combine(dir!.FullName, "src", "CcDirector.RuleHook");
    }

    private static IReadOnlyList<(string Name, string Text)> Sources()
    {
        var files = Directory.GetFiles(ProjectDirectory(), "*.cs", SearchOption.TopDirectoryOnly)
            .Select(f => (Path.GetFileName(f), File.ReadAllText(f)))
            .ToList();
        // A presence, not an absence: the guards below mean nothing if they read no source at all.
        Assert.Contains(files, f => f.Item1 == "HookRunner.cs");
        Assert.Contains(files, f => f.Item1 == "RuleResolver.cs");
        return files;
    }

    [Theory]
    [InlineData("Console.Out")]
    [InlineData("Console.Write")]
    [InlineData("OpenStandardOutput")]
    public void RuleHookSource_NeverWritesStandardOutput(string forbidden)
    {
        foreach (var (name, text) in Sources())
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{name} contains {forbidden}");
    }

    [Theory]
    [InlineData("System.Net")]
    [InlineData("HttpClient")]
    [InlineData("Socket")]
    [InlineData("http://")]
    [InlineData("https://")]
    public void RuleHookSource_HoldsNothingThatCallsTheNetwork(string forbidden)
    {
        foreach (var (name, text) in Sources())
            Assert.False(text.Contains(forbidden, StringComparison.Ordinal), $"{name} contains {forbidden}");
    }

    /// <summary>
    /// The cryptographic random generator loads OpenSSL on Linux (libssl, libcrypto and openssl.cnf),
    /// which measured at about ten milliseconds on every command a rule matched - for eight characters
    /// whose only job is to keep two decisions in the same tick apart. Nothing in the hook is a secret.
    /// </summary>
    [Fact]
    public void RuleHookSource_NeverLoadsTheCryptographyLibrary()
    {
        foreach (var (name, text) in Sources())
            Assert.False(text.Contains("System.Security.Cryptography", StringComparison.Ordinal), $"{name} uses System.Security.Cryptography");
    }

    [Fact]
    public void RuleHookProject_HasNoProjectReferencesAndIsPublishedAheadOfTime()
    {
        var project = XDocument.Load(Path.Combine(ProjectDirectory(), "CcDirector.RuleHook.csproj"));

        Assert.Empty(project.Descendants("ProjectReference"));
        Assert.Empty(project.Descendants("PackageReference"));
        Assert.Equal("cc-rule-hook", project.Descendants("AssemblyName").Single().Value);
        Assert.Equal("true", project.Descendants("PublishAot").Single().Value);
        Assert.Equal("false", project.Descendants("JsonSerializerIsReflectionEnabledByDefault").Single().Value);
    }
}
