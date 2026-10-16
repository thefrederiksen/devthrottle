using CcDirector.Core.Storage;

namespace CcDirector.ControlApi;

/// <summary>
/// Issue #4078: the files a session's file viewer never serves, however they were reached - inside the
/// working folder, named by the agent, or inside a folder the agent named. The owner's rule opens any file an
/// agent names in the conversation; this list is the exception to it, and it is checked on the RESOLVED REAL
/// path, so a symbolic link or junction with an innocent name that leads to a secret is refused like the
/// secret itself.
///
/// Two kinds of entry:
///   - NAME rules: a folder name anywhere in the path (<c>.ssh</c>, a browser's profile folder) or a file name
///     (<c>gateway-token.txt</c>, <c>.env*</c>, a private key, a credentials file). Compared ignoring case on
///     every platform - refusing a case variant of a secret's name can only refuse too much, never too little.
///   - PROTECTED ROOTS: the paths <see cref="CcStorage.ProtectedPaths"/> already names as never to be touched
///     (the cc-secrets store, the config folder with the OAuth tokens in it, the key vault file, the account
///     credentials, the automation browser profiles). Composed from that list rather than restated, so a
///     path added there is protected here too.
/// </summary>
internal sealed class SessionFileSecrets
{
    /// <summary>A folder with any of these names, anywhere in the path, holds secrets.</summary>
    private static readonly HashSet<string> SecretFolderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".ssh",
        ".gnupg",
        ".aws",
        ".azure",
        ".kube",
        ".docker",
        ".password-store",
        // Browser profiles: the signed-in state of the owner's browsers.
        "User Data",          // Chrome, Edge and Brave on Windows keep every profile under "...\User Data\"
        "google-chrome",      // Chrome on Linux
        "chromium",
        "microsoft-edge",     // Edge on Linux
        "BraveSoftware",
        ".mozilla",           // Firefox on Linux
    };

    /// <summary>A pair of folder names, one inside the other, that is a browser profile root.</summary>
    private static readonly (string Parent, string Child)[] SecretFolderPairs =
    {
        ("Google", "Chrome"),     // Chrome on macOS: Library/Application Support/Google/Chrome
        ("Microsoft", "Edge"),    // Edge on macOS
        ("Mozilla", "Firefox"),   // Firefox on Windows and macOS
    };

    /// <summary>A file with exactly one of these names is a secret.</summary>
    private static readonly HashSet<string> SecretFileNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "gateway-token.txt",
        ".git-credentials",
        ".netrc",
        "_netrc",
        ".npmrc",
        ".pypirc",
        ".pgpass",
        "credentials",
        "credentials.json",
        ".credentials.json",
        "secrets.json",
        "auth.json",
    };

    /// <summary>A file whose name begins with one of these is a secret (<c>.env</c>, <c>.env.local</c>, <c>id_rsa.pub</c>).</summary>
    private static readonly string[] SecretFileNamePrefixes =
    {
        ".env",
        "id_rsa",
        "id_dsa",
        "id_ecdsa",
        "id_ed25519",
    };

    /// <summary>A file with one of these extensions holds a key or a certificate with its key.</summary>
    private static readonly HashSet<string> SecretFileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pem",
        ".key",
        ".pfx",
        ".p12",
        ".kdbx",
        ".jks",
        ".keystore",
    };

    private readonly Func<IEnumerable<string>> _protectedRoots;

    /// <param name="protectedRoots">
    /// The protected roots, read at the moment of each decision. Production passes
    /// <see cref="ForThisMachine"/>; a test passes folders it made.
    /// </param>
    public SessionFileSecrets(Func<IEnumerable<string>> protectedRoots)
    {
        _protectedRoots = protectedRoots ?? throw new ArgumentNullException(nameof(protectedRoots));
    }

    /// <summary>The list for this machine: the name rules plus every path in <see cref="CcStorage.ProtectedPaths"/>.</summary>
    public static SessionFileSecrets ForThisMachine() =>
        new(() => CcStorage.ProtectedPaths().Select(p => p.Path));

    /// <summary>
    /// True when <paramref name="path"/> is, or lies inside, a secret. Pass the RESOLVED REAL path (and, for
    /// good measure, the lexical one as well): the name a link carries says nothing about what it leads to.
    /// </summary>
    public bool IsSecret(string path)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        var segments = path.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < segments.Length; i++)
        {
            if (SecretFolderNames.Contains(segments[i]))
                return true;
            if (i > 0)
            {
                foreach (var (parent, child) in SecretFolderPairs)
                {
                    if (string.Equals(segments[i - 1], parent, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(segments[i], child, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
        }

        if (segments.Length > 0 && IsSecretFileName(segments[^1]))
            return true;

        return IsUnderProtectedRoot(path);
    }

    /// <summary>The file-name half of <see cref="IsSecret"/>, for filtering a folder listing by entry name.</summary>
    public static bool IsSecretFileName(string name)
    {
        if (string.IsNullOrEmpty(name))
            return false;
        if (SecretFileNames.Contains(name))
            return true;
        foreach (var prefix in SecretFileNamePrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return SecretFileExtensions.Contains(Path.GetExtension(name));
    }

    private bool IsUnderProtectedRoot(string path)
    {
        string full;
        try { full = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(path)); }
        catch (ArgumentException) { return true; } // not a path that can be shown to be safe - refuse

        foreach (var root in _protectedRoots())
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;
            // Compare against both the root as named and the root's real identity, so a protected root that
            // is itself reached through a link still protects what is under it.
            foreach (var candidate in new[] { root, ControlEndpoints.ResolveRealPath(root) })
            {
                if (candidate is null)
                    continue;
                var rootFull = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(candidate));
                if (string.Equals(full, rootFull, StringComparison.OrdinalIgnoreCase)
                    || full.StartsWith(ControlEndpoints.ContainmentPrefix(rootFull), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
}
