using System.Runtime.InteropServices;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// Issue #4078: the session file access rule. The owner's rule is "if a path is listed by an agent I can see
/// remotely, I can download it" - so a path the session's own conversation names may be opened even outside the
/// working folder - and every July protection still holds for every path, however it was admitted: the real
/// path is resolved (symbolic links and junctions), a file with more than one name is refused, traversal cannot
/// reach past a name, and the secrets list is refused even when named.
///
/// Every link and hard link here is REAL, made on disk; a host that cannot make one fails the test loudly with
/// the reason and never skips into a false green (the same discipline as PathContainmentLinkEscapeTests and
/// PathContainmentHardLinkTests). Named paths are written in backticks, the way an agent writes a path, and
/// found by the same LinkDetector the Director uses in production.
///
/// In the "DirectorRoot" collection: CC_DIRECTOR_ROOT is redirected to an isolated temp root (the handler's
/// default secrets list reads CcStorage, which follows it).
/// </summary>
[Collection("DirectorRoot")]
public sealed class SessionFileAccessTests : IDisposable
{
    private readonly string _base;
    private readonly string? _prevRoot;
    private readonly string _work;       // the session's working folder
    private readonly string _outside;    // a folder outside it
    private readonly string _protected;  // a stand-in for a CcStorage protected root (the cc-secrets store folder)
    private readonly string _home;       // a stand-in for the user's home folder
    private readonly SessionFileSecrets _secrets;

    public SessionFileAccessTests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _base = Path.Combine(Path.GetTempPath(), "ccd-4078-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Path.Combine(_base, "director-root"));
        _work = Path.Combine(_base, "repo");
        _outside = Path.Combine(_base, "briefs");
        _protected = Path.Combine(_base, "protected-store");
        _home = Path.Combine(_base, "home");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(_protected);
        Directory.CreateDirectory(_home);
        _secrets = new SessionFileSecrets(() => new[] { _protected }, _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        try { if (Directory.Exists(_base)) Directory.Delete(_base, recursive: true); } catch { /* best effort */ }
    }

    // ------------------------------------------------------------------------------ helpers ----

    /// <summary>The conversation names each of these paths, in backticks, as an agent writes one.</summary>
    private static SessionNamedPaths Named(params string[] paths) =>
        SessionNamedPaths.FromTexts(paths.Select(p => $"I wrote the brief to `{p}` for you."));

    private string Write(string path, string content = "content")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    private SessionFileAccess.Decision Decide(string path, SessionNamedPaths named) =>
        SessionFileAccess.Decide(_work, path, named, _secrets);

    private static void CreateFileLink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Assert.Fail($"This host cannot create a FILE symbolic link ({ex.Message}). On Windows that needs Developer " +
                        "Mode or elevation. The link-to-a-secret refusal cannot be proven without a real link, so this " +
                        "test fails loudly instead of silently skipping into a false green.");
        }
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0, "test setup: the link was not created as a link");
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string newFileName, string existingFileName, IntPtr attributes);

    [DllImport("libc", EntryPoint = "link", SetLastError = true)]
    private static extern int UnixLink(string existingPath, string newPath);

    private static void CreateHardLink(string alias, string target)
    {
        if (OperatingSystem.IsWindows())
        {
            if (!CreateHardLinkW(alias, target, IntPtr.Zero))
                Assert.Fail($"test setup: CreateHardLink failed with Windows error {Marshal.GetLastWin32Error()}; this test needs a real hard link.");
        }
        else if (UnixLink(target, alias) != 0)
        {
            Assert.Fail($"test setup: link() failed with errno {Marshal.GetLastWin32Error()}; this test needs a real hard link.");
        }
        Assert.True(File.Exists(alias), "test setup: the hard link was not created");
        Assert.True((File.GetAttributes(alias) & FileAttributes.ReparsePoint) == 0, "test setup: made a symbolic link, not a hard link");
    }

    // ------------------------------------------------------------- the owner's rule: named files ----

    [Fact]
    public void Decide_namedPathOutsideTheWorkingFolder_isAllowed()
    {
        // The reported symptom: D:\mz-briefs\...pdf, named by the agent, refused because it was outside the repo.
        var brief = Write(Path.Combine(_outside, "brief.pdf"));

        var decision = Decide(brief, Named(brief));

        Assert.Equal(PathStatusCodes.File, decision.Code);
        Assert.Equal(Path.GetFullPath(brief), decision.FullPath);
    }

    [Fact]
    public void Decide_unnamedPathOutsideTheWorkingFolder_isRefused()
    {
        var brief = Write(Path.Combine(_outside, "brief.pdf"));

        var decision = Decide(brief, SessionNamedPaths.Empty);

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Equal(SessionFileAccess.NotAllowedSentence, decision.Sentence);
        Assert.Null(decision.FullPath);
    }

    [Fact]
    public void Decide_siblingOfANamedFile_isRefused()
    {
        var named = Write(Path.Combine(_outside, "brief.pdf"));
        var sibling = Write(Path.Combine(_outside, "other.pdf"));

        var decision = Decide(sibling, Named(named));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
    }

    [Fact]
    public void Decide_parentFolderOfANamedFile_isRefused()
    {
        // Naming a file never admits the folder above it.
        var named = Write(Path.Combine(_outside, "brief.pdf"));

        var decision = Decide(_outside, Named(named));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Throws<UnauthorizedAccessException>(() => SessionFileAccess.ListFolder(_work, _outside, Named(named), _secrets));
    }

    [Fact]
    public void Decide_traversalFromANamedFolder_isRefused()
    {
        // "<named>/../secret-sibling" spells a path that STARTS at a named folder; it must be decided on what it
        // resolves to, which nothing named.
        var namedFolder = Path.Combine(_outside, "hosting");
        Directory.CreateDirectory(namedFolder);
        var elsewhere = Write(Path.Combine(_base, "elsewhere", "private.txt"));
        var traversal = namedFolder + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + ".."
                        + Path.DirectorySeparatorChar + "elsewhere" + Path.DirectorySeparatorChar + "private.txt";

        var decision = Decide(traversal, Named(namedFolder));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.True(File.Exists(elsewhere));
    }

    [Fact]
    public void Decide_traversalFromANamedFile_isRefused()
    {
        var named = Write(Path.Combine(_outside, "brief.pdf"));
        Write(Path.Combine(_outside, "other.pdf"));
        var traversal = named + Path.DirectorySeparatorChar + ".." + Path.DirectorySeparatorChar + "other.pdf";

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(traversal, Named(named)).Code);
    }

    [Fact]
    public void Decide_traversalOutOfTheWorkingFolder_isRefused()
    {
        Write(Path.Combine(_outside, "brief.pdf"));
        var traversal = Path.Combine(_work, "..", Path.GetFileName(_outside), "brief.pdf");

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(traversal, SessionNamedPaths.Empty).Code);
    }

    [Fact]
    public void Decide_namedPathThatIsNotOnDisk_saysNotOnDisk()
    {
        var missing = Path.Combine(_outside, "not-pulled-yet.md");

        var decision = Decide(missing, Named(missing));

        Assert.Equal(PathStatusCodes.NotOnDisk, decision.Code);
        Assert.Equal(SessionFileAccess.NotOnDiskSentence, decision.Sentence);
    }

    [Fact]
    public void Decide_unnamedMissingPathOutside_saysNotAllowedNotNotOnDisk()
    {
        // Existence is only ever reported for an allowed path: asking about an unnamed path must not reveal
        // whether anything is there.
        var missing = Path.Combine(_outside, "nothing-here.md");

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(missing, SessionNamedPaths.Empty).Code);
    }

    [Fact]
    public void Decide_fileInsideTheWorkingFolder_isStillAllowed()
    {
        var inside = Write(Path.Combine(_work, "docs", "README.md"));

        Assert.Equal(PathStatusCodes.File, Decide(inside, SessionNamedPaths.Empty).Code);
    }

    [Fact]
    public void Decide_missingFileInsideTheWorkingFolder_saysNotOnDisk()
    {
        Assert.Equal(PathStatusCodes.NotOnDisk, Decide(Path.Combine(_work, "nope.md"), SessionNamedPaths.Empty).Code);
    }

    // ------------------------------------------------------------------------- the secrets list ----

    public static IEnumerable<object[]> SecretRelativePaths() => new[]
    {
        new object[] { Path.Combine(".ssh", "id_rsa") },
        new object[] { Path.Combine(".ssh", "config") },
        new object[] { "gateway-token.txt" },
        new object[] { ".env" },
        new object[] { ".env.local" },
        new object[] { ".env.production" },
        new object[] { "id_ed25519" },
        new object[] { "server.pem" },
        new object[] { "signing.pfx" },
        new object[] { ".git-credentials" },
        new object[] { ".netrc" },
        new object[] { ".npmrc" },
        new object[] { "credentials.json" },
        new object[] { Path.Combine(".aws", "credentials") },
        new object[] { Path.Combine(".azure", "accessTokens.json") },
        new object[] { Path.Combine(".gnupg", "private-keys.kbx") },
        new object[] { Path.Combine(".kube", "config") },
        new object[] { Path.Combine("Google", "Chrome", "User Data", "Default", "Cookies") },
        new object[] { Path.Combine("Microsoft", "Edge", "User Data", "Default", "Login Data") },
        new object[] { Path.Combine("google-chrome", "Default", "Cookies") },
        new object[] { Path.Combine(".mozilla", "firefox", "abc.default", "logins.json") },
        new object[] { Path.Combine("Mozilla", "Firefox", "Profiles", "abc.default", "key4.db") },
        // Review of #4081, defect 1: each of these was admitted when named.
        new object[] { Path.Combine(".config", "gh", "hosts.yml") },
        new object[] { Path.Combine(".config", "gcloud", "application_default_credentials.json") },
        new object[] { Path.Combine(".config", "gcloud", "credentials.db") },
        new object[] { Path.Combine(".config", "gcloud", "access_tokens.db") },
        new object[] { Path.Combine(".cargo", "credentials.toml") },
        new object[] { Path.Combine(".cargo", "credentials") },
        new object[] { Path.Combine(".terraform.d", "credentials.tfrc.json") },
        new object[] { Path.Combine(".local", "share", "keyrings", "login.keyring") },
        new object[] { Path.Combine(".config", "rclone", "rclone.conf") },
        new object[] { "rclone.conf" },
        new object[] { ".vault-token" },
        new object[] { ".boto" },
        new object[] { ".s3cfg" },
        new object[] { ".my.cnf" },
        new object[] { ".dockercfg" },
        new object[] { ".bash_history" },
        new object[] { ".zsh_history" },
        new object[] { "ConsoleHost_history.txt" },
        new object[] { Path.Combine("GitHub CLI", "hosts.yml") },
    };

    [Theory]
    [MemberData(nameof(SecretRelativePaths))]
    public void Decide_secretEvenWhenNamed_isRefused(string relative)
    {
        var secret = Write(Path.Combine(_outside, relative), "secret");

        var decision = Decide(secret, Named(secret));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Equal(SessionFileAccess.SecretSentence, decision.Sentence);
        Assert.Null(decision.FullPath);
    }

    [Theory]
    [MemberData(nameof(SecretRelativePaths))]
    public void Decide_secretInsideTheWorkingFolder_isRefused(string relative)
    {
        // The list holds for every path however it was admitted - a .env in the repository included.
        var secret = Write(Path.Combine(_work, relative), "secret");

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(secret, SessionNamedPaths.Empty).Code);
    }

    [Fact]
    public void Decide_fileUnderAProtectedRootEvenWhenNamed_isRefused()
    {
        // The cc-secrets store, the config folder with the OAuth tokens, the Director's browser profiles: every
        // CcStorage protected root. Its file name is innocent; only its place makes it a secret.
        var store = Write(Path.Combine(_protected, "store.dat"), "secret");

        var decision = Decide(store, Named(store));

        Assert.Equal(SessionFileAccess.SecretSentence, decision.Sentence);
    }

    [Fact]
    public void ForThisMachine_coversEveryCcStorageProtectedPath()
    {
        var secrets = SessionFileSecrets.ForThisMachine();
        foreach (var protectedPath in CcDirector.Core.Storage.CcStorage.ProtectedPaths())
        {
            var inside = Path.Combine(protectedPath.Path, "anything.txt");
            Assert.True(secrets.IsSecret(inside), $"{protectedPath.Source} ({protectedPath.Path}) is not on the secrets list");
        }
    }

    [Fact]
    public void Decide_symbolicLinkFromANamedPathToASecret_isRefused()
    {
        // The link's own name and place are innocent and NAMED; what it leads to is the secret.
        var secret = Write(Path.Combine(_base, "home", ".ssh", "id_rsa"), "PRIVATE KEY");
        var link = Path.Combine(_outside, "notes.txt");
        CreateFileLink(link, secret);

        var decision = Decide(link, Named(link));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Equal(SessionFileAccess.SecretSentence, decision.Sentence);
    }

    [Fact]
    public void Decide_symbolicLinkInsideTheWorkingFolderToAProtectedRoot_isRefused()
    {
        var store = Write(Path.Combine(_protected, "store.dat"), "secret");
        var link = Path.Combine(_work, "store-shortcut.dat");
        CreateFileLink(link, store);

        Assert.Equal(SessionFileAccess.SecretSentence, Decide(link, SessionNamedPaths.Empty).Sentence);
    }

    [Fact]
    public void Decide_symbolicLinkFromANamedPathToAnOrdinaryFile_isAllowed()
    {
        // Control: following the link is not itself the refusal - only where it leads is.
        var target = Write(Path.Combine(_base, "elsewhere", "report.txt"));
        var link = Path.Combine(_outside, "report-link.txt");
        CreateFileLink(link, target);

        Assert.Equal(PathStatusCodes.File, Decide(link, Named(link)).Code);
    }

    // --------------------------------------------------------------------------- hard links ----

    [Fact]
    public void Decide_namedFileWithASecondName_isRefused()
    {
        var target = Write(Path.Combine(_base, "elsewhere", "private.txt"), "private");
        var alias = Path.Combine(_outside, "innocent.txt");
        CreateHardLink(alias, target);

        var decision = Decide(alias, Named(alias));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Equal(SessionFileAccess.HardLinkSentence, decision.Sentence);
    }

    [Fact]
    public void Decide_hardLinkInsideTheWorkingFolder_isRefused()
    {
        var target = Write(Path.Combine(_base, "elsewhere", "private.txt"), "private");
        var alias = Path.Combine(_work, "innocent.txt");
        CreateHardLink(alias, target);

        Assert.Equal(SessionFileAccess.HardLinkSentence, Decide(alias, SessionNamedPaths.Empty).Sentence);
    }

    // ------------------------------------------------------------------------------- folders ----

    [Fact]
    public void Decide_namedFolder_isAFolder()
    {
        var hosting = Path.Combine(_outside, "hosting");
        Directory.CreateDirectory(hosting);

        // The agent wrote it with a trailing separator, as the owner's report shows ("...\hosting\").
        var decision = Decide(hosting, Named(hosting + Path.DirectorySeparatorChar));

        Assert.Equal(PathStatusCodes.Folder, decision.Code);
        Assert.Equal(SessionFileAccess.FolderSentence, decision.Sentence);
    }

    [Fact]
    public void ListFolder_namedFolder_listsItsFilesWithoutSecretsOrSubfolders()
    {
        var hosting = Path.Combine(_outside, "hosting");
        Write(Path.Combine(hosting, "README.md"));
        Write(Path.Combine(hosting, "deploy.ps1"));
        Write(Path.Combine(hosting, ".env"), "secret");
        Write(Path.Combine(hosting, "nested", "inner.txt"));

        var listing = SessionFileAccess.ListFolder(_work, hosting, Named(hosting), _secrets);

        Assert.Equal(Path.GetFullPath(hosting), listing.CurrentPath);
        Assert.Null(listing.ParentPath);
        Assert.Equal(new[] { "deploy.ps1", "README.md" }, listing.Entries.Select(e => e.Name).ToArray());
        Assert.All(listing.Entries, e => Assert.True(e.IsFile));
    }

    [Fact]
    public void Decide_fileInANamedFolder_isAllowed()
    {
        var hosting = Path.Combine(_outside, "hosting");
        var readme = Write(Path.Combine(hosting, "README.md"));

        Assert.Equal(PathStatusCodes.File, Decide(readme, Named(hosting)).Code);
    }

    [Fact]
    public void Decide_fileTwoLevelsBelowANamedFolder_isRefused()
    {
        // Naming a folder admits the files directly in it, never the folders inside it.
        var hosting = Path.Combine(_outside, "hosting");
        var inner = Write(Path.Combine(hosting, "nested", "inner.txt"));

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(inner, Named(hosting)).Code);
        Assert.Equal(PathStatusCodes.NotAllowed, Decide(Path.Combine(hosting, "nested"), Named(hosting)).Code);
    }

    [Fact]
    public void Decide_secretInANamedFolder_isRefused()
    {
        var hosting = Path.Combine(_outside, "hosting");
        var env = Write(Path.Combine(hosting, ".env"), "secret");

        Assert.Equal(SessionFileAccess.SecretSentence, Decide(env, Named(hosting)).Sentence);
    }

    [Fact]
    public void Decide_namedSecretFolder_isRefused()
    {
        var ssh = Path.Combine(_base, "home", ".ssh");
        Write(Path.Combine(ssh, "id_rsa"));

        Assert.Equal(SessionFileAccess.SecretSentence, Decide(ssh, Named(ssh)).Sentence);
        Assert.Throws<UnauthorizedAccessException>(() => SessionFileAccess.ListFolder(_work, ssh, Named(ssh), _secrets));
    }

    [Fact]
    public void ListFolder_workingFolderSubfolder_listsFoldersAndFilesAndOffersItsParent()
    {
        var src = Path.Combine(_work, "src");
        Write(Path.Combine(src, "a.cs"));
        Directory.CreateDirectory(Path.Combine(src, "lib"));

        var listing = SessionFileAccess.ListFolder(_work, src, SessionNamedPaths.Empty, _secrets);

        Assert.Equal(new[] { "lib", "a.cs" }, listing.Entries.Select(e => e.Name).ToArray());
        Assert.False(listing.Entries[0].IsFile);
        Assert.True(listing.Entries[1].IsFile);
        Assert.Equal(Path.GetFullPath(_work), listing.ParentPath);
    }

    [Fact]
    public void ListFolder_theWorkingFolderItself_isListableAndOffersNoParent()
    {
        Write(Path.Combine(_work, "README.md"));

        var listing = SessionFileAccess.ListFolder(_work, _work, SessionNamedPaths.Empty, _secrets);

        Assert.Contains(listing.Entries, e => e.Name == "README.md");
        Assert.Null(listing.ParentPath);
    }

    // ------------------------------------------------------------------- the named-path set ----

    [Fact]
    public void SessionNamedPaths_isExactNeverAPrefix()
    {
        var named = Path.Combine(_outside, "brief.pdf");
        var set = Named(named);

        Assert.True(set.Contains(named));
        Assert.False(set.Contains(_outside));
        Assert.False(set.Contains(named + ".bak"));
        Assert.False(set.Contains(Path.Combine(_outside, "brief")));
    }

    [Fact]
    public void SessionNamedPaths_ignoresRelativePaths()
    {
        var set = SessionNamedPaths.FromTexts(new[] { "see `docs/brief.pdf` and `./x.txt`" });

        Assert.Equal(0, set.Count);
    }

    // ------------------------------------------------- the verbs: read-file and path-status ----

    private DirectorCommandResult ReadFile(DirectorUpStreamHandler handler, Session session, string path) => handler.Handle(new DirectorCommand
    {
        Verb = "read-file",
        SessionId = session.Id.ToString(),
        PayloadJson = SessionCommandExecutor.Serialize(new OpenStreamRequest { StreamId = Guid.NewGuid().ToString("N"), Path = path }),
    });

    private (SessionManager Sm, Session Session, DirectorUpStreamHandler Handler) Director(SessionNamedPaths named)
    {
        var sm = new SessionManager(new AgentOptions());
        var session = sm.CreateEmbeddedSession(_work, null, new ExecuteActionTestBackend());
        var handler = new DirectorUpStreamHandler(sm, async (_, frames) => { await foreach (var frame in frames) { } }, _ => named, _secrets);
        return (sm, session, handler);
    }

    [Fact]
    public void ReadFile_namedFileOutsideTheWorkingFolder_isServed()
    {
        var brief = Write(Path.Combine(_outside, "brief.pdf"), "pdf bytes");
        var (sm, session, handler) = Director(Named(brief));
        using (sm)
        {
            var result = ReadFile(handler, session, brief);

            Assert.True(result.Ok, result.Error);
            Assert.Equal(9, DirectorCommandRouterBody<OpenReadResponse>(result).TotalBytes);
        }
    }

    [Fact]
    public void ReadFile_unnamedFileOutsideTheWorkingFolder_isRefusedWithTheSentenceAndCode()
    {
        var brief = Write(Path.Combine(_outside, "brief.pdf"));
        var (sm, session, handler) = Director(SessionNamedPaths.Empty);
        using (sm)
        {
            var result = ReadFile(handler, session, brief);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(SessionFileAccess.NotAllowedSentence, result.Error);
            Assert.Equal(PathStatusCodes.NotAllowed, DirectorCommandRouterBody<PathStatusDto>(result).Code);
            Assert.Equal(0, handler.ActiveStreamCount);
        }
    }

    [Fact]
    public void ReadFile_namedFolder_isAConflictCarryingTheFolderCode()
    {
        var hosting = Path.Combine(_outside, "hosting");
        Directory.CreateDirectory(hosting);
        var (sm, session, handler) = Director(Named(hosting));
        using (sm)
        {
            var result = ReadFile(handler, session, hosting);

            Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
            Assert.Equal(PathStatusCodes.Folder, DirectorCommandRouterBody<PathStatusDto>(result).Code);
        }
    }

    [Fact]
    public void ReadFile_namedButMissing_isNotFoundSayingNotOnDisk()
    {
        var missing = Path.Combine(_outside, "not-pulled.md");
        var (sm, session, handler) = Director(Named(missing));
        using (sm)
        {
            var result = ReadFile(handler, session, missing);

            Assert.Equal(DirectorCommandStatus.NotFound, result.Status);
            Assert.Equal(SessionFileAccess.NotOnDiskSentence, result.Error);
        }
    }

    [Fact]
    public void PathStatus_answersEachPathInOrder()
    {
        var brief = Write(Path.Combine(_outside, "brief.pdf"));
        var missing = Path.Combine(_outside, "missing.md");
        var unnamed = Write(Path.Combine(_outside, "unnamed.txt"));
        var hosting = Path.Combine(_outside, "hosting");
        Directory.CreateDirectory(hosting);
        var named = Named(brief, missing, hosting);
        var (sm, session, _) = Director(named);
        using (sm)
        {
            var result = CatalogReadExecutor.PathStatus(sm, new DirectorCommand
            {
                Verb = "path-status",
                SessionId = session.Id.ToString(),
                PayloadJson = SessionCommandExecutor.Serialize(new PathStatusRequest { Paths = { brief, missing, unnamed, hosting } }),
            }, _ => named, _secrets);

            Assert.True(result.Ok, result.Error);
            var rows = DirectorCommandRouterBody<PathStatusResponse>(result).Paths;
            Assert.Equal(new[] { brief, missing, unnamed, hosting }, rows.Select(r => r.Path).ToArray());
            Assert.Equal(
                new[] { PathStatusCodes.File, PathStatusCodes.NotOnDisk, PathStatusCodes.NotAllowed, PathStatusCodes.Folder },
                rows.Select(r => r.Code).ToArray());
        }
    }

    [Fact]
    public void PathStatus_tooManyPaths_isRefused()
    {
        var (sm, session, _) = Director(SessionNamedPaths.Empty);
        using (sm)
        {
            var request = new PathStatusRequest { Paths = Enumerable.Range(0, CatalogReadExecutor.MaxPathStatusPaths + 1).Select(i => $"/x/{i}").ToList() };
            var result = CatalogReadExecutor.PathStatus(sm, new DirectorCommand
            {
                Verb = "path-status",
                SessionId = session.Id.ToString(),
                PayloadJson = SessionCommandExecutor.Serialize(request),
            }, _ => SessionNamedPaths.Empty, _secrets);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        }
    }

    [Fact]
    public void FsListForASession_namedFolder_listsItsFiles()
    {
        var hosting = Path.Combine(_outside, "hosting");
        Write(Path.Combine(hosting, "README.md"));
        var named = Named(hosting);
        var (sm, session, _) = Director(named);
        using (sm)
        {
            var result = CatalogReadExecutor.SessionFolderList(sm, new DirectorCommand { Verb = "fs-list", SessionId = session.Id.ToString() },
                hosting, _ => named, _secrets);

            Assert.True(result.Ok, result.Error);
            var listing = DirectorCommandRouterBody<DirectoryListingDto>(result);
            Assert.Equal("README.md", Assert.Single(listing.Entries).Name);
        }
    }

    [Fact]
    public void FsListForASession_unnamedFolderOutside_isRefusedWithTheSentence()
    {
        var (sm, session, _) = Director(SessionNamedPaths.Empty);
        using (sm)
        {
            var result = CatalogReadExecutor.SessionFolderList(sm, new DirectorCommand { Verb = "fs-list", SessionId = session.Id.ToString() },
                _outside, _ => SessionNamedPaths.Empty, _secrets);

            Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
            Assert.Equal(SessionFileAccess.FolderNotListableSentence, result.Error);
        }
    }

    private static T DirectorCommandRouterBody<T>(DirectorCommandResult result) where T : class =>
        SessionCommandExecutor.Deserialize<T>(result.BodyJson) ?? throw new InvalidOperationException("the result carried no body");

    // ------------------------------------- review of #4081: the home folder, links, data streams ----

    [Fact]
    public void Decide_namingTheHomeFolder_doesNotAdmitItsDotfiles()
    {
        // Defect 2: with the home folder named, ~/.bash_history was served.
        var history = Write(Path.Combine(_home, ".bash_history"), "export TOKEN=abc");

        var decision = Decide(history, Named(_home));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Equal(SessionFileAccess.SecretSentence, decision.Sentence);
    }

    [Fact]
    public void Decide_anyHiddenFileOrFolderUnderHome_isRefusedEvenWhenNamed_andEvenForAToolNoListKnows()
    {
        // The home rule catches what the name list cannot know about.
        var unknownTool = Write(Path.Combine(_home, ".sometool", "nested", "token.txt"), "secret");
        var unknownDotfile = Write(Path.Combine(_home, ".sometoolrc"), "secret");

        Assert.Equal(SessionFileAccess.SecretSentence, Decide(unknownTool, Named(unknownTool)).Sentence);
        Assert.Equal(SessionFileAccess.SecretSentence, Decide(unknownDotfile, Named(unknownDotfile)).Sentence);
        Assert.Equal(SessionFileAccess.SecretSentence, Decide(unknownDotfile, Named(_home)).Sentence);
    }

    [Fact]
    public void Decide_ordinaryFileInANamedHomeFolder_isStillAllowed()
    {
        var notes = Write(Path.Combine(_home, "notes.txt"));

        Assert.Equal(PathStatusCodes.File, Decide(notes, Named(_home)).Code);
    }

    [Fact]
    public void ListFolder_namedHomeFolder_showsNoDotfiles()
    {
        Write(Path.Combine(_home, "notes.txt"));
        Write(Path.Combine(_home, ".bash_history"));
        Write(Path.Combine(_home, ".sometoolrc"));

        var listing = SessionFileAccess.ListFolder(_work, _home, Named(_home), _secrets);

        Assert.Equal(new[] { "notes.txt" }, listing.Entries.Select(e => e.Name).ToArray());
    }

    [Fact]
    public void Decide_symbolicLinkToADotfileUnderHome_isRefused()
    {
        // The home rule is decided on the real path, so an innocent link outside home does not get round it.
        var hidden = Write(Path.Combine(_home, ".sometool", "token.txt"), "secret");
        var link = Path.Combine(_outside, "token-link.txt");
        CreateFileLink(link, hidden);

        Assert.Equal(SessionFileAccess.SecretSentence, Decide(link, Named(link)).Sentence);
    }

    [Fact]
    public void Decide_hiddenFolderInsideAWorkingFolderUnderHome_keepsTheJulyRules()
    {
        // A session whose working folder is inside the home folder still opens its own .github.
        var repoUnderHome = Path.Combine(_home, "repo");
        var workflow = Write(Path.Combine(repoUnderHome, ".github", "workflows", "ci.yml"));

        var decision = SessionFileAccess.Decide(repoUnderHome, workflow, SessionNamedPaths.Empty, _secrets);

        Assert.Equal(PathStatusCodes.File, decision.Code);
    }

    [Fact]
    public void Decide_symbolicLinkInsideANamedFolderToAnUnnamedFile_isRefused()
    {
        // Defect 3a: briefs/innocent.txt -> private/diary.txt, with briefs named, served the diary.
        var diary = Write(Path.Combine(_base, "private", "diary.txt"), "dear diary");
        var briefs = Path.Combine(_outside, "named-briefs");
        Directory.CreateDirectory(briefs);
        var link = Path.Combine(briefs, "innocent.txt");
        CreateFileLink(link, diary);

        Assert.Equal(PathStatusCodes.NotAllowed, Decide(link, Named(briefs)).Code);
        var listing = SessionFileAccess.ListFolder(_work, briefs, Named(briefs), _secrets);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void Decide_fileThroughANamedLinkToAFolder_isRefused()
    {
        // Defect 3b: a named link-to-a-folder out -> private served out/diary.txt.
        var privateFolder = Path.Combine(_base, "private");
        Write(Path.Combine(privateFolder, "diary.txt"), "dear diary");
        var outLink = Path.Combine(_outside, "out");
        CreateDirectoryLink(outLink, privateFolder);

        var decision = Decide(Path.Combine(outLink, "diary.txt"), Named(outLink));

        Assert.Equal(PathStatusCodes.NotAllowed, decision.Code);
        Assert.Empty(SessionFileAccess.ListFolder(_work, outLink, Named(outLink), _secrets).Entries);
    }

    [Fact]
    public void Decide_servesTheRealPathNotTheLink()
    {
        // The bytes are read from the place the decision was made about, so a link swapped afterwards cannot
        // redirect the read.
        var target = Write(Path.Combine(_base, "elsewhere", "report.txt"));
        var link = Path.Combine(_outside, "report-link.txt");
        CreateFileLink(link, target);

        Assert.Equal(Path.GetFullPath(target), Decide(link, Named(link)).FullPath);
    }

    [Theory]
    [InlineData(@"C:\briefs\x.pem::$DATA", true, true)]
    [InlineData(@"C:\briefs\notes.txt:hidden", true, true)]
    [InlineData(@"C:\briefs\notes.txt", true, false)]
    [InlineData(@"C:", true, false)]
    [InlineData("/tmp/a:b.txt", false, false)]
    public void NamesAlternateDataStream_isDecidedPerPlatform(string path, bool windows, bool expected)
    {
        Assert.Equal(expected, SessionFileAccess.NamesAlternateDataStream(path, windows));
    }

    [Theory]
    [InlineData("server.pem.")]
    [InlineData("server.pem ")]
    [InlineData(".env.")]
    [InlineData("gateway-token.txt..")]
    public void IsSecretFileName_trailingDotsAndSpacesWindowsDrops_stillMatch(string name)
    {
        Assert.True(SessionFileSecrets.IsSecretFileName(name));
    }

    [Fact]
    public void WindowsCredentialRoots_namesTheCredentialStoresAndTheyAreSecret()
    {
        var appData = Path.Combine(_base, "AppData", "Roaming");
        var local = Path.Combine(_base, "AppData", "Local");
        var roots = SessionFileSecrets.WindowsCredentialRoots(appData, local);
        var secrets = new SessionFileSecrets(() => roots);

        Assert.True(secrets.IsSecret(Path.Combine(appData, "Microsoft", "Credentials", "ABCDEF")));
        Assert.True(secrets.IsSecret(Path.Combine(appData, "Microsoft", "Protect", "S-1-5", "key")));
        Assert.True(secrets.IsSecret(Path.Combine(appData, "GitHub CLI", "hosts.yml")));
        Assert.True(secrets.IsSecret(Path.Combine(local, "Microsoft", "Vault", "x")));
        Assert.True(secrets.IsSecret(Path.Combine(appData, "Microsoft", "Windows", "PowerShell", "PSReadLine", "history.txt")));
        Assert.False(secrets.IsSecret(Path.Combine(appData, "SomeApp", "settings.json")));
    }

    [Fact]
    public void ForThisMachine_protectsTheRealHomeFolder()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var secrets = SessionFileSecrets.ForThisMachine();

        Assert.True(secrets.IsHiddenUnderHome(Path.Combine(home, ".config", "gh", "hosts.yml")));
        Assert.True(secrets.IsHiddenUnderHome(Path.Combine(home, ".bash_history")));
        Assert.False(secrets.IsHiddenUnderHome(Path.Combine(home, "Documents", "report.pdf")));
    }

    private static void CreateDirectoryLink(string link, string target)
    {
        try
        {
            Directory.CreateSymbolicLink(link, target);
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (!OperatingSystem.IsWindows())
            {
                Assert.Fail($"Could not create a directory symbolic link on this host ({ex.Message}); this test needs a real link.");
                return;
            }
            // No symbolic-link right on this Windows host: a junction, which needs no privilege.
            CcDirector.Core.Utilities.DirectoryJunction.Create(link, target);
        }
    }
}
