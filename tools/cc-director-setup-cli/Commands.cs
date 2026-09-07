using CcDirector.Core.Configuration;
using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>Implements each CLI command over the engine. Thin: no business logic lives here.</summary>
internal static class Commands
{
    // The numbers live in ExitCodes - a contract scripts and agents branch on, not private
    // constants duplicated per file (they were, in two files, with nothing pinning them).
    private const int Ok = ExitCodes.Ok;
    private const int Error = ExitCodes.Error;
    private const int PrereqMissing = ExitCodes.PrerequisiteMissing;

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
        // The platform this machine actually is, not "macOS or else Windows" - that two-way read is
        // what told a Linux machine to install cc-director-win-x64.exe.
        var platform = HostPlatform.Current;
        if (json)
        {
            Program.WriteJson(components.Select(c => new
            {
                id = c.Id,
                kind = c.Kind.ToString(),
                asset = c.AssetFor(platform),
                path = layout.PathFor(c),
                roles = c.Roles.Select(r => r.ToString()).OrderBy(s => s),
            }));
            return Ok;
        }

        Console.WriteLine($"Components for role '{Role(args)}':");
        foreach (var c in components)
            // A Tool with no per-file macOS asset is not "missing on macOS" - every cc-* tool ships on
            // both platforms inside the Python tools bundle; only the standalone per-exe delivery is
            // Windows-only. Say so, instead of implying Mac users get no tools (#1711 audit, defect 5).
            Console.WriteLine($"  {c.Id,-14} {c.Kind,-9} {c.AssetFor(platform) ?? "(ships in the Python tools bundle)"}");
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

    // ---- autostart on|off|status (issue #2022) ----------------------------
    //
    // Start-at-login left the web Settings page and lives here: the CLI + the per-OS mechanism is the one
    // home that works on every platform, INCLUDING a headless Linux server that has no tray or window. The
    // real per-OS work (Windows Run key, macOS launch agent, Linux systemd --user unit) lives in the engine's
    // GatewayAutostartControl; this command is a thin front-end over it. The user-facing entry point is
    // `cc-devthrottle autostart`, which shells to this.
    public static int Autostart(CliArgs args, InstallLayout layout, bool json)
    {
        var verb = (args.Positionals.Count > 0 ? args.Positionals[0] : "status").ToLowerInvariant();

        if (!GatewayAutostartControl.Supported)
        {
            var msg = "Gateway autostart is not supported on this platform (it exists on Windows, macOS, and Linux).";
            if (json) Program.WriteJson(new { command = "autostart", verb, supported = false, message = msg });
            else Console.Error.WriteLine(msg);
            return Error;
        }

        switch (verb)
        {
            case "on":
            {
                // Register the SAME entry the installer and the running Gateway do: the installed Gateway exe,
                // run --managed. No fallback - if the Gateway is not installed, say so rather than write a
                // Run entry that points at nothing.
                var exe = layout.PathFor(ComponentRegistry.Gateway);
                if (!File.Exists(exe))
                {
                    var msg = $"The Gateway is not installed at {exe}. Install it first, then turn autostart on.";
                    if (json) Program.WriteJson(new { command = "autostart", verb, supported = true, enabled = false, message = msg });
                    else Console.Error.WriteLine(msg);
                    return Error;
                }
                GatewayAutostartControl.Enable(exe, GatewayTrayInstaller.InstalledArguments);
                return ReportAutostart(json, verb, "The Gateway will start when you log in.");
            }
            case "off":
                GatewayAutostartControl.Disable();
                return ReportAutostart(json, verb, "Gateway autostart is off.");
            case "status":
                return ReportAutostart(json, verb,
                    GatewayAutostartControl.IsEnabled ? "Gateway autostart is on." : "Gateway autostart is off.");
            default:
                var usage = $"Unknown autostart verb '{verb}'. Use: autostart on|off|status.";
                if (json) Program.WriteJson(new { command = "autostart", verb, message = usage });
                else Console.Error.WriteLine(usage);
                return 2; // usage error (matches Program.ExitUsage)
        }
    }

    private static int ReportAutostart(bool json, string verb, string message)
    {
        var enabled = GatewayAutostartControl.IsEnabled;
        if (json)
        {
            Program.WriteJson(new
            {
                command = "autostart",
                verb,
                supported = true,
                enabled,
                mechanism = GatewayAutostartControl.MechanismName,
                registeredCommand = GatewayAutostartControl.RegisteredCommand,
                message,
            });
        }
        else
        {
            Console.WriteLine(message);
            Console.WriteLine($"  mechanism: {GatewayAutostartControl.MechanismName}");
            if (enabled) Console.WriteLine($"  command:   {GatewayAutostartControl.RegisteredCommand}");
        }
        return Ok;
    }

    /// <summary>
    /// Is there a coding agent on this machine? Nothing else is checked, because nothing else is
    /// needed: the Director and the launcher carry their own .NET runtime, the cc-* tools bring their
    /// own Python, and the tunnel-only architecture opens no inbound port, so no mesh network or
    /// firewall change is involved either.
    ///
    /// The answer comes from the shared <see cref="AgentPresence"/> - the same code the wizards use -
    /// so the two install paths cannot disagree. This command used to have its own detector that knew
    /// only Claude and Codex and looked only at PATH, while the wizards knew all eight agents and also
    /// looked where installers actually put them. One product, one answer.
    /// </summary>
    public static int Prereqs(bool json)
    {
        var found = AgentPresence.AnyAgent();

        if (json)
        {
            Program.WriteJson(new
            {
                satisfied = found,
                codingAgentPresent = found,
                agentsChecked = AgentPresence.AgentCommands,
                searched = AgentPresence.SearchDirectories(),
            });
            return found ? Ok : PrereqMissing;
        }

        Console.WriteLine(found
            ? "Coding agent: found."
            : "Coding agent: none found.");

        if (!found)
        {
            Console.WriteLine();
            Console.WriteLine("DevThrottle runs coding agents, so it needs at least one to be useful.");
            Console.WriteLine($"Looked for: {string.Join(", ", AgentPresence.AgentCommands)}");
            Console.WriteLine("Install one and re-run this check. The Director also detects agents when it opens,");
            Console.WriteLine("and can add what it finds to your board.");
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

    /// <summary>
    /// Sign this machine's DevThrottle account in from the command line (the headless sibling of the
    /// wizard's sign-in step). Opens the browser to devthrottle.com, where the user signs in - or creates a
    /// free account - and the captured credential is stored where the Gateway reads it, so the Gateway does
    /// not re-prompt on first launch. The account credential lives on the Gateway (Windows-only, per-user), so
    /// this command is Windows-only; on macOS a machine is a Workstation and uses <c>enroll</c> instead. The
    /// access token is never printed or logged (security rule DT-05).
    /// </summary>
    public static async Task<int> SignInAsync(CliArgs args, bool json)
    {
        if (!OperatingSystem.IsWindows())
        {
            const string reason =
                "'signin' stores the account on the Gateway, which is Windows-only. On macOS, install a Workstation and run 'enroll' to join your gateway.";
            if (json) Program.WriteJson(new { command = "signin", signedIn = false, outcome = "Failed", message = reason });
            else Console.Error.WriteLine(reason);
            return Error;
        }

        if (!json)
        {
            Console.WriteLine("Opening your browser to sign in to DevThrottle...");
            Console.WriteLine("Sign in - or create a free account - in the browser. Waiting for you to finish (up to 5 minutes)...");
        }

        var result = await new AccountSignInRunner().RunAsync();
        if (json)
            Program.WriteJson(new { command = "signin", signedIn = result.Succeeded, outcome = result.Outcome.ToString(), message = result.Message });
        else
            Console.WriteLine(result.Succeeded ? $"OK: {result.Message}" : $"Sign-in did not complete: {result.Message}");
        return result.Succeeded ? Ok : Error;
    }

    /// <summary>
    /// Join this machine (a Workstation) to its gateway from the command line (the headless sibling of the
    /// wizard's gateway-connect step). Opens the browser to sign in - or create a free account - then registers
    /// this machine on the account and enrolls it at the gateway for a local, revocable device key, which is
    /// persisted so the Director and cc-* tools connect on first run. With <c>--gateway &lt;url&gt;</c> it uses
    /// that address (proven reachable first); without it, it discovers the account's gateways and joins the one
    /// it finds. Idempotent: a machine already connected reports so and succeeds. Device keys are never printed
    /// or logged (security rule DT-05).
    ///
    /// With <c>--hosted</c> it joins DevThrottle's HOSTED gateway instead of one the account runs itself: the
    /// same browser sign-in, but the account token goes straight to the hosted gateway, which mints this
    /// machine's key bound to the account's tenant. There is no address to give and nothing to discover, so
    /// <c>--hosted</c> and <c>--gateway</c> are mutually exclusive.
    /// </summary>
    public static async Task<int> EnrollAsync(CliArgs args, bool json)
    {
        // Idempotent: an already-connected machine (an update/repair run) has nothing to do.
        var existing = GatewayConfig.Load();
        if (existing.IsEnabled)
        {
            var msg = $"This machine is already connected to its gateway ({existing.Url}).";
            if (json) Program.WriteJson(new { command = "enroll", enrolled = true, alreadyConnected = true, gatewayUrl = existing.Url, message = msg });
            else Console.WriteLine(msg);
            return Ok;
        }

        // No running Director yet, so mint a stable device id for this machine (wizard parity): used both as
        // the account-registration install id and the enroll device id.
        var deviceId = Guid.NewGuid().ToString();
        var machineName = Environment.MachineName;
        var runner = new GatewayAccountEnrollRunner();
        var gatewayUrl = args.Option("gateway");
        var hosted = args.HasFlag("hosted");

        // Hosted has no address to give and nothing to discover, so naming a gateway alongside it is a
        // contradiction, not a preference to resolve silently.
        if (hosted && !string.IsNullOrWhiteSpace(gatewayUrl))
            throw new UsageException("--hosted joins DevThrottle's hosted gateway, so it cannot be combined with --gateway <url>.");

        if (!json)
        {
            Console.WriteLine("Opening your browser to sign in to DevThrottle...");
            Console.WriteLine("Sign in - or create a free account - in the browser. Waiting for you to finish (up to 5 minutes)...");
        }

        // Hosted: the account token goes straight to the hosted gateway, which mints this machine's key bound
        // to the account's tenant. No address to prove reachable and no discovery - enrolling IS the join.
        if (hosted)
        {
            if (!json) Console.WriteLine("Joining the DevThrottle hosted gateway...");
            var host = await runner.SignInAndEnrollHostedAsync(deviceId, machineName);
            return ReportEnroll(host.Success, host.ErrorMessage, json);
        }

        // A given gateway URL is proven reachable BEFORE we sign in against it (wizard parity: never register
        // against an address we could not reach), then sign in + register + enroll.
        if (!string.IsNullOrWhiteSpace(gatewayUrl))
        {
            var test = await runner.TestGatewayAddressAsync(gatewayUrl, 0);
            if (!test.Success || test.Value is null)
                return FailEnroll(test.ErrorMessage ?? "Could not reach a gateway at that address.", json);

            var verified = await runner.VerifyAndSaveAsync(test.Value, deviceId, machineName);
            return ReportEnroll(verified.Success, verified.ErrorMessage, json);
        }

        // No URL given: sign in and discover the account's gateways.
        var discovered = await runner.SignInAndDiscoverGatewaysAsync();
        if (!discovered.Success || discovered.Value is null)
            return FailEnroll(discovered.ErrorMessage ?? "Could not discover a gateway on your account.", json);

        var gateways = discovered.Value;
        if (gateways.Count > 1)
        {
            // A headless command cannot show an interactive chooser; list the account's gateways and ask the
            // user to re-run naming one, rather than silently picking.
            if (json)
                Program.WriteJson(new
                {
                    command = "enroll",
                    enrolled = false,
                    message = "Your account has more than one gateway. Re-run 'enroll --gateway <url>' with one of these.",
                    gateways = gateways.Select(g => new { name = g.Name, url = g.EndpointUrl }),
                });
            else
            {
                Console.WriteLine("Your account has more than one gateway. Re-run with --gateway <url> naming one of these:");
                foreach (var g in gateways) Console.WriteLine($"  {g.Name,-24} {g.EndpointUrl}");
            }
            return Error;
        }

        var only = gateways[0];
        if (!json) Console.WriteLine($"Joining gateway: {only.Name} ({only.EndpointUrl})");
        var enrolled = await runner.EnrollWithDiscoveredGatewayAsync(only.EndpointUrl, deviceId, machineName);
        return ReportEnroll(enrolled.Success, enrolled.ErrorMessage, json);
    }

    /// <summary>Render a successful/failed enroll outcome. The issued device key is never printed (DT-05).</summary>
    private static int ReportEnroll(bool success, string? errorMessage, bool json)
    {
        if (success)
        {
            if (json) Program.WriteJson(new { command = "enroll", enrolled = true, message = "Connected to the gateway." });
            else Console.WriteLine("OK: connected to the gateway.");
            return Ok;
        }
        return FailEnroll(errorMessage ?? "The gateway did not complete the enrollment.", json);
    }

    /// <summary>Render an enroll failure with a user-safe reason.</summary>
    private static int FailEnroll(string reason, bool json)
    {
        if (json) Program.WriteJson(new { command = "enroll", enrolled = false, message = reason });
        else Console.Error.WriteLine(reason);
        return Error;
    }

    public static async Task<int> UpdateAsync(CliArgs args, InstallLayout layout, bool json, bool installMode)
    {
        var role = Role(args);
        var isGatewayInstall = installMode && role == InstallRole.Gateway && !args.HasFlag("dry-run");

        // Gateway installs are per-user (tray app, %LOCALAPPDATA%) - NO elevation. Verify the platform
        // (the managed Gateway is Windows-only) and fail loudly before doing any work. No OPENAI_API_KEY
        // is required: inference routes through the account-minted dt_live_ key the runtime auto-mints.
        if (isGatewayInstall)
        {
            var preflight = GatewayInstallPreflight.Check(OperatingSystem.IsWindows());
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
        if (InstallScope.IsComponentScoped(only))
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

        // macOS: the Director ships as a .app-bundle zip (cc-director-mac-arm64.zip) that the
        // generic single-file runner cannot place. Route it through MacAppPlacer - the exact step
        // the setup wizard uses - and hand the rest of the plan to the generic runner. Before this
        // split the CLI downloaded the WINDOWS Director exe onto macOS (issue #1445).
        PlanItem? macDirectorItem = null;
        if (OperatingSystem.IsMacOS())
        {
            macDirectorItem = plan.Items.FirstOrDefault(i =>
                i.ComponentId.Equals(ComponentRegistry.Director.Id, StringComparison.OrdinalIgnoreCase)
                && i.Kind is PlanItemKind.Install or PlanItemKind.Update);
            if (macDirectorItem is not null)
                plan = new UpdatePlan { Items = plan.Items.Where(i => !ReferenceEquals(i, macDirectorItem)).ToList() };
        }

        var applied = new List<ApplyResult>();
        if (plan.HasWork)
        {
            var runner = new UpdateRunner(layout, components,
                (item, ct) => source.DownloadAssetAsync(item.AssetName, release.DownloadUrls, ct));
            var runResult = await runner.ApplyAsync(plan);
            applied.AddRange(runResult.Results);
        }

        if (macDirectorItem is not null)
        {
            var placed = await PlaceMacDirectorAsync(layout, release, source, json);
            applied.Add(new ApplyResult(
                ComponentRegistry.Director.Id,
                placed.Success
                    ? (macDirectorItem.Kind == PlanItemKind.Update ? ApplyStatus.Updated : ApplyStatus.Installed)
                    : ApplyStatus.Failed,
                macDirectorItem.FromVersion, placed.Version ?? macDirectorItem.ToVersion,
                placed.Success ? null : placed.Message, null));
        }

        var result = new UpdateRunResult { Results = applied };
        if (applied.Count > 0)
        {
            PrintRun(result, installMode, json);
        }
        else if (!isGatewayInstall && !(installMode && Role(args) == InstallRole.Workstation))
        {
            // An install of EITHER role still (re)installs the Python tools bundle and runs the
            // per-user finalization below even when the apps are already current, so only a plain
            // `update` short-circuits here.
            if (json) Program.WriteJson(new { mode = installMode ? "install" : "update", applied = Array.Empty<object>(), message = "nothing to do" });
            else Console.WriteLine("Nothing to do - all components up to date.");
            return Ok;
        }

        // The generic runner places the Gateway exe but never starts the tray app. On a Gateway
        // install, finish the work here (extract the mobile side-car app, start the tray app in
        // managed mode, wait for health; the app registers its own autostart Run key). The Cockpit is
        // served in-process by the Gateway now (issue #979), so there is no Cockpit zip to extract.
        if (isGatewayInstall && result.Failed == 0 && OperatingSystem.IsWindows())
        {
            var installer = new GatewayTrayInstaller(layout);
            var tray = await installer.InstallAsync(release, source);
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

        // Per-user Python tools bundle (the shared venv with every cc-* tool). Installed for BOTH
        // roles: the Gateway is a per-user tray app (no elevation), so a Gateway install is a true
        // SUPERSET of a Workstation install and gets the tools too (INSTALLATION.md section 1). The
        // old workstation-only gate dated from when the Gateway was an elevated Windows service that
        // ran as admin while the venv belonged in the logged-in user's profile; that model is retired.
        var toolsInstalled = false;
        if (InstallScope.InstallsPythonTools(Role(args), installMode, args.HasFlag("dry-run"),
                InstallScope.IsComponentScoped(args.Option("component"))))
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
        // Skipped when only the machine-tier Gateway changed.
        var perUserTouched = toolsInstalled || result.Results.Any(r =>
            r.Status is ApplyStatus.Installed or ApplyStatus.Updated &&
            r.ComponentId is not "gateway");
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
        else if (perUserTouched && OperatingSystem.IsMacOS())
        {
            // Wizard parity: make sure ~/.local/bin (the cc-* tool shims) is on the login PATH.
            var pathChanged = InstallFinalizer.EnsureMacUserBinOnPath();
            if (!json)
                Console.WriteLine(pathChanged ? "PATH: added ~/.local/bin (open a new terminal to use the tools)" : "PATH: already set");
        }

        // The Launcher tray app ships to BOTH roles. The generic runner PLACES its exe but never
        // starts it (so a fresh install leaves it dormant and its autostart Run key unwritten). Start
        // it whenever the launcher exe is on disk - INDEPENDENT of how other components fared. Gating
        // on the whole install succeeding (result.Failed == 0) was wrong: on a live machine the running
        // Director locks its exe, that swap fails (result.Failed > 0), and the launcher - which is
        // unrelated and installed fine - would never start. The Director auto-updates itself separately,
        // so its locked swap must not block the launcher. The app self-registers its Run key and runs
        // the self-update loop on start. Idempotent: an already-running launcher just keeps serving. If
        // the launcher's OWN install failed its exe is absent and we skip (counted in result.Failed below).
        var launcherExe = layout.PathFor(ComponentRegistry.Launcher);
        if (installMode && File.Exists(launcherExe))
        {
            // Same contract both platforms: the exe/binary is already placed; this step starts it
            // and verifies health + autostart registration (Run key on Windows, launch agent on
            // macOS, where a kickstart hands a running launcher over to the newly placed binary).
            LauncherInstallResult launcherStart;
            if (OperatingSystem.IsWindows())
                launcherStart = await new LauncherTrayInstaller(layout).InstallAsync();
            else if (OperatingSystem.IsMacOS())
                launcherStart = await new LauncherMacInstaller(layout).InstallAsync();
            else
                throw new PlatformNotSupportedException("The launcher install step supports Windows and macOS only.");

            if (json)
                Program.WriteJson(new { launcherTray = new { success = launcherStart.Success, message = launcherStart.Message, steps = launcherStart.Steps } });
            else
            {
                Console.WriteLine();
                Console.WriteLine(launcherStart.Success ? "Launcher tray app:" : "Launcher tray app FAILED:");
                foreach (var s in launcherStart.Steps) Console.WriteLine($"  {s}");
                Console.WriteLine($"  {launcherStart.Message}");
            }
            if (!launcherStart.Success) return Error;
        }

        return result.Failed > 0 ? Error : Ok;
    }

    /// <summary>
    /// Place the macOS Director .app bundle via <see cref="MacAppPlacer"/> (download, SHA-256
    /// verify, ditto-extract, swap into ~/Applications, de-quarantine). Fresh install and update
    /// are the same operation; the placer records the installed version for the planner.
    /// </summary>
    private static async Task<MacAppResult> PlaceMacDirectorAsync(
        InstallLayout layout, ResolvedRelease release, ReleaseSource source, bool json)
    {
        if (!OperatingSystem.IsMacOS())
            throw new InvalidOperationException("PlaceMacDirectorAsync is macOS-only.");
        if (!json) Console.WriteLine("Placing Director.app:");
        return await MacAppPlacer.PlaceAsync(layout, release, source,
            log: m => { if (!json) Console.WriteLine($"  {m}"); });
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
            // The CLI ships inside the setup bundle and does the real install work, so it resolves
            // the release its OWN stamped version was built for: a pre-release CLI installs its
            // matching pre-release, a stable CLI installs the latest stable (issue #1294).
            return await new ReleaseSource().FetchReleaseForSetupAsync(CancellationToken.None);
        return ReleaseSource.LoadLocalManifest(manifest);
    }

    private static Component ResolveComponent(string id) => id.ToLowerInvariant() switch
    {
        "director" => ComponentRegistry.Director,
        "gateway" => ComponentRegistry.Gateway,
        "launcher" => ComponentRegistry.Launcher,
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
