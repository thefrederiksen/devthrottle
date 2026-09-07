namespace CcDirector.Setup.Engine;

/// <summary>
/// The canonical list of installable components and the role -> component mapping.
///
/// The apps (Director, Gateway, Launcher) are fixed entries with known assets and
/// paths. Tools are built on demand from their id, because the full tool set is
/// enumerated at runtime from the release manifest / what is present in bin (it
/// changes faster than this code).
///
/// Asset naming follows the release pipeline (release.yml):
///   apps   -> cc-director(-gateway)?-win-x64.(exe)
///   tools  -> &lt;id&gt;-win-x64.exe
/// </summary>
public static class ComponentRegistry
{
    private static readonly IReadOnlySet<InstallRole> BothRoles =
        new HashSet<InstallRole> { InstallRole.Workstation, InstallRole.Gateway };

    private static readonly IReadOnlySet<InstallRole> GatewayOnly =
        new HashSet<InstallRole> { InstallRole.Gateway };

    /// <summary>The Director ships to every machine, in both roles. On macOS it ships as the
    /// application-bundle zip that <see cref="MacAppPlacer"/> places (not a single file); on Linux
    /// and Windows it is a single self-contained executable, placed by the generic runner.</summary>
    public static readonly Component Director = new(
        Id: "director",
        Kind: ComponentKind.Director,
        DisplayName: "DevThrottle",
        WindowsAsset: "cc-director-win-x64.exe",
        Roles: BothRoles,
        MacAsset: MacAppPlacer.DirectorAsset,
        LinuxAsset: "cc-director-linux-x64");

    /// <summary>
    /// The Gateway service ships only to the one Gateway-role machine, and only on Windows. There is
    /// no macOS or Linux Gateway build in the release pipeline, so both assets are null - which
    /// <see cref="Component.AssetFor"/> reports as "no build here", not as an error.
    /// </summary>
    public static readonly Component Gateway = new(
        Id: "gateway",
        Kind: ComponentKind.Gateway,
        DisplayName: "DevThrottle Gateway",
        WindowsAsset: "devthrottle-gateway-win-x64.exe",
        Roles: GatewayOnly);

    /// <summary>
    /// The CC Launcher tray app (issue #250): always-on launcher with clean process
    /// parentage and a loopback REST API. Ships to both roles so any machine can use it.
    /// On macOS it ships as a self-contained single-file executable and runs as a user
    /// launch agent (the CC Launcher mission, 2026-07-11).
    /// </summary>
    public static readonly Component Launcher = new(
        Id: "cc-launcher",
        Kind: ComponentKind.Launcher,
        DisplayName: "DevThrottle Launcher",
        WindowsAsset: "cc-launcher-win-x64.exe",
        Roles: BothRoles,
        MacAsset: "cc-launcher-mac-arm64",
        LinuxAsset: "cc-launcher-linux-x64");

    /// <summary>The fixed app components (Director, Gateway, Launcher).</summary>
    public static readonly IReadOnlyList<Component> Apps = [Director, Gateway, Launcher];

    /// <summary>
    /// A conservative default tool set (the tools the release pipeline ships
    /// today). Callers that know the live tool set should pass their own ids to
    /// <see cref="Build"/> instead of relying on this.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolIds = ["cc-pdf", "cc-html", "cc-word"];

    /// <summary>
    /// Build a tool component from its id. Tools have NO macOS and NO Linux asset, and that is not
    /// an omission: the standalone per-executable delivery is Windows-only, and every cc-* tool
    /// reaches macOS and Linux inside the shared Python tools bundle instead. So
    /// <see cref="Component.AssetFor"/> answers null off Windows, which callers render as "ships in
    /// the Python tools bundle" rather than as a missing download.
    /// </summary>
    public static Component ToolComponent(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Tool id must not be empty.", nameof(id));

        return new Component(
            Id: id,
            Kind: ComponentKind.Tool,
            DisplayName: id,
            WindowsAsset: $"{id}-win-x64.exe",
            Roles: BothRoles);
    }

    /// <summary>
    /// The full component list for the given tool ids: the three apps plus a tool
    /// component per id. Duplicate / blank tool ids are rejected.
    /// </summary>
    public static IReadOnlyList<Component> Build(IEnumerable<string> toolIds)
    {
        ArgumentNullException.ThrowIfNull(toolIds);

        var result = new List<Component>(Apps);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in toolIds)
        {
            if (!seen.Add(id))
                throw new ArgumentException($"Duplicate tool id '{id}'.", nameof(toolIds));
            result.Add(ToolComponent(id));
        }
        return result;
    }

    /// <summary>The default component list (apps + <see cref="DefaultToolIds"/>).</summary>
    public static IReadOnlyList<Component> Default() => Build(DefaultToolIds);

    /// <summary>
    /// The tool ids actually shipped in a release: every asset named
    /// "&lt;id&gt;-win-x64.exe" except the apps (Director/Gateway/Launcher) and the
    /// installer itself. The manifest is authoritative about what shipped, so the
    /// installer tracks the release pipeline with no code change when a tool is
    /// added or dropped. Returned in a stable (ordinal-sorted) order.
    /// </summary>
    public static IReadOnlyList<string> DiscoverToolIds(ReleaseManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        const string suffix = "-win-x64.exe";
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "cc-director",          // the Director app (its own component, not a tool)
            "cc-director-gateway",  // the Gateway app (legacy asset name, pre-rename)
            "cc-director-cockpit",  // the retired Blazor Cockpit (legacy asset name; never a tool)
            "devthrottle-gateway",  // the Gateway app
            "devthrottle-cockpit",  // the retired Blazor Cockpit (legacy asset; never a tool)
            "cc-director-setup",    // the installer wizard (legacy asset name, pre-rename)
            "cc-director-setup-cli",// the installer CLI (legacy asset name, pre-rename)
            "devthrottle-setup",    // the installer wizard itself
            "devthrottle-setup-cli",// the installer CLI (downloaded by the wizard for elevated installs)
            "cc-launcher",          // the Launcher tray app (issue #250): its own Launcher component
        };

        var ids = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assetName in manifest.Assets.Keys)
        {
            if (!assetName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var id = assetName[..^suffix.Length];
            if (excluded.Contains(id)) continue;
            if (seen.Add(id)) ids.Add(id);
        }
        ids.Sort(StringComparer.Ordinal);
        return ids;
    }

    /// <summary>The subset of <paramref name="all"/> that belongs to <paramref name="role"/>.</summary>
    public static IReadOnlyList<Component> ForRole(IEnumerable<Component> all, InstallRole role)
    {
        ArgumentNullException.ThrowIfNull(all);
        return all.Where(c => c.InRole(role)).ToList();
    }
}
