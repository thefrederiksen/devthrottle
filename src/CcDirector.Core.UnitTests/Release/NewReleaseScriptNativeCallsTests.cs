using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Core.UnitTests.Release;

/// <summary>
/// scripts/new-release.ps1 stopped halfway through three releases in a row (v2.24.0, v2.28.0, v2.32.0) under
/// Windows PowerShell 5.1, each time on a line git writes to stderr when nothing is wrong: the CRLF warning from
/// git add, the "remote:" hint after git push (issues #4330, #4331). 5.1 wraps each such line as a
/// NativeCommandError, and under $ErrorActionPreference = "Stop" that ends the script although git exited 0. A
/// seat then finished the tag, the tag push, the merge-back and the claim release by hand. The fix is one rule:
/// every git and gh call goes through Invoke-Native, and $LASTEXITCODE alone says whether it failed.
///
/// WHAT THIS DOES NOT DO: it reads the script as text and never runs it, because a gate test may not spawn
/// PowerShell or git. The run-time proof is scripts/new-release-stderr-rig.ps1, which runs -Claim and
/// -Release -Yes under powershell.exe against a throwaway remote that writes to stderr, and stops on the script
/// as it stood before the fix. What this guard catches is the regression that keeps happening: a new git or gh
/// call written bare, which works on every machine until a git line lands on stderr in the middle of a release.
/// </summary>
public sealed class NewReleaseScriptNativeCallsTests
{
    private const string ScriptPath = "scripts/new-release.ps1";

    // A call, not a mention: the forms the script runs. The how-to-fix messages name other forms
    // ("git push origin --delete", "gh auth login", "gh run list") that a person types, not the script.
    private static readonly Regex NativeCall = new(@"(?<![\w-])(git -C |gh api |gh pr |gh auth status)", RegexOptions.Compiled);

    [Fact]
    public void EveryGitAndGhCall_GoesThroughInvokeNative()
    {
        var script = ReadScript();
        var calls = NativeCall.Matches(StripComments(script)).ToList();

        // A presence, so a renamed or emptied script cannot pass by having no calls to check.
        Assert.True(calls.Count >= 20, $"{ScriptPath} shows only {calls.Count} git or gh calls; this guard can no longer see them.");

        var code = StripComments(script);
        var bare = calls
            .Where(m => !Regex.IsMatch(code[..m.Index], @"(Invoke-Native|Get-NativeExitCode)(\s+-Quiet)?\s*\{\s*$"))
            .Select(m => LineOf(code, m.Index))
            .ToList();
        Assert.True(bare.Count == 0,
            $"{ScriptPath} calls git or gh outside Invoke-Native on {bare.Count} line(s):\n{string.Join("\n", bare)}\n"
            + "Under Windows PowerShell 5.1 any stderr line from that call (a CRLF warning, a 'remote:' hint) stops the "
            + "release although the call succeeded. Wrap it: Invoke-Native { git ... }, then check $LASTEXITCODE.");
    }

    [Fact]
    public void InvokeNative_DecidesOnTheExitCode_AndNeverOnStderr()
    {
        var script = ReadScript();

        // The script keeps stopping on a PowerShell error. Removing "Stop" would hide every other failure.
        Assert.Contains("$ErrorActionPreference = \"Stop\"", script, StringComparison.Ordinal);

        var helper = Regex.Match(script, @"function Invoke-Native[\s\S]*?\n\}");
        Assert.True(helper.Success, $"{ScriptPath} no longer defines Invoke-Native.");
        var body = helper.Value;
        Assert.Contains("$ErrorActionPreference = \"Continue\"", body, StringComparison.Ordinal);
        Assert.Contains("2>&1", body, StringComparison.Ordinal);
        Assert.Contains("[System.Management.Automation.ErrorRecord]", body, StringComparison.Ordinal);
        Assert.Contains("finally { $ErrorActionPreference = $outerPreference }", body, StringComparison.Ordinal);
        // The helper reports; the caller decides from $LASTEXITCODE. A helper that throws or exits on stderr is the defect.
        Assert.DoesNotContain("throw", body, StringComparison.Ordinal);
        Assert.DoesNotContain("exit", body, StringComparison.Ordinal);
    }

    /// <summary>
    /// Issue #4242: after a release the script printed an address on an old storage account that answers HTTP 409.
    /// It prints the address the website's Download button links to (RELEASE_BASE in the website's download.js).
    /// </summary>
    [Fact]
    public void Release_PrintsTheDownloadAddressTheWebsiteLinksTo()
    {
        var script = ReadScript();

        Assert.Contains("https://devthrottle.blob.core.windows.net/download/devthrottle-setup-win-x64.exe", script, StringComparison.Ordinal);
        Assert.DoesNotContain("stdevthrottledl", script, StringComparison.Ordinal);
    }

    private static string ReadScript() => File.ReadAllText(Path.Combine(RepoRoot(), ScriptPath)).Replace("\r\n", "\n");

    // Comment lines and the help block are blanked, keeping every offset, so a call named in prose is not a call.
    private static string StripComments(string script)
    {
        var noHelp = Regex.Replace(script, @"<#[\s\S]*?#>", m => new string(' ', m.Length));
        return Regex.Replace(noHelp, @"(?m)^\s*#.*$", m => new string(' ', m.Length));
    }

    private static string LineOf(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var lineNumber = text[..index].Count(c => c == '\n') + 1;
        return $"  line {lineNumber}: {text[start..(end < 0 ? text.Length : end)].Trim()}";
    }

    private static string RepoRoot([CallerFilePath] string thisFile = "")
    {
        var dir = Path.GetDirectoryName(thisFile);
        while (dir is not null && !File.Exists(Path.Combine(dir, ScriptPath)))
            dir = Path.GetDirectoryName(dir);
        Assert.True(dir is not null, "could not find the repository root from " + thisFile);
        return dir!;
    }
}
