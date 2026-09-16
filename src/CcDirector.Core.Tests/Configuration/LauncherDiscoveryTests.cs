using CcDirector.Core.Configuration;
using Xunit;

namespace CcDirector.Core.Tests.Configuration;

/// <summary>
/// The launcher presence fact (issue #330), read from the registration file the running launcher
/// writes: absent file = NOT INSTALLED (a valid fact, never an error), present file = installed +
/// the writing process's identity, and a present but unreadable file must say so honestly instead
/// of masquerading as "not installed". Liveness is a separate question from presence: a file whose
/// pid is dead is a crashed launcher's leftover, and <see cref="LauncherDiscovery.IsRunning"/> is
/// what tells the two apart.
/// </summary>
public sealed class LauncherDiscoveryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "launcher-fact-tests", Guid.NewGuid().ToString("N"));

    public LauncherDiscoveryTests()
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
    public void Read_FileAbsent_ReportsNotInstalled_NoError()
    {
        var fact = LauncherDiscovery.Read(Path.Combine(_dir, "does-not-exist.json"));

        Assert.False(fact.Installed);
        Assert.Null(fact.Pid);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Read_FilePresentWithPid_ReportsInstalledAndIdentity()
    {
        var path = PathFor("""{ "pid": 1234, "version": "1.9.8", "userInterface": "tray" }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Equal(1234, fact.Pid);
        Assert.Equal("1.9.8", fact.Version);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Read_PidKeyIsCaseInsensitive()
    {
        var path = PathFor("""{ "Pid": 4321 }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Equal(4321, fact.Pid);
    }

    [Fact]
    public void Read_CorruptJson_ReportsInstalledWithError_NeverNotInstalled()
    {
        var path = PathFor("{ torn-by-power-loss");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed); // the file existing IS the presence fact
        Assert.Null(fact.Pid);
        Assert.NotNull(fact.Error);
        Assert.Contains("unparsable", fact.Error);
    }

    [Fact]
    public void Read_NoPidField_ReportsInstalledWithError()
    {
        var path = PathFor("""{ "version": "1.9.8" }""");

        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Null(fact.Pid);
        Assert.Equal("1.9.8", fact.Version);
        Assert.Contains("no pid field", fact.Error);
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheCurrentProcess()
    {
        var path = Path.Combine(_dir, "launcher.json");

        LauncherDiscovery.Write("1.2.3+abcdef", "tray",
            autostartChecked: true, autostartRegistered: true, autostartFailure: null, path);
        var fact = LauncherDiscovery.Read(path);

        Assert.True(fact.Installed);
        Assert.Equal(Environment.ProcessId, fact.Pid);
        Assert.Equal("1.2.3+abcdef", fact.Version);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Write_ThenRead_RoundTripsTheSignalsTheLauncherArmed()
    {
        // The fact that makes "can this launcher be told anything" answerable on a platform where
        // nothing can be asked directly. It travels as NAMES rather than a flag, so a reader can check
        // them against the names it computes for its own storage root - a launcher serving somebody
        // else's root is registered, alive, and completely unreachable.
        var path = Path.Combine(_dir, "launcher.json");

        LauncherDiscovery.Write("2.3.0", "tray",
            autostartChecked: true, autostartRegistered: true, autostartFailure: null, path,
            commandSignals: ["signal-shutdown-ROOTKEY", "signal-restart-director-ROOTKEY"]);
        var fact = LauncherDiscovery.Read(path);

        Assert.Equal(["signal-shutdown-ROOTKEY", "signal-restart-director-ROOTKEY"], fact.CommandSignals);
    }

    [Fact]
    public void Read_NoCommandSignalsField_IsEmpty_NotAnError()
    {
        // Every launcher built before this field existed. It is a real answer - that launcher declared
        // nothing - and it must read as an empty list rather than as a broken file, because "declares
        // nothing" is precisely the build the Director is entitled to replace.
        var fact = LauncherDiscovery.Read(PathFor("""{"pid": 4242, "version": "2.1.0"}"""));

        Assert.True(fact.Installed);
        Assert.Empty(fact.CommandSignals);
        Assert.Null(fact.Error);
    }

    [Fact]
    public void Read_MalformedCommandSignalEntries_AreSkipped_NotReadAsNames()
    {
        // A name nothing could ever match would read as a launcher serving a different root. Blank and
        // non-string entries are dropped; the real names beside them survive.
        var fact = LauncherDiscovery.Read(PathFor(
            """{"pid": 4242, "commandSignals": ["real-signal", "", 7, null, "  "]}"""));

        Assert.Equal(["real-signal"], fact.CommandSignals);
    }

    [Fact]
    public void IsRunning_OwnProcess_IsTrue()
    {
        var path = Path.Combine(_dir, "launcher.json");
        LauncherDiscovery.Write("1.0.0", "tray", false, false, null, path);

        Assert.True(LauncherDiscovery.IsRunning(LauncherDiscovery.Read(path)));
    }

    [Fact]
    public void IsRunning_DeadPid_IsFalse_StaleFileIsNotALiveLauncher()
    {
        // A pid that is PROVABLY dead: start a real short-lived process, wait for it to exit, and use
        // its id - never a guessed number, which some other process could genuinely hold.
        int deadPid;
        var psi = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo("cmd.exe", "/c exit") { CreateNoWindow = true, UseShellExecute = false }
            : new System.Diagnostics.ProcessStartInfo("/bin/sh", "-c true") { UseShellExecute = false };
        using (var p = System.Diagnostics.Process.Start(psi)!)
        {
            p.WaitForExit(10_000);
            deadPid = p.Id;
        }
        var path = PathFor($$"""{ "pid": {{deadPid}}, "version": "1.0.0" }""");

        Assert.False(LauncherDiscovery.IsRunning(LauncherDiscovery.Read(path)));
    }

    [Fact]
    public void IsRunning_NoReadablePid_IsFalse_UncheckableIdentityIsNotHealth()
    {
        var path = PathFor("{ torn-by-power-loss");

        Assert.False(LauncherDiscovery.IsRunning(LauncherDiscovery.Read(path)));
    }

    // The registration file is where the fleet can tell a MANAGED launcher from an unmanaged one -
    // the invariant that used to live on /healthz (a Mac once ran for hours as an unmanageable orphan
    // reporting perfect health, because its failed autostart registration was invisible). Pinned at
    // the JSON level: undecided must read as NULL, never as "ok" - saying "ok" about a question
    // nobody has asked yet is the same lie the field exists to remove.
    [Fact]
    public void Write_AutostartUndecided_RecordsNullNotOk()
    {
        var path = Path.Combine(_dir, "launcher.json");
        LauncherDiscovery.Write("1.0.0", "tray",
            autostartChecked: false, autostartRegistered: false, autostartFailure: null, path);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("autostartOk").ValueKind);
        Assert.Equal(System.Text.Json.JsonValueKind.Null, doc.RootElement.GetProperty("autostartRegistered").ValueKind);
    }

    [Fact]
    public void Write_AutostartFailed_RecordsTheFailureVisibly()
    {
        var path = Path.Combine(_dir, "launcher.json");
        LauncherDiscovery.Write("1.0.0", "degraded",
            autostartChecked: true, autostartRegistered: false,
            autostartFailure: "the autostart Run key is not present after registering", path);

        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
        Assert.False(doc.RootElement.GetProperty("autostartOk").GetBoolean());
        Assert.False(doc.RootElement.GetProperty("autostartRegistered").GetBoolean());
        Assert.Contains("Run key", doc.RootElement.GetProperty("autostartFailure").GetString());
        Assert.Equal("degraded", doc.RootElement.GetProperty("userInterface").GetString());
    }

    [Fact]
    public void Delete_RemovesTheRegistration()
    {
        var path = Path.Combine(_dir, "launcher.json");
        LauncherDiscovery.Write("1.0.0", "tray", false, false, null, path);

        LauncherDiscovery.Delete(path);

        Assert.False(LauncherDiscovery.Read(path).Installed);
    }
}
