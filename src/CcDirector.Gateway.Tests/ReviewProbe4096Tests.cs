using CcDirector.ControlApi;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

// REVIEW PROBE - not to be committed. Tries to get a secret served through the home rule of #4096.
[Collection("DirectorRoot")]
public sealed class ReviewProbe4096Tests : IDisposable
{
    private readonly string _base;
    private readonly string _work;
    private readonly string _home;
    private readonly SessionFileSecrets _secrets;
    private readonly string? _prevHome;
    private readonly string? _prevRoot;

    public ReviewProbe4096Tests()
    {
        _prevRoot = Environment.GetEnvironmentVariable("CC_DIRECTOR_ROOT");
        _prevHome = Environment.GetEnvironmentVariable("HOME");
        _base = Path.Combine(Path.GetTempPath(), "ccd-probe-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", Path.Combine(_base, "director-root"));
        _work = Path.Combine(_base, "repo");
        _home = Path.Combine(_base, "home");
        Directory.CreateDirectory(_work);
        Directory.CreateDirectory(_home);
        _secrets = new SessionFileSecrets(() => Array.Empty<string>(), _home);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CC_DIRECTOR_ROOT", _prevRoot);
        Environment.SetEnvironmentVariable("HOME", _prevHome);
        try { Directory.Delete(_base, recursive: true); } catch { }
    }

    private static SessionNamedPaths Named(params string[] paths) =>
        SessionNamedPaths.FromTexts(paths.Select(p => $"see `{p}` now"));

    private static string Write(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Production_redirectedHomeVariable_realHomeDotfolderStillRefused()
    {
        // The production path end to end: HOME redirected to an existing folder, ForThisMachine() built after.
        var fake = Path.Combine(_base, "fake-home");
        Directory.CreateDirectory(fake);
        Environment.SetEnvironmentVariable("HOME", fake);
        var realHome = SessionFileSecrets.PasswordFileHome()!;
        var probe = Path.Combine(realHome, ".review-probe-4096", "token.txt");
        Write(probe, "secret");
        try
        {
            var secrets = SessionFileSecrets.ForThisMachine();
            var decision = SessionFileAccess.Decide(_work, probe, Named(probe), secrets);
            Assert.Equal(SessionFileAccess.SecretSentence, decision.Sentence);
            // and the fake home is guarded as well
            var inFake = Write(Path.Combine(fake, ".tool", "t.txt"));
            Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(_work, inFake, Named(inFake), secrets).Sentence);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(probe)!, recursive: true);
        }
    }

    [Fact]
    public void WorkingFolderIsFilesystemRoot_hiddenUnderHomeRefused()
    {
        var token = Write(Path.Combine(_home, ".tool", "token.txt"));
        var root = Path.GetPathRoot(_home)!;
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(root, token, SessionNamedPaths.Empty, _secrets).Sentence);
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(root, token, Named(token), _secrets).Sentence);
    }

    [Fact]
    public void NamedFolderLinkIntoHiddenHomeFolder_refusedToListAndToOpen()
    {
        var token = Write(Path.Combine(_home, ".tool", "token.txt"));
        var link = Path.Combine(_base, "briefs", "innocent");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        Directory.CreateSymbolicLink(link, Path.Combine(_home, ".tool"));
        var viaLink = Path.Combine(link, "token.txt");

        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(_work, viaLink, Named(link, viaLink), _secrets).Sentence);
        var ex = Assert.Throws<UnauthorizedAccessException>(() => SessionFileAccess.ListFolder(_work, link, Named(link), _secrets));
        Assert.Equal(SessionFileAccess.SecretSentence, ex.Message);
    }

    [Fact]
    public void WorkingFolderInsideHiddenFolder_linkToNeighbourRefused()
    {
        var workIn = Path.Combine(_home, ".tool", "project");
        Directory.CreateDirectory(workIn);
        var neighbour = Write(Path.Combine(_home, ".tool", "token.txt"), "secret");
        var link = Path.Combine(workIn, "link.txt");
        File.CreateSymbolicLink(link, neighbour);
        var dirLink = Path.Combine(workIn, "up");
        Directory.CreateSymbolicLink(dirLink, Path.Combine(_home, ".tool"));

        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(workIn, link, Named(link), _secrets).Sentence);
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(workIn, Path.Combine(dirLink, "token.txt"), Named(dirLink), _secrets).Sentence);
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(workIn, "../token.txt", SessionNamedPaths.Empty, _secrets).Sentence);
        // another hidden folder's file, named
        var other = Write(Path.Combine(_home, ".other", "t.txt"));
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(workIn, other, Named(other), _secrets).Sentence);
        // listing the working folder shows the links? the link to the neighbour must not appear
        var listing = SessionFileAccess.ListFolder(workIn, workIn, SessionNamedPaths.Empty, _secrets);
        Assert.DoesNotContain(listing.Entries, e => e.Name == "link.txt");
        Assert.DoesNotContain(listing.Entries, e => e.Name == "up");
    }

    [Fact]
    public void WorkingFolderIsHome_linkInsideToDotfolderRefused_andListingHidesIt()
    {
        var token = Write(Path.Combine(_home, ".tool", "token.txt"), "secret");
        var link = Path.Combine(_home, "notes.txt");
        File.CreateSymbolicLink(link, token);
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(_home, link, SessionNamedPaths.Empty, _secrets).Sentence);
        var listing = SessionFileAccess.ListFolder(_home, _home, SessionNamedPaths.Empty, _secrets);
        Assert.Empty(listing.Entries);
    }

    [Fact]
    public void HomeFolderItselfIsALink_realDotfolderRefused()
    {
        // HOME is a link to the real home folder; the file is named by its real spelling.
        var realHome = Path.Combine(_base, "real");
        Directory.CreateDirectory(realHome);
        var linkHome = Path.Combine(_base, "linked-home");
        Directory.CreateSymbolicLink(linkHome, realHome);
        var secrets = new SessionFileSecrets(() => Array.Empty<string>(), linkHome);
        var token = Write(Path.Combine(realHome, ".tool", "token.txt"));
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(_work, token, Named(token), secrets).Sentence);
        // and the other way round
        var secrets2 = new SessionFileSecrets(() => Array.Empty<string>(), realHome);
        var viaLink = Path.Combine(linkHome, ".tool", "token.txt");
        Assert.Equal(SessionFileAccess.SecretSentence, SessionFileAccess.Decide(_work, viaLink, Named(viaLink), secrets2).Sentence);
    }

    [Fact]
    public void WorkingFolderIsHome_dotfolderNamedAsFolder_notListable()
    {
        Write(Path.Combine(_home, ".tool", "token.txt"), "secret");
        var dot = Path.Combine(_home, ".tool");
        Assert.Throws<UnauthorizedAccessException>(() => SessionFileAccess.ListFolder(_home, dot, Named(dot), _secrets));
        Assert.Throws<UnauthorizedAccessException>(() => SessionFileAccess.ListFolder(_work, dot, Named(dot), _secrets));
    }

    [Fact]
    public void ProcSelfCwdAndFdPaths_toAHomeDotfile_refused()
    {
        if (!OperatingSystem.IsLinux()) return;
        var token = Write(Path.Combine(_home, ".tool", "token.txt"), "secret");
        // /proc/self/root/<real path>
        var viaRoot = "/proc/self/root" + token;
        var d = SessionFileAccess.Decide(_work, viaRoot, Named(viaRoot), _secrets);
        Assert.NotEqual(PathStatusCodes.File, d.Code);
    }
}
