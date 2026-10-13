using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class GatewayAutostartTests
{
    [Fact]
    public void CommandLine_NoArguments_QuotesExeOnly()
    {
        Assert.Equal(
            "\"C:\\Users\\example\\AppData\\Local\\cc-director\\gateway\\devthrottle-gateway.exe\"",
            GatewayAutostart.CommandLine(@"C:\Users\example\AppData\Local\cc-director\gateway\devthrottle-gateway.exe"));
    }

    [Fact]
    public void CommandLine_WithArguments_AppendsThem()
    {
        Assert.Equal(
            "\"C:\\x\\devthrottle-gateway.exe\" --managed",
            GatewayAutostart.CommandLine(@"C:\x\devthrottle-gateway.exe", "--managed"));
    }

    [Fact]
    public void CommandLine_BlankArguments_TreatedAsNone()
    {
        Assert.Equal("\"C:\\x\\g.exe\"", GatewayAutostart.CommandLine(@"C:\x\g.exe", "  "));
    }

    [Fact]
    public void ValueName_IsStable()
    {
        // The Run-key value name is an on-machine contract (installer writes it, uninstaller
        // removes it, the app re-asserts it). Lock it against accidental rename.
        Assert.Equal("CcDirectorGateway", GatewayAutostart.ValueName);
    }
}

public class GatewayTrayInstallerTests
{
    [Fact]
    public void InstalledArguments_IsManaged()
    {
        // The installed tray app must run managed (self-update on); the relauncher and the autostart
        // Run key both reuse this constant.
        Assert.Equal("--managed", GatewayTrayInstaller.InstalledArguments);
    }

    [Fact]
    public void DefaultPorts_AreCanonical()
    {
        Assert.Equal(7878, GatewayTrayInstaller.GatewayDefaultPort);
    }

    // D2a at the PRODUCTION SEAM: run the real GatewayTrayInstaller.InstallAsync in a no-key
    // environment and prove it neither demands nor writes an OPENAI_API_KEY. With the Gateway exe
    // present, control runs PAST the point where the old key demand/write lived and fails later (here,
    // at sidecar extraction because the offline release carries no download URL) - NOT with a key
    // demand, and without touching the User-scope OPENAI_API_KEY.
    //
    // Revert-proof: re-insert an executable OPENAI_API_KEY demand in the REAL
    // GatewayTrayInstaller.InstallAsync (right after the exe-present check, GatewayTrayInstaller.cs ~:56)
    // - e.g. Fail(...) when the key is blank -> the failure message becomes the key demand, so the
    // "does not contain OPENAI" assertion goes red.
    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task InstallAsync_NoOpenAiKey_RunsPastKeyRegion_NoDemandNoWrite()
    {
        if (!OperatingSystem.IsWindows()) return;

        var root = Directory.CreateTempSubdirectory("d2a-eng-").FullName;
        var savedProcessKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.Process);
        var userKeyBefore = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
        Environment.SetEnvironmentVariable("OPENAI_API_KEY", null, EnvironmentVariableTarget.Process);
        try
        {
            var layout = new InstallLayout(Path.Combine(root, "local"));
            Directory.CreateDirectory(layout.GatewayDir);
            // A file that exists (so the exe-present check passes) but is not a valid executable.
            File.WriteAllBytes(layout.PathFor(ComponentRegistry.Gateway), new byte[] { 0x00, 0x01, 0x02 });

            // Offline release: the mobile sidecar is in the manifest but has NO download URL, so
            // extraction fails fast with "No source for asset" - no network, and BEFORE any process
            // launch. The point is only that the failure is not a key demand.
            var assets = new Dictionary<string, ManifestAsset>
            {
                [MobilePackage.AssetName] = new ManifestAsset(MobilePackage.AssetName, "0.0.0-test", "", "windows", 0),
            };
            var release = new ResolvedRelease(
                new ReleaseManifest { Version = "0.0.0-test", Assets = assets },
                new Dictionary<string, string>());

            var result = await new GatewayTrayInstaller(layout).InstallAsync(release, new ReleaseSource());

            Assert.False(result.Success);
            Assert.DoesNotContain("OPENAI", result.Message, StringComparison.OrdinalIgnoreCase);
            // The installer never writes the User-scope OPENAI_API_KEY.
            Assert.Equal(userKeyBefore, Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", savedProcessKey, EnvironmentVariableTarget.Process);
            Directory.Delete(root, recursive: true);
        }
    }

    // --- Issue #175: the tray launch must NOT inherit the caller's stdio ---------------------------
    // When step 4 launched the long-lived tray Gateway with UseShellExecute=false, the Gateway
    // inherited the setup CLI's stdout handle and held it open for its whole lifetime, so any caller
    // that PIPED the CLI ("... | Select-Object", "... | tail") hung forever after the CLI exited.

    [Fact]
    public void BuildTrayLaunchInfo_UsesShellExecute_NoRedirection()
    {
        var exe = OperatingSystem.IsWindows() ? @"C:\some\devthrottle-gateway.exe" : "/some/devthrottle-gateway";
        var dir = OperatingSystem.IsWindows() ? @"C:\some" : "/some";

        var psi = GatewayTrayInstaller.BuildTrayLaunchInfo(exe, dir);

        // The fix: UseShellExecute=true so the launched Gateway does NOT inherit the caller's stdio.
        Assert.True(psi.UseShellExecute);
        // UseShellExecute=true is incompatible with redirection; assert none is requested so the
        // Gateway never receives an inherited/redirected std handle.
        Assert.False(psi.RedirectStandardOutput);
        Assert.False(psi.RedirectStandardError);
        Assert.False(psi.RedirectStandardInput);
        Assert.Equal(GatewayTrayInstaller.InstalledArguments, psi.Arguments);
        Assert.Equal(exe, psi.FileName);
        Assert.Equal(dir, psi.WorkingDirectory);
    }
}

public class UpdatePinsTests
{
    [Fact]
    public void Pin_BlocksMatchingVersion()
    {
        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "1.2.0");

        Assert.True(pins.IsPinned("cc-pdf", "1.2.0"));
        Assert.True(pins.IsPinned("cc-pdf", "v1.2.0"));   // normalized compare
        Assert.False(pins.IsPinned("cc-pdf", "1.3.0"));   // a newer version is not pinned
        Assert.False(pins.IsPinned("cc-html", "1.2.0"));  // different component
    }

    [Fact]
    public void JsonRoundTrip_PreservesPins()
    {
        var pins = new UpdatePins();
        pins.Pin("director", "0.4.0");
        pins.Pin("cc-pdf", "1.2.0");

        var restored = UpdatePins.FromJson(pins.ToJson());

        Assert.True(restored.IsPinned("director", "0.4.0"));
        Assert.True(restored.IsPinned("cc-pdf", "1.2.0"));
    }

    [Fact]
    public void Clear_RemovesPin()
    {
        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "1.2.0");
        pins.Clear("cc-pdf");
        Assert.False(pins.IsPinned("cc-pdf", "1.2.0"));
    }

    [Fact]
    public void FromJson_EmptyOrNull_IsEmpty()
    {
        Assert.False(UpdatePins.FromJson(null).IsPinned("x", "1.0.0"));
        Assert.False(UpdatePins.FromJson("").IsPinned("x", "1.0.0"));
    }
}
