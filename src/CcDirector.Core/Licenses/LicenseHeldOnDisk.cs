using System.Text.Json;
using System.Text.Json.Nodes;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;

namespace CcDirector.Core.Licenses;

/// <summary>
/// Which accounts this machine's license store holds, read from the ACCOUNT FILES alone - never a key, and without
/// running the license tool (issue #4327). Two things need that answer while license switching may be off, when the
/// tool must not open the machine's real default login:
///
///   - whether the setting is on when config.json says nothing (<see cref="Configuration.LicenseSwitchingSetting"/>):
///     on exactly when the store holds a spare;
///   - the report a Director with switching OFF still sends, so the Gateway knows the machine and that it will not
///     take part in a switch.
///
/// The layout is the key store's own (tools/cc-devthrottle/src/license_store.py): each spare is a folder under
/// <c>&lt;store&gt;/spares/</c> whose <c>.claude.json</c> names the account in its <c>oauthAccount</c> block, and the
/// active account is named the same way in <c>~/.claude.json</c> for the real default folder, or in
/// <c>&lt;folder&gt;/.claude.json</c> for an isolated one. The key itself may be in a file or the macOS Keychain; it is
/// not looked at, so a spare folder whose sign-in key went missing still counts here, while the tool would not list it.
/// </summary>
public sealed class LicenseHeldOnDisk
{
    public const string SparesFolder = "spares";
    public const string AccountFile = ".claude.json";

    private readonly string? _defaultConfigDir;
    private readonly string _storeDir;
    private readonly string _home;

    /// <param name="defaultConfigDir">The isolated default config folder (<see cref="LicenseSwitching.DefaultConfigDirVariable"/>),
    /// or null for the machine's real one, <c>~/.claude</c>.</param>
    /// <param name="storeDir">The key store's folder.</param>
    /// <param name="home">The user's home folder; the real default folder's account file is in it.</param>
    public LicenseHeldOnDisk(string? defaultConfigDir, string storeDir, string home)
    {
        if (string.IsNullOrWhiteSpace(storeDir)) throw new ArgumentException("A store folder is required", nameof(storeDir));
        if (string.IsNullOrWhiteSpace(home)) throw new ArgumentException("A home folder is required", nameof(home));
        _defaultConfigDir = string.IsNullOrWhiteSpace(defaultConfigDir) ? null : defaultConfigDir;
        _storeDir = storeDir;
        _home = home;
    }

    /// <summary>This machine's store: <paramref name="storeDir"/> when set, else <c>&lt;machine root&gt;/licenses</c>,
    /// where the tool keeps it when nothing overrides it.</summary>
    public static LicenseHeldOnDisk ForThisMachine(string? defaultConfigDir, string? storeDir) => new(
        defaultConfigDir,
        string.IsNullOrWhiteSpace(storeDir) ? Path.Combine(CcStorage.MachineRoot(), "licenses") : storeDir,
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    /// <summary>True when the store holds at least one spare.</summary>
    public bool HoldsSpare() => SpareEmails().Count > 0;

    /// <summary>Every spare's account, lower-cased, in order.</summary>
    public IReadOnlyList<string> SpareEmails()
    {
        var spares = Path.Combine(_storeDir, SparesFolder);
        if (!Directory.Exists(spares)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(spares)
            .Select(folder => EmailIn(Path.Combine(folder, AccountFile)))
            .OfType<string>()
            .Distinct(StringComparer.Ordinal)
            .OrderBy(e => e, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The active account, lower-cased, or null when the default folder names none.</summary>
    public string? ActiveEmail() => EmailIn(ActiveAccountFile());

    /// <summary>What the machine holds, as a report lists it: the active account first, then each spare, with no
    /// usage numbers - nothing was read from the provider.</summary>
    public IReadOnlyList<LicenseHolding> Holdings()
    {
        var active = ActiveEmail();
        var holdings = new List<LicenseHolding>();
        if (active is not null) holdings.Add(new LicenseHolding { AccountEmail = active, IsActive = true });
        holdings.AddRange(SpareEmails().Where(e => e != active).Select(e => new LicenseHolding { AccountEmail = e }));
        return holdings;
    }

    private string ActiveAccountFile()
    {
        var realDefault = Path.Combine(_home, ".claude");
        if (_defaultConfigDir is null || SameFolder(_defaultConfigDir, realDefault))
            return Path.Combine(_home, AccountFile);
        return Path.Combine(_defaultConfigDir, AccountFile);
    }

    private static bool SameFolder(string a, string b) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    /// <summary>The <c>oauthAccount.emailAddress</c> in <paramref name="accountFile"/>, lower-cased, or null when the
    /// file is missing or names no account. A file that cannot be read as JSON names no account, exactly as the key
    /// store reads it, and is logged.</summary>
    private static string? EmailIn(string accountFile)
    {
        if (!File.Exists(accountFile)) return null;
        JsonNode? root;
        try
        {
            root = JsonNode.Parse(File.ReadAllText(accountFile));
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            FileLog.Write($"[LicenseHeldOnDisk] EmailIn FAILED: {accountFile} could not be read, so it names no account: {ex.Message}");
            return null;
        }
        if (root is not JsonObject obj || obj["oauthAccount"] is not JsonObject block) return null;
        if (block["emailAddress"] is not JsonValue value || value.GetValueKind() != JsonValueKind.String) return null;
        var email = value.GetValue<string>().Trim().ToLowerInvariant();
        return email.Length == 0 ? null : email;
    }
}
