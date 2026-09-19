using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace CcDirector.Core.Tests.Setup;

/// <summary>
/// The rule, held over the two callers that no unit test can reach.
///
/// <see cref="FleetToolPathRepairTests"/> proves what the rewrite DECIDES. It cannot prove that the
/// decision is asked for, and that is the half this phase is about: the rule was already written -
/// backwards - and its only caller was a button on a settings page that somebody had to notice and
/// click. A correct rule nobody runs repairs nothing, and a suite that only exercises the rule reports
/// the same green either way.
///
/// So each caller is NAMED here, with the call that proves it runs, and a reader that stops making the
/// call fails by name rather than quietly ceasing to be covered:
///
///   the Director, at every start, before it constructs the session manager - because a session
///     inherits this process's path, and a repair after that point leaves every session of the run on
///     whatever the Director inherited at launch;
///   the installer's path step - because an install that adds the master while leaving a copy the
///     master replaces in FRONT of it has put the files in the right place and changed nothing about
///     which copy answers a command.
///
/// WHAT THIS DOES NOT SEE, said plainly so the next reader spends their scepticism in the right place:
/// it reads source text. It cannot tell whether the call is reached at run time, whether it is inside
/// an unreachable branch, or whether the Director start still happens before the session manager is
/// built - only that the statement is written and spelled the way the product needs. The end-to-end
/// answer for the Director is <c>scripts\one-tool-path-rig-proof.ps1</c>, which starts a real Director
/// on a throwaway root and reads what it reports about its own path. The installer's registry write
/// itself is exercised by nothing: a test that edited the developer's real saved path would be a worse
/// bug than the one being fixed.
/// </summary>
public sealed class ThePathRuleRunsWhereItMustTests
{
    private static readonly (string File, string MustContain, string Why)[] Callers =
    {
        ("src/CcDirector.Avalonia/App.axaml.cs",
            "CcDirector.Core.Setup.FleetToolPathRepair.RepairAtDirectorStart()",
            "the Director's start-time repair. Without it the rule runs only when somebody notices the "
            + "problem and presses a button, which is to say after the damage"),
        ("tools/cc-director-setup-engine/InstallFinalizer.cs",
            "FleetToolPathRepair.RewriteForInstall(current, layout.BinDir)",
            "the installer's path step. Appending the master is only half of it; the copies the master "
            + "replaces have to come off, or the older one goes on answering"),
    };

    [Fact]
    public void Every_caller_of_the_path_rule_still_asks_for_it()
    {
        var root = GetRepoRoot();
        var broken = new List<string>();

        foreach (var (file, mustContain, why) in Callers)
        {
            var path = Path.Combine(root, file.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(path))
            {
                broken.Add($"{file}: the file is gone. It held {why}; find where that moved to and name it here.");
                continue;
            }

            if (!File.ReadAllText(path).Contains(mustContain, StringComparison.Ordinal))
                broken.Add($"{file}: expected to find `{mustContain}` - {why}.");
        }

        Assert.True(broken.Count == 0,
            "The one-tools-folder-per-machine rule is no longer asked for where it has to run. A rule "
            + "that is only reachable from a button repairs nothing until somebody clicks it, and an "
            + "install that adds the master without removing what it replaces changes which files are "
            + "on disk and not which one answers."
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, broken));
    }

    [Fact]
    public void The_Directors_repair_runs_before_the_session_manager_is_built()
    {
        // Order is the whole point of putting it where it is. A session inherits the Director's own
        // running path, so a repair that happened after the session manager was constructed and after
        // sessions were restored would leave every session of this run reaching whatever the Director
        // inherited at launch - which on the machine that prompted this was a copy of the tools from
        // July.
        //
        // Source order is not execution order, and this test claims only the first. It is still worth
        // having: moving the call below the session manager is a one-line edit that nothing else would
        // notice, and the end-to-end answer costs a build and a real Director start.
        var source = File.ReadAllText(Path.Combine(
            GetRepoRoot(), "src", "CcDirector.Avalonia", "App.axaml.cs"));

        var repair = source.IndexOf("FleetToolPathRepair.RepairAtDirectorStart()", StringComparison.Ordinal);
        var sessionManager = source.IndexOf("SessionManager = new SessionManager(", StringComparison.Ordinal);

        Assert.True(repair >= 0, "App.axaml.cs no longer calls the start-time tool path repair at all.");
        Assert.True(sessionManager >= 0, "App.axaml.cs no longer constructs the session manager; find where that moved to.");
        Assert.True(repair < sessionManager,
            "The tool path repair must be written BEFORE the session manager is constructed. A session "
            + "inherits the Director's running path, so repairing it afterwards leaves every session of "
            + "this run on the path the Director was launched with.");
    }

    private static string GetRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "cc-director.sln")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        return dir!.FullName;
    }
}
