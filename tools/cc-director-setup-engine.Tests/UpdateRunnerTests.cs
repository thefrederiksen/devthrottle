using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class UpdateRunnerTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;

    public UpdateRunnerTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-runner-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _layout = new InstallLayout(Path.Combine(_dir, "local"), Path.Combine(_dir, "svc"));
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    /// <summary>A downloader that writes the given content to a staged temp file.</summary>
    private UpdateRunner.Downloader DownloaderWith(string content)
    {
        return (item, ct) =>
        {
            var staged = Path.Combine(_dir, "staged-" + Guid.NewGuid().ToString("N"));
            File.WriteAllText(staged, content);
            return Task.FromResult(staged);
        };
    }

    private static UpdatePlan PlanOf(PlanItem item) => new() { Items = [item] };

    [Fact]
    public async Task Install_PlacesFileAndReportsInstalled()
    {
        const string content = "tool-v1";
        var sha = Hashing.Sha256OfFile(WriteTemp(content));
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var runner = new UpdateRunner(_layout, [pdf], DownloaderWith(content));

        var plan = PlanOf(new PlanItem("cc-pdf", PlanItemKind.Install, pdf.WindowsAsset, null, "1.0.0", sha));
        var result = await runner.ApplyAsync(plan);

        Assert.Equal(1, result.Installed);
        Assert.Equal(0, result.Failed);
        Assert.True(File.Exists(_layout.PathFor(pdf)));
        Assert.Equal(content, File.ReadAllText(_layout.PathFor(pdf)));
    }

    [Fact]
    public async Task Update_KeepsBackupAndReportsUpdated()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var target = _layout.PathFor(pdf);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "tool-v1");

        const string newContent = "tool-v2";
        var sha = Hashing.Sha256OfFile(WriteTemp(newContent));
        var runner = new UpdateRunner(_layout, [pdf], DownloaderWith(newContent));

        var plan = PlanOf(new PlanItem("cc-pdf", PlanItemKind.Update, pdf.WindowsAsset, "1.0.0", "2.0.0", sha));
        var result = await runner.ApplyAsync(plan);

        Assert.Equal(1, result.Updated);
        Assert.Equal(newContent, File.ReadAllText(target));
        Assert.Equal("tool-v1", File.ReadAllText(target + ".old"));
        Assert.Equal(target + ".old", result.Results[0].BackupPath);
    }

    [Fact]
    public async Task ShaMismatch_FailsAndLeavesTargetUntouched()
    {
        var pdf = ComponentRegistry.ToolComponent("cc-pdf");
        var target = _layout.PathFor(pdf);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, "original");

        var runner = new UpdateRunner(_layout, [pdf], DownloaderWith("tampered-content"));
        // Deliberately wrong sha.
        var plan = PlanOf(new PlanItem("cc-pdf", PlanItemKind.Update, pdf.WindowsAsset, "1.0.0", "2.0.0", "DEADBEEF"));
        var result = await runner.ApplyAsync(plan);

        Assert.Equal(1, result.Failed);
        Assert.Equal("original", File.ReadAllText(target)); // not swapped
        Assert.False(File.Exists(target + ".old"));
        Assert.Contains("SHA-256", result.Results[0].Error);
    }

    [Fact]
    public async Task ZipAsset_IsSkipped()
    {
        var cockpit = ComponentRegistry.Cockpit; // ships as .zip
        var runner = new UpdateRunner(_layout, [cockpit], DownloaderWith("ignored"));
        var plan = PlanOf(new PlanItem("cockpit", PlanItemKind.Update, cockpit.WindowsAsset, "0.3.0", "0.4.0", "AB"));

        var result = await runner.ApplyAsync(plan);

        Assert.Equal(1, result.Skipped);
        Assert.Contains("extraction", result.Results[0].Error);
    }

    private string WriteTemp(string content)
    {
        var p = Path.Combine(_dir, "tmp-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(p, content);
        return p;
    }
}
