using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// Parses the Gateway fleet endpoints used by the "start a new session" flow:
/// GET /directors -> <see cref="DirectorInfo"/>[] and
/// GET /directors/{id}/repos -> <see cref="RepoInfo"/>[]. Also builds the
/// POST /directors/{id}/sessions request body. Pure (System.Text.Json only, no
/// MAUI/Android) so it is unit tested off-device. Field names are matched
/// case-insensitively against the server DTOs.
/// </summary>
public static class FleetParser
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parse a GET /directors JSON array into the fleet list. Entries without a
    /// directorId are skipped (they cannot be addressed). Sorted most-recently-seen
    /// first (so the picker can default-select the top one), tie-broken by machine
    /// name. Throws on malformed JSON - a list the client cannot parse is a real
    /// error to surface, not something to paper over.
    /// </summary>
    public static List<DirectorInfo> ParseDirectors(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<DirectorInfo>();

        var dtos = JsonSerializer.Deserialize<List<DirectorDto>>(json, Opts)
                   ?? new List<DirectorDto>();

        var list = new List<DirectorInfo>(dtos.Count);
        foreach (var d in dtos)
        {
            if (string.IsNullOrWhiteSpace(d.DirectorId)) continue;

            // The reachable base URL for this Director is its tailnet endpoint when it
            // registered one (a remote machine over Tailscale), else its control
            // endpoint (a local, file-discovered Director on the Gateway's own box).
            // This mirrors how the Gateway stamps each session's tailnetEndpoint, so a
            // session we create reaches the Director the same way the roster does.
            var tailnet = (d.TailnetEndpoint ?? "").TrimEnd('/');
            var control = (d.ControlEndpoint ?? "").TrimEnd('/');
            list.Add(new DirectorInfo
            {
                DirectorId = d.DirectorId!,
                MachineName = d.MachineName ?? "",
                TailnetEndpoint = string.IsNullOrWhiteSpace(tailnet) ? control : tailnet,
                LastSeen = d.LastSeen,
                Version = d.Version ?? "",
            });
        }

        list.Sort((a, b) =>
        {
            var bySeen = Nullable.Compare(b.LastSeen, a.LastSeen);   // newest first; nulls last
            if (bySeen != 0) return bySeen;
            return string.Compare(a.MachineName, b.MachineName, StringComparison.OrdinalIgnoreCase);
        });
        return list;
    }

    /// <summary>
    /// Parse a GET /directors/{id}/repos JSON array into the recent-repos list.
    /// Entries without a path are skipped. Sorted newest-used first (repos with no
    /// lastUsed sort last). The server already sorts this way; we re-sort so a mixed
    /// or unsorted body is still correct. Throws on malformed JSON.
    /// </summary>
    public static List<RepoInfo> ParseRepos(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<RepoInfo>();

        var dtos = JsonSerializer.Deserialize<List<RepoDto>>(json, Opts)
                   ?? new List<RepoDto>();

        var list = new List<RepoInfo>(dtos.Count);
        foreach (var r in dtos)
        {
            if (string.IsNullOrWhiteSpace(r.Path)) continue;
            list.Add(new RepoInfo
            {
                Name = r.Name ?? "",
                Path = r.Path!,
                LastUsed = r.LastUsed,
            });
        }

        list.Sort((a, b) => Nullable.Compare(b.LastUsed, a.LastUsed));   // newest first; nulls last
        return list;
    }

    /// <summary>
    /// Build the POST /directors/{id}/sessions request body for a new session in
    /// <paramref name="repoPath"/>. camelCase keys bind to the server's
    /// NewSessionRequest (ASP.NET model binding is case-insensitive). Optional fields
    /// are omitted when not set so the server applies its own defaults. Throws when
    /// <paramref name="repoPath"/> is blank - the server requires it, so failing here
    /// gives a clear client-side error instead of a 400 round-trip.
    /// </summary>
    public static string BuildCreateBody(
        string repoPath, string agent = "ClaudeCode", string? type = null, bool wingmanEnabled = false)
    {
        if (string.IsNullOrWhiteSpace(repoPath))
            throw new ArgumentException("repoPath is required to create a session", nameof(repoPath));

        var body = new Dictionary<string, object?>
        {
            ["repoPath"] = repoPath.Trim(),
            ["agent"] = string.IsNullOrWhiteSpace(agent) ? "ClaudeCode" : agent.Trim(),
            ["wingmanEnabled"] = wingmanEnabled,
        };
        if (!string.IsNullOrWhiteSpace(type))
            body["type"] = type!.Trim();

        return JsonSerializer.Serialize(body);
    }

    /// <summary>Wire shape matching the server DirectorDto fields the client needs.</summary>
    private sealed class DirectorDto
    {
        public string? DirectorId { get; set; }
        public string? MachineName { get; set; }
        public string? TailnetEndpoint { get; set; }
        public string? ControlEndpoint { get; set; }
        public DateTime? LastSeen { get; set; }
        public string? Version { get; set; }
    }

    /// <summary>Wire shape matching the server RepositoryDto.</summary>
    private sealed class RepoDto
    {
        public string? Name { get; set; }
        public string? Path { get; set; }
        public DateTime? LastUsed { get; set; }
    }
}
