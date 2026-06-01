namespace CcDirector.Setup.Engine;

/// <summary>
/// Resolves where each component lives on disk. Roots are injectable so tests can
/// point at temp directories; defaults match production:
///   - Director + tools: %LOCALAPPDATA%\cc-director  (app\, bin\) - per-user, admin-free
///   - Gateway + Cockpit: C:\cc-tools                - machine-wide, LocalSystem service
/// </summary>
public sealed class InstallLayout
{
    /// <summary>%LOCALAPPDATA%\cc-director (or the CC_DIRECTOR_ROOT override).</summary>
    public string LocalRoot { get; }

    /// <summary>C:\cc-tools - where the Gateway/Cockpit service files live.</summary>
    public string ServiceRoot { get; }

    public InstallLayout(string localRoot, string serviceRoot)
    {
        if (string.IsNullOrWhiteSpace(localRoot))
            throw new ArgumentException("localRoot must not be empty.", nameof(localRoot));
        if (string.IsNullOrWhiteSpace(serviceRoot))
            throw new ArgumentException("serviceRoot must not be empty.", nameof(serviceRoot));
        LocalRoot = localRoot;
        ServiceRoot = serviceRoot;
    }

    /// <summary>The production layout, honoring CC_DIRECTOR_ROOT like CcStorage does.</summary>
    public static InstallLayout Default()
    {
        var root = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        if (string.IsNullOrWhiteSpace(root))
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            root = Path.Combine(localAppData, "cc-director");
        }
        return new InstallLayout(root, @"C:\cc-tools");
    }

    public string AppDir => Path.Combine(LocalRoot, "app");
    public string BinDir => Path.Combine(LocalRoot, "bin");
    public string GatewayDir => Path.Combine(ServiceRoot, "cc-director-gateway");
    public string CockpitDir => Path.Combine(ServiceRoot, "cc-director-cockpit");

    /// <summary>The on-disk file whose presence/version represents the component.</summary>
    public string PathFor(Component component)
    {
        ArgumentNullException.ThrowIfNull(component);
        return component.Kind switch
        {
            ComponentKind.Director => Path.Combine(AppDir, "cc-director.exe"),
            ComponentKind.Gateway => Path.Combine(GatewayDir, "cc-director-gateway.exe"),
            ComponentKind.Cockpit => Path.Combine(CockpitDir, "cc-director-cockpit.exe"),
            ComponentKind.Tool => Path.Combine(BinDir, $"{component.Id}.exe"),
            _ => throw new ArgumentOutOfRangeException(nameof(component), component.Kind, "Unknown component kind."),
        };
    }
}
