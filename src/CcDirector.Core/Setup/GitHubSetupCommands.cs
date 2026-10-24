using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CcDirector.Core.Utilities;

namespace CcDirector.Core.Setup;

/// <summary>How a GitHub setup step is carried out: a script in a terminal window the person watches, or a web page.</summary>
public enum GitHubSetupStepKind
{
    /// <summary>Run <see cref="GitHubSetupStep.Script"/> in a new terminal window.</summary>
    Terminal,

    /// <summary>Open <see cref="GitHubSetupStep.Url"/> in the browser: there is no installer on this machine to run.</summary>
    WebPage,
}

/// <summary>One step a screen offers: what it runs and what it tells the person.</summary>
public sealed record GitHubSetupStep(GitHubSetupStepKind Kind, string Script, string? Url, string Explanation);

/// <summary>
/// What "Install git and gh" and "Sign in to GitHub" run (issue #4426). Both happen in a terminal window the person can
/// see, never hidden: the GitHub sign-in shows a one-time code to type into the browser, and an installer may ask for a
/// password. The scripts are built here, purely, so every platform's text is tested on any machine;
/// <see cref="OpenInTerminal"/> is the only part that starts anything.
/// </summary>
public static class GitHubSetupCommands
{
    /// <summary>The GitHub command line's own download page.</summary>
    public const string GhDownloadUrl = "https://cli.github.com/";

    /// <summary>git's download page.</summary>
    public const string GitDownloadUrl = "https://git-scm.com/downloads";

    /// <summary>
    /// The sign-in: <c>gh auth login</c> through the browser with https for git, then <c>gh auth setup-git</c> so a plain
    /// <c>git push</c> from a session uses the same sign-in ("authenticate Git").
    /// </summary>
    /// <param name="gitPath">Where git was found. Its folder goes first on the script's PATH: gh needs git on the PATH to
    /// set it up, and a git installed a minute ago is not on the PATH the terminal inherits from the Director.</param>
    public static GitHubSetupStep SignIn(string ghPath, string? gitPath, OSPlatform platform)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ghPath);
        var gh = Quote(ghPath, platform);
        var host = GitHubSetupProbe.Host;
        var lines = new[]
        {
            $"{gh} auth login --hostname {host} --git-protocol https --web",
            $"{gh} auth setup-git --hostname {host}",
        };
        var folders = new[] { gitPath, ghPath }.Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => DirectoryOf(p!, platform)).Distinct().ToList();
        return new GitHubSetupStep(GitHubSetupStepKind.Terminal,
            Script(platform, "Signing in to GitHub for DevThrottle. Follow the steps below; your browser opens to finish.", lines, folders),
            null,
            "A terminal window opens. Answer Yes to \"Authenticate Git with your GitHub credentials?\", press Enter when it shows a code, sign in to GitHub in the browser, and come back here.");
    }

    private static string DirectoryOf(string path, OSPlatform platform)
    {
        var cut = platform == OSPlatform.Windows ? path.LastIndexOfAny(new[] { '\\', '/' }) : path.LastIndexOf('/');
        return cut > 0 ? path[..cut] : path;
    }

    /// <summary>
    /// How to install what <paramref name="state"/> says is missing. With Homebrew (Mac) or winget (Windows) it is one
    /// terminal script; without either it is the download page, because there is nothing on the machine to run.
    /// </summary>
    /// <param name="installer">The path to brew on a Mac or winget on Windows, or null when there is none.</param>
    public static GitHubSetupStep? Install(GitHubSetupState state, OSPlatform platform, string? installer)
    {
        var needGit = state == GitHubSetupState.GitMissing;
        var needGh = state is GitHubSetupState.GitMissing or GitHubSetupState.GhMissing;
        if (!needGit && !needGh) return null;

        var what = needGit && needGh ? "git and the GitHub command line" : needGit ? "git" : "the GitHub command line";

        if (platform == OSPlatform.OSX && installer is not null)
        {
            var packages = string.Join(' ', new[] { needGit ? "git" : null, needGh ? "gh" : null }.Where(p => p is not null));
            return new GitHubSetupStep(GitHubSetupStepKind.Terminal,
                Script(platform, $"Installing {what} with Homebrew for DevThrottle.", new[] { $"{Quote(installer, platform)} install {packages}" }),
                null, $"A terminal window opens and Homebrew installs {what}. Come back here when it says Done.");
        }

        if (platform == OSPlatform.Windows && installer is not null)
        {
            var lines = new List<string>();
            if (needGit) lines.Add($"{Quote(installer, platform)} install --id Git.Git -e --source winget --accept-source-agreements --accept-package-agreements");
            if (needGh) lines.Add($"{Quote(installer, platform)} install --id GitHub.cli -e --source winget --accept-source-agreements --accept-package-agreements");
            return new GitHubSetupStep(GitHubSetupStepKind.Terminal,
                Script(platform, $"Installing {what} with winget for DevThrottle.", lines),
                null, $"A terminal window opens and winget installs {what}. Windows may ask for permission. Come back here when it says Done.");
        }

        // No installer to run: the download page of the first thing missing.
        var url = needGit ? GitDownloadUrl : GhDownloadUrl;
        var manager = platform == OSPlatform.OSX ? "Homebrew (brew.sh) is not installed, so " : platform == OSPlatform.Windows ? "winget is not available, so " : "";
        return new GitHubSetupStep(GitHubSetupStepKind.WebPage, "", url,
            $"{manager}install {what} from the download page that opens, then come back and check again.");
    }

    /// <summary>The installer this machine has: Homebrew on a Mac, winget on Windows, or null.</summary>
    public static string? FindInstaller()
    {
        if (OperatingSystem.IsMacOS())
            return new[] { "/opt/homebrew/bin/brew", "/usr/local/bin/brew" }.FirstOrDefault(File.Exists) ?? ExecutableResolver.Resolve("brew");
        if (OperatingSystem.IsWindows())
            return ExecutableResolver.Resolve("winget");
        return null;
    }

    /// <summary>
    /// The script a terminal window runs: a heading, each command in order (stopping at the first that fails), and a
    /// closing line, so the person can read what happened before closing the window.
    /// </summary>
    public static string Script(OSPlatform platform, string heading, IReadOnlyList<string> commands,
        IReadOnlyList<string>? pathFirst = null)
    {
        ArgumentNullException.ThrowIfNull(commands);
        var first = pathFirst ?? Array.Empty<string>();
        var sb = new StringBuilder();
        if (platform == OSPlatform.Windows)
        {
            sb.AppendLine("@echo off");
            if (first.Count > 0) sb.AppendLine($"set \"PATH={string.Join(';', first)};%PATH%\"");
            sb.AppendLine($"echo {heading}");
            sb.AppendLine("echo.");
            foreach (var command in commands)
            {
                sb.AppendLine(command);
                sb.AppendLine("if errorlevel 1 goto failed");
            }
            sb.AppendLine("echo.");
            sb.AppendLine("echo Done. You can close this window and go back to DevThrottle.");
            sb.AppendLine("pause");
            sb.AppendLine("exit /b 0");
            sb.AppendLine(":failed");
            sb.AppendLine("echo.");
            sb.AppendLine("echo That step did not finish. Read the message above, then try again from DevThrottle.");
            sb.AppendLine("pause");
            return sb.ToString();
        }

        sb.AppendLine(platform == OSPlatform.OSX ? "#!/bin/zsh" : "#!/bin/bash");
        if (first.Count > 0) sb.AppendLine($"export PATH={string.Join(':', first.Select(f => Quote(f, platform)))}:\"$PATH\"");
        sb.AppendLine($"echo '{heading.Replace("'", "'\\''")}'");
        sb.AppendLine("echo");
        sb.AppendLine("if " + string.Join(" && ", commands) + "; then");
        sb.AppendLine("  echo; echo 'Done. You can close this window and go back to DevThrottle.'");
        sb.AppendLine("else");
        sb.AppendLine("  echo; echo 'That step did not finish. Read the message above, then try again from DevThrottle.'");
        sb.AppendLine("fi");
        return sb.ToString();
    }

    /// <summary>
    /// Open a terminal window running <paramref name="step"/>'s script, or the browser at its page. Returns null when it
    /// started, or a sentence saying why not.
    /// </summary>
    public static string? Run(GitHubSetupStep step)
    {
        ArgumentNullException.ThrowIfNull(step);
        FileLog.Write($"[GitHubSetupCommands] Run: kind={step.Kind}, url={step.Url ?? "-"}");
        if (step.Kind == GitHubSetupStepKind.WebPage)
        {
            Process.Start(new ProcessStartInfo(step.Url!) { UseShellExecute = true });
            return null;
        }
        return OpenInTerminal(step.Script);
    }

    private static string? OpenInTerminal(string script)
    {
        var folder = Path.Combine(Path.GetTempPath(), "devthrottle-github-setup");
        Directory.CreateDirectory(folder);

        if (OperatingSystem.IsWindows())
        {
            var file = Path.Combine(folder, $"github-setup-{Guid.NewGuid():N}.cmd");
            File.WriteAllText(file, script);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            // A .command file is what Terminal opens and runs; it must be executable.
            var file = Path.Combine(folder, $"github-setup-{Guid.NewGuid():N}.command");
            File.WriteAllText(file, script);
            File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var open = new ProcessStartInfo("/usr/bin/open") { UseShellExecute = false };
            open.ArgumentList.Add("-a");
            open.ArgumentList.Add("Terminal");
            open.ArgumentList.Add(file);
            Process.Start(open);
            return null;
        }

        // Linux has no one terminal every desktop ships. x-terminal-emulator is the Debian and Ubuntu name for "the
        // terminal this desktop uses"; without it the person is told what to run.
        var terminal = ExecutableResolver.Resolve("x-terminal-emulator");
        var linuxFile = Path.Combine(folder, $"github-setup-{Guid.NewGuid():N}.sh");
        File.WriteAllText(linuxFile, script + "read -r -p 'Press Enter to close.' _\n");
        File.SetUnixFileMode(linuxFile, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        if (terminal is null)
            return $"No terminal program was found. Open a terminal and run: bash {linuxFile}";
        // The executable script alone: xterm takes the program and its arguments separately, and gnome-terminal splits one.
        var psi = new ProcessStartInfo(terminal) { UseShellExecute = false };
        psi.ArgumentList.Add("-e");
        psi.ArgumentList.Add(linuxFile);
        Process.Start(psi);
        return null;
    }

    private static string Quote(string path, OSPlatform platform) =>
        platform == OSPlatform.Windows ? $"\"{path}\"" : $"'{path.Replace("'", "'\\''")}'";
}
