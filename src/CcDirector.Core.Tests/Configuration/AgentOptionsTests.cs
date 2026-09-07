using System.Linq;
using CcDirector.Core.AgentPlugins;
using CcDirector.Core.Configuration;
using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

public class AgentOptionsTests
{
    /// <summary>
    /// Codex ships as a standalone native exe (on PATH) far more commonly than as the npm
    /// codex.cmd shim. Hard-coding the npm path made Codex sessions fail to launch for
    /// standalone-installer users (the path did not exist, so cmd.exe reported "not recognized").
    /// The default must be a bare command name so ExecutableResolver finds whichever install is
    /// present via PATH, exactly like opencode/grok/cursor.
    /// </summary>
    [Fact]
    public void CodexPath_DefaultsToBareCommand_ForPathResolution()
    {
        var codexPath = new AgentOptions().CodexPath;

        Assert.Equal("codex", codexPath);
        Assert.DoesNotContain(Path.DirectorySeparatorChar, codexPath);
        Assert.DoesNotContain(Path.AltDirectorySeparatorChar, codexPath);
        Assert.False(codexPath.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// THE SWEEP, and the reason it is a sweep rather than three assertions: pi, gemini and copilot
    /// each defaulted to an npm global <c>.cmd</c> path built by a private helper copied into seven
    /// files. Codex had the identical defect and was fixed alone; the other three kept it for as
    /// long as nobody looked. Asserting the property of EVERY REGISTERED AGENT - rather than naming
    /// the three that were wrong today - is what makes a fourth one fail here instead of failing on
    /// a user machine.
    ///
    /// It walks the plugin registry rather than reflecting over property names. An earlier draft
    /// took every AgentOptions property ending in "Path" and immediately failed on
    /// ChatSessionRepoPath, which is a repository location and legitimately empty - an overbroad
    /// rule turns a check into a hazard. The registry is the product own list of agent executables,
    /// so it is both narrower and self-maintaining.
    ///
    /// A Windows batch path can never exist off Windows, and .NET maps ApplicationData to
    /// ~/.config, so the removed helper "fall back to the bare name" branch was unreachable on the
    /// only platform that needed it.
    /// </summary>
    [Fact]
    public void EveryRegisteredAgentPathDefault_IsABareCommandName_NotAWindowsScriptPath()
    {
        var options = new AgentOptions();
        var plugins = AgentPluginRegistry.BuiltIns;

        Assert.NotEmpty(plugins);

        foreach (var plugin in plugins)
        {
            var name = plugin.Kind.ToString();
            var value = plugin.Settings.GetConfiguredPath(options);

            Assert.False(string.IsNullOrWhiteSpace(value), name + " default must not be empty");
            Assert.False(value.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase),
                name + " defaults to a Windows .cmd path, which cannot exist off Windows: " + value);
            Assert.False(value.EndsWith(".bat", StringComparison.OrdinalIgnoreCase),
                name + " defaults to a Windows .bat path: " + value);
            Assert.DoesNotContain(Path.DirectorySeparatorChar, value);
            Assert.DoesNotContain(Path.AltDirectorySeparatorChar, value);
        }
    }

    [Theory]
    [InlineData("PiPath", "pi")]
    [InlineData("GeminiPath", "gemini")]
    [InlineData("CopilotPath", "copilot")]
    public void NpmInstalledAgents_DefaultToBareCommands(string propertyName, string expected)
    {
        var value = (string?)typeof(AgentOptions).GetProperty(propertyName)!.GetValue(new AgentOptions());

        Assert.Equal(expected, value);
    }

    /// <summary>
    /// The assumption the whole change rests on, tested on Windows rather than asserted: an agent
    /// installed by npm as <c>&lt;name&gt;.cmd</c> must still be found from the bare default, or
    /// this fix trades a broken Linux for a broken Windows - and Windows is the majority.
    ///
    /// It also pins the half that makes copilot safe. npm drops an extensionless bash shim beside
    /// the .cmd; CreateProcess cannot run it, so the resolver must choose the .cmd and not the shim.
    /// </summary>
    [Fact]
    public void OnWindows_BareAgentDefault_ResolvesToTheNpmCmdShim()
    {
        if (!OperatingSystem.IsWindows()) return;

        var dir = Path.Combine(Path.GetTempPath(), "cc-agent-default-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            foreach (var name in new[] { "pi", "gemini", "copilot" })
            {
                // What an npm global install actually leaves behind: both files, same directory.
                File.WriteAllText(Path.Combine(dir, name + ".cmd"), "@echo stub");
                File.WriteAllText(Path.Combine(dir, name), "sh shim, not launchable by CreateProcess");
            }

            var options = new AgentOptions();
            foreach (var (bare, expected) in new[]
                     {
                         (options.PiPath, "pi.cmd"),
                         (options.GeminiPath, "gemini.cmd"),
                         (options.CopilotPath, "copilot.cmd"),
                     })
            {
                var resolved = ExecutableResolver.Resolve(bare, searchPath: dir, pathExt: ".EXE;.CMD");

                Assert.Equal(Path.Combine(dir, expected), resolved, ignoreCase: true);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
