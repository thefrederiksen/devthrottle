using CcDirector.Engine.Jobs;
using Xunit;

namespace CcDirector.Engine.Tests.Jobs;

/// <summary>
/// The process half the factory triggers borrow: the exit code, the standard error kept apart from the output, the
/// timeout, and the shell chosen for the operating system.
/// </summary>
[Collection("RealProcesses")]
public sealed class ProcessJobTests
{

    [Fact]
    public void ShellFor_UsesTheShellOfThisOperatingSystem()
    {
        var info = ProcessJob.ShellFor("echo hi");

        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("cmd.exe", info.FileName);
            Assert.Equal("/c echo hi", info.Arguments);
        }
        else
        {
            Assert.Equal("/bin/sh", info.FileName);
            Assert.Equal(new[] { "-c", "echo hi" }, info.ArgumentList);
        }
    }
}
