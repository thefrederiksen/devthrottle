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
        // The installed tray app must run managed (supervise the Cockpit + self-update);
        // the relauncher and the autostart Run key both reuse this constant.
        Assert.Equal("--managed", GatewayTrayInstaller.InstalledArguments);
    }

    [Fact]
    public void DefaultPorts_AreCanonical()
    {
        Assert.Equal(7878, GatewayTrayInstaller.GatewayDefaultPort);
        Assert.Equal(7470, GatewayTrayInstaller.CockpitDefaultPort);
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

    /// <summary>
    /// End-to-end proof of the hang and its fix at the OS-handle level. We open an anonymous pipe
    /// whose WRITE end is inheritable, then start a long-lived child two ways:
    ///   - UseShellExecute=false (the OLD behavior): Process.Start uses bInheritHandles=TRUE, so the
    ///     child inherits the write handle and the pipe never reaches EOF while the child lives -> a
    ///     piped reader BLOCKS (the #175 hang).
    ///   - UseShellExecute=true  (BuildTrayLaunchInfo): ShellExecuteEx uses bInheritHandles=FALSE, so
    ///     the handle is NOT passed to the child and the reader reaches EOF promptly even though the
    ///     child is still alive -> a piped caller returns.
    /// We assert OLD hangs and NEW does not, which is exactly the difference the fix delivers.
    /// </summary>
    [Fact]
    [SupportedOSPlatform("windows")]
    public void PipedCaller_ReturnsWithFix_AndWouldHangWithoutIt()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A long-lived "Gateway": ping loopback for ~30s. It never writes to stdout, so the only
        // thing that can keep our pipe open is an INHERITED write handle.
        var longLivedExe = Path.Combine(Environment.SystemDirectory, "ping.exe");
        const string longLivedArgs = "-n 30 127.0.0.1";
        var workDir = Path.GetTempPath();

        // OLD behavior hangs: inherited write handle keeps the read end open while the child lives.
        var oldReachedEof = TryDrainWithInheritedWritePipe(
            new ProcessStartInfo
            {
                FileName = longLivedExe,
                Arguments = longLivedArgs,
                WorkingDirectory = workDir,
                UseShellExecute = false,
            },
            waitForEof: TimeSpan.FromSeconds(3));
        Assert.False(oldReachedEof); // reproduces the hang: no EOF within the window

        // NEW behavior (the production config): no inherited handle -> EOF promptly, no hang.
        var newPsi = GatewayTrayInstaller.BuildTrayLaunchInfo(longLivedExe, workDir);
        newPsi.Arguments = longLivedArgs;
        var newReachedEof = TryDrainWithInheritedWritePipe(newPsi, waitForEof: TimeSpan.FromSeconds(10));
        Assert.True(newReachedEof); // piped caller returns even though the child is still alive
    }

    /// <summary>
    /// Starts <paramref name="psi"/> while the write end of an anonymous pipe is marked inheritable,
    /// then tries to read the read end to EOF. Returns true if EOF is reached within
    /// <paramref name="waitForEof"/> (caller would NOT hang), false if the read blocks for the whole
    /// window (caller would hang). The child is always killed.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static bool TryDrainWithInheritedWritePipe(ProcessStartInfo psi, TimeSpan waitForEof)
    {
        // The write end is marked inheritable. Process.Start with UseShellExecute=false creates the
        // child with bInheritHandles=TRUE, so the child (and its tree) INHERIT this write handle -
        // exactly how the real Gateway inherited the setup CLI's stdout. UseShellExecute=true uses
        // ShellExecuteEx (bInheritHandles=FALSE), so the handle is NOT passed to the child.
        using var pipe = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        _ = pipe.GetClientHandleAsString(); // realize the inheritable client handle before the child starts

        Process? child = null;
        try
        {
            child = Process.Start(psi) ?? throw new InvalidOperationException("Process.Start returned null");

            // Release our own copy of the write end so the ONLY remaining writer can be the child
            // (if it inherited the handle). If nothing inherited it, the read end sees EOF at once.
            pipe.DisposeLocalCopyOfClientHandle();

            using var reader = new StreamReader(pipe);
            var readToEnd = Task.Run(() => reader.ReadToEnd());
            // EOF reached within the window => no hang. Timed out => the child is holding the pipe.
            return readToEnd.Wait(waitForEof);
        }
        finally
        {
            try { if (child is { HasExited: false }) child.Kill(entireProcessTree: true); } catch { /* best-effort */ }
            child?.Dispose();
        }
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
