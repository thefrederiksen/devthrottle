using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

public class ExecutableResolverTests
{
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_BlankCommand_ReturnsNull(string command)
    {
        Assert.Null(ExecutableResolver.Resolve(command, searchPath: "C:\\Windows", pathExt: ".EXE"));
    }

    [Fact]
    public void Resolve_BareNameNotOnPath_ReturnsNull()
    {
        var result = ExecutableResolver.Resolve(
            "definitely-not-a-real-command-xyz",
            searchPath: "C:\\Windows;C:\\Windows\\System32",
            pathExt: ".EXE;.CMD");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_ExplicitExistingFile_ReturnsFullPath()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-exe-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "tool.exe");
            File.WriteAllText(exe, "stub");

            var result = ExecutableResolver.Resolve(exe, searchPath: null, pathExt: ".EXE");

            Assert.Equal(Path.GetFullPath(exe), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_ExplicitMissingFile_ReturnsNull()
    {
        var missing = Path.Combine(Path.GetTempPath(), "cc-missing-" + Guid.NewGuid().ToString("N"), "nope.exe");

        var result = ExecutableResolver.Resolve(missing, searchPath: null, pathExt: ".EXE");

        Assert.Null(result);
    }

    [Fact]
    public void Resolve_BareNameOnPath_AppliesPathExt()
    {
        // Mirrors the real scenario: a CLI installed as 'opencode.cmd' must resolve from
        // the bare name 'opencode' by trying PATHEXT extensions.
        var dir = Path.Combine(Path.GetTempPath(), "cc-exe-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var cmd = Path.Combine(dir, "opencode.cmd");
            File.WriteAllText(cmd, "@echo stub");

            var result = ExecutableResolver.Resolve("opencode", searchPath: dir, pathExt: ".EXE;.CMD");

            // PATHEXT supplies the extension casing (".CMD"); Windows paths are case-insensitive.
            Assert.Equal(Path.GetFullPath(cmd), result, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_BareNameWithExplicitExtension_ResolvesExactFile()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-exe-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "tool.exe");
            File.WriteAllText(exe, "stub");

            var result = ExecutableResolver.Resolve("tool.exe", searchPath: dir, pathExt: ".EXE");

            Assert.Equal(Path.GetFullPath(exe), result);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Resolve_SkipsBlankPathEntries()
    {
        var dir = Path.Combine(Path.GetTempPath(), "cc-exe-resolve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var exe = Path.Combine(dir, "tool.exe");
            File.WriteAllText(exe, "stub");

            // Empty segments around the real directory must not derail the search.
            var result = ExecutableResolver.Resolve("tool", searchPath: $";;{dir};", pathExt: ".EXE");

            Assert.Equal(Path.GetFullPath(exe), result, ignoreCase: true);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
