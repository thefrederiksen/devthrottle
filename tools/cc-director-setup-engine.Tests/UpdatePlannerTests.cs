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

    private static Dictionary<string, InstalledComponent> InstalledWithFileVersion(
        string id, string? recordedVersion, string? fileVersion)
    {
        return new Dictionary<string, InstalledComponent>(StringComparer.OrdinalIgnoreCase)
        {
            [id] = new InstalledComponent(id, Present: true, Version: recordedVersion, Path: $@"C:\x\{id}", FileVersion: fileVersion),
        };
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

    // --- Issue #176: sanity guard against a poisoned (test-polluted) installed version ---

    [Fact]
    public void PoisonedInstalledVersion_DoesNotReportUpToDate_PrefersExeFileVersion()
    {
        // The exact #176 scenario: installed.json records a FAKE 9.9.9 for the gateway (self-update
        // test pollution) while the released gateway is 0.6.6 and the exe on disk is really 0.6.5.
        // The planner must NOT trust 9.9.9 (which would say UpToDate and skip the swap); it must use
        // the exe's real FileVersion 0.6.5 and therefore flag an Update to 0.6.6.
        var components = new[] { ComponentRegistry.Gateway };
        var manifest = Manifest(("devthrottle-gateway-win-x64.exe", "0.6.6"));
        var installed = InstalledWithFileVersion("gateway", recordedVersion: "9.9.9", fileVersion: "0.6.5");

        var plan = UpdatePlanner.Plan(components, installed, manifest);

        var item = plan.Items.Single(i => i.ComponentId == "gateway");
        Assert.NotEqual(PlanItemKind.UpToDate, item.Kind);
        Assert.Equal(PlanItemKind.Update, item.Kind);
        Assert.Equal("0.6.5", item.FromVersion);   // the real exe stamp, not the poisoned 9.9.9
        Assert.Equal("0.6.6", item.ToVersion);
    }

    [Fact]
    public void PoisonedInstalledVersion_ExeAlreadyAtRelease_ReportsUpToDateFromExeStamp()
    {
        // Poisoned record 9.9.9 but the exe is genuinely already at the released 0.6.6: the guard
        // discards 9.9.9, uses the real stamp 0.6.6, and correctly reports UpToDate on THAT basis.
        var components = new[] { ComponentRegistry.Gateway };
        var manifest = Manifest(("devthrottle-gateway-win-x64.exe", "0.6.6"));
        var installed = InstalledWithFileVersion("gateway", recordedVersion: "9.9.9", fileVersion: "0.6.6");

        var plan = UpdatePlanner.Plan(components, installed, manifest);

        var item = plan.Items.Single(i => i.ComponentId == "gateway");
        Assert.Equal(PlanItemKind.UpToDate, item.Kind);
        Assert.Equal("0.6.6", item.FromVersion);
    }

    [Fact]
    public void PoisonedInstalledVersion_NoReadableExeStamp_ReappliesInsteadOfUpToDate()
    {
        // Poisoned record 9.9.9 and the exe carries no readable stamp: we must never report UpToDate
        // on the basis of the discarded fake version - re-apply the released build to correct it.
        var components = new[] { ComponentRegistry.Gateway };
        var manifest = Manifest(("devthrottle-gateway-win-x64.exe", "0.6.6"));
        var installed = InstalledWithFileVersion("gateway", recordedVersion: "9.9.9", fileVersion: null);

        var plan = UpdatePlanner.Plan(components, installed, manifest);

        var item = plan.Items.Single(i => i.ComponentId == "gateway");
        Assert.Equal(PlanItemKind.Update, item.Kind);
    }

    [Fact]
    public void NormalInstalledVersion_NotTreatedAsPoisoned()
    {
        // A legitimate installed version at-or-below the release is never disturbed by the guard:
        // recorded 0.6.6 == released 0.6.6 stays UpToDate even when no separate file stamp is read.
        var components = new[] { ComponentRegistry.Gateway };
        var manifest = Manifest(("devthrottle-gateway-win-x64.exe", "0.6.6"));
        var installed = InstalledWithFileVersion("gateway", recordedVersion: "0.6.6", fileVersion: "0.6.6");

        var plan = UpdatePlanner.Plan(components, installed, manifest);

        var item = plan.Items.Single(i => i.ComponentId == "gateway");
        Assert.Equal(PlanItemKind.UpToDate, item.Kind);
        Assert.Equal("0.6.6", item.FromVersion);
    }
}
