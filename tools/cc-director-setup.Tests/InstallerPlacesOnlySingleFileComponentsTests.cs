using System.IO;
using System.Linq;
using System.Text;
using CcDirector.Setup.Engine;
using CcDirectorSetup.Services;
using Xunit;

namespace CcDirectorSetup.Tests;

/// <summary>
/// Issue #4481: a fresh Windows install ended on "Setup finished with problems - Tools did not install.
/// Tools: Archive asset requires extraction (Gateway-side path)". The wizard handed every app in the role
/// to the generic single-file placer, and once the Python tools bundle (a .zip, installed by its own step
/// on the Director's first launch) joined that list, the placer skipped it and the wizard reported the
/// skip as a failure.
///
/// This runs the wizard's own component list through the real <see cref="UpdateRunner"/>, built the way
/// the wizard's ApplyAsync builds it, and requires every item to be PLACED - nothing skipped, nothing failed.
/// </summary>
public sealed class InstallerPlacesOnlySingleFileComponentsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "installer-4481-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(InstallRole.Workstation)]
    [InlineData(InstallRole.Gateway)]
    public async Task ComponentsToPlace_EveryComponent_IsPlacedByTheRunner(InstallRole role)
    {
        // Arrange
        var layout = new InstallLayout(Path.Combine(_dir, "local"));
        var staging = Directory.CreateDirectory(Path.Combine(_dir, "staging")).FullName;
        var components = EngineInstallRunner.ComponentsToPlace(role);
        var payloads = components.ToDictionary(c => c.WindowsAsset, c => Encoding.UTF8.GetBytes("payload of " + c.WindowsAsset));
        var plan = new UpdatePlan
        {
            Items = components.Select(c =>
            {
                var staged = Path.Combine(_dir, "hash-" + c.WindowsAsset);
                File.WriteAllBytes(staged, payloads[c.WindowsAsset]);
                return new PlanItem(c.Id, PlanItemKind.Install, c.WindowsAsset, null, "1.0.0", Hashing.Sha256OfFile(staged));
            }).ToList(),
        };
        var runner = new UpdateRunner(layout, components, (item, _) =>
        {
            var path = Path.Combine(staging, item.AssetName);
            File.WriteAllBytes(path, payloads[item.AssetName]);
            return Task.FromResult(path);
        });

        // Act
        var result = await runner.ApplyAsync(plan);

        // Assert
        Assert.NotEmpty(result.Results);
        Assert.All(result.Results, r => Assert.True(r.Status == ApplyStatus.Installed,
            $"{r.ComponentId} came back {r.Status}: {r.Error}"));
    }

    [Theory]
    [InlineData(InstallRole.Workstation)]
    [InlineData(InstallRole.Gateway)]
    public void ComponentsToPlace_LeavesOutOnlyTheToolsBundle(InstallRole role)
    {
        // Arrange
        var inRole = ComponentRegistry.ForRole(ComponentRegistry.Apps, role);

        // Act
        var placed = EngineInstallRunner.ComponentsToPlace(role);

        // Assert
        Assert.Equal(
            inRole.Where(c => c.Kind != ComponentKind.ToolsBundle).Select(c => c.Id),
            placed.Select(c => c.Id));
        Assert.Contains(placed, c => c.Kind == ComponentKind.Director);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
