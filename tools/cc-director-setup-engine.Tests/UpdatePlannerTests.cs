using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

public class UpdatePlannerTests
{
    private static ReleaseManifest Manifest(params (string asset, string version)[] assets)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("{ \"version\": \"0.4.0\", \"assets\": {");
        for (int i = 0; i < assets.Length; i++)
        {
            if (i > 0) sb.Append(',');
            sb.Append($"\"{assets[i].asset}\": {{ \"version\": \"{assets[i].version}\", \"sha256\": \"AB\", \"platform\": \"windows\" }}");
        }
        sb.Append("} }");
        return ReleaseManifest.Parse(sb.ToString());
    }

    private static Dictionary<string, InstalledComponent> Installed(params (string id, string? version)[] items)
    {
        var map = new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase);
        foreach (var (id, version) in items)
            map[id] = new InstalledComponent(id, Present: true, Version: version, Path: $@"C:\x\{id}");
        return map;
    }

    [Fact]
    public void NotInstalled_BecomesInstall()
    {
        var components = ComponentRegistry.Build(["cc-pdf"]);
        var manifest = Manifest(
            ("cc-director-win-x64.exe", "0.4.0"),
            ("cc-pdf-win-x64.exe", "1.0.0"));
        var plan = UpdatePlanner.Plan(components, Installed(), manifest);

        Assert.All(plan.Items.Where(i => i.ComponentId is "director" or "cc-pdf"),
            i => Assert.Equal(PlanItemKind.Install, i.Kind));
    }

    [Fact]
    public void IndependentVersioning_OnlyBehindComponentUpdates()
    {
        // Director is current; cc-pdf is behind. Only cc-pdf should be flagged.
        var components = ComponentRegistry.Build(["cc-pdf", "cc-html"]);
        var manifest = Manifest(
            ("cc-director-win-x64.exe", "0.4.0"),
            ("cc-pdf-win-x64.exe", "1.2.0"),
            ("cc-html-win-x64.exe", "1.1.0"));
        var installed = Installed(
            ("director", "0.4.0"),
            ("cc-pdf", "1.1.0"),   // behind -> update
            ("cc-html", "1.1.0")); // current -> up to date

        var plan = UpdatePlanner.Plan(components, installed, manifest);

        Assert.Equal(PlanItemKind.UpToDate, plan.Items.Single(i => i.ComponentId == "director").Kind);
        Assert.Equal(PlanItemKind.Update, plan.Items.Single(i => i.ComponentId == "cc-pdf").Kind);
        Assert.Equal(PlanItemKind.UpToDate, plan.Items.Single(i => i.ComponentId == "cc-html").Kind);

        Assert.Single(plan.ToUpdate);
        Assert.Equal("cc-pdf", plan.ToUpdate[0].ComponentId);
        Assert.Equal("1.1.0", plan.ToUpdate[0].FromVersion);
        Assert.Equal("1.2.0", plan.ToUpdate[0].ToVersion);
    }

    [Fact]
    public void NoAssetInManifest_BecomesMissingAsset()
    {
        var components = ComponentRegistry.Build(["cc-pdf"]);
        var manifest = Manifest(("cc-director-win-x64.exe", "0.4.0")); // no cc-pdf asset
        var plan = UpdatePlanner.Plan(components, Installed(("director", "0.4.0")), manifest);

        // cc-pdf has no asset, and (in this minimal manifest) neither do gateway/cockpit.
        Assert.Equal(PlanItemKind.MissingAsset, plan.Items.Single(i => i.ComponentId == "cc-pdf").Kind);
        Assert.Contains(plan.MissingAssets, i => i.ComponentId == "cc-pdf");
        Assert.DoesNotContain(plan.MissingAssets, i => i.ComponentId == "director");
    }

    [Fact]
    public void PinnedVersion_IsSkippedNotUpdated()
    {
        var components = ComponentRegistry.Build(["cc-pdf"]);
        var manifest = Manifest(
            ("cc-director-win-x64.exe", "0.4.0"),
            ("cc-pdf-win-x64.exe", "1.2.0"));
        var installed = Installed(("director", "0.4.0"), ("cc-pdf", "1.1.0"));

        var pins = new UpdatePins();
        pins.Pin("cc-pdf", "1.2.0"); // rolled back from 1.2.0

        var plan = UpdatePlanner.Plan(components, installed, manifest, pins);

        Assert.Equal(PlanItemKind.Pinned, plan.Items.Single(i => i.ComponentId == "cc-pdf").Kind);
        Assert.Empty(plan.ToUpdate);
        Assert.False(plan.HasWork);
    }
}
