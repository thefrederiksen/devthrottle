using System.Text.RegularExpressions;

namespace CcDirector.Gateway.Alerts;

/// <summary>
/// HOW AN ALERT NAMES A SESSION (the Alerts mission, phase 5). The owner reads "Release Manager (121) wants to message
/// Release Manager (125)", never "Let 121 Release - Manager message 125 Release - Release Manager?". Every source names
/// the sessions it mentions through here, from what its own record already holds - no source looks anything new up.
///
/// A session's name follows the fleet convention "Mission - Role - what this seat does". The part an owner recognises
/// is the mission and the role: "Release - Manager" reads "Release Manager", and "Release - Release Manager" reads
/// "Release Manager" too, because a role that already starts with its mission's words is not given them twice. A name
/// that is not in that shape is used as it is.
/// </summary>
public static class AlertWords
{
    private static readonly Regex AskedBySession = new(@"^Session (?<number>\d+) ""(?<name>.*)""$", RegexOptions.CultureInvariant);
    private static readonly Regex AskedBySessionWithoutNumber = new(@"^Session ""(?<name>.*)""$", RegexOptions.CultureInvariant);
    // At most seven digits: a short id is always eight characters, and one made only of digits is still a short id.
    private static readonly Regex NameWithNumber = new(@"^(?<name>.+) \((?<number>\d{1,7})\)$", RegexOptions.CultureInvariant);
    private static readonly Regex NameWithShortId = new(@"^(?<name>.+) \([0-9a-f]{8}\)$", RegexOptions.CultureInvariant);

    /// <summary>"Release Manager (121)", or the role alone when the session has no number.</summary>
    public static string SessionLabel(int? number, string? name)
    {
        var role = string.IsNullOrWhiteSpace(name) ? null : Role(name);
        return (number, role) switch
        {
            ({ } n, { } r) => $"{r} ({n:000})",
            ({ } n, null) => $"Session {n:000}",
            (null, { } r) => r,
            _ => "A session this Gateway no longer knows",
        };
    }

    /// <summary>The mission and role of a session name: "Alerts - Developer - Phase 5" reads "Alerts Developer".</summary>
    public static string Role(string name)
    {
        var parts = name.Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2) return name.Trim();
        var mission = parts[0];
        var role = parts[1];
        return role.StartsWith(mission + " ", StringComparison.OrdinalIgnoreCase) || role.Equals(mission, StringComparison.OrdinalIgnoreCase)
            ? role
            : $"{mission} {role}";
    }

    /// <summary>A secret transfer's stored "asked by" (written by <c>SecretTransferEndpoints.AskedBy</c>) as the owner
    /// reads it. Its session form, <c>Session 104 "Release - Manager"</c>, becomes "Release Manager (104)"; its other
    /// forms ("You, on the phone", "The cc-secrets window or a terminal on mac-mini") are already plain words.</summary>
    public static string FromSecretAskedBy(string askedBy)
    {
        var match = AskedBySession.Match(askedBy);
        if (match.Success) return SessionLabel(int.Parse(match.Groups["number"].Value), match.Groups["name"].Value);
        match = AskedBySessionWithoutNumber.Match(askedBy);
        if (match.Success) return SessionLabel(null, match.Groups["name"].Value);
        return askedBy;
    }

    /// <summary>A restart request's stored requester (written by <c>DirectorRestartRequestService.RequesterName</c>) as
    /// the owner reads it. Its usual form, "Release - Manager (121)", reads "Release Manager (121)"; a session with no
    /// number is stored with its short id, "Release - Manager (1512339f)", and reads "Release Manager"; the other forms
    /// ("a device on this account") are plain words, capitalised to start a sentence.</summary>
    public static string FromRestartRequester(string requester)
    {
        var numbered = NameWithNumber.Match(requester);
        if (numbered.Success) return SessionLabel(int.Parse(numbered.Groups["number"].Value), numbered.Groups["name"].Value);
        var match = NameWithShortId.Match(requester);
        var who = match.Success ? Role(match.Groups["name"].Value) : requester.Trim();
        return who.Length == 0 ? who : char.ToUpperInvariant(who[0]) + who[1..];
    }
}
