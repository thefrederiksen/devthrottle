using CcDirector.Core.Claude;
using CcDirector.Core.Codex;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// THE START-UP QUESTIONS THAT SWALLOWED A FIRST PROMPT (Command Line Agent Handling mission): Codex's update prompt and
/// its "Trust this folder?" in a folder the owner trusts in Claude Code, and Claude Code's one-time Bypass Permissions
/// warning. Every case writes scratch files only - never the user's own configuration.
/// </summary>
public sealed class StartupQuestionsTests : IDisposable
{
    private readonly string _scratch = Directory.CreateTempSubdirectory("dt-startup-questions-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_scratch, recursive: true); } catch (IOException) { /* scratch folder */ }
    }

    private string Config => Path.Combine(_scratch, ".claude.json");

    private string Dir(params string[] parts)
    {
        var path = Path.Combine(new[] { _scratch }.Concat(parts).ToArray());
        Directory.CreateDirectory(path);
        return path;
    }

    private string Repo(params string[] parts)
    {
        var path = Dir(parts);
        Directory.CreateDirectory(Path.Combine(path, ".git"));
        return path;
    }

    private void Trusted(params string[] folders) => File.WriteAllText(Config,
        "{\"projects\": {" + string.Join(",", folders.Select(f =>
            $"\"{ClaudeFolderTrust.KeyFor(f).Replace("\\", "\\\\")}\": {{\"hasTrustDialogAccepted\": true}}")) + "}}");

    // ===== Claude Code's record, read to carry trust over =====

    [Fact]
    public void Covers_TheFolderItselfIsTrusted_True()
    {
        var repo = Repo("repo");
        Trusted(repo);
        Assert.True(new ClaudeFolderTrust(Config).Covers(repo));
    }

    [Fact]
    public void Covers_ASubfolderOfATrustedRepository_True()
    {
        var repo = Repo("repo");
        Trusted(repo);
        Assert.True(new ClaudeFolderTrust(Config).Covers(Dir("repo", "src", "deep")));
    }

    [Fact]
    public void Covers_ARepositoryInsideATrustedFolder_FalseBecauseTheWalkStopsAtItsGitRoot()
    {
        var parent = Dir("work");
        var repo = Repo("work", "repo");
        Trusted(parent);
        Assert.False(new ClaudeFolderTrust(Config).Covers(repo));
    }

    [Fact]
    public void Covers_ALinkedWorktreeOfATrustedRepository_True()
    {
        var main = Repo("main");
        Directory.CreateDirectory(Path.Combine(main, ".git", "worktrees", "wt"));
        var worktree = Dir("main-wt");
        File.WriteAllText(Path.Combine(worktree, ".git"), "gitdir: " + Path.Combine(main, ".git", "worktrees", "wt"));
        Trusted(main);
        Assert.True(new ClaudeFolderTrust(Config).Covers(worktree));
    }

    [Fact]
    public void Covers_AFolderNobodyTrusted_False()
    {
        Trusted(Repo("other"));
        Assert.False(new ClaudeFolderTrust(Config).Covers(Repo("repo")));
    }

    [Fact]
    public void Covers_NoRecordAtAll_False()
        => Assert.False(new ClaudeFolderTrust(Config).Covers(Repo("repo")));

    [Fact]
    public void Covers_ARecordThatIsNotJson_FalseRatherThanThrowing()
    {
        File.WriteAllText(Config, "{ not json");
        Assert.False(new ClaudeFolderTrust(Config).Covers(Repo("repo")));
    }

    // ===== Codex's launch line =====

    [Fact]
    public void Append_AnyCodexLine_TurnsTheUpdateCheckOff()
    {
        var line = CodexStartupArguments.Append("--dangerously-bypass-approvals-and-sandbox", Dir("repo"), trustedByOwner: false);
        Assert.Equal("--dangerously-bypass-approvals-and-sandbox -c check_for_update_on_startup=false", line);
    }

    [Fact]
    public void Append_ALineThatAlreadySetsTheUpdateCheck_IsNotGivenASecond()
    {
        var line = CodexStartupArguments.Append("-c check_for_update_on_startup=true", Dir("repo"), trustedByOwner: false);
        Assert.Equal("-c check_for_update_on_startup=true", line);
    }

    [Fact]
    public void Append_AFolderTheOwnerTrusts_IsTrustedForThisRunAsATomlLiteralString()
    {
        var repo = Dir("repo");
        var line = CodexStartupArguments.Append("", repo, trustedByOwner: true);
        Assert.Equal($"-c check_for_update_on_startup=false -c \"projects={{'{Path.GetFullPath(repo)}'={{trust_level='trusted'}}}}\"", line);
    }

    [Fact]
    public void Append_AFolderNobodyTrusted_GetsNoTrustLine()
        => Assert.DoesNotContain("trust_level", CodexStartupArguments.Append("", Dir("repo"), trustedByOwner: false));

    [Fact]
    public void TrustFolder_APathHoldingAQuote_GetsNoTrustLine()
        => Assert.Null(CodexStartupArguments.TrustFolder(Dir("it's")));

    // ===== Claude Code's Bypass Permissions warning =====

    [Theory]
    [InlineData("--dangerously-skip-permissions --model haiku", true)]
    [InlineData("--permission-mode bypassPermissions", true)]
    [InlineData("--permission-mode=bypassPermissions", true)]
    [InlineData("--allow-dangerously-skip-permissions", false)]
    [InlineData("--permission-mode auto", false)]
    [InlineData("", false)]
    public void IsRequested_TheLine_SaysWhetherItStartsInBypassMode(string args, bool expected)
        => Assert.Equal(expected, ClaudeBypassConsent.IsRequested(args));

    private string? Why(string args, string? userArgs = null, string? folder = null) =>
        ClaudeBypassConsent.WhyItWouldStop(userArgs, args, folder ?? Dir("repo"), "devlinux", NoEnvironment, _scratch,
            managedSettings: Path.Combine(_scratch, "managed-settings.json"));

    private static string? NoEnvironment(string name) => null;

    [Fact]
    public void WhyItWouldStop_BypassNeverAccepted_RefusesNamingTheMachineAndTheCommand()
    {
        var why = Why("--dangerously-skip-permissions");
        Assert.NotNull(why);
        Assert.Contains("devlinux", why);
        Assert.Contains("claude --dangerously-skip-permissions", why);
        Assert.Contains("does not accept it for the owner", why);
    }

    [Fact]
    public void WhyItWouldStop_ConsentInTheUserSettings_GoesAhead()
    {
        File.WriteAllText(Path.Combine(Dir(".claude"), "settings.json"), "{\"skipDangerousModePermissionPrompt\": true}");
        Assert.Null(Why("--dangerously-skip-permissions"));
    }

    [Fact]
    public void WhyItWouldStop_ConsentInTheFoldersLocalSettings_GoesAhead()
    {
        var repo = Dir("repo");
        File.WriteAllText(Path.Combine(Dir("repo", ".claude"), "settings.local.json"), "{\"skipDangerousModePermissionPrompt\": true}");
        Assert.Null(Why("--dangerously-skip-permissions", folder: repo));
    }

    [Fact]
    public void WhyItWouldStop_TheOlderAcceptedKeyInTheConfiguration_GoesAhead()
    {
        File.WriteAllText(Config, "{\"bypassPermissionsModeAccepted\": true}");
        Assert.Null(Why("--dangerously-skip-permissions"));
    }

    [Fact]
    public void WhyItWouldStop_ConsentInTheMachinesManagedSettings_GoesAhead()
    {
        File.WriteAllText(Path.Combine(_scratch, "managed-settings.json"), "{\"skipDangerousModePermissionPrompt\": true}");
        Assert.Null(Why("--dangerously-skip-permissions"));
    }

    [Fact]
    public void WhyItWouldStop_ConsentSetFalse_Refuses()
    {
        File.WriteAllText(Path.Combine(Dir(".claude"), "settings.json"), "{\"skipDangerousModePermissionPrompt\": false}");
        Assert.NotNull(Why("--dangerously-skip-permissions"));
    }

    [Fact]
    public void WhyItWouldStop_NoBypassAsked_GoesAhead()
        => Assert.Null(Why("--model haiku"));

    [Fact]
    public void WhyItWouldStop_ALineWithItsOwnSettings_GoesAheadBecauseThatFileMayHoldTheConsent()
        => Assert.Null(Why("--dangerously-skip-permissions --settings x.json", userArgs: "--dangerously-skip-permissions --settings x.json"));
}
