using CcDirector.Core.Secrets;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Backends;

/// <summary>
/// Reads the GitHub token from the cc-secrets store at point of use, so the secret only enters the
/// process when a remote session is actually created. The store is the only source: the entry is
/// <see cref="TokenEntry"/>, added by the owner with <c>cc-secrets add github-token</c>.
/// </summary>
public static class GitHubCredentials
{
    /// <summary>The cc-secrets entry holding the token (its environment variable name is GITHUB_TOKEN).</summary>
    public const string TokenEntry = "github-token";

    /// <summary>
    /// Read the token. Throws <see cref="SecretNotAvailableException"/> with an explicit, fixable message
    /// when the store or the entry is missing - no silent fallback to an empty token.
    /// </summary>
    public static string ReadToken()
    {
        FileLog.Write("[GitHubCredentials] ReadToken");
        return SecretStoreReader.ReadSecret(TokenEntry);
    }
}
