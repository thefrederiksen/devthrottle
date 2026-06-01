using CcDirector.Setup.Engine;

namespace CcDirector.Setup.Cli;

/// <summary>
/// Loads/saves rollback version pins to a JSON file under the install root
/// (config/setup/update-pins.json). The update planner consults these so a
/// rolled-back version is not immediately re-staged by silent-auto.
/// </summary>
public static class PinStore
{
    public static string PathFor(InstallLayout layout) =>
        Path.Combine(layout.LocalRoot, "config", "setup", "update-pins.json");

    public static UpdatePins Load(InstallLayout layout)
    {
        var path = PathFor(layout);
        if (!File.Exists(path)) return new UpdatePins();
        return UpdatePins.FromJson(File.ReadAllText(path));
    }

    public static void Save(InstallLayout layout, UpdatePins pins)
    {
        var path = PathFor(layout);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        File.WriteAllText(path, pins.ToJson());
    }
}
