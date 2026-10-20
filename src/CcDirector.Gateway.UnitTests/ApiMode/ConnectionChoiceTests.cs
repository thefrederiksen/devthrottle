using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.ControlApi;
using CcDirector.Core.Agents;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Discovery;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Running;
using CcDirector.Gateway.Tenancy;
using CcDirector.Gateway.Util;
using CcDirector.Gateway.Workspaces;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.UnitTests.ApiMode;

/// <summary>
/// API mode, phase 5 (mission #4070): CHOOSING IT. The one switch (<see cref="ConnectionModes.ApiModeOffered"/>), the
/// connections the New Session row offers for each agent, the refusal of a create that asks for a mode that is not
/// offered, and the words the Director reports for each wrapper. The routes run on an in-memory test server with a
/// fake Director: no port, no process, no database.
/// </summary>
public sealed class ConnectionChoiceTests : IDisposable
{
    private const string DirectorId = "director-connection-choice";
    private const string Machine = "CHOICE-PC";
    private const string DeviceHeader = "X-Test-Device";
    /// <summary>Stands in for the machine token a Director authenticates with: no device type, no session.</summary>
    private const string DirectorCredentialHeader = "X-Test-Director";

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly string _root = Path.Combine(Path.GetTempPath(), "cc-connection-choice-" + Guid.NewGuid().ToString("N"));
    private readonly DirectorRegistry _registry;
    private WebApplication? _app;
    private HttpClient? _http;
    private DeviceRegistry? _devices;
    private readonly GatewayDbTestHarness _db = new();

    /// <summary>What the fake Director's agents-list verb answers.</summary>
    private List<AgentChoiceDto> _reportedAgents = new();
    private NewSessionRequest? _directorDoorSaw;
    /// <summary>What the reporting Director's interrupted-list verb answers, as the JSON it would serve.</summary>
    private string _journalsJson = "[]";
    /// <summary>Whether the restore asked the reporting Director to drop the journal row.</summary>
    private bool _journalRowRemoved;
    private NewSessionRequest? _machineDoorSaw;
    private WorkspaceStore? _workspaces;

    public ConnectionChoiceTests()
    {
        Directory.CreateDirectory(_root);
        _registry = new DirectorRegistry(Path.Combine(_root, "instances"));
    }

    public void Dispose()
    {
        _http?.Dispose();
        if (_app is not null)
        {
            _app.StopAsync().GetAwaiter().GetResult();
            _app.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        _registry.Dispose();
        _devices?.Dispose();
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    // ---- the contract: what is offered, and the words -------------------------------------------------------------

    [Fact]
    public void Offered_ApiModeOnAndTheWrapperDeclaresIt_IsTerminalThenApi()
    {
        // Act
        var offered = ConnectionModes.Offered(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, apiModeOffered: true);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, offered);
    }

    [Fact]
    public void Offered_ApiModeOff_IsTerminalAloneEvenForAWrapperThatDeclaresApi()
    {
        // Act
        var offered = ConnectionModes.Offered(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, apiModeOffered: false);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal }, offered);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("terminal")]
    [InlineData("terminal,smoke-signals")]
    public void Offered_AWrapperWithoutApi_IsTerminalAloneWhateverItDeclares(string? declaredWords)
    {
        // Arrange
        var declared = declaredWords?.Split(',', StringSplitOptions.RemoveEmptyEntries);

        // Act
        var offered = ConnectionModes.Offered(declared, apiModeOffered: true);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal }, offered);
    }

    [Fact]
    public void Offered_WithoutTheSwitchArgument_ReadsTheOneSwitch()
    {
        // Act
        var offered = ConnectionModes.Offered(new[] { ConnectionModes.Terminal, ConnectionModes.Api });

        // Assert
        Assert.Equal(ConnectionModes.Offered(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, ConnectionModes.ApiModeOffered), offered);
    }

    [Theory]
    [InlineData("terminal", "Terminal")]
    [InlineData("api", "API mode")]
    public void LabelOf_EachMode_IsTheWordsAPersonSees(string mode, string label)
    {
        // Act
        var result = ConnectionModes.LabelOf(mode);

        // Assert
        Assert.Equal(label, result);
    }

    [Fact]
    public void LabelOf_AnUnknownWord_Throws()
    {
        // Act
        var error = Assert.Throws<ArgumentException>(() => ConnectionModes.LabelOf("rpc"));

        // Assert
        Assert.Contains("\"rpc\" is not a connection mode", error.Message);
    }

    [Theory]
    [InlineData("api", "API mode")]
    [InlineData("API", "API mode")]
    [InlineData("terminal", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TagOf_ASessionsConnectionMode_IsTheRosterTagOrNone(string? mode, string? tag)
    {
        // Act
        var result = ConnectionModes.TagOf(mode);

        // Assert
        Assert.Equal(tag, result);
    }

    [Fact]
    public void ConnectionChoiceFor_Modes_IsOneLabelledDescribedChoicePerModeInOrder()
    {
        // Act
        var choices = ConnectionChoiceDto.For(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, "Pi");

        // Assert
        Assert.Equal(new[] { ("terminal", "Terminal"), ("api", "API mode") }, choices.Select(c => (c.Mode, c.Label)));
        Assert.Null(choices[0].Description);
        Assert.Equal(ConnectionModes.DescriptionOf(ConnectionModes.Api, "Pi"), choices[1].Description);
    }

    [Fact]
    public void DescriptionOf_ApiMode_SaysWhatItDoesForTheNamedAgent_AndPromisesOnlyWhatTheMissionDelivers()
    {
        // Act
        var description = ConnectionModes.DescriptionOf(ConnectionModes.Api, "Pi");

        // Assert
        Assert.Equal(
            "Prompts are handed to Pi as messages, and each one is confirmed or refused with a reason. " +
            "The Chat shows the conversation live. You can take the wheel to use Pi's own terminal, then hand it back.",
            description);
    }

    [Fact]
    public void DescriptionOf_Terminal_IsNone_TheDefaultNeedsNoExplaining()
    {
        // Act
        var description = ConnectionModes.DescriptionOf(ConnectionModes.Terminal, "Pi");

        // Assert
        Assert.Null(description);
    }

    [Fact]
    public void DescriptionOf_AnUnknownWord_Throws()
    {
        // Act
        var error = Assert.Throws<ArgumentException>(() => ConnectionModes.DescriptionOf("rpc", "Pi"));

        // Assert
        Assert.Contains("\"rpc\" is not a connection mode", error.Message);
    }

    // ---- the Director: what each wrapper declares -----------------------------------------------------------------

    [Fact]
    public void DeclaredConnectionModes_Pi_IsTerminalAndApi()
    {
        // Act
        var modes = CatalogReadExecutor.DeclaredConnectionModes(AgentKind.Pi);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, modes);
    }

    [Theory]
    [InlineData(AgentKind.ClaudeCode)]
    [InlineData(AgentKind.Codex)]
    [InlineData(AgentKind.Gemini)]
    public void DeclaredConnectionModes_AnAgentWithoutAnApiDialect_IsTerminalAlone(AgentKind kind)
    {
        // Act
        var modes = CatalogReadExecutor.DeclaredConnectionModes(kind);

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal }, modes);
    }

    // ---- the Gateway's fold ----------------------------------------------------------------------------------------

    [Fact]
    public void Offer_ApiModeOn_GivesApiModeOnlyToTheAgentWhoseWrapperDeclaresIt()
    {
        // Arrange
        var agents = ReportedAgents();

        // Act
        var offered = ConnectionChoiceFold.Offer(agents, apiModeOffered: true, DirectorOf("2.25.0"));

        // Assert
        Assert.Equal(new[] { "Terminal", "API mode" }, offered.Single(a => a.Type == "Pi").Connections.Select(c => c.Label));
        Assert.Equal(new[] { null, ConnectionModes.DescriptionOf(ConnectionModes.Api, "Pi") },
            offered.Single(a => a.Type == "Pi").Connections.Select(c => c.Description));
        Assert.Equal(new[] { "Terminal" }, offered.Single(a => a.Type == "ClaudeCode").Connections.Select(c => c.Label));
        Assert.Equal(new[] { "Terminal" }, offered.Single(a => a.Type == "Codex").Connections.Select(c => c.Label));
    }

    [Fact]
    public void Offer_ApiModeOff_GivesEveryAgentTerminalAlone()
    {
        // Arrange
        var agents = ReportedAgents();

        // Act
        var offered = ConnectionChoiceFold.Offer(agents, apiModeOffered: false, DirectorOf("2.25.0"));

        // Assert
        Assert.All(offered, a => Assert.Equal(new[] { ConnectionModes.Terminal }, a.Connections.Select(c => c.Mode)));
    }

    [Theory]
    [InlineData("2.23.0")]
    [InlineData("2.24.0")]
    [InlineData("v2.24.9+68fd9d7")]
    [InlineData("")]
    [InlineData("dev")]
    public void Offer_ADirectorOlderThan2_25OrUnreadable_GivesEveryAgentTerminalAloneEvenWhereTheWrapperDeclaresApi(string version)
    {
        // Arrange: v2.24.0 declares API mode for Pi, and the create would be refused (the #4257 review, finding 1).
        var agents = ReportedAgents();

        // Act
        var offered = ConnectionChoiceFold.Offer(agents, apiModeOffered: true, DirectorOf(version));

        // Assert
        Assert.All(offered, a => Assert.Equal(new[] { ConnectionModes.Terminal }, a.Connections.Select(c => c.Mode)));
    }

    [Theory]
    [InlineData("2.25.0")]
    [InlineData("v2.25.0+1a2b3c4")]
    [InlineData("2.26.1")]
    public void Offer_ADirectorOf2_25OrLater_GivesPiApiMode(string version)
    {
        // Act
        var offered = ConnectionChoiceFold.Offer(ReportedAgents(), apiModeOffered: true, DirectorOf(version));

        // Assert
        Assert.Equal(new[] { ConnectionModes.Terminal, ConnectionModes.Api }, offered.Single(a => a.Type == "Pi").Connections.Select(c => c.Mode));
    }

    [Theory]
    [InlineData(null, false, null)]
    [InlineData("", false, null)]
    [InlineData("terminal", false, null)]
    [InlineData("terminal", true, null)]
    [InlineData("api", true, null)]
    public void RefusalOf_TerminalOrOfferedApi_GoesOnToTheDirector(string? mode, bool offered, string? expected)
    {
        // Act
        var refusal = ConnectionChoiceFold.RefusalOf(new NewSessionRequest { ConnectionMode = mode }, offered, validatedRestore: false);

        // Assert
        Assert.Equal(expected, refusal);
    }

    [Fact]
    public void RefusalOf_ApiWhileNotOffered_IsRefusedWithTheReason()
    {
        // Act
        var refusal = ConnectionChoiceFold.RefusalOf(new NewSessionRequest { ConnectionMode = ConnectionModes.Api }, apiModeOffered: false, validatedRestore: false);

        // Assert
        Assert.NotNull(refusal);
        Assert.StartsWith("API mode is not offered yet", refusal);
    }

    [Fact]
    public void RefusalOf_AValidatedRestoreOfAnApiModeSeatWhileNotOffered_GoesOnSoTheSeatComesBackAsItRan()
    {
        // Act
        var refusal = ConnectionChoiceFold.RefusalOf(
            new NewSessionRequest { ConnectionMode = ConnectionModes.Api, RestoreClaim = ForgedClaim() }, apiModeOffered: false, validatedRestore: true);

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void RefusalOf_AClaimInTheBodyThatNoDoorValidated_IsRefusedWhileNotOffered()
    {
        // Act: the phase 5 review's finding 1 - a claim the caller wrote proves nothing by being there.
        var refusal = ConnectionChoiceFold.RefusalOf(
            new NewSessionRequest { ConnectionMode = ConnectionModes.Api, RestoreClaim = ForgedClaim() }, apiModeOffered: false, validatedRestore: false);

        // Assert
        Assert.NotNull(refusal);
        Assert.StartsWith("API mode is not offered yet", refusal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void RefusalOf_AnUnknownWord_IsRefusedWhateverTheSwitch(bool offered)
    {
        // Act
        var refusal = ConnectionChoiceFold.RefusalOf(new NewSessionRequest { ConnectionMode = "API" }, offered, validatedRestore: false);

        // Assert
        Assert.Equal("\"API\" is not a connection mode: it is \"terminal\" or \"api\".", refusal);
    }

    // ---- a Director too old to carry API mode (the phase 6 review, finding 1) --------------------------------------

    [Theory]
    [InlineData("2.22.0")]
    [InlineData("v2.23.0+68fd9d7")]
    [InlineData("2.24.0")]
    [InlineData("2.24.9")]
    [InlineData("2.25.0-rc1")]
    public void DirectorCannotCarry_ApiModeToADirectorOlderThan2_25_IsRefusedNamingTheVersionNeeded(string version)
    {
        // Arrange
        var director = new DirectorDto { DirectorId = "d-old", MachineName = "OLD-PC", Version = version };

        // Act
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = ConnectionModes.Api }, director);

        // Assert: "2.25.0-rc1" is release 2.25.0 and goes; the older ones, 2.23 and 2.24 among them, are refused in words.
        if (version == "2.25.0-rc1")
            Assert.Null(refusal);
        else
        {
            Assert.NotNull(refusal);
            Assert.Contains("OLD-PC", refusal);
            Assert.Contains("cannot start a session in API mode", refusal);
            Assert.Contains("Update this Director to 2.25.0 or later", refusal);
        }
    }

    [Fact]
    public void DirectorCannotCarry_ADirectorThatDoesNotReportItsVersion_IsRefused()
    {
        // Act
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = ConnectionModes.Api },
            new DirectorDto { DirectorId = "d-unknown", MachineName = "SOME-PC", Version = "" });

        // Assert
        Assert.NotNull(refusal);
        Assert.Contains("a version it does not report", refusal);
    }

    [Theory]
    [InlineData("not-a-version")]
    [InlineData("two.twenty-five")]
    [InlineData("dev")]
    public void DirectorCannotCarry_ADirectorWhoseVersionCannotBeRead_IsRefused(string version)
    {
        // Act: whether such a Director carries API mode cannot be told, so it is refused like one that reports nothing.
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = ConnectionModes.Api },
            new DirectorDto { DirectorId = "d-odd", MachineName = "ODD-PC", Version = version });

        // Assert
        Assert.NotNull(refusal);
        Assert.Contains($"version {version}", refusal);
        Assert.Contains("Update this Director to 2.25.0 or later", refusal);
    }

    [Theory]
    [InlineData("2.25.0")]
    [InlineData("v2.25.0+1a2b3c4")]
    [InlineData("2.25.3")]
    [InlineData("2.30.0")]
    [InlineData("3.0.0")]
    public void DirectorCannotCarry_ApiModeToADirectorOf2_25OrLater_GoesOn(string version)
    {
        // Act
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = ConnectionModes.Api },
            new DirectorDto { DirectorId = "d-new", MachineName = "NEW-PC", Version = version });

        // Assert
        Assert.Null(refusal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("terminal")]
    public void DirectorCannotCarry_TerminalOrNoWordToAnyDirector_GoesOn(string? mode)
    {
        // Act: an old Director starts the terminal, which is exactly what a terminal create asks for.
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = mode },
            new DirectorDto { DirectorId = "d-old", MachineName = "OLD-PC", Version = "2.10.0" });

        // Assert
        Assert.Null(refusal);
    }

    [Fact]
    public void DirectorCannotCarry_ByIdWithNoWayToReadTheDirector_IsRefused()
    {
        // Act
        var refusal = ConnectionChoiceFold.DirectorCannotCarry(new NewSessionRequest { ConnectionMode = ConnectionModes.Api }, "d-x", directorOf: null);

        // Assert
        Assert.NotNull(refusal);
        Assert.Contains("cannot read which version Director d-x is", refusal);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("director")]
    public async Task PostSessions_ApiModeToADirectorOlderThan2_25_Is409WithTheReasonAndNothingIsSent(string door)
    {
        // Arrange: the Director the create resolves to is v2.24.0, which starts API mode but only half carries it.
        await StartAsync(directorVersion: "2.24.0");

        // Act
        using var response = await Create(door, ConnectionModes.Api);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot start a session in API mode", await response.Content.ReadAsStringAsync());
        Assert.Null(SawAt(door));
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("director")]
    public async Task PostSessions_TerminalToADirectorOlderThan2_25_GoesToTheDirector(string door)
    {
        // Arrange: v2.24.0 is refused API mode, and still takes a terminal create.
        await StartAsync(directorVersion: "2.24.0");

        // Act
        using var response = await Create(door, ConnectionModes.Terminal);

        // Assert: the guard is for API mode only; an older Director still takes a terminal create.
        Assert.True(response.IsSuccessStatusCode, $"{door} door answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(SawAt(door));
    }

    // ---- the crash-journal restore (the phase 6 review, round 2, finding R2-1) -------------------------------------

    [Fact]
    public async Task RestoreInterrupted_AnApiModeSeatToADirectorOlderThan2_25_Is409WithTheReason_NothingIsStarted_TheRowIsKept()
    {
        // Arrange: the reporting Director, which is also the target, is v2.24.0; the journal holds an API-mode Pi seat.
        await StartAsync(directorVersion: "2.24.0");
        var row = JournalApiModeSeat(ConnectionModes.Api);

        // Act
        using var response = await RestoreAsync(row);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("cannot start a session in API mode", await response.Content.ReadAsStringAsync());
        Assert.Null(_directorDoorSaw);
        Assert.False(_journalRowRemoved);
    }

    [Fact]
    public async Task RestoreInterrupted_AnApiModeSeatToADirectorOf2_25_ContinuesInApiMode()
    {
        // Arrange
        await StartAsync(directorVersion: "2.25.0");
        var row = JournalApiModeSeat(ConnectionModes.Api);

        // Act
        using var response = await RestoreAsync(row);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"restore answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(ConnectionModes.Api, _directorDoorSaw?.ConnectionMode);
    }

    [Fact]
    public async Task RestoreInterrupted_ATerminalSeatToADirectorOlderThan2_25_GoesOn()
    {
        // Arrange: the guard is for API mode only; a terminal seat restores on an older Director as before.
        await StartAsync(directorVersion: "2.24.0");
        var row = JournalApiModeSeat(connectionMode: null);

        // Act
        using var response = await RestoreAsync(row);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"restore answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(_directorDoorSaw);
        Assert.Null(_directorDoorSaw!.ConnectionMode);
    }

    private const string DeadDirector = "director-that-died";
    private const int DeadPid = 4242;

    /// <summary>Put one Pi seat in the reporting Director's crash journal, with the given connection mode.</summary>
    private CrashJournalSessionDto JournalApiModeSeat(string? connectionMode)
    {
        var row = new CrashJournalSessionDto
        {
            SessionId = Guid.NewGuid().ToString(), Name = "pi seat", RepoPath = Path.GetTempPath(), Agent = "Pi",
            CreatedAtUtc = new DateTimeOffset(2026, 10, 18, 9, 0, 0, TimeSpan.Zero), ConnectionMode = connectionMode,
        };
        _journalsJson = JsonSerializer.Serialize(new List<CrashJournalDto>
        {
            new()
            {
                DirectorId = DeadDirector, Pid = DeadPid, MachineName = Machine,
                StartedAtUtc = new DateTimeOffset(2026, 10, 18, 8, 0, 0, TimeSpan.Zero),
                LastUpdatedUtc = new DateTimeOffset(2026, 10, 18, 9, 30, 0, TimeSpan.Zero),
                Sessions = { row },
            },
        }, Web);
        return row;
    }

    /// <summary>A person's restore of the row from the Cockpit: the reporting Director is the target.</summary>
    private Task<HttpResponseMessage> RestoreAsync(CrashJournalSessionDto row)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/interrupted/{DeadDirector}/{DeadPid}/restore")
        {
            Content = JsonContent.Create(new RestoreInterruptedRequest { SessionId = row.SessionId, Via = DirectorId }, options: Web),
        };
        request.Headers.Add(DeviceHeader, "desktop");
        return _http!.SendAsync(request);
    }

    // ---- through the routes ---------------------------------------------------------------------------------------

    [Fact]
    public async Task GetAgents_ApiModeOffered_ServesApiModeOnlyToTheWrapperThatDeclaresIt()
    {
        // Arrange: Pi declares API mode; an older Director's row carries no connections at all.
        await StartAsync();
        _reportedAgents = ReportedAgents();

        // Act
        using var response = await _http!.GetAsync($"/directors/{DirectorId}/agents");

        // Assert: read off the wire as client-core reads it.
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var connections = doc.RootElement.EnumerateArray().ToDictionary(
            a => a.GetProperty("type").GetString()!,
            a => a.GetProperty("connections").EnumerateArray()
                .Select(c => (c.GetProperty("mode").GetString()!, c.GetProperty("label").GetString()!)).ToList());
        Assert.Equal(3, connections.Count);
        Assert.Equal(new[] { ("terminal", "Terminal"), ("api", "API mode") }, connections["Pi"]);
        Assert.Equal(new[] { ("terminal", "Terminal") }, connections["ClaudeCode"]);
        Assert.Equal(new[] { ("terminal", "Terminal") }, connections["Codex"]);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("director")]
    public async Task PostSessions_ApiModeOffered_GoesToTheDirectorInApiMode(string door)
    {
        // Arrange
        await StartAsync();

        // Act
        using var response = await Create(door, ConnectionModes.Api);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"{door} door answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(SawAt(door));
        Assert.Equal(ConnectionModes.Api, SawAt(door)!.ConnectionMode);
    }

    [Theory]
    [InlineData("machine")]
    [InlineData("director")]
    public async Task PostSessions_AnUnknownConnectionWord_Is400AndNothingIsSent(string door)
    {
        // Arrange
        await StartAsync();

        // Act
        using var response = await Create(door, "telepathy");

        // Assert
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("\\\"telepathy\\\" is not a connection mode", await response.Content.ReadAsStringAsync());
        Assert.Null(SawAt(door));
    }

    [Theory]
    [InlineData("machine", "terminal")]
    [InlineData("director", "terminal")]
    [InlineData("machine", null)]
    [InlineData("director", null)]
    public async Task PostSessions_TerminalOrNoWord_GoesToTheDirectorUnchanged(string door, string? mode)
    {
        // Arrange
        await StartAsync();

        // Act
        using var response = await Create(door, mode);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"{door} door answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(SawAt(door));
        Assert.Equal(mode, SawAt(door)!.ConnectionMode);
    }

    [Fact]
    public async Task PostDirectorSessions_ApiModeWithAForgedRestoreClaimFromADevice_IsRefusedAndNothingIsSent()
    {
        // Arrange
        await StartAsync();

        // Act
        using var response = await Create("director", ConnectionModes.Api, ForgedClaim());

        // Assert: the claim is checked before the switch is read and fails - this key is not the one the Director said
        // Hello on - so a claim that is not real never exempts anything.
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains($"a restore claim is sent only by Director '{DirectorId}' itself", await response.Content.ReadAsStringAsync());
        Assert.Null(_directorDoorSaw);
    }

    [Fact]
    public async Task PostDirectorSessions_ARealRestoreOfAnApiModeSeat_GoesToTheDirectorInApiMode()
    {
        // Arrange: the Director the create is for, on the credential it said Hello on, restoring a seat that ran in API mode.
        await StartAsync();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/directors/{DirectorId}/sessions")
        {
            Content = JsonContent.Create(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "Pi",
                ConnectionMode = ConnectionModes.Api,
                RestoreClaim = new WorkspaceRestoreClaim { WorkspaceId = "w-1", SeatSessionId = "s-1", Token = "token-1" },
            }, options: Web),
        };
        request.Headers.Add(DirectorCredentialHeader, "yes");

        // Act
        using var response = await _http!.SendAsync(request);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"director door answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.NotNull(_directorDoorSaw);
        Assert.Equal(ConnectionModes.Api, _directorDoorSaw!.ConnectionMode);
        Assert.Equal("s-1", _directorDoorSaw.RestoreClaim?.SeatSessionId);
    }

    [Fact]
    public async Task GetAgents_ADirectorOf2_24_ServesTerminalAloneEvenForPi()
    {
        // Arrange: the same Director answer as above, from a v2.24.0 Director that declares API mode for Pi.
        await StartAsync(directorVersion: "2.24.0");
        _reportedAgents = ReportedAgents();

        // Act
        using var response = await _http!.GetAsync($"/directors/{DirectorId}/agents");

        // Assert: the Cockpit and the phone are never offered what the create would refuse.
        response.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.All(doc.RootElement.EnumerateArray(), a => Assert.Equal(new[] { "terminal" },
            a.GetProperty("connections").EnumerateArray().Select(c => c.GetProperty("mode").GetString())));
    }

    // ---- a released v2.23 or v2.24 Director reopening its own API-mode seat (the #4257 review, finding 2) -----------

    [Fact]
    public async Task PostDirectorSessions_ARefusedReopenOfAnApiModeSeat_GivesTheClaimBack_SoTheSeatCanBeReopenedLater()
    {
        // Arrange: a v2.24.0 Director claims its API-mode seat on the record, as its way up does, then sends the reopen.
        await StartAsync(directorVersion: "2.24.0");
        SeedClaimedApiModeSeat();

        // Act
        using var response = await SendReopen(asTheDirector: true);

        // Assert: refused, nothing started, and the claim is gone, so the updated Director may claim and reopen it.
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("Update this Director to 2.25.0 or later", await response.Content.ReadAsStringAsync());
        Assert.Null(_directorDoorSaw);
        Assert.Null(SeatOfTheRecord().Restore!.ReopenedAtUtc);
        _workspaces!.RecordRestoreMark(ReopenWorkspace, ReopenMark(), DateTime.UtcNow);
        Assert.Equal(DirectorId, SeatOfTheRecord().Restore!.ReopenedByDirectorId);
    }

    [Fact]
    public async Task PostDirectorSessions_ARefusedApiModeCreateFromADevice_LeavesTheClaimStanding()
    {
        // Arrange: the same create, but from a person's device - only the Director itself may have its claim given back.
        await StartAsync(directorVersion: "2.24.0");
        SeedClaimedApiModeSeat();

        // Act
        using var response = await SendReopen(asTheDirector: false);

        // Assert
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotNull(SeatOfTheRecord().Restore!.ReopenedAtUtc);
    }

    [Fact]
    public async Task PostDirectorSessions_AReopenOfAnApiModeSeatOn2_25_GoesOn_AndTheClaimStands()
    {
        // Arrange
        await StartAsync(directorVersion: "2.25.0");
        SeedClaimedApiModeSeat();

        // Act
        using var response = await SendReopen(asTheDirector: true);

        // Assert
        Assert.True(response.IsSuccessStatusCode, $"director door answered {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        Assert.Equal(ConnectionModes.Api, _directorDoorSaw?.ConnectionMode);
        Assert.NotNull(SeatOfTheRecord().Restore!.ReopenedAtUtc);
    }

    // ---- helpers ---------------------------------------------------------------------------------------------------

    private const string ReopenWorkspace = "smart-shutdown-api";
    private const string ReopenSeat = "a0000000-0000-0000-0000-000000000001";
    private const string SavedConversation = "the-saved-pi-conversation";

    private static DirectorDto DirectorOf(string version) => new() { DirectorId = "d-1", MachineName = "SOME-PC", Version = version };

    private static WorkspaceRestoreMark ReopenMark() => new()
    {
        DirectorId = DirectorId,
        Kind = WorkspaceRestoreMarkKinds.Reopened,
        SeatSessionId = ReopenSeat,
    };

    /// <summary>A captured record with one API-mode Pi seat, claimed for a reopen by this Director just now.</summary>
    private void SeedClaimedApiModeSeat()
    {
        var now = DateTime.UtcNow;
        _workspaces!.Create(new WorkspaceDocument
        {
            Id = ReopenWorkspace,
            Name = "Smart shutdown",
            Origin = WorkspaceOrigins.Captured,
            Machine = Machine,
            DirectorId = DirectorId,
            ShutdownKind = WorkspaceShutdownKinds.SmartShutdown,
            Seats = new()
            {
                new WorkspaceSeat
                {
                    SessionId = ReopenSeat,
                    Name = "A Pi seat",
                    Agent = "Pi",
                    RepoPath = "/repos/devthrottle",
                    DrainState = WorkspaceDrainStates.EndedAtLimit,
                    ClaudeSessionId = SavedConversation,
                    ConnectionMode = ConnectionModes.Api,
                    Restore = new WorkspaceSeatRestore { Decision = WorkspaceRestoreDecisions.Undecided },
                },
            },
        }, now);
        _workspaces.RecordRestoreMark(ReopenWorkspace, ReopenMark(), now);
    }

    private WorkspaceSeat SeatOfTheRecord() => _workspaces!.Get(ReopenWorkspace)!.Seats.Single();

    /// <summary>The create a released Director's way up sends for a reopen: the saved conversation, no restore claim.</summary>
    private Task<HttpResponseMessage> SendReopen(bool asTheDirector)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/directors/{DirectorId}/sessions")
        {
            Content = JsonContent.Create(new NewSessionRequest
            {
                RepoPath = "/repos/devthrottle",
                Agent = "Pi",
                Name = "A Pi seat",
                ResumeSessionId = SavedConversation,
                ConnectionMode = ConnectionModes.Api,
            }, options: Web),
        };
        if (asTheDirector) request.Headers.Add(DirectorCredentialHeader, "yes");
        else request.Headers.Add(DeviceHeader, "desktop");
        return _http!.SendAsync(request);
    }

    /// <summary>A restore claim as any caller could write one: nothing about it says who sent it.</summary>
    private static WorkspaceRestoreClaim ForgedClaim() => new() { WorkspaceId = "forged", SeatSessionId = "forged" };

    private static List<AgentChoiceDto> ReportedAgents() => new()
    {
        new AgentChoiceDto { Type = "Pi", DisplayName = "Pi", Connections = ConnectionChoiceDto.For(new[] { "terminal", "api" }, "Pi") },
        new AgentChoiceDto { Type = "ClaudeCode", DisplayName = "Claude Code", Connections = ConnectionChoiceDto.For(new[] { "terminal" }, "Claude Code") },
        // A Director that predates phase 5 sends no connections.
        new AgentChoiceDto { Type = "Codex", DisplayName = "Codex" },
    };

    private NewSessionRequest? SawAt(string door) => door == "machine" ? _machineDoorSaw : _directorDoorSaw;

    /// <summary>A person's New Session from the Cockpit: a device credential, so the origin is settled as a person's.</summary>
    private Task<HttpResponseMessage> Create(string door, string? connectionMode, WorkspaceRestoreClaim? restoreClaim = null)
    {
        var url = door == "machine" ? $"/machines/{Machine}/sessions" : $"/directors/{DirectorId}/sessions";
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(new NewSessionRequest
            {
                RepoPath = Path.GetTempPath(),
                Agent = "Pi",
                ConnectionMode = connectionMode,
                RestoreClaim = restoreClaim,
            }, options: Web),
        };
        request.Headers.Add(DeviceHeader, "desktop");
        return _http!.SendAsync(request);
    }

    private sealed class AlwaysFound : IDirectorTargetResolver
    {
        public Task<DirectorTargetResult> ResolveAsync(string machine, string? director, CancellationToken ct)
            => Task.FromResult(new DirectorTargetResult(DirectorId, null));
    }

    /// <summary>The real agents leg and both real create doors, with a fake Director behind them, on an in-memory test server.</summary>
    private async Task StartAsync(string directorVersion = "2.25.0-test")
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseTestServer();
        var app = builder.Build();
        app.Use(async (ctx, next) =>
        {
            if (ctx.Request.Headers.TryGetValue(DeviceHeader, out var device))
                ctx.Items[AuthMiddleware.DeviceTypeItemKey] = device.ToString();
            if (ctx.Request.Headers.ContainsKey(DirectorCredentialHeader))
                ctx.Items[AuthMiddleware.AuthenticatedCredentialItemKey] = "machine-token";
            await next();
        });
        app.UseRouting();

        _registry.RegisterFromStream(DirectorId, Machine, "tester", directorVersion, 1,
            new DateTime(2026, 10, 16, 9, 0, 0, DateTimeKind.Utc), TenantId.Local, registeredByCredential: "machine-token");

        DirectorCommandRouter.SendDirectorCommandAsync send = (directorId, command, ct) =>
        {
            switch (command.Verb)
            {
                case "agents-list":
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(JsonSerializer.Serialize(_reportedAgents, Web)));
                case "interrupted-list":
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(_journalsJson));
                case "interrupted-remove":
                    _journalRowRemoved = true;
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success("{}"));
                case "create":
                    _directorDoorSaw = JsonSerializer.Deserialize<NewSessionRequest>(command.PayloadJson ?? "{}", Web);
                    return Task.FromResult<DirectorCommandResult?>(DirectorCommandResult.Success(
                        JsonSerializer.Serialize(new SessionDto { SessionId = Guid.NewGuid().ToString() }, Web)));
                default:
                    return Task.FromResult<DirectorCommandResult?>(null);
            }
        };
        var spawner = new MachineSessionSpawner(new AlwaysFound(), (directorId, req, ct) =>
        {
            _machineDoorSaw = req;
            return Task.FromResult<(bool, SessionDto?, string?)>((true, new SessionDto { SessionId = Guid.NewGuid().ToString() }, null));
        });

        // Its own store in this test's folder: the parameterless registry shares one file with every other test.
        _devices = new DeviceRegistry(Path.Combine(_root, "devices.json"));
        var boundary = new HostedTenantBoundary(new SingleTenantContext(), _devices, hosted: false);
        GatewayEndpoints.Map(app, false, _registry, version: "test", token: "test-token",
            tenantBoundary: boundary, aiGate: TenantAiGate.SelfHost,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown, sendCommand: send,
            workspaces: _workspaces = new WorkspaceStore(_db.Open()));
        MachineEndpoints.Map(app, false, new LauncherRegistry(), spawner, boundary: boundary,
            sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown, directors: _registry);
        await app.StartAsync();
        _app = app;
        _http = app.GetTestClient();
    }
}
