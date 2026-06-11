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

            // Sanity guard (issue #176): a recorded installed version that is strictly NEWER than the
            // newest released asset is anomalous - it never happens legitimately and is the signature
            // of test-harness pollution writing a fake version (e.g. 9.9.9) into installed.json. If we
            // trusted it we would report UpToDate forever and silently skip a real swap. So we log
            // loudly and prefer the exe's actual on-disk FileVersion for the decision instead. When the
            // exe carries no readable stamp to fall back on, we refuse to report UpToDate on the basis
            // of the discarded fake version and re-apply the released build instead.
            var poisonedInstalledVersion = present
                && VersionUtil.IsNewer(fromVersion, asset.Version)
                && !string.Equals(fromVersion, state?.FileVersion, StringComparison.OrdinalIgnoreCase);
            if (poisonedInstalledVersion)
            {
                var fileVersion = state?.FileVersion;
                EngineLog.Write(
                    $"[UpdatePlanner] WARNING: recorded installed version '{fromVersion}' for '{c.Id}' is " +
                    $"NEWER than the newest released version '{asset.Version}'. This is anomalous (likely " +
                    $"self-update test pollution in installed.json). Ignoring the recorded version and " +
                    $"using the exe's actual FileVersion '{fileVersion ?? "(unreadable)"}' instead.");
                fromVersion = fileVersion;
            }

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
            else if (poisonedInstalledVersion && string.IsNullOrWhiteSpace(fromVersion))
                // Poisoned record AND no readable exe stamp to trust: never report UpToDate on a
                // version we just discarded - re-apply the released build to correct the state.
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
