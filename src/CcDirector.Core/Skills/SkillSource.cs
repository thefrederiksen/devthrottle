using CcDirector.Core.Configuration;
using CcDirector.Core.Teams;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Skills;

/// <summary>
/// WHICH library a skill on disk came from: the Gateway it was served by and the account on that Gateway -
/// the person's own personal account, or one team (devthrottle_internal#2311).
///
/// WHY THE INSTALLED SKILL HAS TO SAY THIS. The skill folders are per USER, not per Director
/// (<see cref="SkillInstallTargets.For"/>): every Director on the computer writes into the same
/// <c>~/.agents/skills</c> and <c>~/.claude/skills</c>. The marker used to say only "DevThrottle installed
/// this", so a Director reconciling its own library deleted every marked folder its library did not hold -
/// including the ones another Director had put there. Observed live (#2311 live proof, finding F7): two test
/// Directors on a Gateway serving a different skill set removed six of the person's skills, and the person's
/// own Directors put them back about half an hour later, back and forth forever.
///
/// WHY THE GATEWAY AND THE ACCOUNT, AND NOT THE DIRECTOR. One person's two Directors on the same personal
/// account are served the same library and must SHARE their folders - a Director id would make them fight
/// exactly as before. The account alone is not enough either: a personal account on a test Gateway is a
/// different library from the personal account on the hosted one, and that is precisely the F7 case. So the
/// identity is the pair. A self-hosted Gateway is reachable at several addresses (machine name, Tailscale,
/// local network - issue #1233), so a Director recognises its OWN stamp under any address its configuration
/// lists, and stamps the active one.
/// </summary>
/// <param name="GatewayUrl">The Gateway address stamped on what this source installs, normalized.</param>
/// <param name="TeamId">The team on that Gateway, or null for the person's own personal account.</param>
/// <param name="KnownGatewayUrls">Every address this Director knows its Gateway by, normalized; includes
/// <paramref name="GatewayUrl"/>.</param>
public sealed record SkillSource(string GatewayUrl, string? TeamId, IReadOnlyList<string> KnownGatewayUrls)
{
    /// <summary>The marker line that names the Gateway.</summary>
    public const string GatewayKey = "gateway=";

    /// <summary>The marker line that names the account: <c>personal</c> or <c>team:&lt;id&gt;</c>.</summary>
    public const string AccountKey = "account=";

    private const string PersonalAccount = "personal";
    private const string TeamPrefix = "team:";

    /// <summary>True when this is the person's own personal account, which wins every name clash.</summary>
    public bool IsPersonal => TeamId is null;

    /// <summary>A source on one Gateway address.</summary>
    public static SkillSource On(string gatewayUrl, string? teamId)
    {
        var url = Normalize(gatewayUrl);
        return new SkillSource(url, string.IsNullOrWhiteSpace(teamId) ? null : teamId.Trim(), new[] { url });
    }

    /// <summary>
    /// The source THIS Director installs from: its own Gateway configuration and its own team file, both in
    /// its own storage home. Null when no Gateway is configured - there is then no library to install from.
    /// No team file means the personal account, because every enrollment that writes no file binds it
    /// (see <see cref="DirectorTeamStore"/>).
    /// </summary>
    public static SkillSource? ForThisDirector()
    {
        var config = GatewayConfig.Load();
        var urls = config.CandidateUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(Normalize).Distinct().ToList();
        if (urls.Count == 0)
        {
            FileLog.Write("[SkillSource] ForThisDirector: no gateway.url configured - no skill source");
            return null;
        }
        var team = DirectorTeamStore.Load();
        var source = new SkillSource(urls[0], team?.TeamId, urls);
        FileLog.Write($"[SkillSource] ForThisDirector: {source.Describe()}");
        return source;
    }

    /// <summary>The two marker lines this source appends to what it installs.</summary>
    public string MarkerLines() =>
        $"{GatewayKey}{GatewayUrl}\n{AccountKey}{(IsPersonal ? PersonalAccount : TeamPrefix + TeamId)}\n";

    /// <summary>One line for a log or a placement message.</summary>
    public string Describe() =>
        $"{(IsPersonal ? "the personal account" : "team " + TeamId)} on {GatewayUrl}";

    /// <summary>True when <paramref name="stamp"/> was written by this source, under any of its addresses.</summary>
    public bool Wrote(SkillSourceStamp stamp) =>
        KnownGatewayUrls.Contains(stamp.GatewayUrl, StringComparer.OrdinalIgnoreCase)
        && string.Equals(stamp.TeamId, TeamId, StringComparison.Ordinal);

    /// <summary>
    /// Read who installed a marked folder from its marker's lines. Null when the marker carries no source -
    /// one written before sources were recorded, which <see cref="SkillDirectoryInstaller"/> treats as the
    /// personal account's.
    /// </summary>
    public static SkillSourceStamp? ReadStamp(IReadOnlyList<string> markerLines)
    {
        string? gateway = null, account = null;
        foreach (var raw in markerLines)
        {
            var line = raw.Trim();
            if (line.StartsWith(GatewayKey, StringComparison.Ordinal))
                gateway = line[GatewayKey.Length..];
            else if (line.StartsWith(AccountKey, StringComparison.Ordinal))
                account = line[AccountKey.Length..];
        }
        if (gateway is null || account is null)
            return null;
        if (account == PersonalAccount)
            return new SkillSourceStamp(Normalize(gateway), null);
        // Anything else is a team. An account spelled some way this code does not know is therefore never
        // mistaken for the person's own, and never for this source - so it is only ever kept, not deleted.
        return new SkillSourceStamp(Normalize(gateway),
            account.StartsWith(TeamPrefix, StringComparison.Ordinal) ? account[TeamPrefix.Length..] : account);
    }

    /// <summary>An address compared the way a person means it: no trailing slash, no case difference.</summary>
    public static string Normalize(string url) => url.Trim().TrimEnd('/').ToLowerInvariant();
}

/// <summary>The source recorded on one installed skill folder.</summary>
/// <param name="GatewayUrl">The Gateway address it was stamped with, normalized.</param>
/// <param name="TeamId">The team, or null for the personal account.</param>
public sealed record SkillSourceStamp(string GatewayUrl, string? TeamId)
{
    /// <summary>True when the personal account installed it.</summary>
    public bool IsPersonal => TeamId is null;

    /// <summary>One line for a log or a placement message.</summary>
    public string Describe() => $"{(IsPersonal ? "the personal account" : "team " + TeamId)} on {GatewayUrl}";
}
