using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>Implements each CLI command over the engine. Thin: no business logic lives here.</summary>
internal static class Commands
{
    private const int Ok = 0;
    private const int Error = 1;
    private const int PrereqMissing = 3;

    // ---- component scope helpers ------------------------------------------

    private static IReadOnlyList<string> ToolIds(CliArgs args)
    {
        var raw = args.Option("tools");
        if (string.IsNullOrWhiteSpace(raw)) return ComponentRegistry.DefaultToolIds;
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static InstallRole Role(CliArgs args)
    {
        var raw = args.Option("role", "workstation").ToLowerInvariant();
        return raw switch
        {
            "workstation" => InstallRole.Workstation,
            "gateway" => InstallRole.Gateway,
            _ => throw new UsageException($"--role must be 'workstation' or 'gateway', got '{raw}'."),
        };
    }

    private static IReadOnlyList<Component> ScopedComponents(CliArgs args) =>
        ComponentRegistry.ForRole(ComponentRegistry.Build(ToolIds(args)), Role(args));

    // ---- commands ----------------------------------------------------------

    public static int Components(CliArgs args, InstallLayout layout, bool json)
    {
        var components = ScopedComponents(args);
        if (json)
        {
            Program.WriteJson(components.Select(c => new
            {
                id = c.Id,
                kind = c.Kind.ToString(),
                asset = c.WindowsAsset,
                path = layout.PathFor(c),
                roles = c.Roles.Select(r => r.ToString()).OrderBy(s => s),
            }));
            return Ok;
        }

        Console.WriteLine($"Components for role '{Role(args)}':");
        foreach (var c in components)
            Console.WriteLine($"  {c.Id,-14} {c.Kind,-9} {c.WindowsAsset}");
        return Ok;
    }

    public static int Status(CliArgs args, InstallLayout layout, bool json)
    {
        var components = ScopedComponents(args);
        var reader = new InstalledStateReader(layout);
        var state = reader.ReadAll(components);

        if (json)
        {
            Program.WriteJson(components.Select(c =>
            {
                var s = state[c.Id];
                return new { id = c.Id, present = s.Present, version = s.Version, path = s.Path };
            }));
            return Ok;
        }

        Console.WriteLine($"Installed status (role '{Role(args)}', root '{layout.LocalRoot}'):");
        foreach (var c in components)
        {
            var s = state[c.Id];
            var ver = s.Present ? (s.Version ?? "version unknown") : "not installed";
            Console.WriteLine($"  {c.Id,-14} {ver}");
        }
        return Ok;
    }

    public static int Prereqs(bool json)
    {
        var statuses = FrameworkDetector.DetectAll();
        var anyFound = statuses.Any(s => s.Found);

        if (json)
        {
            Program.WriteJson(new
            {
                satisfied = anyFound,
                frameworks = statuses.Select(s => new { name = s.Name, found = s.Found, location = s.Location }),
            });
            return anyFound ? Ok : PrereqMissing;
        }

        Console.WriteLine("Agent framework check:");
        foreach (var s in statuses)
            Console.WriteLine($"  {s.Name,-8} {(s.Found ? $"found ({s.Location})" : "not found")}");

        if (!anyFound)
        {
            Console.WriteLine();
            Console.WriteLine("No agent framework detected. Install one, then re-run:");
            Console.WriteLine($"  Claude Code: {FrameworkDetector.ClaudeInstallUrl}");
            Console.WriteLine($"  Codex:       {FrameworkDetector.CodexInstallUrl}");
            return PrereqMissing;
        }
        return Ok;
    }

    public static async Task<int> PlanAsync(CliArgs args, InstallLayout layout, bool json)
    {
        var (plan, _) = await ComputePlanAsync(args, layout);
        PrintPlan(plan, json);
        return Ok;
    }

    public static async Task<int> UpdateAsync(CliArgs args, InstallLayout layout, bool json, bool installMode)
    {
        var (plan, release) = await ComputePlanAsync(args, layout);

        // Optionally narrow to one component.
        var only = args.Option("component");
        if (!string.IsNullOrWhiteSpace(only) && !only.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            var filtered = plan.Items.Where(i => i.ComponentId.Equals(only, StringComparison.OrdinalIgnoreCase)).ToList();
            if (filtered.Count == 0) throw new UsageException($"--component '{only}' is not in scope.");
            plan = new UpdatePlan { Items = filtered };
        }

        if (args.HasFlag("dry-run"))
        {
            PrintPlan(plan, json);
            return Ok;
        }

        if (!plan.HasWork)
        {
            if (json) Program.WriteJson(new { mode = installMode ? "install" : "update", applied = Array.Empty<object>(), message = "nothing to do" });
            else Console.WriteLine("Nothing to do - all components up to date.");
            return Ok;
        }

        var components = ScopedComponents(args);
        var source = new ReleaseSource();
        var runner = new UpdateRunner(layout, components,
            (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));

        var result = await runner.ApplyAsync(plan);
        PrintRun(result, installMode, json);
        return result.Failed > 0 ? Error : Ok;
    }

    public static int Rollback(CliArgs args, InstallLayout layout, bool json)
    {
        if (args.Positionals.Count == 0)
            throw new UsageException("rollback requires a component id, e.g. 'rollback director'.");
        var id = args.Positionals[0];
        var component = ResolveComponent(id);
        var path = layout.PathFor(component);

        // The version currently installed is the (bad) version we are rolling back FROM.
        var reader = new InstalledStateReader(layout);
        var badVersion = reader.Read(component).Version;

        var restored = InstallSwapper.Rollback(path);
        if (!restored)
        {
            if (json) Program.WriteJson(new { component = id, rolledBack = false, message = "no .old backup found" });
            else Console.WriteLine($"No previous build to roll back to for '{id}'.");
            return Error;
        }

        if (!string.IsNullOrWhiteSpace(badVersion))
        {
            var pins = PinStore.Load(layout);
            pins.Pin(component.Id, badVersion);
            PinStore.Save(layout, pins);
        }

        if (json) Program.WriteJson(new { component = id, rolledBack = true, pinnedAwayFrom = badVersion });
        else Console.WriteLine($"Rolled back '{id}'" + (badVersion != null ? $" and pinned away from {badVersion}." : "."));
        return Ok;
    }

    // ---- shared helpers ----------------------------------------------------

    private static async Task<(UpdatePlan plan, ResolvedRelease release)> ComputePlanAsync(CliArgs args, InstallLayout layout)
    {
        var components = ScopedComponents(args);
        var release = await ResolveReleaseAsync(args);
        var reader = new InstalledStateReader(layout);
        var installed = reader.ReadAll(components);
        var pins = PinStore.Load(layout);
        var plan = UpdatePlanner.Plan(components, installed, release.Manifest, pins);
        return (plan, release);
    }

    private static async Task<ResolvedRelease> ResolveReleaseAsync(CliArgs args)
    {
        // --release-dir wins: a local directory acting as a full release (offline).
        var releaseDir = args.Option("release-dir");
        if (!string.IsNullOrWhiteSpace(releaseDir))
            return ReleaseSource.LoadLocalReleaseDir(releaseDir);

        var manifest = args.Option("manifest", "latest");
        if (manifest.Equals("latest", StringComparison.OrdinalIgnoreCase))
            return await new ReleaseSource().FetchLatestAsync(CancellationToken.None);
        return ReleaseSource.LoadLocalManifest(manifest);
    }

    private static Component ResolveComponent(string id) => id.ToLowerInvariant() switch
    {
        "director" => ComponentRegistry.Director,
        "gateway" => ComponentRegistry.Gateway,
        "cockpit" => ComponentRegistry.Cockpit,
        _ => ComponentRegistry.ToolComponent(id),
    };

    private static void PrintPlan(UpdatePlan plan, bool json)
    {
        if (json)
        {
            Program.WriteJson(plan.Items.Select(i => new
            {
                component = i.ComponentId,
                action = i.Kind.ToString(),
                from = i.FromVersion,
                to = i.ToVersion,
            }));
            return;
        }

        Console.WriteLine("Plan:");
        foreach (var i in plan.Items)
        {
            var detail = i.Kind switch
            {
                PlanItemKind.Update => $"{i.FromVersion} -> {i.ToVersion}",
                PlanItemKind.Install => $"install {i.ToVersion}",
                PlanItemKind.UpToDate => $"up to date ({i.ToVersion})",
                PlanItemKind.MissingAsset => "no asset in release",
                PlanItemKind.Pinned => $"pinned (skipping {i.ToVersion})",
                _ => i.Kind.ToString(),
            };
            Console.WriteLine($"  {i.ComponentId,-14} {i.Kind,-12} {detail}");
        }
        Console.WriteLine($"Actionable: {plan.Actionable.Count} ({plan.ToInstall.Count} install, {plan.ToUpdate.Count} update)");
    }

    private static void PrintRun(UpdateRunResult result, bool installMode, bool json)
    {
        if (json)
        {
            Program.WriteJson(new
            {
                mode = installMode ? "install" : "update",
                installed = result.Installed,
                updated = result.Updated,
                failed = result.Failed,
                skipped = result.Skipped,
                results = result.Results.Select(r => new
                {
                    component = r.ComponentId,
                    status = r.Status.ToString(),
                    from = r.FromVersion,
                    to = r.ToVersion,
                    error = r.Error,
                }),
            });
            return;
        }

        Console.WriteLine($"{(installMode ? "Install" : "Update")} complete:");
        foreach (var r in result.Results)
        {
            var line = $"  {r.ComponentId,-14} {r.Status}";
            if (r.Error != null) line += $" - {r.Error}";
            Console.WriteLine(line);
        }
        Console.WriteLine($"installed={result.Installed} updated={result.Updated} failed={result.Failed} skipped={result.Skipped}");
    }
}
