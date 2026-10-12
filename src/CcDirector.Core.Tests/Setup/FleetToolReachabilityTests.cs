using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using CcDirector.Core.Setup;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// The check answers "can a session I spawn actually reach the fleet?", so these tests drive it with
/// a stand-in tool whose exit code they control. That is the whole discriminator: the Director reads
/// the exit code and never asks the tool to judge itself.
///
/// Since the Remove-the-network-port mission the probe supplies the Gateway pair a real session gets
/// (CC_GATEWAY_URL + CC_GATEWAY_SESSION_KEY) rather than a Director address. The old "the tool said
/// there is no Gateway" classification is gone WITH ITS CAUSE: the probe always supplies both
/// variables now, so that answer cannot occur here - the no-Gateway verdict is reached upstream,
/// where the caller finds it has no credential to mint, before any tool is run.
///
/// A note on what the fixture can and cannot distinguish: a stand-in that exits 0 proves the PASS path
/// and nothing about authentication, because the fake never authenticates. What authentication failure
/// looks like from here is exactly what a non-zero exit looks like - which is why the real machine
/// proof is a live run recorded in the phase's QA evidence rather than simulated here.
/// </summary>
public class FleetToolReachabilityTests : IDisposable
{
    private readonly List<string> _temporaryFiles = new();

    public void Dispose()
    {
        foreach (var path in _temporaryFiles)
        {
            try { if (File.Exists(path)) File.Delete(path); }
            catch (IOException) { /* a leftover temp file is not a test failure */ }
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>A runnable stand-in for cc-devthrottle that exits with the code we want to test.</summary>
    private string StubTool(int exitCode, string message)
    {
        var extension = OperatingSystem.IsWindows() ? ".cmd" : ".sh";
        var path = Path.Combine(Path.GetTempPath(), $"fleet-tool-stub-{Guid.NewGuid():N}{extension}");
        var body = OperatingSystem.IsWindows()
            ? $"@echo off\r\necho {message}\r\nexit /b {exitCode}\r\n"
            : $"#!/bin/sh\necho \"{message}\"\nexit {exitCode}\n";
        File.WriteAllText(path, body);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        _temporaryFiles.Add(path);
        return path;
    }

    private static FleetToolReachability WithResolved(string? resolvedPath)
        => new(TimeSpan.FromSeconds(30), _ => resolvedPath);

    /// <summary>
    /// Resolve the bare command name one way and an explicit path another, which is the whole point of
    /// the second probe: PATH's answer and our own copy are different questions with different answers.
    /// </summary>
    private static FleetToolReachability WithPathAndOwn(string? onPath, string? ours)
        => new(TimeSpan.FromSeconds(30),
            command => command == FleetToolReachability.ToolName ? onPath : ours);

    private static readonly string OurBinDir =
        Path.Combine("C:", "cc-director", "instances", "default", "bin");

    private const string GatewayUrl = "http://127.0.0.1:7878";
    private const string SessionKey = "a-registered-probe-key";

    [Fact]
    public async Task RunAsync_ToolNotOnPath_ReportsNotFound()
    {
        var check = await WithResolved(null).RunAsync(GatewayUrl, SessionKey, expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.NotFound, check.Verdict);
        Assert.Null(check.ResolvedPath);
        Assert.Contains("PATH", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_ToolReachesTheFleet_ReportsWorking()
    {
        var stub = StubTool(exitCode: 0, "ok");

        var check = await WithResolved(stub).RunAsync(GatewayUrl, SessionKey, expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.Working, check.Verdict);
        Assert.Equal(stub, check.ResolvedPath);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_ToolFails_ReportsCannotReachGatewayWithTheToolsOwnReason()
    {
        // What a stale build does when handed a credential scheme it predates.
        var stub = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithResolved(stub).RunAsync(GatewayUrl, SessionKey, expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.CannotReachGateway, check.Verdict);
        // The tool's own sentence survives to the panel. Reporting a bare exit code here is what made
        // the original failure undiagnosable from the outside.
        Assert.Contains("missing or invalid token", check.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task RunAsync_ToolThatCannotLaunch_IsAFailureNotAPass()
    {
        // A tool too old to accept the arguments, or missing from disk between resolution and launch.
        // Absence of a clean answer must never read as a good one.
        var check = await WithResolved(Path.Combine(Path.GetTempPath(), "definitely-not-here-x9.cmd"))
            .RunAsync(GatewayUrl, SessionKey, expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.CannotReachGateway, check.Verdict);
    }

    [Fact]
    public async Task RunAsync_NoGatewayAddress_Throws()
    {
        var reachability = WithResolved("/some/tool");

        await Assert.ThrowsAsync<ArgumentException>(
            () => reachability.RunAsync("", SessionKey, expectedBinDir: null));
    }

    [Fact]
    public async Task RunAsync_NoSessionKey_Throws()
    {
        // The probe must never run credential-less: the failure it produced would say nothing about
        // the fault this check exists to find, and the caller decides the no-Gateway case upstream.
        var reachability = WithResolved("/some/tool");

        await Assert.ThrowsAsync<ArgumentException>(
            () => reachability.RunAsync(GatewayUrl, "", expectedBinDir: null));
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_PathToolFails_AndOurOwnCopyWorks_IsRepairableByRepointingPath()
    {
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");
        var ours = StubTool(exitCode: 0, "ok");

        var check = await WithPathAndOwn(onPath: stale, ours: ours).RunAsync(GatewayUrl, SessionKey, OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachGateway, check.Verdict);
        Assert.Equal(FleetToolVerdict.Working, check.OwnVerdict);
        Assert.True(check.CanRepairByRepointingPath);
        Assert.False(check.OwnToolsAreMissingOrBroken);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_PathToolFails_AndWeHaveNoCopyOfOurOwn_IsNotRepairableByRepointingPath()
    {
        // THE FAILURE THIS EXISTS FOR, 2026-08-01 on SOREN_NORTH. PATH resolved a stale install's
        // cc-devthrottle and this Director's own bin directory was EMPTY - its tools had never been
        // installed. The panel saw only "PATH points somewhere else", offered "Repoint PATH", and the
        // repair put an empty directory in front: resolution fell straight through it to the same
        // stale copy and reported the same "missing or invalid token" it had been pressed to fix.
        //
        // Repointing must not be offered here. There is nothing to point at.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithPathAndOwn(onPath: stale, ours: null).RunAsync(GatewayUrl, SessionKey, OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachGateway, check.Verdict);
        Assert.Equal(FleetToolVerdict.NotFound, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.True(check.OwnToolsAreMissingOrBroken);
        Assert.Contains(OurBinDir, check.OwnDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_PathToolFails_AndOurOwnCopyAlsoFails_IsNotRepairableByRepointingPath()
    {
        // Our tools are installed and broken. Reordering PATH would hand sessions a second copy that
        // fails the same way, so the button must stay away from this one too.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");
        var oursBroken = StubTool(exitCode: 1, "ModuleNotFoundError: no module named cc_shared");

        var check = await WithPathAndOwn(onPath: stale, ours: oursBroken)
            .RunAsync(GatewayUrl, SessionKey, OurBinDir);

        Assert.Equal(FleetToolVerdict.CannotReachGateway, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.True(check.OwnToolsAreMissingOrBroken);
        Assert.Contains("ModuleNotFoundError", check.OwnDetail, StringComparison.Ordinal);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_PathToolWorks_LeavesOurOwnCopyUnasked()
    {
        // Nothing is wrong, so there is no second question - and an unasked question must report as
        // Unchecked, never as a pass.
        var working = StubTool(exitCode: 0, "ok");

        var check = await WithPathAndOwn(onPath: working, ours: null).RunAsync(GatewayUrl, SessionKey, OurBinDir);

        Assert.Equal(FleetToolVerdict.Working, check.Verdict);
        Assert.Equal(FleetToolVerdict.Unchecked, check.OwnVerdict);
        Assert.False(check.CanRepairByRepointingPath);
        Assert.False(check.OwnToolsAreMissingOrBroken);
    }

    [Trait("Gate", "Quarantine")]
    [Fact]
    public async Task RunAsync_NoBinDirOfOurOwn_ReportsOurCopyAsUncheckedRatherThanMissing()
    {
        // A development build has no install directory. "We have no tools" would be a claim the check
        // cannot support, and it would light this banner on every developer machine.
        var stale = StubTool(exitCode: 1, "Error: missing or invalid token");

        var check = await WithPathAndOwn(onPath: stale, ours: null)
            .RunAsync(GatewayUrl, SessionKey, expectedBinDir: null);

        Assert.Equal(FleetToolVerdict.Unchecked, check.OwnVerdict);
        Assert.False(check.OwnToolsAreMissingOrBroken);
        Assert.False(check.CanRepairByRepointingPath);
    }

    [Fact]
    public void CanRepairByRepointingPath_OurOwnCopyWorksButPathResolvedItAnyway_IsFalse()
    {
        // Same install, still refused. Repointing PATH at the directory it already resolves repairs
        // nothing, so the fault must not be offered a fix that cannot touch it.
        var check = new FleetToolCheck(
            FleetToolVerdict.CannotReachGateway,
            ResolvedPath: Path.Combine(OurBinDir, "cc-devthrottle.cmd"),
            ExpectedBinDir: OurBinDir,
            Detail: "missing or invalid token",
            OwnVerdict: FleetToolVerdict.Working);

        Assert.False(check.CanRepairByRepointingPath);
    }

    [Fact]
    public void IsDifferentInstall_ResolvedOutsideOurBinDir_IsTrue()
    {
        var check = new FleetToolCheck(
            FleetToolVerdict.CannotReachGateway,
            ResolvedPath: Path.Combine("C:", "cc-director", "bin", "cc-devthrottle.cmd"),
            ExpectedBinDir: Path.Combine("C:", "cc-director", "instances", "default", "bin"),
            Detail: "missing or invalid token");

        Assert.True(check.IsDifferentInstall);
    }

    [Fact]
    public void IsDifferentInstall_ResolvedInsideOurBinDir_IsFalse()
    {
        var binDir = Path.Combine("C:", "cc-director", "instances", "default", "bin");
        var check = new FleetToolCheck(
            FleetToolVerdict.Working,
            ResolvedPath: Path.Combine(binDir, "cc-devthrottle.cmd"),
            ExpectedBinDir: binDir,
            Detail: "reached the fleet through the Gateway");

        Assert.False(check.IsDifferentInstall);
    }

    [Fact]
    public void IsDifferentInstall_WithNoBinDirToCompareAgainst_IsFalse()
    {
        // A development build has no install directory of its own. It must not be reported as a
        // different install, because that is a claim the check cannot support - and asserting it
        // would light the badge on every developer machine for a fault that is not there.
        var check = new FleetToolCheck(
            FleetToolVerdict.Working,
            ResolvedPath: Path.Combine("C:", "anywhere", "cc-devthrottle.cmd"),
            ExpectedBinDir: null,
            Detail: "reached the fleet through the Gateway");

        Assert.False(check.IsDifferentInstall);
    }
}
