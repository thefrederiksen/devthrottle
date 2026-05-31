using CcDirector.Core.Utilities;
using Xunit;

namespace CcDirector.Core.Tests.Utilities;

public class CommandLineLauncherTests
{
    [Fact]
    public void Build_Exe_PassesThroughUnchanged()
    {
        var (exe, args) = CommandLineLauncher.Build(@"C:\tools\opencode.exe", "--flag");

        Assert.Equal(@"C:\tools\opencode.exe", exe);
        Assert.Equal("--flag", args);
    }

    [Fact]
    public void Build_NullArgs_BecomesEmpty()
    {
        var (exe, args) = CommandLineLauncher.Build(@"C:\tools\opencode.exe", null);

        Assert.Equal(@"C:\tools\opencode.exe", exe);
        Assert.Equal(string.Empty, args);
    }

    [Fact]
    public void Build_CmdShim_WrapsThroughComSpec()
    {
        var (exe, args) = CommandLineLauncher.Build(@"C:\Users\me\AppData\Roaming\npm\opencode.cmd", "");

        // Should invoke a cmd.exe, not the .cmd directly.
        Assert.EndsWith("cmd.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/s /c \"\"C:\\Users\\me\\AppData\\Roaming\\npm\\opencode.cmd\"\"", args);
    }

    [Fact]
    public void Build_CmdShimWithArgs_QuotesProgramAndKeepsArgs()
    {
        var (exe, args) = CommandLineLauncher.Build(@"C:\path with space\tool.cmd", "--a --b");

        Assert.EndsWith("cmd.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("/s /c \"\"C:\\path with space\\tool.cmd\" --a --b\"", args);
    }

    [Fact]
    public void Build_BatShim_AlsoWrapped()
    {
        var (exe, args) = CommandLineLauncher.Build(@"C:\tools\legacy.bat", null);

        Assert.EndsWith("cmd.exe", exe, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("legacy.bat", args);
    }
}
