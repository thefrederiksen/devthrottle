using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class OrchestratorTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public OrchestratorTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-orch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "svc"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private static ReleaseManifest ManifestWith(string asset, string version, string sha) =>
        ReleaseManifest.Parse($"{{ \"version\": \"{version}\", \"assets\": {{ \"{asset}\": " +
                              $"{{ \"version\": \"{version}\", \"sha256\": \"{sha}\", \"platform\": \"windows\" }} }} }}");

    [Fact]
    public async Task RunAsync_NoWork_ReturnsNullRun()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        // Reader reports the tool already present and current.
        var reader = new InstalledStateReader(_layout,
            fileExists: _ => true,
            readVersion: _ => "1.2.0");
        var manifest = ManifestWith(pdf.WindowsAsset, "1.2.0", "AB");
        var orch = new Orchestrator(_layout, reader);

        var result = await orch.RunAsync([pdf], manifest, (_, _) => throw new InvalidOperationException("should not download"));

        Assert.True(result.NoWork);
        Assert.Null(result.Run);
    }

    [Fact]
    public async Task RunAsync_InstallsMissingComponent()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var reader = new InstalledStateReader(_layout, fileExists: _ => false, readVersion: _ => null);

        const string content = "fresh-tool";
        var staged = Path.Combine(_dir, "src.bin");
        File.WriteAllText(staged, content);
        var sha = Hashing.Sha256OfFile(staged);
        var manifest = ManifestWith(pdf.WindowsAsset, "1.0.0", sha);
        var orch = new Orchestrator(_layout, reader);

        var result = await orch.RunAsync([pdf], manifest, (_, _) =>
        {
            var copy = Path.Combine(_dir, "dl-" + Guid.NewGuid().ToString("N"));
            File.Copy(staged, copy);
            return Task.FromResult(copy);
        });

        Assert.False(result.NoWork);
        Assert.Equal(1, result.Run!.Installed);
        Assert.True(File.Exists(_layout.PathFor(pdf)));
        Assert.Equal(content, File.ReadAllText(_layout.PathFor(pdf)));
    }
}
