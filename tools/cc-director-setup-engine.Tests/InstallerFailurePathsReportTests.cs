using System.Text.RegularExpressions;
using Xunit;

namespace CcDirector.Setup.Engine.Tests;

/// <summary>
/// Issue #3645: installer failure paths that used to exist only on the person's screen now send a report to
/// <c>POST /install-reports</c>. The shell installers and the command-line setup cannot be run here end to end, so
/// each path is read from the source it ships as, and the read is pinned to the exact failure text so a test
/// that finds nothing cannot pass.
/// </summary>
public sealed class InstallerFailurePathsReportTests
{
    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine(new[] { RepoRoot() }.Concat(parts).ToArray())).Replace("\r\n", "\n");

    [Fact]
    public void LinuxInstaller_FailReportsBeforeItExits_AndAnUnguardedStopIsTrapped()
    {
        var script = Read("scripts", "install-linux.sh");

        var fail = Regex.Match(script, @"^fail\(\) \{\n(?<body>.*?)\n\}", RegexOptions.Multiline | RegexOptions.Singleline);
        Assert.True(fail.Success, "fail() was not found in install-linux.sh");
        var body = fail.Groups["body"].Value;
        Assert.Contains("report_failure", body);
        Assert.True(body.IndexOf("report_failure", StringComparison.Ordinal) < body.IndexOf("exit 1", StringComparison.Ordinal),
            "fail() must report before it exits");
        Assert.Contains("/install-reports", script);
        Assert.Contains("set -Eeuo pipefail", script);
        Assert.Matches(@"trap 'fail ""Unexpected failure at line \$LINENO", script);
        // A missing python3 is one of the failures it reports, so the report must not need python3.
        var json = Regex.Match(script, @"^json_string\(\) \{.*?^\}", RegexOptions.Multiline | RegexOptions.Singleline).Value;
        Assert.NotEmpty(json);
        var code = json.Split('\n').Where(l => !l.TrimStart().StartsWith('#'));
        Assert.DoesNotContain(code, l => l.Contains("python3", StringComparison.Ordinal));
    }

    [Fact]
    public void MacInstaller_RefusalAsRoot_IsReported_WithoutWritingTheInstallId()
    {
        var script = Read("scripts", "install-mac.sh");

        var refusal = Regex.Match(script, @"if \[\[ ""\$\(id -u\)"" -eq 0 \]\]; then\n(?<body>.*?)\n    exit 1\nfi", RegexOptions.Singleline);
        Assert.True(refusal.Success, "the root refusal was not found in install-mac.sh");
        var body = refusal.Groups["body"].Value;
        Assert.Contains("/install-reports", body);
        Assert.Contains("refused-as-root", body);
        // As root, writing the id would make the root-owned file the refusal exists to prevent.
        Assert.DoesNotContain("> \"$", body);
        Assert.DoesNotContain("mkdir", body);
    }

    [Theory]
    [InlineData("Gateway tray app FAILED", "\"gateway\", \"tray\"")]
    [InlineData("Python tools FAILED", "\"tools\", \"install\"")]
    [InlineData("Launcher tray app FAILED", "\"launcher\", \"start\"")]
    public void SetupCli_AFailureItPrints_IsAlsoReported(string printed, string reportArguments)
    {
        var commands = Read("tools", "cc-director-setup-cli", "Commands.cs");

        var at = commands.IndexOf(printed, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{printed}' was not found in Commands.cs");
        var returnsError = commands.IndexOf("return Error;", at, StringComparison.Ordinal);
        Assert.True(returnsError > at, $"no 'return Error;' after '{printed}'");
        var path = commands[at..returnsError];
        Assert.Contains($"ReportStepFailureAsync(layout, {reportArguments}", path);
    }

    [Fact]
    public void SetupCli_AComponentThatFailedToPlace_IsReported()
    {
        var commands = Read("tools", "cc-director-setup-cli", "Commands.cs");

        Assert.Matches(@"foreach \(var failed in applied\.Where\(r => r\.Status == ApplyStatus\.Failed\)\)\s*\n\s*await ReportStepFailureAsync\(layout, failed\.ComponentId, ""place""", commands);
    }

    [Fact]
    public void AvaloniaUninstall_AThrownUninstall_IsReported()
    {
        var step = Read("tools", "cc-director-setup-avalonia", "Steps", "UninstallStep.axaml.cs");

        var failed = step.IndexOf("[UninstallStep] uninstall FAILED", StringComparison.Ordinal);
        Assert.True(failed >= 0, "the uninstall failure line was not found");
        var shown = step.IndexOf("ShowComplete(success: false", failed, StringComparison.Ordinal);
        Assert.Contains("ReportUninstallError(", step[failed..shown]);
        Assert.Contains("WizardErrorReport.SendAsync(\"wizard\", \"uninstall\"", step);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "scripts", "install-linux.sh"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found above " + AppContext.BaseDirectory);
    }
}
