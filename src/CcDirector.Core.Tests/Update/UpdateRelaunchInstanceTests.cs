using System.Diagnostics;
using CcDirector.Core.Update;
using Xunit;

namespace CcDirector.Core.Tests.Update;

/// <summary>
/// An update must hand the freshly-installed build a CLEAN environment.
///
/// A running Director sets CC_DIRECTOR_ROOT to its own instance home. The relaunched build resolves
/// the machine-wide root from that same variable, so inheriting it makes the new process treat its
/// own home as the machine root and settle one level deeper - a brand-new, empty data tree with no
/// settings, no sessions, and the first-run wizard waiting. To the user that reads as "the update
/// wiped my Director". The instance therefore travels as an explicit --instance argument.
///
/// These assert the decision (<see cref="UpdateInstaller.BuildRelaunchStartInfo"/>), which is what
/// both the post-update relaunch and the post-rollback relaunch start from.
/// </summary>
[Collection("ProcessWideState")]
public class UpdateRelaunchInstanceTests
{
    [Fact]
    public void BuildRelaunchStartInfo_DropsTheInheritedInstanceRoot()
    {
        var old = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", @"C:\Users\someone\AppData\Local\cc-director\instances\default");
        try
        {
            var psi = UpdateInstaller.BuildRelaunchStartInfo(@"C:\install\cc-director.exe", "default");

            Assert.False(psi.Environment.ContainsKey("CC_DIRECTOR_ROOT"));

            // The scrub only works on a direct start - a shell-executed process cannot be given an
            // edited environment block.
            Assert.False(psi.UseShellExecute);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", old);
        }
    }

    [Fact]
    public void BuildRelaunchStartInfo_CarriesTheInstanceExplicitly()
    {
        var psi = UpdateInstaller.BuildRelaunchStartInfo(@"C:\install\cc-director.exe", "work");

        var args = string.Join(' ', psi.ArgumentList);
        Assert.Contains("--instance", args);
        Assert.Contains("work", args);
    }

    [Fact]
    public void BuildRelaunchStartInfo_WithNoInstance_PassesNoInstanceArgument()
    {
        // An update launched by a build older than the instance argument sends no slug; the new
        // process then resolves the default instance itself rather than being told a wrong one.
        var psi = UpdateInstaller.BuildRelaunchStartInfo(@"C:\install\cc-director.exe", null);

        Assert.DoesNotContain("--instance", string.Join(' ', psi.ArgumentList));
    }
}
