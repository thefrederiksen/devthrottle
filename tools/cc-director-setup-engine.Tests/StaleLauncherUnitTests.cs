using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Removing the broken launcher unit a branch build left behind (issue devthrottle_internal#2475). Every
/// systemctl call goes through a fake, so these run on every host and never touch the real user systemd.
/// That the removal stops a real crash loop is proven by a run on devlinux, recorded in the report.
/// </summary>
public sealed class StaleLauncherUnitTests : IDisposable
{
    private const string Name = StaleLauncherUnit.UnitName;

    private const string BrokenUnit = """
        [Unit]
        Description=DevThrottle Launcher
        PartOf=graphical-session.target
        After=graphical-session.target

        [Service]
        Type=simple
        ExecStart="/home/soren/.dotnet/dotnet"
        Restart=on-failure
        RestartSec=5
        KillMode=process

        [Install]
        WantedBy=graphical-session.target

        """;

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "StaleLauncherUnit_" + Guid.NewGuid().ToString("N"));
    private readonly string _unitPath;
    private readonly List<string> _calls = new();

    public StaleLauncherUnitTests()
    {
        Directory.CreateDirectory(_dir);
        _unitPath = Path.Combine(_dir, Name);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    /// <summary>A fake systemctl: reports the crash loop while the unit file exists, and "not-found" after.</summary>
    private (int, string) FakeSystemctl(string exe, string args)
    {
        _calls.Add($"{exe} {args}");
        if (args.Contains(" show ") && args.Contains("LoadState"))
            return File.Exists(_unitPath)
                ? (0, "LoadState=loaded\nActiveState=activating\n")
                : (0, "LoadState=not-found\nActiveState=inactive\n");
        if (args.Contains(" show "))
            return (0, "NRestarts=29190\nActiveState=activating\nSubState=auto-restart\n");
        return (0, "");
    }

    [Fact]
    public void Inspect_NoUnitFile_AsksSystemdNothing()
    {
        var finding = StaleLauncherUnit.Inspect(Name, _unitPath, FakeSystemctl);

        Assert.False(finding.Exists);
        Assert.Empty(_calls);
    }

    [Fact]
    public void Inspect_BrokenUnit_ReadsCommandAndCrashLoop()
    {
        File.WriteAllText(_unitPath, BrokenUnit);

        var finding = StaleLauncherUnit.Inspect(Name, _unitPath, FakeSystemctl);

        Assert.True(finding.Exists);
        Assert.Equal("\"/home/soren/.dotnet/dotnet\"", finding.ExecStart);
        Assert.Equal(29190, finding.Restarts);
        Assert.True(finding.IsBareRuntimeHost);
        Assert.True(finding.IsCrashLooping);
        Assert.Contains("only the runtime host", finding.Describe());
    }

    [Fact]
    public void Repair_BrokenUnit_DisablesDeletesReloadsAndCertifies()
    {
        File.WriteAllText(_unitPath, BrokenUnit);

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, FakeSystemctl);

        Assert.True(repair.Removed);
        Assert.False(repair.StillBroken);
        Assert.False(File.Exists(_unitPath));
        Assert.Contains($"systemctl --user disable --now {Name}", _calls);
        Assert.Contains("systemctl --user daemon-reload", _calls);
        Assert.Contains($"systemctl --user reset-failed {Name}", _calls);
        Assert.Contains("29190", repair.Message);
    }

    [Fact]
    public void Repair_DryRun_ChangesNothing()
    {
        File.WriteAllText(_unitPath, BrokenUnit);

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: true, FakeSystemctl);

        Assert.False(repair.Removed);
        Assert.True(repair.StillBroken);
        Assert.True(File.Exists(_unitPath));
        Assert.DoesNotContain(_calls, c => c.Contains("disable") || c.Contains("daemon-reload") || c.Contains("reset-failed"));
        Assert.Contains(repair.Steps, s => s.StartsWith("would delete:", StringComparison.Ordinal));
    }

    [Fact]
    public void Repair_UnitWithARealProgram_IsLeftInPlaceAndNamedWhenCrashLooping()
    {
        // Lean to keep: a unit with this name that runs a real program may be somebody's deliberate one.
        File.WriteAllText(_unitPath, BrokenUnit.Replace(
            "ExecStart=\"/home/soren/.dotnet/dotnet\"",
            "ExecStart=\"/home/soren/.local/share/cc-director/launcher/cc-launcher\" --managed"));

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, FakeSystemctl);

        Assert.False(repair.Removed);
        Assert.True(repair.StillBroken);
        Assert.True(File.Exists(_unitPath));
        Assert.Contains("crash-looping", repair.Message);
        Assert.DoesNotContain(_calls, c => c.Contains("disable"));
    }

    [Fact]
    public void Repair_NoUnit_NothingToDo()
    {
        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, FakeSystemctl);

        Assert.False(repair.Removed);
        Assert.False(repair.StillBroken);
        Assert.Contains("nothing to do", repair.Message);
    }

    [Fact]
    public void Repair_DisableFails_KeepsTheFileAndStops()
    {
        File.WriteAllText(_unitPath, BrokenUnit);
        (int, string) NoUserManager(string exe, string args)
        {
            _calls.Add($"{exe} {args}");
            return (1, "Failed to connect to bus: No medium found");
        }

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, NoUserManager);

        Assert.False(repair.Removed);
        Assert.True(repair.StillBroken);
        Assert.True(File.Exists(_unitPath));
        Assert.Contains("could not be stopped", repair.Message);
        Assert.DoesNotContain(_calls, c => c.Contains("daemon-reload"));
    }

    [Fact]
    public void Repair_UnitWithADropIn_IsLeftInPlace()
    {
        // "systemctl --user edit" fixes the unit with a drop-in that resets ExecStart; the file alone then
        // proves nothing about what systemd runs.
        File.WriteAllText(_unitPath, BrokenUnit);
        Directory.CreateDirectory(_unitPath + ".d");
        File.WriteAllText(Path.Combine(_unitPath + ".d", "override.conf"),
            "[Service]\nExecStart=\nExecStart=/opt/launcher/cc-launcher --managed\n");

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, FakeSystemctl);

        Assert.False(repair.Removed);
        Assert.True(File.Exists(_unitPath));
        Assert.Contains("drop-ins", repair.Message);
        Assert.DoesNotContain(_calls, c => c.Contains("disable"));
    }

    [Fact]
    public void Repair_SystemdStillKnowsTheUnit_IsReportedNotCertified()
    {
        File.WriteAllText(_unitPath, BrokenUnit);
        (int, string) Stubborn(string exe, string args)
        {
            _calls.Add($"{exe} {args}");
            if (args.Contains("LoadState")) return (0, "LoadState=loaded\nActiveState=activating\n");
            if (args.Contains(" show ")) return (0, "NRestarts=12\nActiveState=activating\nSubState=auto-restart\n");
            if (args.Contains("disable")) return (0, "");
            return (1, "Failed to connect to bus");
        }

        var repair = StaleLauncherUnit.Repair(Name, _unitPath, dryRun: false, Stubborn);

        Assert.True(repair.StillBroken);
        Assert.Contains("could not be certified removed", repair.Message);
    }

    [Theory]
    [InlineData(null, null, 0, false)]
    [InlineData("active", "running", 3, false)]
    [InlineData("activating", "auto-restart", 1, true)]
    [InlineData("failed", "failed", 5, true)]
    [InlineData("active", "running", 10, true)]
    public void IsCrashLooping_FollowsSystemdState(string? active, string? sub, int restarts, bool expected)
    {
        var finding = new UserUnitFinding(Name, _unitPath, true, "\"/opt/x\"", restarts, active, sub);

        Assert.Equal(expected, finding.IsCrashLooping);
    }
}
