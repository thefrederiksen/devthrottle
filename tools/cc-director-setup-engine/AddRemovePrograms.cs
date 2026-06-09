using System.Runtime.Versioning;
using Microsoft.Win32;

namespace CcDirector.Setup.Engine;

/// <summary>
/// The Windows "Apps &amp; features" / "Add or remove programs" registration for CC Director
/// (issue #257), under HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall\&lt;key&gt;.
/// Per-user (HKCU) matches the per-user install: no elevation. The install path registers it so
/// uninstall is discoverable from Windows even when the app will not launch; the uninstaller
/// removes it last. The subkey name is parameterized only so tests can use a throwaway key and
/// never touch the real registration; production always uses <see cref="DefaultKeyName"/>.
/// </summary>
public static class AddRemovePrograms
{
    /// <summary>The production Uninstall subkey name.</summary>
    public const string DefaultKeyName = "CcDirector";

    /// <summary>The display name shown in Windows "Apps &amp; features".</summary>
    public const string DisplayName = "CC Director";

    private const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall";

    private static string SubKeyPath(string keyName) => $@"{UninstallRoot}\{keyName}";

    /// <summary>
    /// Write (or overwrite) the Add/Remove Programs entry. <paramref name="uninstallCommand"/> is the
    /// command Windows runs when the user clicks Uninstall (e.g. the setup exe with an uninstall arg);
    /// <paramref name="installLocation"/> is the per-user root. Idempotent. Returns true on success.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static bool Register(string version, string uninstallCommand, string installLocation,
        string? displayIcon = null, string keyName = DefaultKeyName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(version);
        ArgumentException.ThrowIfNullOrWhiteSpace(uninstallCommand);
        EngineLog.Write($"[AddRemovePrograms] Register key={keyName} version={version}");

        using var key = Registry.CurrentUser.CreateSubKey(SubKeyPath(keyName));
        if (key is null)
            throw new InvalidOperationException($"Could not create HKCU\\{SubKeyPath(keyName)}");

        key.SetValue("DisplayName", DisplayName, RegistryValueKind.String);
        key.SetValue("DisplayVersion", version, RegistryValueKind.String);
        key.SetValue("Publisher", "CC Director", RegistryValueKind.String);
        key.SetValue("InstallLocation", installLocation ?? "", RegistryValueKind.String);
        key.SetValue("UninstallString", uninstallCommand, RegistryValueKind.String);
        key.SetValue("DisplayIcon", displayIcon ?? uninstallCommand, RegistryValueKind.String);
        // Per-user install: no modify/repair entry points, so hide those buttons.
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        return true;
    }

    /// <summary>True if the Add/Remove Programs entry exists.</summary>
    [SupportedOSPlatform("windows")]
    public static bool IsRegistered(string keyName = DefaultKeyName)
    {
        using var key = Registry.CurrentUser.OpenSubKey(SubKeyPath(keyName), writable: false);
        return key is not null;
    }

    /// <summary>Remove the Add/Remove Programs entry if present. Returns true if a key was deleted.</summary>
    [SupportedOSPlatform("windows")]
    public static bool Unregister(string keyName = DefaultKeyName)
    {
        EngineLog.Write($"[AddRemovePrograms] Unregister key={keyName}");
        using var parent = Registry.CurrentUser.OpenSubKey(UninstallRoot, writable: true);
        if (parent is null || parent.OpenSubKey(keyName) is null) return false;
        parent.DeleteSubKeyTree(keyName, throwOnMissingSubKey: false);
        return true;
    }
}
