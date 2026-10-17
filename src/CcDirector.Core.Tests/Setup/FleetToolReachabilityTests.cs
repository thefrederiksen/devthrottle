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
