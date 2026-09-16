using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>The registration reading's IDENTITY rules (issue #2042): liveness alone never certifies an
/// install when the expected version is known, and an unreadable identity never reads as health.</summary>
public class LauncherHealthProbeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-probe-tests", Guid.NewGuid().ToString("N"));

    public LauncherHealthProbeTests()
    {
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private string PathFor(string content)
    {
        var path = Path.Combine(_dir, "launcher.json");
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void ReadRegistration_WellFormedFile_ReadsIdentity()
    {
        var h = LauncherHealthProbe.ReadRegistration(
            PathFor("""{"pid":77,"version":"1.7.4+abc","startedAtUtc":"2026-08-03T00:00:00Z"}"""),
            processIsAlive: _ => true);

        Assert.NotNull(h);
        Assert.True(h!.Ok);
        Assert.Equal("1.7.4+abc", h.Version);
        Assert.Equal(77, h.Pid);
    }

    [Fact]
    public void ReadRegistration_AbsentFile_IsNull()
    {
        Assert.Null(LauncherHealthProbe.ReadRegistration(Path.Combine(_dir, "does-not-exist.json")));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"version\":\"1.7.4\"}")]   // present but no pid: identity unreadable
    public void ReadRegistration_GarbageOrNoPid_NeverOk(string body)
    {
        var h = LauncherHealthProbe.ReadRegistration(PathFor(body), processIsAlive: _ => true);
        Assert.NotNull(h);       // the file existing is still an observation...
        Assert.False(h!.Ok);     // ...but an unreadable identity must never read as health
    }

    [Fact]
    public void ReadRegistration_DeadPid_IsNotOk_ACrashLeftoverIsNotALiveLauncher()
    {
        var h = LauncherHealthProbe.ReadRegistration(PathFor("""{"pid":77,"version":"1.7.4"}"""), processIsAlive: _ => false);
        Assert.NotNull(h);
        Assert.False(h!.Ok);
        Assert.Equal(77, h.Pid);
    }

    [Theory]
    [InlineData("1.7.4", "1.7.4", true)]
    [InlineData("1.7.4", "1.7.4+d26a09", true)]     // build metadata ignored
    [InlineData("v1.7.4", "1.7.4.0", true)]          // normalized forms match
    [InlineData("1.7.4", "1.7.1", false)]            // the issue #2042 incident shape
    [InlineData("1.7.4", null, false)]               // expected but nothing reported
    [InlineData(null, "9.9.9", true)]                // nothing expected: cannot check, accept
    public void VersionMatches_ComparesNormalizedAndIgnoresBuildMetadata(string? expected, string? reported, bool matches) =>
        Assert.Equal(matches, LauncherHealthProbe.VersionMatches(expected, reported));

    [Fact]
    public void Certifies_RequiresOkAndIdentity()
    {
        Assert.True(LauncherHealthProbe.Certifies(new LauncherHealth(true, "1.7.4", 1, []), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(new LauncherHealth(true, "1.7.1", 1, []), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(new LauncherHealth(false, "1.7.4", 1, []), "1.7.4"));
        Assert.False(LauncherHealthProbe.Certifies(null, "1.7.4"));
    }
}
