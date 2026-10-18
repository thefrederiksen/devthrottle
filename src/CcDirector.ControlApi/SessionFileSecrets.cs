using System.Runtime.InteropServices;
using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;

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
        ".terraform.d",       // Terraform's credentials.tfrc.json
        "gcloud",             // Google Cloud: application_default_credentials.json, credentials.db, access_tokens.db
        "keyrings",           // GNOME keyring: ~/.local/share/keyrings
        "GitHub CLI",         // the GitHub command-line tool's token on Windows (%APPDATA%\GitHub CLI)
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
        (".config", "gh"),        // the GitHub command-line tool's token: ~/.config/gh/hosts.yml
        (".config", "rclone"),    // rclone's remotes and their keys
        (".cargo", "credentials.toml"),
        (".cargo", "credentials"),
        ("Library", "Keychains"), // macOS keychains: ~/Library/Keychains and /Library/Keychains
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
        "rclone.conf",
        ".vault-token",
        ".boto",
        ".s3cfg",
        ".my.cnf",
        ".dockercfg",
        ".terraformrc",
        "terraform.rc",
        // Shell and console history: every command typed, tokens pasted on a command line included.
        ".bash_history",
        ".zsh_history",
        ".sh_history",
        ".history",
        ".python_history",
        ".node_repl_history",
        ".psql_history",
        ".mysql_history",
        ".sqlite_history",
        ".lesshst",
        "ConsoleHost_history.txt",
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
    private readonly IReadOnlyList<string> _homeFolders;

    /// <param name="protectedRoots">
    /// The protected roots, read at the moment of each decision. Production passes
    /// <see cref="ForThisMachine"/>; a test passes folders it made.
    /// </param>
    /// <param name="homeFolder">
    /// The user's home folder, for <see cref="IsHiddenUnderHome"/>. Null means no home rule (a test of the name
    /// rules alone); production always passes the real one.
    /// </param>
    public SessionFileSecrets(Func<IEnumerable<string>> protectedRoots, string? homeFolder = null)
        : this(protectedRoots, string.IsNullOrWhiteSpace(homeFolder) ? Array.Empty<string>() : new[] { homeFolder })
    {
    }

    /// <param name="protectedRoots">As above.</param>
    /// <param name="homeFolders">
    /// Every folder that is the user's home by any account (issue #4087): the HOME or USERPROFILE variable can
    /// point somewhere other than the real profile folder, and the hidden-files rule must guard all of them.
    /// </param>
    public SessionFileSecrets(Func<IEnumerable<string>> protectedRoots, IReadOnlyList<string> homeFolders)
    {
        _protectedRoots = protectedRoots ?? throw new ArgumentNullException(nameof(protectedRoots));
        ArgumentNullException.ThrowIfNull(homeFolders);
        _homeFolders = homeFolders.Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
    }

    /// <summary>
    /// The list for this machine: the name rules, every path in <see cref="CcStorage.ProtectedPaths"/>, the
    /// Windows credential stores, and the hidden-files-under-home rule for every home folder this user has.
    /// </summary>
    public static SessionFileSecrets ForThisMachine()
    {
        var homes = MachineHomeFolders();
        if (homes.Count == 0)
            throw new InvalidOperationException("The user's home folder is not known, so the files under it cannot be protected.");
        var credentialRoots = OperatingSystem.IsWindows()
            ? WindowsCredentialRoots(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData))
            : Array.Empty<string>();
        return new SessionFileSecrets(
            () => CcStorage.ProtectedPaths().Select(p => p.Path).Concat(credentialRoots),
            homes);
    }

    /// <summary>
    /// Every home folder of the user this process runs as: the one .NET reports, the HOME and USERPROFILE
    /// variables, and the REAL one - the home in the password file on Linux and macOS, the profile folder
    /// Windows records for the account. A variable can be changed by whoever started the process; the real
    /// home is where the user's credentials actually are, so it is guarded whatever the variables say.
    /// </summary>
    internal static IReadOnlyList<string> MachineHomeFolders() =>
        MachineHomeFolders(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            Environment.GetEnvironmentVariable("HOME"),
            Environment.GetEnvironmentVariable("USERPROFILE"));

    /// <summary>
    /// <see cref="MachineHomeFolders()"/> with the reported folder and the two variables given, so a test can
    /// point all three somewhere else and see that the real home is still among the result.
    /// </summary>
    internal static IReadOnlyList<string> MachineHomeFolders(string? reportedProfile, string? homeVariable, string? userProfileVariable)
    {
        var realHome = OperatingSystem.IsWindows() ? WindowsProfileFolder() : PasswordFileHome();
        var homes = HomeFolders(reportedProfile, homeVariable, userProfileVariable, realHome);
        FileLog.Write($"[SessionFileSecrets] MachineHomeFolders: real={realHome ?? "(unknown)"}, homes={string.Join(";", homes)}");
        return homes;
    }

    /// <summary>
    /// The distinct, fully qualified home folders among <paramref name="candidates"/>. Pure, so the rule that
    /// the real home is guarded alongside a redirected HOME is testable on any machine.
    /// </summary>
    internal static IReadOnlyList<string> HomeFolders(params string?[] candidates)
    {
        var homes = new List<string>();
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !Path.IsPathFullyQualified(candidate))
                continue;
            var full = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(candidate));
            if (!homes.Contains(full, StringComparer.OrdinalIgnoreCase))
                homes.Add(full);
        }
        return homes;
    }

    /// <summary>
    /// The home folder the password file gives this process's user (getpwuid_r), which HOME does not change.
    /// Linux and macOS only; the layout of <c>struct passwd</c> differs between them, so each is read by its
    /// own offset. Null on any other system or when the account has no entry.
    /// </summary>
    internal static string? PasswordFileHome()
    {
        // Both begin: char* name, char* passwd, uint uid, uint gid.
        int homeOffset;
        if (OperatingSystem.IsLinux())
            homeOffset = 2 * IntPtr.Size + 8 + IntPtr.Size;          // ... char* gecos, char* DIR
        else if (OperatingSystem.IsMacOS())
            homeOffset = 2 * IntPtr.Size + 8 + 8 + 2 * IntPtr.Size;  // ... long change, char* class, char* gecos, char* DIR
        else
            return null;

        var entry = Marshal.AllocHGlobal(256);       // larger than either struct passwd
        var buffer = Marshal.AllocHGlobal(16384);
        try
        {
            var error = GetPasswordEntry(GetUserId(), entry, buffer, 16384, out var result);
            if (error != 0 || result == IntPtr.Zero)
            {
                FileLog.Write($"[SessionFileSecrets] PasswordFileHome: no entry, error={error}");
                return null;
            }
            return Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(result, homeOffset));
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            Marshal.FreeHGlobal(entry);
        }
    }

    [DllImport("libc", EntryPoint = "getuid")]
    private static extern uint GetUserId();

    [DllImport("libc", EntryPoint = "getpwuid_r")]
    private static extern int GetPasswordEntry(uint uid, IntPtr entry, IntPtr buffer, nuint bufferSize, out IntPtr result);

    /// <summary>
    /// The profile folder Windows records for this account (the ProfileList entry for the user's security
    /// identifier), which the USERPROFILE and HOME variables do not change. Null off Windows or when absent.
    /// </summary>
    internal static string? WindowsProfileFolder()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value;
        if (sid is null)
            return null;
        using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        return key?.GetValue("ProfileImagePath") is string path ? Environment.ExpandEnvironmentVariables(path) : null;
    }

    /// <summary>
    /// Where Windows and the common tools keep credentials under the roaming and local application-data
    /// folders (these are not dot-folders, so the home rule does not see them). Pure, so it is testable anywhere.
    /// </summary>
    internal static IReadOnlyList<string> WindowsCredentialRoots(string? appData, string? localAppData)
    {
        var roots = new List<string>();
        if (!string.IsNullOrWhiteSpace(appData))
        {
            roots.Add(Path.Combine(appData, "Microsoft", "Credentials"));
            roots.Add(Path.Combine(appData, "Microsoft", "Protect"));
            roots.Add(Path.Combine(appData, "Microsoft", "Vault"));
            roots.Add(Path.Combine(appData, "Microsoft", "SystemCertificates"));
            roots.Add(Path.Combine(appData, "GitHub CLI"));
            roots.Add(Path.Combine(appData, "gcloud"));
            roots.Add(Path.Combine(appData, "rclone"));
            roots.Add(Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine"));
        }
        if (!string.IsNullOrWhiteSpace(localAppData))
        {
            roots.Add(Path.Combine(localAppData, "Microsoft", "Credentials"));
            roots.Add(Path.Combine(localAppData, "Microsoft", "Vault"));
            roots.Add(Path.Combine(localAppData, "Microsoft", "IdentityCache"));
            roots.Add(Path.Combine(localAppData, "Microsoft", "TokenBroker"));
            roots.Add(Path.Combine(localAppData, ".IdentityService"));
        }
        return roots;
    }

    /// <summary>
    /// True when <paramref name="path"/> is a hidden (dot) file or folder DIRECTLY under a home folder, or lies
    /// anywhere inside one: ~/.config, ~/.cargo, ~/.bash_history, ~/.anything. Most tools keep their tokens in
    /// such a place, and a list of names will always miss one. Compared against every home folder, as named and
    /// by its real identity.
    ///
    /// <paramref name="workingFolder"/> is the session's working folder when <paramref name="path"/> lies inside
    /// it, and null otherwise. The rule then stands aside only when that working folder is itself inside the
    /// same hidden folder (a session that works IN ~/.something reads its own files). A working folder that IS
    /// the home folder, or holds it, gets the rule like any path outside (issue #4087).
    /// </summary>
    public bool IsHiddenUnderHome(string path, string? workingFolder = null)
    {
        if (_homeFolders.Count == 0 || string.IsNullOrEmpty(path))
            return false;
        string full;
        try { full = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(path)); }
        catch (ArgumentException) { return true; } // not a path that can be shown to be safe - refuse

        foreach (var home in _homeFolders.Concat<string?>(_homeFolders.Select(ControlEndpoints.ResolveRealPath)))
        {
            if (home is null)
                continue;
            var homeFull = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(home));
            var prefix = ControlEndpoints.ContainmentPrefix(homeFull);
            if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                continue;
            var firstSegment = full[prefix.Length..].Split(new[] { '/', '\\' }, 2)[0];
            if (!firstSegment.StartsWith('.'))
                continue;
            if (workingFolder is not null && IsWithin(workingFolder, prefix + firstSegment))
                continue;
            return true;
        }
        return false;
    }

    // True when the folder, as named or by its real identity, is the hidden entry or lies inside it.
    private static bool IsWithin(string folder, string hiddenEntry)
    {
        foreach (var candidate in new[] { folder, ControlEndpoints.ResolveRealPath(folder) })
        {
            if (candidate is null)
                continue;
            string candidateFull;
            try { candidateFull = ControlEndpoints.NormalizeDirectoryPath(Path.GetFullPath(candidate)); }
            catch (ArgumentException) { continue; }
            if (string.Equals(candidateFull, hiddenEntry, StringComparison.OrdinalIgnoreCase)
                || candidateFull.StartsWith(ControlEndpoints.ContainmentPrefix(hiddenEntry), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

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
        // Windows drops trailing dots and spaces from a name, so "server.pem." and "server.pem " open server.pem.
        name = name.TrimEnd('.', ' ');
        if (name.Length == 0)
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
