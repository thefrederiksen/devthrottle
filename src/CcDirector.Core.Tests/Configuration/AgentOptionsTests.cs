using CcDirector.Core.Configuration;
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
}
