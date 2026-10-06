using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// The name the team question suggested for this Director, "&lt;computer&gt; - &lt;team&gt;", recorded ONLY when the
/// Director was named exactly that (devthrottle_internal#2311, live proof F4). It is how a move knows the name is
/// the suggestion rather than one the person chose: a recorded suggestion that still matches the Director's name
/// follows the move to "&lt;computer&gt; - &lt;new team&gt;"; a name the person typed has no record and is never touched.
/// </summary>
/// <param name="MachineName">The computer's name the suggestion was built from.</param>
/// <param name="Name">The suggested name the Director was given.</param>
public sealed record DirectorNameSuggestion(string MachineName, string Name)
{
    /// <summary>The suggestion for a Director on <paramref name="machineName"/> working for <paramref name="teamName"/>.</summary>
    public static DirectorNameSuggestion For(string machineName, string teamName)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        if (string.IsNullOrWhiteSpace(teamName))
            throw new ArgumentException("teamName is required", nameof(teamName));
        var machine = machineName.Trim();
        return new DirectorNameSuggestion(machine, $"{machine} - {teamName.Trim()}");
    }

    /// <summary>
    /// What setup records when the Director was named <paramref name="givenName"/> for <paramref name="team"/>: the
    /// suggestion when the name is exactly it, otherwise null - the person typed a name of their own.
    /// </summary>
    public static DirectorNameSuggestion? IfSuggested(string machineName, DirectorTeam team, string givenName)
    {
        ArgumentNullException.ThrowIfNull(team);
        var suggestion = For(machineName, team.Name);
        return string.Equals((givenName ?? "").Trim(), suggestion.Name, StringComparison.Ordinal) ? suggestion : null;
    }
}

/// <summary>What a move does to the Director's name: the new name, or null to keep it; and what is recorded after.</summary>
/// <param name="NewName">The name to rename the Director to, or null when its name stays.</param>
/// <param name="Record">The suggestion to record after the move, or null to record none.</param>
public sealed record DirectorNameAfterMove(string? NewName, DirectorNameSuggestion? Record)
{
    /// <summary>
    /// The one rule. With no recorded suggestion - a name the person typed, or a Director set up before this was
    /// recorded - the name stays. With a recorded suggestion the Director still carries, the name follows to the
    /// suggestion for <paramref name="newTeam"/>, on the same computer name. With a recorded suggestion the person has
    /// since renamed away from, the name stays and the record is dropped: the name is theirs now.
    /// </summary>
    public static DirectorNameAfterMove Decide(string? currentName, DirectorNameSuggestion? recorded, DirectorTeam newTeam)
    {
        ArgumentNullException.ThrowIfNull(newTeam);
        if (recorded is null)
            return new DirectorNameAfterMove(null, null);
        if (!string.Equals((currentName ?? "").Trim(), recorded.Name, StringComparison.Ordinal))
            return new DirectorNameAfterMove(null, null);
        var next = DirectorNameSuggestion.For(recorded.MachineName, newTeam.Name);
        return new DirectorNameAfterMove(next.Name, next);
    }
}

/// <summary>
/// Applies <see cref="DirectorNameAfterMove.Decide"/> to the running Director: reads its name and its recorded
/// suggestion, renames it when the rule says so, and records what the rule says. Every outside dependency is passed
/// in, so a test drives exactly what a move runs.
/// </summary>
public sealed class DirectorNameFollower
{
    private readonly Func<string?> _currentName;
    private readonly Action<string> _rename;
    private readonly Func<DirectorNameSuggestion?> _loadSuggestion;
    private readonly Action<DirectorNameSuggestion?> _saveSuggestion;

    /// <param name="currentName">The Director's name now.</param>
    /// <param name="rename">Renames the Director.</param>
    /// <param name="loadSuggestion">Reads the recorded suggestion, or null when none is recorded.</param>
    /// <param name="saveSuggestion">Records a suggestion, or with null removes the record.</param>
    public DirectorNameFollower(
        Func<string?> currentName,
        Action<string> rename,
        Func<DirectorNameSuggestion?> loadSuggestion,
        Action<DirectorNameSuggestion?> saveSuggestion)
    {
        _currentName = currentName ?? throw new ArgumentNullException(nameof(currentName));
        _rename = rename ?? throw new ArgumentNullException(nameof(rename));
        _loadSuggestion = loadSuggestion ?? throw new ArgumentNullException(nameof(loadSuggestion));
        _saveSuggestion = saveSuggestion ?? throw new ArgumentNullException(nameof(saveSuggestion));
    }

    /// <summary>
    /// Follow a move to <paramref name="newTeam"/>. Returns the Director's new name, or null when its name stays.
    /// Renames before recording, so a failed rename leaves the old record matching the old name.
    /// </summary>
    public string? FollowMove(DirectorTeam newTeam)
    {
        ArgumentNullException.ThrowIfNull(newTeam);
        var recorded = _loadSuggestion();
        var decision = DirectorNameAfterMove.Decide(_currentName(), recorded, newTeam);
        if (decision.NewName is not null)
        {
            _rename(decision.NewName);
            _saveSuggestion(decision.Record);
            FileLog.Write("[DirectorNameFollower] FollowMove: the name was the suggestion, so it follows the move");
            return decision.NewName;
        }
        if (recorded is not null)
            _saveSuggestion(null);
        FileLog.Write($"[DirectorNameFollower] FollowMove: name kept ({(recorded is null ? "no suggestion recorded" : "renamed by the person since")})");
        return null;
    }
}

/// <summary>
/// Records the suggested name of THIS Director in its own storage home, beside its team file:
/// <c>&lt;home&gt;/config/director/director-name-suggestion.json</c>. No file means no suggestion is recorded - the person
/// typed the name, or the Director was set up before this was recorded - and a move then leaves the name alone.
/// </summary>
public static class DirectorNameSuggestionStore
{
    private const string FileName = "director-name-suggestion.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    /// <summary>This process's suggestion file, in its own storage home.</summary>
    public static string SuggestionFile => Path.Combine(CcStorage.Config(), "director", FileName);

    /// <summary>The suggestion file of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static string SuggestionFileAt(string storageRoot)
    {
        if (string.IsNullOrWhiteSpace(storageRoot))
            throw new ArgumentException("storageRoot is required", nameof(storageRoot));
        return Path.Combine(storageRoot, "config", "director", FileName);
    }

    /// <summary>This Director's recorded suggestion, or null.</summary>
    public static DirectorNameSuggestion? Load() => LoadFile(SuggestionFile);

    /// <summary>Record this Director's suggestion, or with null remove the record.</summary>
    public static void Save(DirectorNameSuggestion? suggestion) => SaveFile(SuggestionFile, suggestion);

    /// <summary>The recorded suggestion of the Director whose storage home is <paramref name="storageRoot"/>, or null.</summary>
    public static DirectorNameSuggestion? LoadAt(string storageRoot) => LoadFile(SuggestionFileAt(storageRoot));

    /// <summary>Record, or with null remove, the suggestion of the Director whose storage home is <paramref name="storageRoot"/>.</summary>
    public static void SaveAt(string storageRoot, DirectorNameSuggestion? suggestion) => SaveFile(SuggestionFileAt(storageRoot), suggestion);

    private static DirectorNameSuggestion? LoadFile(string path)
    {
        if (!File.Exists(path))
        {
            FileLog.Write($"[DirectorNameSuggestionStore] Load: no suggestion recorded at {path}");
            return null;
        }
        var stored = JsonSerializer.Deserialize<StoredSuggestion>(File.ReadAllText(path), JsonOptions)
            ?? throw new InvalidDataException($"The name suggestion file {path} is empty.");
        if (string.IsNullOrWhiteSpace(stored.MachineName) || string.IsNullOrWhiteSpace(stored.Name))
            throw new InvalidDataException($"The name suggestion file {path} has no computer name or no name.");
        FileLog.Write($"[DirectorNameSuggestionStore] Load: a suggestion is recorded at {path}");
        return new DirectorNameSuggestion(stored.MachineName, stored.Name);
    }

    private static void SaveFile(string path, DirectorNameSuggestion? suggestion)
    {
        if (suggestion is null)
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                FileLog.Write($"[DirectorNameSuggestionStore] Save: record removed at {path}");
            }
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        // Written to a temporary file and moved into place, so a crash mid-write never leaves half a record.
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(
            new StoredSuggestion { MachineName = suggestion.MachineName, Name = suggestion.Name }, JsonOptions));
        File.Move(temp, path, overwrite: true);
        FileLog.Write($"[DirectorNameSuggestionStore] Save: suggestion recorded at {path}");
    }

    private sealed class StoredSuggestion
    {
        public string? MachineName { get; set; }
        public string? Name { get; set; }
    }
}
