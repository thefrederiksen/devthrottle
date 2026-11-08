using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Configuration;
using CcDirector.Core.Sessions;
using CcDirector.Core.Setup;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Inbox;

/// <summary>
/// Issue #4744, on the Director: a create that names the cc-devthrottle commands its instructions use is refused, with
/// the check's sentence, BEFORE anything is created, when this machine's tools lack one - a 409, so the Gateway passes
/// the sentence on as the reason the Secretary did not start. A create that names none is never checked.
/// </summary>
[Trait("Category", "FactoryInbox")]
public sealed class SeatToolCommandsOnTheDirectorTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly SessionManager _sm = new(new AgentOptions());
    private readonly List<string> _asked = new();

    public void Dispose()
    {
        foreach (var session in _sm.ListSessions())
            _sm.KillSessionAsync(session.Id).GetAwaiter().GetResult();
        _sm.Dispose();
    }

    /// <summary>Tools that have "factory" but no "factory inbox", answering as the real command line does.</summary>
    private ToolCommandCheck ToolsWithoutTheInbox() => new(() => "cc-devthrottle.exe", (_, args, _) =>
    {
        lock (_asked) _asked.Add(string.Join(' ', args));
        return Task.FromResult(args.Count > 1 && args[0] == "factory" && args[1] == "inbox"
            ? new ProcessRunner.Result(2, "", "Error: No such command 'inbox'.", true)
            : new ProcessRunner.Result(0, "Usage: ...", "", true));
    });

    private Task<DirectorCommandResult> CreateAsync(NewSessionRequest req, ToolCommandCheck check)
        => SessionCommandExecutor.CreateAsync(_sm, "dir-tools", new DirectorCommand
        {
            CommandId = "cmd-tools",
            Verb = "create",
            SessionId = "",
            PayloadJson = JsonSerializer.Serialize(req, Json),
        }, new SessionCommandServices { ToolCommands = check });

    [Fact]
    public async Task Create_InstructionsUseACommandTheToolsLack_RefusedAsAConflictNamingIt_AndNothingIsCreated()
    {
        var repo = Path.GetTempPath();
        var result = await CreateAsync(new NewSessionRequest
        {
            RepoPath = repo,
            // A harmless shell, so that if the check were gone the create would start something this test can see
            // (and Dispose kills), never an agent.
            Agent = "RawCli",
            Command = OperatingSystem.IsWindows() ? "cmd" : "sh",
            CommandArgs = OperatingSystem.IsWindows() ? "/k" : "",
            Name = "Citizenship - Secretary - file the factory inbox",
            RequiredToolCommands = ["factory inbox new", "session done"],
        }, ToolsWithoutTheInbox());

        Assert.Equal(DirectorCommandStatus.Conflict, result.Status);
        Assert.Contains("'cc-devthrottle factory inbox new'", result.Error);
        Assert.Contains("No such command 'inbox'", result.Error);
        Assert.Empty(_sm.ListSessions());
        Assert.Contains("factory inbox new --help", _asked);
    }

    [Fact]
    public async Task Create_NamesNoCommands_TheToolsAreNeverAsked()
    {
        // No repository either, so the create stops at its ordinary first check without starting anything - the
        // point is only that the tools were not asked.
        var result = await CreateAsync(new NewSessionRequest { RepoPath = "", Agent = "ClaudeCode" }, ToolsWithoutTheInbox());

        Assert.Equal(DirectorCommandStatus.BadRequest, result.Status);
        Assert.Equal("repoPath is required", result.Error);
        Assert.Empty(_asked);
    }
}
