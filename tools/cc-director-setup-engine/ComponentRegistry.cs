namespace CcDirector.Setup.Engine;

/// <summary>
/// The canonical list of installable components and the role -> component mapping.
///
/// The three apps (Director, Gateway, Cockpit) are fixed entries with known
/// assets and paths. Tools are built on demand from their id, because the full
/// tool set is enumerated at runtime from the release manifest / what is present
/// in bin (it changes faster than this code).
///
/// Asset naming follows the release pipeline (release.yml):
///   apps   -> cc-director[-gateway|-cockpit]-win-x64.(exe|zip)
///   tools  -> &lt;id&gt;-win-x64.exe
/// </summary>
public static class ComponentRegistry
{
    private static readonly IReadOnlySet<InstallRole> BothRoles =
        new HashSet<InstallRole> { InstallRole.Workstation, InstallRole.Gateway };

    private static readonly IReadOnlySet<InstallRole> GatewayOnly =
        new HashSet<InstallRole> { InstallRole.Gateway };

    /// <summary>The Director ships to every machine, in both roles.</summary>
    public static readonly Component Director = new(
        Id: "director",
        Kind: ComponentKind.Director,
        DisplayName: "CC Director",
        WindowsAsset: "cc-director-win-x64.exe",
        Roles: BothRoles);

    /// <summary>The Gateway service ships only to the one Gateway-role machine.</summary>
    public static readonly Component Gateway = new(
        Id: "gateway",
        Kind: ComponentKind.Gateway,
        DisplayName: "CC Gateway Service",
        WindowsAsset: "cc-director-gateway-win-x64.exe",
        Roles: GatewayOnly);

    /// <summary>The Cockpit ships only to the Gateway-role machine (the service supervises it).</summary>
    public static readonly Component Cockpit = new(
        Id: "cockpit",
        Kind: ComponentKind.Cockpit,
        DisplayName: "CC Cockpit",
        WindowsAsset: "cc-director-cockpit-win-x64.zip",
        Roles: GatewayOnly);

    /// <summary>The fixed app components.</summary>
    public static readonly IReadOnlyList<Component> Apps = [Director, Gateway, Cockpit];

    /// <summary>
    /// A conservative default tool set (the tools the release pipeline ships
    /// today). Callers that know the live tool set should pass their own ids to
    /// <see cref="Build"/> instead of relying on this.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultToolIds = ["cc-pdf", "cc-html", "cc-word"];

    /// <summary>Build a tool component from its id.</summary>
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
    /// "&lt;id&gt;-win-x64.exe" except the apps (Director/Gateway/Cockpit) and the
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
            "cc-director",         // the Director app (its own component, not a tool)
            "cc-director-gateway", // the Gateway app
            "cc-director-cockpit", // the Cockpit app (ships as .zip anyway)
            "cc-director-setup",   // the installer itself
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
