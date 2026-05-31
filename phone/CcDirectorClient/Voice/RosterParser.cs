using System.Text.Json;

namespace CcDirectorClient.Voice;

/// <summary>
/// Parses the Gateway GET /sessions JSON payload into <see cref="SessionInfo"/>
/// objects. Pure (System.Text.Json only, no MAUI/Android) so it is unit tested
/// off-device. Field names are matched case-insensitively against the server's
/// SessionDto.
/// </summary>
public static class RosterParser
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>
    /// Parse a /sessions JSON array into a roster. Exited sessions are dropped
    /// (they cannot be talked to). Throws on malformed JSON - a roster the client
    /// cannot parse is a real error to surface, not something to paper over.
    /// </summary>
    public static List<SessionInfo> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new List<SessionInfo>();

        var dtos = JsonSerializer.Deserialize<List<RosterDto>>(json, Opts)
                   ?? new List<RosterDto>();

        var result = new List<SessionInfo>(dtos.Count);
        foreach (var d in dtos)
        {
            if (string.Equals(d.ActivityState, "Exited", StringComparison.OrdinalIgnoreCase))
                continue;
            if (string.Equals(d.Status, "Exited", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(d.Status, "Failed", StringComparison.OrdinalIgnoreCase))
                continue;

            result.Add(new SessionInfo
            {
                SessionId = d.SessionId ?? "",
                Name = d.Name,
                RepoPath = d.RepoPath ?? "",
                ActivityState = d.ActivityState ?? "",
                StatusColor = string.IsNullOrWhiteSpace(d.StatusColor) ? "unknown" : d.StatusColor!,
                LastStatusReason = d.LastStatusReason ?? "",
                TailnetEndpoint = (d.TailnetEndpoint ?? "").TrimEnd('/'),
                MachineName = d.MachineName ?? "",
                VoiceMode = d.VoiceMode,
                OnHold = d.OnHold,
                WingmanEnabled = d.WingmanEnabled,
            });
        }
        return result;
    }

    /// <summary>Wire shape matching the server SessionDto fields the client needs.</summary>
    private sealed class RosterDto
    {
        public string? SessionId { get; set; }
        public string? Name { get; set; }
        public string? RepoPath { get; set; }
        public string? Status { get; set; }
        public string? ActivityState { get; set; }
        public string? StatusColor { get; set; }
        public string? LastStatusReason { get; set; }
        public string? TailnetEndpoint { get; set; }
        public string? MachineName { get; set; }
        public bool VoiceMode { get; set; }
        public bool OnHold { get; set; }
        public bool WingmanEnabled { get; set; } = false;
    }
}
