using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>Implements each CLI command over the engine. Thin: no business logic lives here.</summary>
internal static class Commands
{
    private const int Ok = 0;
    private const int Error = 1;
    private const int PrereqMissing = 3;

    // ---- component scope helpers ------------------------------------------

    private static IReadOnlyList<string> ToolIds(CliArgs args, ReleaseManifest? manifest = null)
    {
        var raw = args.Option("tools");
        if (!string.IsNullOrWhiteSpace(raw))
            return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        // Parity with the wizard: when --tools is not given, install exactly what the release ships
        // (discovered from the manifest), not a hardcoded default. Only fall back to the small default
        // set when no release is in hand (e.g. the offline 'components'/'status' commands).
        return manifest is not null
            ? ComponentRegistry.DiscoverToolIds(manifest)
            : ComponentRegistry.DefaultToolIds;
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

    private static IReadOnlyList<Component> ScopedComponents(CliArgs args, ReleaseManifest? manifest = null) =>
        ComponentRegistry.ForRole(ComponentRegistry.Build(ToolIds(args, manifest)), Role(args));

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
        var (plan, _, _) = await ComputePlanAsync(args, layout);
        PrintPlan(plan, json);
        return Ok;
    }

    public static async Task<int> UpdateAsync(CliArgs args, InstallLayout layout, bool json, bool installMode)
    {
        var role = Role(args);
        var isGatewayInstall = installMode && role == InstallRole.Gateway && !args.HasFlag("dry-run");

        // Gateway installs are per-user (tray app, %LOCALAPPDATA%) - NO elevation. Still verify the
        // key the Gateway needs, and fail loudly (no silent degrade) before doing any work.
        if (isGatewayInstall)
        {
            var preflight = GatewayPreflight();
            if (preflight is not null)
            {
                if (json) Program.WriteJson(new { mode = "install", role = "gateway", failed = preflight });
                else Console.Error.WriteLine(preflight);
                return Error;
            }
        }

        var (plan, release, components) = await ComputePlanAsync(args, layout);

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

        var source = new ReleaseSource();

        var result = new UpdateRunResult { Results = Array.Empty<ApplyResult>() };
        if (plan.HasWork)
        {
            var runner = new UpdateRunner(layout, components,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));
            result = await runner.ApplyAsync(plan);
            PrintRun(result, installMode, json);
        }
        else if (!isGatewayInstall && !(installMode && Role(args) == InstallRole.Workstation))
        {
            // A workstation install still (re)installs the Python tools bundle below even when the
            // apps are current, so only short-circuit for update mode / Gateway here.
            if (json) Program.WriteJson(new { mode = installMode ? "install" : "update", applied = Array.Empty<object>(), message = "nothing to do" });
            else Console.WriteLine("Nothing to do - all components up to date.");
            return Ok;
        }

        // The generic runner places the Gateway exe but skips the Cockpit .zip and never starts the
        // tray app. On a Gateway install, finish the work here (extract Cockpit, start the tray app
        // in managed mode, wait for health; the app registers its own autostart Run key).
        if (isGatewayInstall && result.Failed == 0 && OperatingSystem.IsWindows())
        {
            var key = OpenAiKey()
                ?? throw new InvalidOperationException("OPENAI_API_KEY missing after Gateway pre-flight passed.");
            var installer = new GatewayTrayInstaller(layout);
            var tray = await installer.InstallAsync(release, source, key);
            if (json)
                Program.WriteJson(new { gatewayTray = new { success = tray.Success, message = tray.Message, steps = tray.Steps } });
            else
            {
                Console.WriteLine();
                Console.WriteLine(tray.Success ? "Gateway tray app:" : "Gateway tray app FAILED:");
                foreach (var s in tray.Steps) Console.WriteLine($"  {s}");
                Console.WriteLine($"  {tray.Message}");
            }
            if (!tray.Success) return Error;
        }

        // Per-user Python tools bundle (the shared venv with every cc-* tool). Only on a per-user
        // (workstation) install - NOT an elevated Gateway-only install, which runs as admin while the
        // venv belongs in the logged-in user's profile (the non-elevated workstation install owns it).
        var toolsInstalled = false;
        if (installMode && Role(args) == InstallRole.Workstation && !args.HasFlag("dry-run") && OperatingSystem.IsWindows())
        {
            var py = await new PythonToolsInstaller(layout).InstallAsync(release, source);
            if (json)
                Program.WriteJson(new { pythonTools = new { success = py.Success, message = py.Message, toolCount = py.ToolCount } });
            else
            {
                Console.WriteLine();
                Console.WriteLine(py.Success ? $"Python tools: {py.Message}" : $"Python tools FAILED: {py.Message}");
                foreach (var s in py.Steps) Console.WriteLine($"  {s}");
            }
            if (!py.Success) return Error;
            toolsInstalled = py.ToolCount > 0;
        }

        // Per-user finalization (wizard parity): if the Director, the tools bundle, or any other
        // per-user component was placed, add the bin dir to PATH and create the Start Menu shortcut.
        // Skipped when only machine components (gateway/cockpit) changed.
        var perUserTouched = toolsInstalled || result.Results.Any(r =>
            r.Status is ApplyStatus.Installed or ApplyStatus.Updated &&
            r.ComponentId is not ("gateway" or "cockpit"));
        if (perUserTouched && OperatingSystem.IsWindows())
        {
            var pathChanged = InstallFinalizer.AddBinToPath(layout);
            var shortcut = InstallFinalizer.CreateDirectorShortcut(layout);
            if (!json)
            {
                Console.WriteLine(pathChanged ? $"PATH: added {layout.BinDir} (open a new terminal to use the tools)" : "PATH: already set");
                Console.WriteLine(shortcut ? "Start Menu shortcut: created" : "Start Menu shortcut: skipped (Director not installed)");
            }
        }

        return result.Failed > 0 ? Error : Ok;
    }

    /// <summary>
    /// Pre-flight checks for a Gateway install. Returns an error message to print, or null if OK.
    /// Not a fallback: it stops the install with an exact fix rather than half-installing.
    /// </summary>
    private static string? GatewayPreflight()
    {
        if (!OperatingSystem.IsWindows())
            return "ERROR: The Gateway role is Windows-only.";
        if (string.IsNullOrWhiteSpace(OpenAiKey()))
            return "ERROR: OPENAI_API_KEY is not set in your environment; the Gateway needs it to start.\n" +
                   "       Set it (User scope) and re-run, e.g.:\n" +
                   "         setx OPENAI_API_KEY \"sk-...\"";
        return null;
    }

    /// <summary>The OpenAI key from the process env, falling back to the User-scope env on Windows.</summary>
    private static string? OpenAiKey()
    {
        var key = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        if (string.IsNullOrWhiteSpace(key) && OperatingSystem.IsWindows())
            key = Environment.GetEnvironmentVariable("OPENAI_API_KEY", EnvironmentVariableTarget.User);
        return string.IsNullOrWhiteSpace(key) ? null : key;
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

    public static int Uninstall(CliArgs args, InstallLayout layout, bool json)
    {
        var role = Role(args);
        var uninstaller = new Uninstaller(layout);

        if (args.HasFlag("dry-run"))
        {
            var plan = uninstaller.Plan(role);
            if (json)
            {
                Program.WriteJson(plan.Select(t => new { kind = t.Kind.ToString(), t.Description, t.Path, t.Present }));
            }
            else
            {
                Console.WriteLine($"Uninstall plan (role '{role}') - removes ONLY install-owned files; your data is preserved:");
                foreach (var t in plan)
                    Console.WriteLine($"  [{(t.Present ? "x" : " ")}] {t.Kind,-10} {t.Description} ({t.Path})");
            }
            return Ok;
        }

        // The Gateway is a per-user tray app under %LOCALAPPDATA%: uninstall needs no elevation.
        var report = uninstaller.Apply(role);
        if (json)
        {
            Program.WriteJson(new { success = report.Success, steps = report.Steps, errors = report.Errors });
        }
        else
        {
            Console.WriteLine(report.Success ? "Uninstall complete:" : "Uninstall finished with errors:");
            foreach (var s in report.Steps) Console.WriteLine($"  {s}");
            foreach (var e in report.Errors) Console.WriteLine($"  ERROR: {e}");
        }
        return report.Success ? Ok : Error;
    }

    // ---- shared helpers ----------------------------------------------------

    private static async Task<(UpdatePlan plan, ResolvedRelease release, IReadOnlyList<Component> components)> ComputePlanAsync(CliArgs args, InstallLayout layout)
    {
        // Resolve the release first so the tool set is discovered from ITS manifest (wizard parity),
        // not a hardcoded default.
        var release = await ResolveReleaseAsync(args);
        var components = ScopedComponents(args, release.Manifest);
        var reader = new InstalledStateReader(layout);
        var installed = reader.ReadAll(components);
        var pins = PinStore.Load(layout);
        var plan = UpdatePlanner.Plan(components, installed, release.Manifest, pins);
        return (plan, release, components);
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
