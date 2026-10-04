using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// Remembers which team THIS Director works for (devthrottle_internal#2311), in the Director's own storage
/// home, beside the device key the Gateway issued for that team:
/// <c>&lt;home&gt;/config/director/gateway-team.json</c>, next to <c>gateway-token.txt</c>.
///
/// Never machine-wide. Every Director instance has its own home (<c>CC_DIRECTOR_ROOT</c> points each one at
/// <c>instances\&lt;slug&gt;</c>), so two Directors on one computer hold two different teams and two different
/// keys - which is the owner's ruling of 3 October 2026.
///
/// Three states, and each means something different:
/// <list type="bullet">
/// <item>No file: the Gateway has no teams (Teams is not released there) or this Director never enrolled. No
/// chip is shown, and nothing differs from before Teams.</item>
/// <item>A team id: the Director works for that team.</item>
/// <item>A null team id: the Director works for the person's own personal account.</item>
/// </list>
/// </summary>
public static class DirectorTeamStore
{
    private const string FileName = "gateway-team.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>
    /// Raised after this process saves or clears its own Director's team, so the title bar can redraw its
    /// chip. Raised on the thread that made the change; a subscriber that touches a control dispatches.
    /// </summary>
    public static event Action? Changed;

    /// <summary>This process's team file, in its own storage home.</summary>
    public static string TeamFile => Path.Combine(CcStorage.Config(), "director", FileName);

    /// <summary>The team file of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static string TeamFileAt(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("storageRoot is required", nameof(storageRoot));
        return Path.Combine(storageRoot, "config", "director", FileName);
    }

    /// <summary>This Director's team, or null when none is recorded (no chip).</summary>
    public static DirectorTeam? Load() => LoadFile(TeamFile);

    /// <summary>The team of the Director whose storage home is <paramref name="storageRoot"/>, or null.</summary>
    public static DirectorTeam? LoadAt(string storageRoot) => LoadFile(TeamFileAt(storageRoot));

    /// <summary>Record the team this Director works for, and tell the title bar.</summary>
    public static void Save(DirectorTeam team)
    {
        SaveFile(TeamFile, team);
        Changed?.Invoke();
    }

    /// <summary>Record the team of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static void SaveAt(string storageRoot, DirectorTeam team) => SaveFile(TeamFileAt(storageRoot), team);

    /// <summary>Forget this Director's team - its Gateway has no teams, or it was disconnected.</summary>
    public static void Clear()
    {
        ClearFile(TeamFile);
        Changed?.Invoke();
    }

    /// <summary>Forget the team of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static void ClearAt(string storageRoot) => ClearFile(TeamFileAt(storageRoot));

    private static DirectorTeam? LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            FileLog.Write($"[DirectorTeamStore] Load: no team recorded at {path}");
            return null;
        }

        var stored = JsonSerializer.Deserialize<StoredTeam>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"The team file {path} is empty.");
        if (string.IsNullOrWhiteSpace(stored.TeamName))
            throw new InvalidDataException($"The team file {path} has no team name.");

        var team = new DirectorTeam(string.IsNullOrWhiteSpace(stored.TeamId) ? null : stored.TeamId, stored.TeamName);
        FileLog.Write($"[DirectorTeamStore] Load: {Describe(team)} from {path}");
        return team;
    }

    private static void SaveFile(string path, DirectorTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (string.IsNullOrWhiteSpace(team.Name))
            throw new ArgumentException("A team needs a name to be recorded.", nameof(team));

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // Written to a temporary file and moved into place, so a crash mid-write never leaves half a team.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(new StoredTeam { TeamId = team.TeamId, TeamName = team.Name }, JsonOptions));
        File.Move(temp, path, overwrite: true);
        FileLog.Write($"[DirectorTeamStore] Save: {Describe(team)} to {path}");
    }

    private static void ClearFile(string path)
    {
        if (!File.Exists(path))
        {
            FileLog.Write($"[DirectorTeamStore] Clear: nothing recorded at {path}");
            return;
        }
        File.Delete(path);
        FileLog.Write($"[DirectorTeamStore] Clear: deleted {path}");
    }

    private static string Describe(DirectorTeam team)
        => team.IsPersonal ? "personal account" : $"team {team.TeamId}";

    private sealed class StoredTeam
    {
        public string? TeamId { get; set; }
        public string? TeamName { get; set; }
    }
}
