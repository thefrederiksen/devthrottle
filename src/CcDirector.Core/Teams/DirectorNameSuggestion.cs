using System.Text.Json;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Teams;

/// <summary>
/// The name the team question suggested for this Director, "&lt;computer&gt; - &lt;team&gt;", recorded ONLY when the
/// Director was named exactly that (devthrottle_internal#2311, live proof F4). It is how a move knows the name is
/// the suggestion rather than one the person chose: a recorded suggestion that still matches the Director's name
/// follows the move to "&lt;computer&gt; - &lt;new team&gt;"; a name the person typed has no record and is never touched.
/// A record belongs to the ONE team it was suggested for (review RM-F5): a move follows it only when the Director is
/// leaving that team, so a record left behind anywhere else can never rename anything.
/// </summary>
/// <param name="TeamId">The team the suggestion was made for - never the personal account.</param>
/// <param name="MachineName">The computer's name the suggestion was built from.</param>
/// <param name="Name">The suggested name the Director was given.</param>
public sealed record DirectorNameSuggestion(string TeamId, string MachineName, string Name)
{
    /// <summary>The suggested name, "&lt;computer&gt; - &lt;team&gt;", for any choice the team question offers.</summary>
    public static string NameFor(string machineName, string teamName)
    {
        if (string.IsNullOrWhiteSpace(machineName))
            throw new ArgumentException("machineName is required", nameof(machineName));
        if (string.IsNullOrWhiteSpace(teamName))
            throw new ArgumentException("teamName is required", nameof(teamName));
        return $"{machineName.Trim()} - {teamName.Trim()}";
    }

    /// <summary>The suggestion recorded for a Director on <paramref name="machineName"/> working for the TEAM
    /// <paramref name="team"/>. The personal account has no record.</summary>
    public static DirectorNameSuggestion For(string machineName, DirectorTeam team)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (team.IsPersonal || string.IsNullOrWhiteSpace(team.TeamId))
            throw new ArgumentException("A name suggestion is recorded only for a team, never the personal account.", nameof(team));
        return new DirectorNameSuggestion(team.TeamId, machineName.Trim(), NameFor(machineName, team.Name));
    }

    /// <summary>
    /// What setup records when the Director was named <paramref name="givenName"/> for <paramref name="team"/>: the
    /// suggestion when the team is a TEAM and the name is exactly its suggestion, otherwise null - the person typed a
    /// name of their own, or chose the personal account. A personal Director is never renamed by a move (brief item 3,
    /// review RM-F2), so its setup records nothing even when it kept "&lt;computer&gt; - Personal".
    /// </summary>
    public static DirectorNameSuggestion? IfSuggested(string machineName, DirectorTeam team, string givenName)
    {
        ArgumentNullException.ThrowIfNull(team);
        if (team.IsPersonal)
            return null;
        var suggestion = For(machineName, team);
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
    /// since renamed away from, the name stays and the record is dropped: the name is theirs now. A move to the
    /// personal account keeps the name and drops the record, so a personal Director never carries one and is never
    /// renamed by a later move (review RM-F2). And a record follows ONLY when the team being left is the team it was
    /// suggested for (review RM-F5): one left behind by a failed removal applies to no other move.
    /// </summary>
    /// <param name="currentName">The Director's name now.</param>
    /// <param name="recorded">The recorded suggestion, or null.</param>
    /// <param name="leavingTeamId">The team the Director is leaving, or null for the personal account or none recorded.</param>
    /// <param name="newTeam">The team it is moving to.</param>
    public static DirectorNameAfterMove Decide(string? currentName, DirectorNameSuggestion? recorded, string? leavingTeamId,
        DirectorTeam newTeam)
    {
        ArgumentNullException.ThrowIfNull(newTeam);
        if (recorded is null || newTeam.IsPersonal)
            return new DirectorNameAfterMove(null, null);
        if (leavingTeamId is null || !string.Equals(recorded.TeamId, leavingTeamId, StringComparison.Ordinal))
            return new DirectorNameAfterMove(null, null);
        if (!string.Equals((currentName ?? "").Trim(), recorded.Name, StringComparison.Ordinal))
            return new DirectorNameAfterMove(null, null);
        var next = DirectorNameSuggestion.For(recorded.MachineName, newTeam);
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
    private readonly Func<string?> _leavingTeamId;
    private readonly Action<string> _rename;
    private readonly Func<DirectorNameSuggestion?> _loadSuggestion;
    private readonly Action<DirectorNameSuggestion?> _saveSuggestion;

    /// <param name="currentName">The Director's name now.</param>
    /// <param name="leavingTeamId">The team the Director works for now - the one a move leaves - or null.</param>
    /// <param name="rename">Renames the Director.</param>
    /// <param name="loadSuggestion">Reads the recorded suggestion, or null when none is recorded.</param>
    /// <param name="saveSuggestion">Records a suggestion, or with null removes the record.</param>
    public DirectorNameFollower(
        Func<string?> currentName,
        Func<string?> leavingTeamId,
        Action<string> rename,
        Func<DirectorNameSuggestion?> loadSuggestion,
        Action<DirectorNameSuggestion?> saveSuggestion)
    {
        _currentName = currentName ?? throw new ArgumentNullException(nameof(currentName));
        _leavingTeamId = leavingTeamId ?? throw new ArgumentNullException(nameof(leavingTeamId));
        _rename = rename ?? throw new ArgumentNullException(nameof(rename));
        _loadSuggestion = loadSuggestion ?? throw new ArgumentNullException(nameof(loadSuggestion));
        _saveSuggestion = saveSuggestion ?? throw new ArgumentNullException(nameof(saveSuggestion));
    }

    /// <summary>
    /// Follow a move to <paramref name="newTeam"/>. Returns the Director's new name, or null when its name stays.
    /// Renames before recording, so a failed rename leaves the old record matching the old name. A record that cannot
    /// be saved AFTER the rename is never left behind as the old one (review RM-F3): the old record is removed - no
    /// record means the name is never followed again, the safe state - and
    /// <see cref="NameChangedButNotRecordedException"/> says the name changed but will not follow later moves.
    /// </summary>
    public string? FollowMove(DirectorTeam newTeam)
    {
        ArgumentNullException.ThrowIfNull(newTeam);
        var recorded = _loadSuggestion();
        var decision = DirectorNameAfterMove.Decide(_currentName(), recorded, _leavingTeamId(), newTeam);
        if (decision.NewName is not null)
        {
            _rename(decision.NewName);
            try
            {
                _saveSuggestion(decision.Record);
            }
            catch (Exception saveError)
            {
                FileLog.Write($"[DirectorNameFollower] FollowMove: renamed, but the new suggestion could not be recorded: {saveError.Message}");
                try
                {
                    _saveSuggestion(null);
                    FileLog.Write("[DirectorNameFollower] FollowMove: the old suggestion record was removed, so the name will not follow later moves");
                }
                catch (Exception removeError)
                {
                    FileLog.Write($"[DirectorNameFollower] FollowMove: the old suggestion record could NOT be removed either ({removeError.Message}); " +
                                  "it no longer matches the name, so the next move will keep the name and try to remove it again");
                }
                throw new NameChangedButNotRecordedException(decision.NewName, saveError);
            }
            FileLog.Write("[DirectorNameFollower] FollowMove: the name was the suggestion, so it follows the move");
            return decision.NewName;
        }
        if (recorded is not null)
        {
            // The record no longer applies. Removing it is tidiness, not safety: a record follows only when the
            // Director leaves the team it was made for, so one that survives a failed removal renames nothing (RM-F5).
            try
            {
                _saveSuggestion(null);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[DirectorNameFollower] FollowMove: a record that no longer applies could NOT be removed ({ex.Message}); " +
                              $"it belongs to team {recorded.TeamId} and can follow only a move out of that team");
            }
        }
        FileLog.Write($"[DirectorNameFollower] FollowMove: name kept ({(recorded is null ? "no suggestion recorded" : "renamed by the person since")})");
        return null;
    }
}

/// <summary>
/// The Director WAS renamed for its new team, but the record that its name is the suggestion could not be saved, so
/// the old record was removed and the name will not follow later moves (review RM-F3).
/// </summary>
public sealed class NameChangedButNotRecordedException : Exception
{
    /// <summary>The name the Director now has.</summary>
    public string NewName { get; }

    public NameChangedButNotRecordedException(string newName, Exception inner)
        : base($"renamed to \"{newName}\", but the record of its suggested name could not be saved: {inner.Message}", inner)
    {
        NewName = newName;
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
        if (string.IsNullOrWhiteSpace(stored.TeamId))
        {
            // Written before a record named its team (review RM-F5): it cannot be tied to a team, so it is no record.
            FileLog.Write($"[DirectorNameSuggestionStore] Load: the record at {path} names no team, so it is treated as no record");
            return null;
        }
        FileLog.Write($"[DirectorNameSuggestionStore] Load: a suggestion is recorded at {path}");
        return new DirectorNameSuggestion(stored.TeamId, stored.MachineName, stored.Name);
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
            new StoredSuggestion { TeamId = suggestion.TeamId, MachineName = suggestion.MachineName, Name = suggestion.Name }, JsonOptions));
        File.Move(temp, path, overwrite: true);
        FileLog.Write($"[DirectorNameSuggestionStore] Save: suggestion recorded at {path}");
    }

    private sealed class StoredSuggestion
    {
        public string? TeamId { get; set; }
        public string? MachineName { get; set; }
        public string? Name { get; set; }
    }
}
