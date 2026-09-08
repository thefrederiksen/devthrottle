using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using CcDirector.Setup.Engine;
using System.Runtime.InteropServices;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// The engine's place-and-record step, against a local release directory.
///
/// Nothing covered this before: there were tests for the command line tool's argument parsing and its
/// install scope, and none that ran a placement and looked at what landed on disk. It is the same
/// engine both wizards and the command line installer drive, so a break here breaks all three.
///
/// SCOPE, stated so nobody reads more into it than it proves. This covers exactly one thing: given a
/// plan and a release, the runner stages each asset, verifies its hash, places it, and records the
/// version. It does NOT:
///   - run the command line executable, or <c>Commands.UpdateAsync</c>, or the planner;
///   - install the Python tools, finalize an install, start a launcher, or register autostart;
///   - prove anything about an uninstall (see the note at the end of this file);
///   - use real component binaries - the fabricated assets are short text files, because the runner
///     hashes and copies them and never executes them. That is enough for placement and recording,
///     and it is NOT a substitute for installing a real release on a real machine.
/// On macOS the Director's release asset is a .zip, which this runner deliberately skips (archive
/// extraction is the Gateway-side path), so the macOS run covers the launcher only.
///
/// Hermetic in the ways that matter here: a sandbox root and a fabricated release per test, no network.
/// </summary>
public sealed class UnattendedInstallRoundTripTests : IDisposable
{
    private readonly string _dir;
    private readonly string _releaseDir;
    private readonly InstallLayout _layout;

    public UnattendedInstallRoundTripTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "unattended-" + Guid.NewGuid().ToString("N"));
        _releaseDir = Path.Combine(_dir, "release");
        Directory.CreateDirectory(_releaseDir);
        _layout = new InstallLayout(Path.Combine(_dir, "cc-director"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* temp cleanup only */ }
    }

    /// <summary>A release directory holding the two components this installer places, with the real
    /// hashes the engine will verify.</summary>
    private ResolvedRelease BuildRelease(string version)
    {
        var assets = new Dictionary<string, object>();

        foreach (var component in new[] { ComponentRegistry.Director, ComponentRegistry.Launcher })
        {
            var assetName = component.AssetFor(HostPlatform.Current);
            if (assetName is null) continue;

            var path = Path.Combine(_releaseDir, assetName);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"{component.Id}@{version}");
            assets[assetName] = new
            {
                version,
                sha256 = Hashing.Sha256OfFile(path),
                platform = OperatingSystem.IsWindows() ? "windows" : "macos",
                size = new FileInfo(path).Length,
            };
        }

        File.WriteAllText(
            Path.Combine(_releaseDir, "release-manifest.json"),
            JsonSerializer.Serialize(new { version, assets }));
        return ReleaseSource.LoadLocalReleaseDir(_releaseDir);
    }

    private static UpdatePlan PlanFor(ResolvedRelease release, IReadOnlyList<Component> components)
    {
        var items = new List<PlanItem>();
        foreach (var c in components)
        {
            var assetName = c.AssetFor(HostPlatform.Current);
            if (assetName is null) continue;
            var asset = release.Manifest.TryGetAsset(assetName);
            if (asset is null) continue;
            items.Add(new PlanItem(c.Id, PlanItemKind.Install, asset.Name, null, asset.Version, asset.Sha256));
        }
        return new UpdatePlan { Items = items };
    }

    /// <summary>Stages the asset from the local release directory - the same seam the real downloader
    /// fills from GitHub, which is what makes this hermetic.</summary>
    private UpdateRunner.Downloader FromReleaseDir() =>
        (item, _) =>
        {
            var source = Path.Combine(_releaseDir, item.AssetName);
            var staged = Path.Combine(_dir, "staged-" + Guid.NewGuid().ToString("N"));
            File.Copy(source, staged, overwrite: true);
            return Task.FromResult(staged);
        };

    private static int CountOf(UpdateRunResult r, ApplyStatus status) => r.Results.Count(x => x.Status == status);

    // The whole point: an unattended install puts the components on disk and RECORDS what it put
    // there, so a later status or update can tell what this machine has.
    [Fact]
    public async Task Install_PlacesEveryComponentAndRecordsTheVersion()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => c.AssetFor(HostPlatform.Current) is not null)
            .ToList();

        var runner = new UpdateRunner(_layout, components, FromReleaseDir());
        var result = await runner.ApplyAsync(PlanFor(release, components));

        Assert.True(CountOf(result, ApplyStatus.Installed) + CountOf(result, ApplyStatus.Updated) > 0,
            "the install placed nothing at all");
        Assert.Equal(0, CountOf(result, ApplyStatus.Failed));

        var recorded = InstalledManifest.Load(_layout);
        foreach (var c in components)
        {
            var placed = _layout.PathFor(c);
            Assert.True(File.Exists(placed) || Directory.Exists(placed), $"{c.Id} is not at {placed}");
            Assert.Equal("1.8.6", recorded.Get(c.Id));
        }
    }

    // Running it twice must be safe and must not report phantom work. An agent that installs, checks,
    // and installs again is normal.
    [Fact]
    public async Task Install_RunTwice_IsSafeAndStaysRecordedAtTheSameVersion()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => c.AssetFor(HostPlatform.Current) is not null)
            .ToList();
        var plan = PlanFor(release, components);

        var downloads = 0;
        UpdateRunner.Downloader counted = (item, ct) => { downloads++; return FromReleaseDir()(item, ct); };

        var first = await new UpdateRunner(_layout, components, counted).ApplyAsync(plan);
        var afterFirst = downloads;
        var second = await new UpdateRunner(_layout, components, counted).ApplyAsync(plan);

        Assert.Equal(0, CountOf(first, ApplyStatus.Failed));
        Assert.Equal(0, CountOf(second, ApplyStatus.Failed));

        // Honest about what this shows: the plan handed in says Install for both runs, so the second run
        // DOES place the files again. That is the caller's decision, not the runner's - the planner is
        // what decides there is no work to do, and it is not exercised here. What this pins is that a
        // repeat is safe and leaves the recorded version correct, which is what an agent that installs,
        // checks, and installs again depends on.
        Assert.Equal(afterFirst * 2, downloads);
        var recorded = InstalledManifest.Load(_layout);
        foreach (var c in components)
            Assert.Equal("1.8.6", recorded.Get(c.Id));
    }

    // A corrupted asset must FAIL the install rather than place a file whose contents nobody vouched
    // for. The hash is the only thing standing between a truncated download and a broken machine.
    [Fact]
    public async Task Install_WithAWrongHash_Fails_AndPlacesNothing()
    {
        var release = BuildRelease("1.8.6");
        var components = ComponentRegistry.ForRole(ComponentRegistry.Apps, InstallRole.Workstation)
            .Where(c => c.AssetFor(HostPlatform.Current) is not null)
            .Take(1)
            .ToList();

        var poisoned = new UpdatePlan
        {
            Items = PlanFor(release, components).Items
                .Select(i => new PlanItem(i.ComponentId, i.Kind, i.AssetName, i.FromVersion, i.ToVersion,
                                          new string('0', 64)))
                .ToList(),
        };

        var result = await new UpdateRunner(_layout, components, FromReleaseDir()).ApplyAsync(poisoned);

        Assert.True(CountOf(result, ApplyStatus.Failed) > 0, "a wrong hash must fail the install");
        Assert.Equal(0, CountOf(result, ApplyStatus.Installed) + CountOf(result, ApplyStatus.Updated));
        Assert.Null(InstalledManifest.Load(_layout).Get(components[0].Id));
    }

    // NOTE: there is deliberately NO uninstall test here.
    //
    // One was written and it was dangerous: Uninstaller.Apply reaches machine-GLOBAL locations that are
    // not derived from the sandbox root - the launcher's autostart Run value, the Add/Remove Programs
    // entry, the Start Menu shortcut, scheduled tasks, and on macOS the real launch agent. Running it
    // with a temporary layout removed real registrations from the developer's own machine while
    // reporting 424 tests green. "Hermetic" was false, and a test that quietly damages the machine it
    // runs on is worse than no test.
    //
    // Proving an uninstall needs either a layout that owns those integration points too (so they can be
    // pointed at a sandbox) or a throwaway virtual machine. Both are real work and neither is this file.
}
