using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Reads the GitHub token from the shared credentials file at point of use, so
/// the secret only enters the process when a remote session is actually created.
/// File: %LOCALAPPDATA%\cc-director\config\credentials.env (KEY=VALUE per line).
/// </summary>
public static class GitHubCredentials
{
    public const string TokenKey = "GITHUB_TOKEN";

    /// <summary>
    /// Resolve the credentials.env path. Honors LOCALAPPDATA on Windows and falls
    /// back to the OS-specific local application data folder elsewhere.
    /// </summary>
    public static string CredentialsPath
    {
        get
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return Path.Combine(localAppData, "cc-director", "config", "credentials.env");
        }
    }

    /// <summary>
    /// Read GITHUB_TOKEN. Throws with an explicit, fixable message when the file
    /// is missing or the key is absent - no silent fallback to an empty token.
    /// </summary>
    public static string ReadToken()
    {
        var path = CredentialsPath;
        if (!File.Exists(path))
        {
            FileLog.Write($"[GitHubCredentials] credentials.env not found at {path}");
            throw new InvalidOperationException(
                $"GitHub token not configured. Create {path} and add a line: {TokenKey}=<your token>. " +
                "The token needs repo, actions, and issues scopes.");
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line[..eq].Trim();
            if (!string.Equals(key, TokenKey, StringComparison.Ordinal)) continue;
            var value = line[(eq + 1)..].Trim().Trim('"');
            if (string.IsNullOrEmpty(value))
                throw new InvalidOperationException(
                    $"{TokenKey} is present in {path} but empty. Set it to a GitHub token with repo, actions, and issues scopes.");
            return value;
        }

        throw new InvalidOperationException(
            $"{TokenKey} not found in {path}. Add a line: {TokenKey}=<your token> (needs repo, actions, and issues scopes).");
    }
}
