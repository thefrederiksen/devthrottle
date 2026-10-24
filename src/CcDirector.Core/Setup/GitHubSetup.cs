using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>How far this machine is from pushing to GitHub, in the order a person fixes it (issue #4426).</summary>
public enum GitHubSetupState
{
    /// <summary>git is not installed: nothing can be cloned or pushed.</summary>
    GitMissing,

    /// <summary>git is there, the GitHub command line (gh) is not: no sign-in, no pull requests tab.</summary>
    GhMissing,

    /// <summary>Both are installed and gh is not signed in to github.com.</summary>
    SignedOut,

    /// <summary>
    /// gh holds a sign-in for an account but could not confirm it: GitHub could not be reached, or the sign-in has
    /// expired - gh says the same words for both. Not "signed out": an offline laptop must not be told nobody is signed in.
    /// </summary>
    SignInUnconfirmed,

    /// <summary>gh is signed in to github.com.</summary>
    SignedIn,
}

/// <summary>One finished reading of this machine's GitHub setup, with the words the screens show.</summary>
public sealed record GitHubSetupStatus(
    GitHubSetupState State,
    string? GitPath,
    string? GitVersion,
    string? GhPath,
    string? GhVersion,
    string? Account,
    string Detail);

/// <summary>What one probe of a tool found: where it is (null when not found) and what it printed.</summary>
public sealed record ToolProbe(string? Path, int ExitCode, string Output);

/// <summary>
/// Reads whether this machine can work with GitHub: is git installed, is the GitHub command line (gh) installed, and is
/// gh signed in (issue #4426). A trial user set up a repository and was never asked to sign in to GitHub anywhere; gh was
/// used by the Pull requests tab but only failed when used.
///
/// The decision (<see cref="Classify"/>) and the parsing are pure, so every state is tested without either tool
/// installed. <see cref="ProbeAsync"/> is the one part that runs anything.
///
/// The tools are looked for on the PATH and in the folders their installers use, because the Director's PATH is the one
/// it started with: on Windows a gh installed a minute ago by winget is not on it until the next start, and an app
/// started by macOS gets a short PATH.
///
/// On a Mac /usr/bin/git always exists, and when Apple's command line tools are missing, running it opens a system
/// "install the developer tools" window. A probe that runs every time Home opens must never do that, so /usr/bin/git is
/// only counted when <c>xcode-select -p</c> says the tools are installed.
/// </summary>
public static class GitHubSetupProbe
{
    /// <summary>The host gh is asked about and signed in to.</summary>
    public const string Host = "github.com";

    private static readonly Regex VersionPattern = new(@"\bversion\s+(\d+(?:\.\d+)+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // gh 2.40+ prints "Logged in to github.com account octocat (keyring)"; older releases print "as octocat".
    private static readonly Regex AccountPattern = new(@"Logged in to github\.com (?:account|as) ([A-Za-z0-9-]+)", RegexOptions.Compiled);

    // A stored sign-in gh could not confirm (offline, or an expired token): "X Failed to log in to github.com account octocat".
    private static readonly Regex UnconfirmedPattern = new(@"Failed to log in to github\.com (?:account|as) ([A-Za-z0-9-]+)", RegexOptions.Compiled);

    /// <summary>"git version 2.43.0" and "gh version 2.40.1 (2023-12-13)" give their version; anything else gives null.</summary>
    public static string? ParseVersion(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = VersionPattern.Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>The account <c>gh auth status</c> says is signed in to github.com, or null when none is.</summary>
    public static string? ParseSignedInAccount(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        var match = AccountPattern.Match(output);
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>
    /// The state, from what the three probes found. <paramref name="authStatus"/> is null when gh was not found (it was
    /// never asked). gh answers <c>auth status</c> with exit 0 only when signed in; the account name is read from what it
    /// printed, and a signed-in answer whose account cannot be read still counts as signed in.
    /// </summary>
    public static GitHubSetupStatus Classify(ToolProbe git, ToolProbe gh, ToolProbe? authStatus)
    {
        ArgumentNullException.ThrowIfNull(git);
        ArgumentNullException.ThrowIfNull(gh);

        var gitVersion = git.Path is null || git.ExitCode != 0 ? null : ParseVersion(git.Output);
        var ghVersion = gh.Path is null || gh.ExitCode != 0 ? null : ParseVersion(gh.Output);

        if (gitVersion is null)
            return new GitHubSetupStatus(GitHubSetupState.GitMissing, null, null, gh.Path, ghVersion, null,
                "git is not installed, so repositories cannot be cloned or pushed");

        if (ghVersion is null)
            return new GitHubSetupStatus(GitHubSetupState.GhMissing, git.Path, gitVersion, null, null, null,
                "the GitHub command line (gh) is not installed, so DevThrottle cannot sign in to GitHub");

        if (authStatus is not null && authStatus.ExitCode != 0 && UnconfirmedPattern.Match(authStatus.Output) is { Success: true } stored)
            return new GitHubSetupStatus(GitHubSetupState.SignInUnconfirmed, git.Path, gitVersion, gh.Path, ghVersion, stored.Groups[1].Value,
                $"signed in as {stored.Groups[1].Value}, but GitHub could not confirm it - it may be unreachable just now, or the sign-in has expired");

        if (authStatus is null || authStatus.ExitCode != 0)
            return new GitHubSetupStatus(GitHubSetupState.SignedOut, git.Path, gitVersion, gh.Path, ghVersion, null,
                "the GitHub command line is not signed in to github.com");

        var account = ParseSignedInAccount(authStatus.Output);
        return new GitHubSetupStatus(GitHubSetupState.SignedIn, git.Path, gitVersion, gh.Path, ghVersion, account,
            account is null ? "signed in to github.com" : $"signed in to github.com as {account}");
    }

    /// <summary>
    /// The places <paramref name="tool"/> ("git" or "gh") is looked for besides the PATH, on <paramref name="platform"/>.
    /// </summary>
    public static IReadOnlyList<string> KnownLocations(string tool, OSPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tool);
        if (platform == OSPlatform.Windows)
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var localPrograms = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs");
            return tool == "gh"
                ? new[] { Path.Combine(programFiles, "GitHub CLI", "gh.exe"), Path.Combine(localPrograms, "GitHub CLI", "gh.exe") }
                : new[] { Path.Combine(programFiles, "Git", "cmd", "git.exe"), Path.Combine(localPrograms, "Git", "cmd", "git.exe") };
        }

        // Homebrew on Apple silicon, then on Intel and Linux. /usr/bin/git on a Mac is handled by the caller (see the
        // class comment); on Linux it is an ordinary install.
        var locations = new List<string> { $"/opt/homebrew/bin/{tool}", $"/usr/local/bin/{tool}" };
        if (platform != OSPlatform.OSX) locations.Add($"/usr/bin/{tool}");
        return locations;
    }

    /// <summary>Read this machine. Runs <c>git --version</c>, <c>gh --version</c> and <c>gh auth status</c>.</summary>
    public static async Task<GitHubSetupStatus> ProbeAsync(CancellationToken ct = default)
    {
        FileLog.Write("[GitHubSetupProbe] ProbeAsync");
        var platform = CurrentPlatform();

        var gitPath = await FindGitAsync(platform, ct).ConfigureAwait(false);
        var git = await RunAsync(gitPath, new[] { "--version" }, ct).ConfigureAwait(false);

        var ghPath = Find("gh", platform);
        var gh = await RunAsync(ghPath, new[] { "--version" }, ct).ConfigureAwait(false);
        var auth = ghPath is null ? null : await RunAsync(ghPath, new[] { "auth", "status", "--hostname", Host }, ct).ConfigureAwait(false);

        var status = Classify(git, gh, auth);
        FileLog.Write($"[GitHubSetupProbe] ProbeAsync: state={status.State}, git={status.GitVersion ?? "none"}, gh={status.GhVersion ?? "none"}, account={status.Account ?? "none"}");
        return status;
    }

    /// <summary>
    /// The folders <paramref name="status"/> found git and gh in that are not on <paramref name="path"/>, in that order.
    /// </summary>
    public static IReadOnlyList<string> FoldersMissingFromPath(GitHubSetupStatus status, string? path)
    {
        ArgumentNullException.ThrowIfNull(status);
        var onPath = (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar)).ToHashSet(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        return new[] { status.GitPath, status.GhPath }
            .Where(p => p is not null)
            .Select(p => Path.GetDirectoryName(p!)!)
            .Distinct()
            .Where(d => !onPath.Contains(d))
            .ToList();
    }

    /// <summary>
    /// Put the folders git and gh were found in onto this process's PATH when they are not on it (review of #4426). A gh
    /// that winget installed a minute ago is not on the PATH the Director started with, and every session's PATH is built
    /// from the Director's - so without this, sessions could not push until the Director restarted. Returns what it added.
    /// </summary>
    public static IReadOnlyList<string> AddFoundFoldersToProcessPath(GitHubSetupStatus status)
    {
        var current = Environment.GetEnvironmentVariable("PATH");
        var missing = FoldersMissingFromPath(status, current);
        if (missing.Count == 0) return missing;
        Environment.SetEnvironmentVariable("PATH", string.Join(Path.PathSeparator, missing.Append(current ?? "")));
        FileLog.Write($"[GitHubSetupProbe] added to this process's PATH so sessions find git and gh: {string.Join(", ", missing)}");
        return missing;
    }

    /// <summary>The gh executable this machine has, or null - for the screens that start a sign-in.</summary>
    public static string? FindGh() => Find("gh", CurrentPlatform());

    private static OSPlatform CurrentPlatform() =>
        OperatingSystem.IsWindows() ? OSPlatform.Windows : OperatingSystem.IsMacOS() ? OSPlatform.OSX : OSPlatform.Linux;

    private static string? Find(string tool, OSPlatform platform) =>
        ExecutableResolver.Resolve(tool) is { } onPath && !IsMacSystemGitShim(onPath, platform)
            ? onPath
            : KnownLocations(tool, platform).FirstOrDefault(File.Exists);

    private static async Task<string?> FindGitAsync(OSPlatform platform, CancellationToken ct)
    {
        if (Find("git", platform) is { } found) return found;
        if (platform != OSPlatform.OSX || !File.Exists("/usr/bin/git")) return null;

        // Only with the command line tools installed; otherwise running it opens Apple's install window.
        var tools = await ProcessRunner.RunAsync("/usr/bin/xcode-select", new[] { "-p" }, null, ct).ConfigureAwait(false);
        return tools.Started && tools.ExitCode == 0 ? "/usr/bin/git" : null;
    }

    private static bool IsMacSystemGitShim(string path, OSPlatform platform) =>
        platform == OSPlatform.OSX && string.Equals(path, "/usr/bin/git", StringComparison.Ordinal);

    private static async Task<ToolProbe> RunAsync(string? path, IReadOnlyList<string> args, CancellationToken ct)
    {
        if (path is null) return new ToolProbe(null, -1, "");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var result = await ProcessRunner.RunAsync(path, args, null, timeout.Token).ConfigureAwait(false);
        if (!result.Started)
        {
            FileLog.Write($"[GitHubSetupProbe] {path} could not start (error {result.StartErrorCode})");
            return new ToolProbe(null, -1, "");
        }
        // gh writes auth status to standard error on some releases and standard output on others.
        return new ToolProbe(path, result.ExitCode, result.StandardOutput + "\n" + result.StandardError);
    }
}
