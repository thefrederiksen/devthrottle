namespace CcDirector.Setup.Engine;

/// <summary>
/// Pure decision logic: given the components in scope, their installed state, the
/// latest release manifest, and any rollback pins, decide what to do with each.
///
/// This is the heart of "independent per component": each component is judged on
/// ITS OWN asset version in the manifest versus ITS OWN installed version, so a
/// release that bumps one tool only marks that tool as needing an update.
/// </summary>
public static class UpdatePlanner
{
    /// <summary>
    /// Build a plan. <paramref name="installed"/> is keyed by component id; a
    /// component missing from the map is treated as not present.
    /// </summary>
    public static UpdatePlan Plan(
        IEnumerable<Component> components,
        IReadOnlyDictionary<string, InstalledComponent> installed,
        ReleaseManifest manifest,
        UpdatePins? pins = null)
    {
        ArgumentNullException.ThrowIfNull(components);
        ArgumentNullException.ThrowIfNull(installed);
        ArgumentNullException.ThrowIfNull(manifest);
        pins ??= new UpdatePins();

        var items = new List<PlanItem>();
        foreach (var c in components)
        {
            var asset = manifest.TryGetAsset(c.WindowsAsset);
            if (asset is null)
            {
                items.Add(new PlanItem(c.Id, PlanItemKind.MissingAsset, c.WindowsAsset, null, null, ""));
                continue;
            }

            installed.TryGetValue(c.Id, out var state);
            var fromVersion = state?.Version;
            var present = state?.Present ?? false;

            if (pins.IsPinned(c.Id, asset.Version))
            {
                items.Add(new PlanItem(c.Id, PlanItemKind.Pinned, asset.Name, fromVersion, asset.Version, asset.Sha256));
                continue;
            }

            PlanItemKind kind;
            if (!present)
                kind = PlanItemKind.Install;
            else if (VersionUtil.IsNewer(asset.Version, fromVersion))
                kind = PlanItemKind.Update;
            else
                kind = PlanItemKind.UpToDate;

            items.Add(new PlanItem(c.Id, kind, asset.Name, fromVersion, asset.Version, asset.Sha256));
        }

        EngineLog.Write($"[UpdatePlanner] Plan: {items.Count} components, " +
                        $"{items.Count(i => i.Kind == PlanItemKind.Install)} install, " +
                        $"{items.Count(i => i.Kind == PlanItemKind.Update)} update, " +
                        $"{items.Count(i => i.Kind == PlanItemKind.MissingAsset)} missing-asset.");

        return new UpdatePlan { Items = items };
    }
}
