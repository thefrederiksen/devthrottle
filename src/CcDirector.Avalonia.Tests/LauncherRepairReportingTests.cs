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
        var bootstraps = 0;
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
            if (args.StartsWith("bootstrap ", StringComparison.Ordinal))
            {
                // A refused bootstrap: the rebuild throws and logs its own FAILED line, carrying launchctl's answer.
                if (state == "bootstrap-refused") return (5, "Bootstrap failed: 5: Input/output error " + identity);
                // ...and the roll back's own bootstrap throws, so the text reads "Roll back FAILED (IOException: ...)".
                if (state == "rollback-throws")
                {
                    if (bootstraps++ > 0) throw new IOException("the disk went away");
                    return (5, "Bootstrap failed: 5: Input/output error");
                }
                held = true;
                return (0, "");
            }
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

        // As in the Director (Program.cs: EngineLog.Sink = FileLog.Write, and FileLog hands every error line to the
        // reporter), so an engine FAILED line logged during the pass is seen by the reporter here too.
        var previousSink = EngineLog.Sink;
        EngineLog.Sink = reporter.OnLogLine;
        LauncherRepairOutcome outcome;
        try
        {
            outcome = LauncherLaunchdRepair.RunPass(_layout, reporter, Fake(state), TimeSpan.FromMilliseconds(300),
                TimeSpan.FromMilliseconds(20), running ?? (() => 0), _plist);
        }
        finally
        {
            EngineLog.Sink = previousSink;
        }
        // The line the Director then logs (App.StartLauncherRepair), outside the pass, carries only the result and
        // the verdict, so whatever the pass's own text says it cannot become a second report.
        reporter.OnLogLine($"[CcDirector] launcher repair: {outcome.Result} ({outcome.Verdict})");
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

        // RunOnce logs "RunOnce FAILED: ..." before it rethrows; in the Director that line reaches the reporter.
        var previousSink = EngineLog.Sink;
        EngineLog.Sink = reporter.OnLogLine;
        LauncherRepairOutcome thrown;
        try
        {
            thrown = LauncherLaunchdRepair.RunPass(_layout, reporter, throwing, TimeSpan.FromMilliseconds(100),
                TimeSpan.FromMilliseconds(20), () => 0, _plist);
        }
        finally
        {
            EngineLog.Sink = previousSink;
        }
        Assert.Equal(1, await reporter.SendPendingAsync(CancellationToken.None));

        Assert.Equal(LauncherRepairResult.Failed, thrown.Result);
        Assert.Equal("Exception", thrown.Verdict);
        var report = Assert.Single(JsonSerializer.Deserialize<ErrorReportBatch>(Assert.Single(handler.Bodies))!.Reports!);
        Assert.StartsWith("launcher repair: failed (Exception): the repair pass ended with InvalidOperationException: launchctl is gone", report.Message);
        Assert.Equal(LauncherRepairReport.Kind, report.Kind);
        Assert.Contains("RunOnce FAILED: InvalidOperationException: launchctl is gone", report.Stack);
    }

    [Fact]
    public async Task RunPass_RebuildThrows_SendsOneReportCarryingTheRebuildsFailedLineWithoutTheUser()
    {
        // The rebuild logs "[LauncherLaunchdAutostart] Rebuild FAILED: ..." with launchctl's own answer, which
        // here names the user outside any path. It is held for the pass's one report and scrubbed with it.
        var (outcome, item, _) = await PassAsync("bootstrap-refused");

        Assert.Equal(LauncherRepairResult.Failed, outcome.Result);
        Assert.StartsWith("launcher repair: failed (Repair): the launch agent could not be rebuilt", item.Message);
        Assert.Contains("Error lines logged during the pass:", item.Stack);
        Assert.Contains("[LauncherLaunchdAutostart] Rebuild FAILED:", item.Stack);
    }

    [Fact]
    public async Task RunPass_RollBackAlsoFails_StillSendsOneReport()
    {
        // The pass's line then carries "Roll back FAILED (", which reads as an error line wherever it is logged
        // outside the pass. It is logged inside the pass, so it is not a second report.
        var (outcome, item, _) = await PassAsync("rollback-throws");

        Assert.Equal(LauncherRepairResult.Failed, outcome.Result);
        Assert.Contains("Roll back FAILED (IOException: the disk went away)", outcome.Line);
        Assert.True(ErrorLine.IsError("[CcDirector] launcher repair: " + outcome.Line), "precondition: the whole line reads as an error line");
        Assert.Contains("Roll back FAILED (IOException: the disk went away)", item.Stack);
    }

    [Fact]
    public void TheDirector_RunsTheRepairThroughRunPassWithItsRunningReporter()
    {
        // RunOnce is internal to the engine, so the Director cannot call it; this guards the other way to lose every
        // report - not running the pass, or running it without the reporter.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "src", "CcDirector.Avalonia", "App.axaml.cs"))) dir = dir.Parent;
        Assert.True(dir is not null, $"could not find the repository above {AppContext.BaseDirectory}; this test reads the source");
        var app = File.ReadAllText(Path.Combine(dir!.FullName, "src", "CcDirector.Avalonia", "App.axaml.cs")).Replace("\r\n", "\n");
        var start = app.IndexOf("private static void StartLauncherRepair(", StringComparison.Ordinal);
        Assert.True(start >= 0, "App.StartLauncherRepair not found");
        var body = app[start..app.IndexOf("\n    }\n", start, StringComparison.Ordinal)];
        Assert.Contains("ErrorReporter.Current", body);
        Assert.Contains("LauncherLaunchdRepair.RunPass(layout, reporter)", body);
        Assert.Contains("StartLauncherRepair(log);", app);
        // The Director's line after the pass must not carry the pass's text, which can read as an error line.
        Assert.Contains("log($\"launcher repair: {outcome.Result} ({outcome.Verdict})\");", body);
        Assert.DoesNotContain("{outcome}", body);
        Assert.DoesNotContain("outcome.Line", body);
    }
}
