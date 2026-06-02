namespace CcDirector.Setup.Engine;

/// <summary>
/// Loads/saves rollback version pins to update-pins.json under the per-user setup-state dir. The
/// update planner consults these so a rolled-back (bad) version is not immediately re-staged by
/// silent auto-update. Lives in the engine so every update path (CLI, resident auto-update) shares
/// one pin store.
/// </summary>
public static class PinStore
{
    public static string PathFor(InstallLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return Path.Combine(layout.SetupStateDir, "update-pins.json");
    }

    public static UpdatePins Load(InstallLayout layout)
    {
        var path = PathFor(layout);
        return File.Exists(path) ? UpdatePins.FromJson(File.ReadAllText(path)) : new UpdatePins();
    }

    public static void Save(InstallLayout layout, UpdatePins pins)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(pins);
        Directory.CreateDirectory(layout.SetupStateDir);
        File.WriteAllText(PathFor(layout), pins.ToJson());
    }
}
