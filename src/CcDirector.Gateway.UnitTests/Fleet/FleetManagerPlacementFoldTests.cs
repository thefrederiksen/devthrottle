using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The Fleet Manager setting's folded answer (the Fleet Manager mission, step 5). Pure: every input is handed in.
///
/// The sentences are asserted whole, as literals, because the page renders them verbatim - a constant substituted
/// for any of them would change what the owner reads, and a looser assertion would stay green.
/// </summary>
public sealed class FleetManagerPlacementFoldTests
{
    // 2026-09-16 14:30 UTC. The account's zone is UTC unless a test says otherwise.
    private static readonly DateTime Now = new(2026, 9, 16, 14, 30, 0, DateTimeKind.Utc);
    private const string MarkedId = "30000000-0000-4000-8000-000000000001";

    private static DirectorDto Running(string machine, string id = "dir-a") => new()
    {
        DirectorId = id, MachineName = machine, LastSeen = Now.AddSeconds(-20), Version = "2.5.0",
    };

    private static LauncherDto Launcher(string machine, DateTime? lastSeen = null) => new()
    {
        MachineName = machine, Version = "2.5.0", LastSeenAt = lastSeen ?? Now.AddSeconds(-10),
    };

    private static FleetManagerMachineFacts WithDirector(string machine, DateTime? firstSeen = null, string? firstAgent = null)
        => new(machine, Launcher(machine), LauncherReach.Connected, true, new[] { Running(machine) }, firstSeen, firstAgent);

    private static FleetManagerMachineFacts LauncherOnly(string machine, DateTime? firstSeen = null)
        => new(machine, Launcher(machine), LauncherReach.Connected, true, Array.Empty<DirectorDto>(), firstSeen);

    private static FleetManagerMachineFacts Offline(string machine, DateTime? historyLastSeen = null, DateTime? firstSeen = null)
        => new(machine, null, LauncherReach.NoLauncher, false, Array.Empty<DirectorDto>(), firstSeen, null, historyLastSeen);

    private static FleetManagerPlacementInputs Inputs(
        IReadOnlyList<FleetManagerMachineFacts> machines,
        string? agent = "ClaudeCode", string? machine = "WORKSTATION-A",
        string? marked = null, IReadOnlyList<SessionDto>? roster = null,
        IReadOnlyList<AgentChoiceDto>? offered = null, TimeZoneInfo? tz = null)
        => new(agent, machine, marked, machines, roster ?? Array.Empty<SessionDto>(), offered, tz ?? TimeZoneInfo.Utc, Now);

    private static SessionDto Session(string id, string state = "Idle", string? controller = null, DateTime? created = null,
        string machine = "WORKSTATION-A", string agent = "ClaudeCode") => new()
    {
        SessionId = id, ActivityState = state, ControllerSessionId = controller,
        CreatedAt = created ?? Now.AddHours(-7).AddMinutes(-28), MachineName = machine, Agent = agent,
    };

    private static FleetManagerMachineChoiceDto MachineOf(FleetManagerPlacementDto dto, string name)
        => Assert.Single(dto.Machines, m => m.Machine == name);

    // ---- the three computer states ----------------------------------------------------------------------

    [Fact]
    public void Fold_DirectorRunning_IsSelectableAndSaysSo()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }));

        var m = MachineOf(dto, "WORKSTATION-A");
        Assert.Equal("running", m.State);
        Assert.Equal("On - Director running", m.StateLabel);
        Assert.Equal("ok", m.Tone);
        Assert.True(m.Selectable);
    }

    [Fact]
    public void Fold_DirectorRunningAndAgentOffered_SaysTheAgentIsInstalled()
    {
        var offered = new[] { new AgentChoiceDto { Type = "Codex" }, new AgentChoiceDto { Type = "ClaudeCode" } };
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, offered: offered));

        Assert.Equal("- Claude Code installed", MachineOf(dto, "WORKSTATION-A").Detail);
        Assert.True(Assert.Single(dto.Agents, a => a.Value == "Codex").OfferedOnSavedMachine);
        Assert.False(Assert.Single(dto.Agents, a => a.Value == "Gemini").OfferedOnSavedMachine);
    }

    [Fact]
    public void Fold_DirectorRunningWithoutTheAgent_SaysItIsNotOffered()
    {
        var offered = new[] { new AgentChoiceDto { Type = "Codex" } };
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, offered: offered));

        Assert.Equal("- Claude Code is not offered by the Director there", MachineOf(dto, "WORKSTATION-A").Detail);
    }

    [Fact]
    public void Fold_AgentListUnknown_MarksNoAgentEitherWay()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }));

        Assert.Null(MachineOf(dto, "WORKSTATION-A").Detail);
        Assert.All(dto.Agents, a => Assert.Null(a.OfferedOnSavedMachine));
    }

    [Fact]
    public void Fold_LauncherConnectedNoDirector_SaysTheLauncherWillStartOne()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { LauncherOnly("WORKSTATION-A") }));

        var m = MachineOf(dto, "WORKSTATION-A");
        Assert.Equal("launcher-will-start", m.State);
        Assert.Equal("On - no Director running. The launcher will start one.", m.StateLabel);
        Assert.Equal("go", m.Tone);
        Assert.True(m.Selectable);
    }

    [Fact]
    public void Fold_StoppedOrQuietDirector_IsNotCalledRunning()
    {
        var stopped = new DirectorDto { DirectorId = "d1", MachineName = "WORKSTATION-A", LastSeen = Now, StoppedAtUtc = Now };
        var quiet = new DirectorDto { DirectorId = "d2", MachineName = "WORKSTATION-A", LastSeen = Now.AddMinutes(-3) };
        var facts = new FleetManagerMachineFacts("WORKSTATION-A", Launcher("WORKSTATION-A"), LauncherReach.Connected, true,
            new[] { stopped, quiet });

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { facts }));

        Assert.Equal("launcher-will-start", MachineOf(dto, "WORKSTATION-A").State);
    }

    [Fact]
    public void Fold_LauncherTooOldForCommands_IsNotReachableInItsOwnWords()
    {
        var facts = new FleetManagerMachineFacts("WORKSTATION-B", Launcher("WORKSTATION-B"), LauncherReach.NotStreamCapable, true,
            Array.Empty<DirectorDto>());

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { facts }, machine: "WORKSTATION-B"));

        var m = MachineOf(dto, "WORKSTATION-B");
        Assert.Equal("not-reachable", m.State);
        Assert.Equal("Not reachable - its launcher is too old for commands.", m.StateLabel);
        Assert.Equal("The launcher is running but is older than remote commands, so it cannot start a Director there. "
                     + "Update it once on that computer; after that the Fleet Manager can run there.", m.Detail);
        Assert.False(m.Selectable);
    }

    [Fact]
    public void Fold_LauncherDeclaresItCannotStartADirector_IsNotSelectable()
    {
        var facts = new FleetManagerMachineFacts("WORKSTATION-B", Launcher("WORKSTATION-B"), LauncherReach.Connected, false,
            Array.Empty<DirectorDto>());

        var m = MachineOf(FleetManagerPlacementFold.Fold(Inputs(new[] { facts }, machine: "WORKSTATION-B")), "WORKSTATION-B");

        Assert.Equal("not-reachable", m.State);
        Assert.Equal("On - but it cannot run there.", m.StateLabel);
        Assert.False(m.Selectable);
    }

    [Fact]
    public void Fold_OfflineComputer_SaysWhenItWasLastSeenAndCannotBeChosen()
    {
        var yesterday = new DateTime(2026, 9, 15, 22, 40, 0, DateTimeKind.Utc);
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A"), Offline("WORKSTATION-C", yesterday) }));

        var m = MachineOf(dto, "WORKSTATION-C");
        Assert.Equal("not-reachable", m.State);
        Assert.Equal("Not reachable - last seen yesterday 22:40.", m.StateLabel);
        Assert.Equal("It cannot run there until it is back.", m.Detail);
        Assert.Equal("bad", m.Tone);
        Assert.False(m.Selectable);
        Assert.Equal(yesterday, m.LastSeenUtc);
    }

    [Fact]
    public void Fold_OfflineComputerWithNoRecord_SaysTheGatewayHasNotHeardFromIt()
    {
        var m = MachineOf(FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A"), Offline("WORKSTATION-C") })),
            "WORKSTATION-C");

        Assert.Equal("Not reachable - the Gateway has not heard from it since the Gateway last started.", m.StateLabel);
        Assert.Null(m.LastSeenUtc);
    }

    [Fact]
    public void Fold_SavedComputerNoLongerKnown_IsListedAsNotReachable()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, machine: "WORKSTATION-GONE"));

        Assert.False(MachineOf(dto, "WORKSTATION-GONE").Selectable);
        Assert.Equal("unreachable", dto.Status.State);
    }

    [Fact]
    public void Fold_Machines_AreInNameOrder()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { LauncherOnly("WORKSTATION-C"), WithDirector("WORKSTATION-A"), LauncherOnly("workstation-b") }));

        Assert.Equal(new[] { "WORKSTATION-A", "workstation-b", "WORKSTATION-C" }, dto.Machines.Select(m => m.Machine));
    }

    // ---- the Fleet Manager's state ----------------------------------------------------------------------

    [Fact]
    public void Fold_MarkedSessionLive_IsRunningWithTheSentenceAndWatchingCount()
    {
        var roster = new[]
        {
            Session(MarkedId, "Working", created: new DateTime(2026, 9, 16, 7, 2, 0, DateTimeKind.Utc)),
            Session("30000000-0000-4000-8000-000000000002", "Working", controller: MarkedId),
            Session("30000000-0000-4000-8000-000000000003", "Idle", controller: MarkedId),
            Session("30000000-0000-4000-8000-000000000004", "Exited", controller: MarkedId),
            Session("30000000-0000-4000-8000-000000000005", "Idle"),
        };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, marked: MarkedId, roster: roster));

        Assert.Equal("running", dto.Status.State);
        Assert.Equal("Running now: Claude Code on WORKSTATION-A, since 07:02. Watching 2 sessions.", dto.Status.Sentence);
        Assert.Equal(2, dto.Status.Watching);
        Assert.Equal(MarkedId, dto.Status.SessionId);
        Assert.Equal(new DateTime(2026, 9, 16, 7, 2, 0, DateTimeKind.Utc), dto.Status.SinceUtc);
        Assert.True(dto.Status.Open.Offered);
        Assert.Equal("Open it", dto.Status.Open.Label);
        Assert.True(dto.Status.Restart.Offered);
        Assert.Equal("Restart it", dto.Status.Restart.Label);
        Assert.Equal("Restart the Fleet Manager?", dto.Status.Restart.ConfirmTitle);
        Assert.False(dto.Status.Start.Offered);
        Assert.Equal("move", dto.Save.Verb);
        Assert.Equal("Save and move it", dto.Save.Label);
        Assert.Equal("Move the Fleet Manager?", dto.Save.ConfirmTitle);
        Assert.Contains("The one running now on WORKSTATION-A finishes its current turn", dto.Save.ConfirmMessage);
    }

    [Fact]
    public void Fold_RunningWithOneOwnedSession_SaysSessionInTheSingular()
    {
        var roster = new[] { Session(MarkedId), Session("30000000-0000-4000-8000-000000000002", controller: MarkedId) };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, marked: MarkedId, roster: roster));

        Assert.EndsWith("Watching 1 session.", dto.Status.Sentence);
    }

    [Fact]
    public void Fold_MarkedSessionOwnedBySession_IsNotRunning()
    {
        // The one rule (FleetManagerSessions.IsFleetManager): a marked session another session owns is not the
        // Fleet Manager while that ownership stands.
        var roster = new[] { Session(MarkedId, "Idle", controller: "30000000-0000-4000-8000-000000000009") };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, marked: MarkedId, roster: roster));

        Assert.Equal("not-running", dto.Status.State);
    }

    [Fact]
    public void Fold_MarkedSessionExited_IsNotRunning()
    {
        var roster = new[] { Session(MarkedId, "Exited") };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, marked: MarkedId, roster: roster));

        Assert.Equal("not-running", dto.Status.State);
        Assert.Equal(MarkedId, dto.Status.SessionId);
        Assert.False(dto.Status.Open.Offered);
    }

    [Fact]
    public void Fold_NotRunningOnAComputerWithADirector_OffersStart()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }));

        Assert.Equal("not-running", dto.Status.State);
        Assert.Equal("The Fleet Manager is not running. Start it where the setting says: Claude Code on WORKSTATION-A.",
            dto.Status.Sentence);
        Assert.True(dto.Status.Start.Offered);
        Assert.Equal("Start it", dto.Status.Start.Label);
        Assert.Equal("Starting can take up to 90 seconds when a Director has to be started first.", dto.Status.Start.Note);
        Assert.False(dto.Status.Restart.Offered);
        Assert.False(dto.Status.Open.Offered);
        Assert.Equal("save", dto.Save.Verb);
        Assert.Equal("Save", dto.Save.Label);
        Assert.Null(dto.Save.ConfirmTitle);
    }

    [Fact]
    public void Fold_NotRunningWhereTheLauncherMustStartADirector_SaysSo()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { LauncherOnly("WORKSTATION-A") }, agent: "Codex"));

        Assert.Equal("The Fleet Manager is not running. Start it where the setting says: Codex on WORKSTATION-A. "
                     + "No Director is running there, so the launcher starts one first.", dto.Status.Sentence);
        Assert.True(dto.Status.Start.Offered);
    }

    [Fact]
    public void Fold_SavedComputerUnreachable_SaysWhichAndSinceWhenAndOffersNoStart()
    {
        var seen = new DateTime(2026, 9, 12, 9, 5, 0, DateTimeKind.Utc);
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { Offline("WORKSTATION-A", seen), LauncherOnly("WORKSTATION-B") },
            marked: MarkedId));

        Assert.Equal("unreachable", dto.Status.State);
        Assert.Equal("bad", dto.Status.Tone);
        Assert.Equal("The Fleet Manager is not running, and its computer, WORKSTATION-A, cannot be reached, last seen 12 Sep 09:05. "
                     + "It does not move by itself: choose another computer below and save to move it.", dto.Status.Sentence);
        Assert.False(dto.Status.Start.Offered);
        Assert.True(dto.Save.Offered);
    }

    [Fact]
    public void Fold_RunningButSavedComputerUnreachable_OffersNoRestart()
    {
        var roster = new[] { Session(MarkedId, machine: "WORKSTATION-B") };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { Offline("WORKSTATION-A"), WithDirector("WORKSTATION-B") },
            marked: MarkedId, roster: roster));

        Assert.Equal("running", dto.Status.State);
        Assert.False(dto.Status.Restart.Offered);
        Assert.StartsWith("It cannot be restarted on WORKSTATION-A now", dto.Status.Restart.Note);
    }

    [Fact]
    public void Fold_NoComputerAtAll_SaysThereIsNowhereToRun()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(Array.Empty<FleetManagerMachineFacts>(), agent: null, machine: null));

        Assert.Equal("no-computer", dto.Status.State);
        Assert.Equal("The Fleet Manager is not running, and this account has no computer for it to run on. "
                     + "Install DevThrottle on a computer and sign in to this account.", dto.Status.Sentence);
        Assert.Null(dto.Machine);
        Assert.Null(dto.Agent);
        Assert.False(dto.Status.Start.Offered);
        Assert.False(dto.Save.Offered);
        Assert.Empty(dto.Machines);
    }

    // ---- the default ------------------------------------------------------------------------------------

    [Fact]
    public void Fold_NothingSaved_DefaultsToTheEarliestComputerAndItsFirstAgent()
    {
        var machines = new[]
        {
            WithDirector("WORKSTATION-B", firstSeen: new DateTime(2026, 8, 1, 9, 0, 0, DateTimeKind.Utc), firstAgent: "ClaudeCode"),
            LauncherOnly("WORKSTATION-A", firstSeen: new DateTime(2026, 7, 20, 8, 15, 0, DateTimeKind.Utc)) with { FirstAgent = "Codex" },
            LauncherOnly("WORKSTATION-C"),
        };

        var dto = FleetManagerPlacementFold.Fold(Inputs(machines, agent: null, machine: null));

        Assert.True(dto.IsDefault);
        Assert.Equal("WORKSTATION-A", dto.Machine);
        Assert.Equal("Codex", dto.Agent);
        Assert.Equal("Codex", dto.AgentLabel);
        Assert.Equal("Nothing is saved yet. This is the default: Codex on WORKSTATION-A, the computer of this account's "
                     + "earliest session the Gateway still holds (it keeps 90 days; that session started 20 Jul 08:15), and "
                     + "the agent that first session ran. It is not stored until you save or start it.", dto.DefaultNote);
    }

    [Fact]
    public void Fold_NothingSavedAndNoFirstAgent_UsesTheFirstAgentTheDirectorOffers()
    {
        var machines = new[] { WithDirector("WORKSTATION-A", firstSeen: Now.AddDays(-3)) };
        var offered = new[] { new AgentChoiceDto { Type = "RawCli" }, new AgentChoiceDto { Type = "Gemini" } };

        var dto = FleetManagerPlacementFold.Fold(Inputs(machines, agent: null, machine: null, offered: offered));

        Assert.Equal("Gemini", dto.Agent);
        Assert.Contains("the first agent its Director offers", dto.DefaultNote);
    }

    [Fact]
    public void Fold_NothingSavedAndNoAgentRecord_UsesClaudeCodeAndSaysWhy()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { LauncherOnly("WORKSTATION-A", firstSeen: Now.AddDays(-3)) },
            agent: null, machine: null));

        Assert.Equal("ClaudeCode", dto.Agent);
        Assert.Contains("Claude Code, because the Gateway cannot tell which agents that computer has", dto.DefaultNote);
    }

    [Fact]
    public void Fold_NothingSavedAndNoFirstSeenRecord_HasNoDefaultAndSaysSo()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, agent: null, machine: null));

        Assert.True(dto.IsDefault);
        Assert.Null(dto.Machine);
        Assert.Null(dto.Agent);
        Assert.Equal("Nothing is saved yet, and the Gateway has no record of which computer this account set up first, "
                     + "so there is no default. Choose an agent and a computer, then save.", dto.DefaultNote);
        Assert.Equal("not-running", dto.Status.State);
        Assert.Equal("The Fleet Manager is not running, and nothing says where it runs yet. Choose an agent and a "
                     + "computer below and save, then start it.", dto.Status.Sentence);
        Assert.False(dto.Status.Start.Offered);
        Assert.True(dto.Save.Offered);
    }

    [Fact]
    public void Fold_Saved_IsNotTheDefault()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A", firstSeen: Now.AddDays(-9)), WithDirector("WORKSTATION-B") },
            agent: "Gemini", machine: "WORKSTATION-B"));

        Assert.False(dto.IsDefault);
        Assert.Null(dto.DefaultNote);
        Assert.Equal("WORKSTATION-B", dto.Machine);
        Assert.Equal("Gemini", dto.Agent);
    }

    [Fact]
    public void Fold_AgentChoices_AreEveryFleetManagerAgentInOrderWithoutACustomCommand()
    {
        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }));

        Assert.Equal(new[] { "ClaudeCode", "Codex", "Gemini", "OpenCode", "Pi", "Grok", "Copilot", "Cursor" },
            dto.Agents.Select(a => a.Value));
        Assert.Equal("Claude Code", dto.Agents[0].DisplayName);
        Assert.DoesNotContain(dto.Agents, a => a.Value == "RawCli");
        Assert.Equal("Claude Code, Codex, Gemini, OpenCode, Pi, Grok, Copilot or Cursor - any agent installed on the "
                     + "chosen computer. It uses that agent's own sign-in and default model.", dto.AgentNote);
    }

    // ---- times ------------------------------------------------------------------------------------------

    [Fact]
    public void FormatWhen_TodayYesterdayAndEarlier_AreWrittenPlainly()
    {
        var utc = TimeZoneInfo.Utc;
        Assert.Equal("07:02", FleetManagerPlacementFold.FormatWhen(new DateTime(2026, 9, 16, 7, 2, 0, DateTimeKind.Utc), utc, Now));
        Assert.Equal("yesterday 23:59", FleetManagerPlacementFold.FormatWhen(new DateTime(2026, 9, 15, 23, 59, 0, DateTimeKind.Utc), utc, Now));
        Assert.Equal("3 Sep 10:00", FleetManagerPlacementFold.FormatWhen(new DateTime(2026, 9, 3, 10, 0, 0, DateTimeKind.Utc), utc, Now));
        Assert.Equal("30 Dec 2025 10:00", FleetManagerPlacementFold.FormatWhen(new DateTime(2025, 12, 30, 10, 0, 0, DateTimeKind.Utc), utc, Now));
    }

    [Fact]
    public void FormatWhen_UsesTheAccountTimeZoneForTheDayAndTheClock()
    {
        // 02:10 UTC on the 16th is 22:10 on the 15th in New York (EDT, four hours behind), and "now" there is
        // 10:30 on the 16th - so it is yesterday there, not today.
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");

        Assert.Equal("yesterday 22:10",
            FleetManagerPlacementFold.FormatWhen(new DateTime(2026, 9, 16, 2, 10, 0, DateTimeKind.Utc), newYork, Now));
    }

    [Fact]
    public void Fold_RunningSince_IsWrittenInTheAccountTimeZone()
    {
        var newYork = TimeZoneInfo.FindSystemTimeZoneById("America/New_York");
        var roster = new[] { Session(MarkedId, created: new DateTime(2026, 9, 16, 11, 2, 0, DateTimeKind.Utc)) };

        var dto = FleetManagerPlacementFold.Fold(Inputs(new[] { WithDirector("WORKSTATION-A") }, marked: MarkedId, roster: roster, tz: newYork));

        Assert.StartsWith("Running now: Claude Code on WORKSTATION-A, since 07:02.", dto.Status.Sentence);
        Assert.Equal(new DateTime(2026, 9, 16, 11, 2, 0, DateTimeKind.Utc), dto.Status.SinceUtc);
    }
}
