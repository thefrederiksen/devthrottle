namespace CcDirector.Gateway.Contracts;

/// <summary>
/// How a rule recognises a repository: by the git remote named <c>origin</c>, reduced to one spelling
/// (mission decision D3, Tech Lead ruling P6). The Director computes it from a session's checkout and the
/// Gateway computes it from the repository an account wrote on a rule; the two are compared as plain text,
/// so this is the ONE place the reduction lives. Every worktree of a repository has the same remote, so
/// every worktree gets that repository's rules without being listed.
///
/// The reduction: strip the scheme, any user name and password, and any port; turn the scp form
/// <c>git@host:owner/repo</c> into <c>host/owner/repo</c>; drop a trailing <c>.git</c> and trailing
/// slashes; lower-case all of it. A checkout with no remote has no identity, which is the empty string.
/// </summary>
public static class GitRemoteIdentity
{
    /// <summary>The one spelling of a remote, or "" when there is no remote.</summary>
    public static string Normalise(string? remote)
    {
        var text = remote?.Trim().ToLowerInvariant() ?? "";
        if (text.Length == 0) return "";

        var schemeEnd = text.IndexOf("://", StringComparison.Ordinal);
        string authority;
        string path;
        if (schemeEnd >= 0)
        {
            // URL form: scheme://[user[:password]@]host[:port]/path
            var rest = text[(schemeEnd + 3)..];
            var slash = rest.IndexOf('/');
            authority = slash >= 0 ? rest[..slash] : rest;
            path = slash >= 0 ? rest[(slash + 1)..] : "";
            authority = StripUser(authority);
            authority = StripPort(authority);
        }
        else
        {
            // The scp form [user@]host:path, or text that is already host/path.
            var slash = text.IndexOf('/');
            var colon = text.IndexOf(':');
            if (colon >= 0 && (slash < 0 || colon < slash))
            {
                authority = StripUser(text[..colon]);
                path = text[(colon + 1)..];
            }
            else
            {
                authority = slash >= 0 ? StripUser(text[..slash]) : StripUser(text);
                path = slash >= 0 ? text[(slash + 1)..] : "";
            }
        }

        path = path.TrimEnd('/');
        if (path.EndsWith(".git", StringComparison.Ordinal)) path = path[..^4];
        path = path.Trim('/');

        return path.Length == 0 ? authority : $"{authority}/{path}";
    }

    private static string StripUser(string authority)
    {
        var at = authority.LastIndexOf('@');
        return at >= 0 ? authority[(at + 1)..] : authority;
    }

    private static string StripPort(string authority)
    {
        var colon = authority.LastIndexOf(':');
        if (colon < 0) return authority;
        var port = authority[(colon + 1)..];
        return port.Length > 0 && port.All(char.IsAsciiDigit) ? authority[..colon] : authority;
    }
}
