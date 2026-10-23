using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using CcDirector.Avalonia.Controls;
using CcDirector.Core.Agents;
using CcDirector.Core.Backends;
using CcDirector.Core.Drivers;
using CcDirector.Core.Memory;
using CcDirector.Core.Sessions;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Avalonia.Tests;

/// <summary>
/// Issue #2169 - the desktop asks before clearing a session's context.
///
/// Clearing destroys the whole conversation and cannot be undone. The button used to fire on the first
/// click, sitting inches from Interrupt and beside a context gauge that turns red - which is exactly when
/// somebody reaches for a button. Worse, the Cockpit had asked since issue #1244, so the SAME action behaved
/// differently depending on which surface you were looking at, and the surface with a real mouse was the
/// unguarded one.
///
/// The decisive test here is the declined one: the driver must receive NOTHING. A test that only checked
/// "a dialog appears" would still pass if the clear were submitted behind it.
/// </summary>
public class ClearContextConfirmationTests
{
    [AvaloniaFact]
    public async Task DecliningTheConfirmation_SubmitsNothingToTheDriver()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = new SessionActionBar();
        bar.Configure(sessionManager: null!, session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(false);

        await bar.ClearContextWithConfirmationAsync();

        Assert.Equal(0, driver.ClearCalls);
    }

    [AvaloniaFact]
    public async Task ConfirmingTheDialog_ClearsTheContext()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = new SessionActionBar();
        bar.Configure(sessionManager: null!, session);
        bar.ConfirmOverride = (_, _) => Task.FromResult(true);

        await bar.ClearContextWithConfirmationAsync();

        Assert.Equal(1, driver.ClearCalls);
    }

    /// <summary>
    /// The guard against the defect quietly coming back. Every other test here drives
    /// ClearContextWithConfirmationAsync, which asks by construction - so if a future edit moved the clear
    /// back into the click handler ahead of the question, they would all still pass. This one pins the
    /// ordering: the confirmation must have been ASKED before the driver is touched.
    /// </summary>
    [AvaloniaFact]
    public async Task TheQuestionIsAskedBeforeTheDriverIsTouched()
    {
        var order = new List<string>();
        var driver = new RecordingDriver { OnClear = () => order.Add("cleared") };
        using var session = NewSession(driver);
        var bar = new SessionActionBar();
        bar.Configure(sessionManager: null!, session);
        bar.ConfirmOverride = (_, _) =>
        {
            order.Add("asked");
            return Task.FromResult(true);
        };

        await bar.ClearContextWithConfirmationAsync();

        Assert.Equal(new[] { "asked", "cleared" }, order);
    }

    /// <summary>
    /// A confirmation that cannot be shown is not permission to proceed. If the dialog throws - no owner
    /// window, a broken window stack - the safe answer is to do nothing, not to fall through to the clear.
    /// </summary>
    [AvaloniaFact]
    public async Task AConfirmationThatFails_DoesNotClear()
    {
        var driver = new RecordingDriver();
        using var session = NewSession(driver);
        var bar = new SessionActionBar();
        bar.Configure(sessionManager: null!, session);
        bar.ConfirmOverride = (_, _) => throw new InvalidOperationException("no owner window");

        await bar.ClearContextWithConfirmationAsync();

        Assert.Equal(0, driver.ClearCalls);
    }

    /// <summary>
    /// The desktop and the Cockpit describe the same action the same way. The message must also say what
    /// SURVIVES - "clear" alone reads to some people as "kill my session", and the useful fact is that the
    /// process keeps running and only the conversation goes.
    /// </summary>
    [Fact]
    public void TheWordingMatchesTheCockpit_AndSaysWhatSurvives()
    {
        Assert.Equal("Clear this session's context?", SessionActionBar.ClearContextConfirmTitle);
        Assert.Contains("resets the conversation in place", SessionActionBar.ClearContextConfirmMessage);
        Assert.Contains("running process keeps going", SessionActionBar.ClearContextConfirmMessage);
        Assert.Contains("cannot be undone", SessionActionBar.ClearContextConfirmMessage);
    }

    private static Session NewSession(IAgentDriver driver)
    {
        var session = new Session(
            Guid.NewGuid(), repoPath: @"C:\repo", workingDirectory: @"C:\repo", claudeArgs: null,
            backend: new NullBackend(), claudeSessionId: "agent-1",
            activityState: ActivityState.WaitingForInput, createdAt: DateTimeOffset.UtcNow,
            customName: null, customColor: null);
        session.DriverOverride = driver;
        return session;
    }

    /// <summary>A driver that records whether the destructive verb was ever submitted.</summary>
    private sealed class RecordingDriver : IAgentDriver
    {
        public int ClearCalls { get; private set; }
        public Action? OnClear { get; init; }

        public AgentKind Kind => AgentKind.ClaudeCode;
        public DriverCapabilities Capabilities => DriverCapabilities.ClearContext;
        public IReadOnlyList<AgentSlashCommand> SlashCommands => [];
        public string ModelFlag => "";
        public IReadOnlyList<AgentModelOption> KnownModels => [];
        public string? ReadConfiguredDefaultModel() => null;
        public string ResolveExecutable(string? configuredPath) => throw new NotSupportedException();
        public AgentLaunchSpec BuildLaunchSpec(string? baseArgs, string? resumeSessionId) => throw new NotSupportedException();
        public Task SubmitAsync(ISessionBackend backend, string text) => Task.CompletedTask;
        public DriverInput Cancel() => DriverInput.PressKeys(DriverKey.Escape);
        public DriverInput Interrupt() => DriverInput.PressKeys(DriverKey.CtrlC);
        public DriverInput ShowHistory() => DriverInput.PressKeys(DriverKey.Escape);

        public DriverInput ClearContext()
        {
            ClearCalls++;
            OnClear?.Invoke();
            return DriverInput.TypeCommand("/clear");
        }

        public List<TurnWidgetDto> ReadWidgets(string agentSessionId, string workingDirectory) => new();
        public SessionUsageDto? ReadUsage(string agentSessionId, string workingDirectory) => null;
        public List<(string AgentSessionId, DateTime LastWriteUtc)> ListTranscripts(string workingDirectory) => new();
    }

    private sealed class NullBackend : ISessionBackend
    {
        public int ProcessId => 1;
        public string Status => "Running";
        public bool IsRunning => true;
        public bool HasExited => false;
        public CircularTerminalBuffer? Buffer => null;

#pragma warning disable CS0067 // Required by the interface, never raised here.
        public event Action<string>? StatusChanged;
        public event Action<int>? ProcessExited;
#pragma warning restore CS0067

        public void Start(string executable, string args, string workingDir, short cols, short rows, Dictionary<string, string>? environmentVars = null) { }
        public void Write(byte[] data) { }
        public Task SendTextAsync(string text) => Task.CompletedTask;
        public Task SendEnterAsync() => Task.CompletedTask;
        public void Resize(short cols, short rows) { }
        public Task GracefulShutdownAsync(int timeoutMs = 5000) => Task.CompletedTask;
        public void Dispose() { }
    }
}
