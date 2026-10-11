using CcDirector.TestInfrastructure;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.Tests.Claude;

/// <summary>
/// Regression gate for issue #168 (recap generation dead: "claude --print exited 1").
///
/// Root cause: RecapGenerator passed <c>--bare</c> to its side-claude spawn. --bare
/// disables keychain reads, so the side-call cannot pick up the user's OAuth
/// credentials and dies with "Not logged in" - printed to STDOUT with an EMPTY
/// stderr, which made the failure log useless. WingmanService had already dropped
/// --bare for exactly this reason; RecapGenerator never got the same fix.
///
/// Like WingmanCharterAuditTests, this is a deterministic source scan that FAILS THE
/// BUILD if the quoted "--bare" literal reappears in any Core source file that spawns
/// a side-claude.
/// </summary>
public sealed class RecapGeneratorAuditTests
{
    [Fact]
    public void No_side_claude_spawn_passes_bare()
    {
        var files = RepositorySourceIndex.Under("src/CcDirector.Core", ".cs").ToArray();
        Assert.NotEmpty(files);

        // Match the QUOTED flag only (an actual ArgumentList.Add("--bare")), so this
        // explanatory text and code comments mentioning --bare do not trip the gate.
        var rx = new Regex("\"--bare\"", RegexOptions.None);

        var offenders = files
            .Where(f => rx.IsMatch(File.ReadAllText(f)))
            .Select(Path.GetFileName)
            .ToList();

        Assert.True(offenders.Count == 0,
            "--bare disables keychain reads and breaks side-claude auth with 'Not logged in' " +
            "(issue #168). Use --tools \"\" / --allowedTools to restrict the session instead:\n  " +
            string.Join("\n  ", offenders));
    }
}
