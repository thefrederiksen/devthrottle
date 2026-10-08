#pragma warning disable CA1416 // RunPass is macOS-only in production; here launchd is faked, so it runs on every operating system.
using System.Net;
using System.Text.Json;
using CcDirector.Core.Configuration;
using CcDirector.Core.ErrorReports;
using CcDirector.Setup.Engine;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// The Director's launcher repair pass, end to end from launchd to the body the Gateway receives (owner ruling of
/// 7 October 2026): <see cref="LauncherLaunchdRepair.RunPass"/> - the call the Director makes at start-up - with
/// launchd faked, through a real <see cref="ErrorReporter"/>, to a stub HTTP handler. Every outcome arrives as
/// exactly one report of kind launcher-repair, and the body names neither this machine's user nor its home folder,
/// even when launchctl's answer carries both. Lives here because this project sees the reporter's send.
/// </summary>
public sealed class LauncherRepairReportingTests : IDisposable
{
    private readonly string _dir;
    private readonly InstallLayout _layout;
    private readonly string _plist;
    private readonly string _user = Environment.UserName;
    private readonly string _home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public LauncherRepairReportingTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "cc-launcher-repair-e2e-" + Guid.NewGuid().ToString("N"));
        _layout = new InstallLayout(Path.Combine(_dir, "local"));
        var binary = _layout.PathFor(ComponentRegistry.Launcher);
        Directory.CreateDirectory(Path.GetDirectoryName(binary)!);
        File.WriteAllText(binary, "");
        _plist = Path.Combine(_dir, "LaunchAgents", LauncherLaunchdAutostart.Label + ".plist");
        Directory.CreateDirectory(Path.GetDirectoryName(_plist)!);
        File.WriteAllText(_plist, "<old/>");
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        public readonly List<string> Bodies = new();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.Accepted);
        }
    }

    /// <summary>launchd faked. Every answer carries this machine's user name and home folder, as a real one can.</summary>
    private LauncherLaunchdAutostart.CommandRunner Fake(string state)
    {
        var identity = $"path = {_home}/Library/LaunchAgents/{LauncherLaunchdAutostart.Label}.plist\nenvironment = {{\n\tUSER => {_user}\n}}\n";
        var kickstarted = false;
        var held = true;
        return (exe, args) =>
        {
            if (exe == "/usr/bin/id") return (0, "501\n");
            if (args.StartsWith("print-disabled", StringComparison.Ordinal))
                return (0, state == "disabled" ? $"disabled services = {{\n\t\"{LauncherLaunchdAutostart.Label}\" => disabled\n}}\n" : "disabled services = {\n}\n");
            if (args.StartsWith("print ", StringComparison.Ordinal))
            {
                if (!held) return (113, "Could not find service");
                if (!kickstarted) return (0, identity + "state = not running\nruns = 6\nlast exit code = 78: EX_CONFIG\njob state = spawn failed\n");
                return state == "starts"
                    ? (0, identity + "state = running\npid = 4242\n")
                    : (0, identity + "state = spawn scheduled\nlast exit code = 78: EX_CONFIG\n");
            }
            if (args.StartsWith("bootout ", StringComparison.Ordinal)) { held = false; return (0, ""); }
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal)) { held = true; return (0, ""); }
            if (args.StartsWith("kickstart ", StringComparison.Ordinal)) { kickstarted = true; return (0, ""); }
            return (0, "");
        };
    }

    private async Task<(LauncherRepairOutcome Outcome, ErrorReportItem Item, string Body)> PassAsync(string state, Func<int>? running = null)
    {
        var handler = new StubHandler();
        using var reporter = new ErrorReporter(ErrorReportLimits.Director,
            () => new GatewayConfig { Url = "https://gateway.example", Token = "device-key" }, new HttpClient(handler),
            machineName: "TEST-MACHINE", productVersion: "2.18.0");

        var outcome = LauncherLaunchdRepair.RunPass(_layout, reporter, Fake(state), TimeSpan.FromMilliseconds(300),
            TimeSpan.FromMilliseconds(20), running ?? (() => 0), _plist);
        // The line the Director then logs (App.StartLauncherRepair) must not read as an error line, or a failed
        // pass would reach the Gateway twice.
        reporter.OnLogLine($"[CcDirector] launcher repair: {outcome}");
        var sent = await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Equal(1, sent);
        var body = Assert.Single(handler.Bodies);
        var item = Assert.Single(JsonSerializer.Deserialize<ErrorReportBatch>(body)!.Reports!);
        Assert.Equal("director", item.Component);
        Assert.Equal(LauncherRepairReport.Source, item.Source);
        Assert.Equal(LauncherRepairReport.Kind, item.Kind);
        Assert.DoesNotContain(_home, body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_home.Replace("\\", "\\\\"), body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain($"USER => {_user}", item.Message + item.Stack, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_user, item.Message + item.Stack, StringComparison.OrdinalIgnoreCase);
        return (outcome, item, body);
    }

    [Fact]
    public async Task RunPass_Rebuilt_SendsOneReportWithThePid()
    {
        var (outcome, item, _) = await PassAsync("starts");

        Assert.Equal(LauncherRepairResult.Rebuilt, outcome.Result);
        Assert.StartsWith("launcher repair: rebuilt the launch agent and launchd reports the launcher running as process 4242.", item.Message);
        Assert.Contains("Rebuilt the launch agent", item.Stack);
    }

    [Fact]
    public async Task RunPass_LeftAlone_SendsOneReportWithItsVerdictAndReason()
    {
        var (outcome, item, _) = await PassAsync("disabled");

        Assert.Equal(LauncherRepairResult.LeftAlone, outcome.Result);
        Assert.StartsWith("launcher repair: left alone (Disabled): launchd's disabled list names the launcher", item.Message);
    }

    [Fact]
    public async Task RunPass_Failed_SendsOneReportWithTheDiagnostics()
    {
        var (outcome, item, _) = await PassAsync("refused");

        Assert.Equal(LauncherRepairResult.Failed, outcome.Result);
        Assert.StartsWith("launcher repair: failed (Repair):", item.Message);
        Assert.Contains("78: EX_CONFIG", item.Stack);
    }

    [Fact]
    public async Task RunPass_PassThrows_SendsOneFailedReportAndDoesNotThrow()
    {
        // A launchctl runner that throws is not caught inside RunOnce, so it reaches RunOnce's rethrow - the
        // pass's own catch is what turns it into the one failed report.
        var handler = new StubHandler();
        using var reporter = new ErrorReporter(ErrorReportLimits.Director,
            () => new GatewayConfig { Url = "https://gateway.example", Token = "device-key" }, new HttpClient(handler),
            machineName: "TEST-MACHINE", productVersion: "2.18.0");
        LauncherLaunchdAutostart.CommandRunner throwing = (_, _) => throw new InvalidOperationException("launchctl is gone");

        var thrown = LauncherLaunchdRepair.RunPass(_layout, reporter, throwing, TimeSpan.FromMilliseconds(100),
            TimeSpan.FromMilliseconds(20), () => 0, _plist);
        await reporter.SendPendingAsync(CancellationToken.None);

        Assert.Equal(LauncherRepairResult.Failed, thrown.Result);
        Assert.Equal("Exception", thrown.Verdict);
        var report = Assert.Single(JsonSerializer.Deserialize<ErrorReportBatch>(Assert.Single(handler.Bodies))!.Reports!);
        Assert.StartsWith("launcher repair: failed (Exception): the repair pass ended with InvalidOperationException: launchctl is gone", report.Message);
        Assert.Equal(LauncherRepairReport.Kind, report.Kind);
    }
}
