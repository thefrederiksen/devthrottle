using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Streaming;
using Xunit;

namespace CcDirector.Gateway.UnitTests;

/// <summary>
/// The decisions behind the restart-request routes - issue #2725. Every refusal is exercised, because
/// every refusal is the feature: the owner is never shown an approval for a restart that cannot work.
///
/// The facts arrive through the service's delegates, so a whole ask-and-accept can be driven without a
/// server: a registered launcher with or without a stream, a Director with or without a stream, a
/// pushed roster, and a Director that answers the eligibility question one way or another.
/// </summary>
public sealed class DirectorRestartRequestServiceTests
{
    private static readonly TenantId Tenant = TenantId.Local;
    private const string Machine = "SOREN_NORTH";
    private const string DirectorId = "d-main";
    private static readonly Guid AskingSession = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>A world the service reads. Every fact is a field a test flips.</summary>
    private sealed class World
    {
        public DateTime Now = new(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        public List<DirectorDto> Directors = new() { new DirectorDto { DirectorId = DirectorId, MachineName = Machine, DisplayName = "DevThrottle_1" } };
        public LauncherDto? Launcher;
        public LauncherStreamConnection? Connection;
        public bool LauncherRegistryWired = true;
        public bool DirectorConnected = true;
        public bool RosterPushed = true;
        public List<SessionDto> Sessions = new()
        {
            new SessionDto { SessionId = AskingSession.ToString(), DirectorId = DirectorId, Name = "Restart Director - Architect" },
            new SessionDto { SessionId = Guid.NewGuid().ToString(), DirectorId = DirectorId, Name = "another seat" },
        };
        public Func<DirectorCommand, DirectorCommandResult?> Director = cmd => cmd.Verb == DirectorRestartVerbs.Eligibility
            ? DirectorCommandResult.Success(JsonSerializer.Serialize(new DirectorRestartEligibilityDto
            {
                Eligible = true, Reason = "it is the launcher's Director",
                DrainAvailable = true, DrainReason = "this build carries the drain",
            }, Web))
            : DirectorCommandResult.Success("{\"taken\":true}");
        public List<DirectorCommand> Sent = new();
        public DirectorRestartRequestStore Store = new();

        public World()
        {
            Store.Clock = () => Now;
            Launcher = new LauncherDto { MachineName = Machine, Version = "2.1.0", LastSeenAt = Now, Pid = 1 };
            Connection = new LauncherStreamConnection("conn-1", new LauncherCapabilityDeclaration
            {
                Commands = new List<string> { LauncherCapabilities.DirectorRestart, LauncherCapabilities.DirectorRestartOnlyIfEmpty },
                RestartSignalArmed = true,
                ServingRootIsInstanceHome = false,
                ServingRootKey = "abcdef123456",
            });
        }

        public DirectorRestartRequestService Service() => new(
            Store,
            listDirectors: _ => Directors,
            launcherRegistration: (_, m) => string.Equals(m, Machine, StringComparison.OrdinalIgnoreCase) ? Launcher : null,
            launcherConnection: LauncherRegistryWired
                ? (_, m) => string.Equals(m, Machine, StringComparison.OrdinalIgnoreCase) ? Connection : null
                : null,
            directorSessions: (_, d) => new PushedSessionStore.DirectorKnowledge(
                d == DirectorId && RosterPushed ? Sessions : Array.Empty<SessionDto>(),
                d == DirectorId && RosterPushed ? Now : null,
                d == DirectorId && DirectorConnected),
            findSession: (_, sid) => Sessions.FirstOrDefault(s => s.SessionId == sid),
            sendCommand: async (d, cmd, ct) =>
            {
                Sent.Add(cmd);
                if (DirectorNeverAnswers)
                {
                    // Hold until the caller's own bound fires, as a Director holding its stream open does.
                    await Task.Delay(Timeout.Infinite, ct);
                }
                return d == DirectorId ? Director(cmd) : null;
            });

        public bool DirectorNeverAnswers;
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private static SessionCredentialIdentity Caller() => new(AskingSession, Tenant, DirectorId);
    private static CreateDirectorRestartRequest Body(string reason = "the launcher has a staged update") => new() { Reason = reason };

    private static JsonElement Json(object body) => JsonDocument.Parse(JsonSerializer.Serialize(body, Web)).RootElement.Clone();
    private static string S(JsonElement e, string name) => e.GetProperty(name).GetString() ?? "";

    // =====================================================================================
    // The ask: created, with every sentence the owner will read
    // =====================================================================================

    [Fact]
    public async Task A_session_asking_about_its_own_Director_creates_a_pending_request_with_finished_sentences()
    {
        var world = new World();
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(201, answer.Status);
        var dto = Assert.IsType<DirectorRestartRequestDto>(answer.Body);
        Assert.Equal(DirectorRestartRequestState.Pending, dto.State);
        Assert.True(dto.CanAccept);
        Assert.Equal(DirectorId, dto.DirectorId);
        Assert.Equal("DevThrottle_1", dto.DirectorName);
        Assert.Equal(AskingSession.ToString(), dto.RequestedBySessionId);
        Assert.StartsWith("Restart Director - Architect", dto.RequestedBySessionName);
        Assert.Equal(2, dto.LiveSessionCount);
        Assert.Contains("2 live sessions", dto.LiveSessionsSentence);
        Assert.Equal("Restart the Director on SOREN_NORTH?", dto.Title);
        Assert.Contains("asks: the launcher has a staged update", dto.AskedBySentence);
        Assert.Equal(world.Now + DirectorRestartRequestStore.Expiry, dto.ExpiresAtUtc);

        // The scrutiny travels with the record, in Phase 1's own words.
        Assert.NotNull(dto.Capability);
        Assert.Equal(RestartVerdict.CanRestart, dto.Capability!.Verdict);
        Assert.Equal(CapabilityState.Available, dto.Capability.GuardedRestart);

        // The Director was asked whether a launcher restart would restart IT, and nothing else was sent.
        Assert.Equal(new[] { DirectorRestartVerbs.Eligibility }, world.Sent.Select(c => c.Verb));
    }

    [Fact]
    public async Task A_request_with_no_reason_is_refused_before_anything_is_consulted()
    {
        var world = new World();
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body("  "), Caller(), CancellationToken.None);

        Assert.Equal(400, answer.Status);
        Assert.Equal("reason_required", S(Json(answer.Body), "code"));
        Assert.Empty(world.Sent);
        Assert.Empty(world.Store.List(Tenant));
    }

    // =====================================================================================
    // The scrutiny: refused BEFORE any approval exists, in Phase 1's words
    // =====================================================================================

    [Fact]
    public async Task A_machine_with_no_launcher_is_refused_before_any_approval_exists_naming_the_Phase_1_reason()
    {
        var world = new World { Launcher = null, Connection = null };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("cannot_restart", S(body, "code"));
        // Phase 1's sentence, verbatim, not a paraphrase written here.
        var expected = MachineRestartCapability.Judge(Machine, null, null, world.Now).Reason;
        Assert.Contains(expected, S(body, "error"));
        Assert.Contains("no launcher is registered", S(body, "error"));
        Assert.Equal("CannotRestart", S(body.GetProperty("capability"), "verdict"));

        Assert.Empty(world.Store.List(Tenant));
        Assert.Empty(world.Sent);
    }

    [Fact]
    public async Task A_launcher_that_declares_no_guard_is_refused_even_though_the_machine_can_be_restarted()
    {
        // A launcher holding a stream that declared nothing: Phase 1 says CanRestart (inferred from the
        // stream) and GuardedRestart Unknown. Unknown is not a yes.
        var world = new World { Connection = new LauncherStreamConnection("conn-1", null) };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("restart_not_guarded", S(body, "code"));
        Assert.Contains("unknown", S(body, "error"));
        Assert.Equal("Unknown", S(body.GetProperty("capability"), "guardedRestart"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Gateway_with_no_launcher_stream_registry_refuses_with_503_rather_than_a_verdict()
    {
        var world = new World { LauncherRegistryWired = false };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(503, answer.Status);
        Assert.Equal("capability_unobservable", S(Json(answer.Body), "code"));
        Assert.Empty(world.Store.List(Tenant));
    }

    // =====================================================================================
    // Which Director
    // =====================================================================================

    [Fact]
    public async Task No_Director_on_the_machine_is_a_404_and_nothing_is_created()
    {
        var world = new World { Directors = new List<DirectorDto>() };
        var answer = await world.Service().CreateAsync(Tenant, "NOWHERE", Body(), Caller(), CancellationToken.None);
        Assert.Equal(404, answer.Status);
        Assert.Equal("no_director_on_machine", S(Json(answer.Body), "code"));
    }

    [Fact]
    public async Task Two_Directors_on_the_machine_and_a_caller_on_neither_is_refused_rather_than_guessed()
    {
        var world = new World();
        world.Directors.Add(new DirectorDto { DirectorId = "d-slot5", MachineName = Machine });
        var stranger = new SessionCredentialIdentity(Guid.NewGuid(), Tenant, "d-elsewhere");

        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), stranger, CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_ambiguous", S(body, "code"));
        Assert.Contains("d-slot5", S(body, "error"));
        Assert.Empty(world.Sent);
    }

    [Fact]
    public async Task Two_Directors_on_the_machine_and_the_caller_on_one_of_them_targets_the_callers_own()
    {
        var world = new World();
        world.Directors.Add(new DirectorDto { DirectorId = "d-slot5", MachineName = Machine });

        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(201, answer.Status);
        Assert.Equal(DirectorId, ((DirectorRestartRequestDto)answer.Body).DirectorId);
    }

    [Fact]
    public async Task A_named_Director_not_on_that_machine_is_a_404()
    {
        var world = new World();
        var answer = await world.Service().CreateAsync(Tenant, Machine, new CreateDirectorRestartRequest { Reason = "x", DirectorId = "d-nope" }, Caller(), CancellationToken.None);
        Assert.Equal(404, answer.Status);
        Assert.Equal("director_not_on_machine", S(Json(answer.Body), "code"));
    }

    // =====================================================================================
    // The Director must be reachable and must say it is the launcher's Director
    // =====================================================================================

    [Fact]
    public async Task A_Director_holding_no_stream_is_refused_and_nothing_is_created()
    {
        var world = new World { DirectorConnected = false };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);
        Assert.Equal(409, answer.Status);
        Assert.Equal("director_not_connected", S(Json(answer.Body), "code"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_connected_Director_that_has_not_yet_reported_its_roster_is_refused_rather_than_read_as_empty()
    {
        var world = new World { RosterPushed = false };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_roster_not_reported", S(body, "code"));
        Assert.Contains("not knowing is not zero", S(body, "error"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Director_build_that_cannot_drain_is_refused_at_ask_time_so_the_owner_is_never_shown_it()
    {
        // The exact answer a build without the drain inside the Director gives (issue #2723 not carried).
        var world = new World();
        world.Director = _ => DirectorCommandResult.Success(JsonSerializer.Serialize(new DirectorRestartEligibilityDto
        {
            Eligible = true, Reason = "it is the launcher's Director",
            DrainAvailable = false, DrainReason = "this Director build carries no drain - the drain inside the Director (issue #2723) is not part of it",
        }, Web));

        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_not_restartable_by_its_launcher", S(body, "code"));
        Assert.Contains("carries no drain", S(body, "error"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Director_that_does_not_say_whether_it_can_drain_is_refused_because_silence_is_not_a_yes()
    {
        var world = new World { Director = _ => DirectorCommandResult.Success("{\"eligible\":true,\"reason\":\"yes\"}") };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);
        Assert.Equal(409, answer.Status);
        Assert.Contains("did not say that it can drain", S(Json(answer.Body), "error"));
    }

    [Fact]
    public async Task A_Director_that_never_answers_the_eligibility_question_is_refused_when_the_bound_fires()
    {
        var world = new World { DirectorNeverAnswers = true };
        var service = world.Service();
        service.DirectorAnswerTimeout = TimeSpan.FromMilliseconds(200);

        var answer = await service.CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_not_restartable_by_its_launcher", S(body, "code"));
        Assert.Contains("did not answer within", S(body, "error"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Director_that_never_answers_the_accept_abandons_the_request_when_the_bound_fires()
    {
        var world = new World();
        var id = await Ask(world);
        world.DirectorNeverAnswers = true;
        var service = world.Service();
        service.DirectorAnswerTimeout = TimeSpan.FromMilliseconds(200);

        var answer = await service.AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(502, answer.Status);
        Assert.Equal("director_did_not_answer", S(Json(answer.Body), "code"));
        var record = world.Store.Get(Tenant, id)!;
        Assert.Equal(DirectorRestartRequestState.Abandoned, record.State);
        Assert.Contains("did not answer within", record.StateReason);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("null")]
    public async Task A_Director_that_says_it_is_not_the_launchers_Director_or_cannot_tell_is_refused(string eligible)
    {
        var world = new World();
        world.Director = cmd => DirectorCommandResult.Success(
            "{\"eligible\":" + eligible + ",\"reason\":\"this Director runs as the named instance 'slot5'\"}");

        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_not_restartable_by_its_launcher", S(body, "code"));
        Assert.Contains("named instance 'slot5'", S(body, "error"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Director_that_does_not_know_the_eligibility_verb_is_refused_because_no_answer_is_not_a_yes()
    {
        // What a Director built before this verb answers: the session executor's BadRequest.
        var world = new World { Director = cmd => DirectorCommandResult.Fail(DirectorCommandStatus.BadRequest, $"unknown verb '{cmd.Verb}'") };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("director_not_restartable_by_its_launcher", S(body, "code"));
        Assert.Contains("Update that Director", S(body, "error"));
        Assert.Empty(world.Store.List(Tenant));
    }

    [Fact]
    public async Task A_Director_whose_eligibility_answer_cannot_be_read_is_refused()
    {
        var world = new World { Director = _ => DirectorCommandResult.Success("this is not json") };
        var answer = await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None);
        Assert.Equal(409, answer.Status);
        Assert.Contains("could not read", S(Json(answer.Body), "error"));
    }

    // =====================================================================================
    // One pending request per machine
    // =====================================================================================

    [Fact]
    public async Task A_second_request_while_one_is_pending_is_refused_naming_the_first()
    {
        var world = new World();
        var service = world.Service();
        var first = (DirectorRestartRequestDto)(await service.CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None)).Body;

        var second = await service.CreateAsync(Tenant, Machine, Body("again"), Caller(), CancellationToken.None);

        Assert.Equal(409, second.Status);
        var body = Json(second.Body);
        Assert.Equal("request_already_pending", S(body, "code"));
        Assert.Contains(first.Id, S(body, "error"));
        Assert.Single(world.Store.List(Tenant));
    }

    // =====================================================================================
    // The accept
    // =====================================================================================

    private static async Task<string> Ask(World world) =>
        ((DirectorRestartRequestDto)(await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None)).Body).Id;

    [Fact]
    public async Task Accepting_re_checks_the_machine_and_hands_the_cycle_to_the_Director()
    {
        var world = new World();
        var id = await Ask(world);
        world.Sent.Clear();

        var answer = await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(200, answer.Status);
        var dto = Assert.IsType<DirectorRestartRequestDto>(answer.Body);
        Assert.Equal(DirectorRestartRequestState.Accepted, dto.State);
        Assert.False(dto.CanAccept);
        Assert.Contains("taken the cycle", dto.Progress);

        var order = Assert.Single(world.Sent);
        Assert.Equal(DirectorRestartVerbs.Cycle, order.Verb);
        var payload = JsonSerializer.Deserialize<DirectorRestartCycleOrder>(order.PayloadJson, Web)!;
        Assert.Equal(id, payload.RequestId);
        Assert.Equal(Machine, payload.Machine);
        Assert.Equal("the launcher has a staged update", payload.Reason);
        Assert.Equal(AskingSession.ToString(), payload.RequestedBySessionId);
    }

    [Fact]
    public async Task Accepting_an_expired_request_is_refused_and_nothing_is_sent()
    {
        var world = new World();
        var id = await Ask(world);
        world.Sent.Clear();
        world.Now += DirectorRestartRequestStore.Expiry;

        var answer = await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(409, answer.Status);
        Assert.Equal("request_expired", S(Json(answer.Body), "code"));
        Assert.Empty(world.Sent);
        Assert.Equal(DirectorRestartRequestState.Expired, world.Store.Get(Tenant, id)!.State);
    }

    [Fact]
    public async Task Accepting_twice_sends_the_cycle_once()
    {
        var world = new World();
        var id = await Ask(world);
        world.Sent.Clear();
        var service = world.Service();

        Assert.Equal(200, (await service.AcceptAsync(Tenant, Machine, id, CancellationToken.None)).Status);
        var again = await service.AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(409, again.Status);
        Assert.Equal("request_not_pending", S(Json(again.Body), "code"));
        Assert.Single(world.Sent);
    }

    [Fact]
    public async Task Accepting_under_the_wrong_machine_is_a_404_and_the_request_is_untouched()
    {
        var world = new World();
        var id = await Ask(world);
        world.Sent.Clear();

        var answer = await world.Service().AcceptAsync(Tenant, "SOME-OTHER-MACHINE", id, CancellationToken.None);

        Assert.Equal(404, answer.Status);
        Assert.Empty(world.Sent);
        var record = world.Store.Get(Tenant, id)!;
        Assert.Equal(DirectorRestartRequestState.Pending, record.State);
        Assert.True(record.CanAccept);
    }

    [Fact]
    public async Task Declining_or_reporting_under_the_wrong_machine_is_a_404_and_the_request_is_untouched()
    {
        var world = new World();
        var id = await Ask(world);
        var service = world.Service();

        Assert.Equal(404, service.Decline(Tenant, "SOME-OTHER-MACHINE", id, "no").Status);
        Assert.Equal(DirectorRestartRequestState.Pending, world.Store.Get(Tenant, id)!.State);

        await service.AcceptAsync(Tenant, Machine, id, CancellationToken.None);
        var report = service.Report(Tenant, "SOME-OTHER-MACHINE", id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Abandoned, Progress = "x" });
        Assert.Equal(404, report.Status);
        Assert.Equal(DirectorRestartRequestState.Accepted, world.Store.Get(Tenant, id)!.State);
    }

    [Fact]
    public async Task The_accept_sentence_is_written_on_the_Gateway_and_names_the_live_count()
    {
        var world = new World();
        var dto = (DirectorRestartRequestDto)(await world.Service().CreateAsync(Tenant, Machine, Body(), Caller(), CancellationToken.None)).Body;
        Assert.Contains("2 live sessions", dto.AcceptSentence);
        Assert.Contains("forces nothing", dto.AcceptSentence);
    }

    [Fact]
    public async Task Accepting_an_unknown_id_is_a_404()
    {
        var world = new World();
        var answer = await world.Service().AcceptAsync(Tenant, Machine, "nope", CancellationToken.None);
        Assert.Equal(404, answer.Status);
    }

    [Fact]
    public async Task A_machine_that_changed_between_the_ask_and_the_accept_is_refused_and_the_request_is_abandoned()
    {
        var world = new World();
        var id = await Ask(world);
        world.Sent.Clear();

        // The launcher went away between the ask and the accept.
        world.Connection = null;
        world.Launcher = new LauncherDto { MachineName = Machine, Version = "2.1.0", Pid = 1, LastSeenAt = world.Now - TimeSpan.FromHours(1) };

        var answer = await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(409, answer.Status);
        var body = Json(answer.Body);
        Assert.Equal("cannot_restart_now", S(body, "code"));
        Assert.Contains("checked again", S(body, "error"));
        Assert.Empty(world.Sent);
        Assert.Equal(DirectorRestartRequestState.Abandoned, world.Store.Get(Tenant, id)!.State);
    }

    [Fact]
    public async Task A_Director_that_cannot_be_reached_on_accept_abandons_the_request_with_502()
    {
        var world = new World();
        var id = await Ask(world);
        world.Director = _ => null; // no stream any more

        var answer = await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(502, answer.Status);
        Assert.Equal("director_not_connected", S(Json(answer.Body), "code"));
        var record = world.Store.Get(Tenant, id)!;
        Assert.Equal(DirectorRestartRequestState.Abandoned, record.State);
        Assert.Contains("Nothing was drained", record.StateReason);
    }

    [Fact]
    public async Task A_Director_that_refuses_the_cycle_abandons_the_request_with_its_own_words()
    {
        var world = new World();
        var id = await Ask(world);
        world.Director = cmd => cmd.Verb == DirectorRestartVerbs.Cycle
            ? DirectorCommandResult.Fail(DirectorCommandStatus.Conflict, "a restart cycle is already running on this Director")
            : DirectorCommandResult.Success("{\"eligible\":true}");

        var answer = await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        Assert.Equal(502, answer.Status);
        Assert.Equal("director_refused", S(Json(answer.Body), "code"));
        Assert.Contains("already running", world.Store.Get(Tenant, id)!.StateReason);
    }

    // =====================================================================================
    // Decline and report
    // =====================================================================================

    [Fact]
    public async Task Declining_closes_the_request_with_the_owners_reason()
    {
        var world = new World();
        var id = await Ask(world);

        var answer = world.Service().Decline(Tenant, Machine, id, "not during the demo");

        Assert.Equal(200, answer.Status);
        var dto = (DirectorRestartRequestDto)answer.Body;
        Assert.Equal(DirectorRestartRequestState.Declined, dto.State);
        Assert.Contains("not during the demo", dto.StateReason);
    }

    [Fact]
    public async Task A_Director_report_of_an_owner_state_is_refused_with_400()
    {
        var world = new World();
        var id = await Ask(world);
        await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        var answer = world.Service().Report(Tenant, Machine, id, new DirectorRestartProgressReport { State = DirectorRestartRequestState.Declined, Progress = "x" });

        Assert.Equal(400, answer.Status);
        Assert.Equal("state_not_reportable", S(Json(answer.Body), "code"));
    }

    [Fact]
    public async Task A_Director_report_of_abandonment_closes_the_request_with_the_Directors_words_and_the_workspace()
    {
        var world = new World();
        var id = await Ask(world);
        await world.Service().AcceptAsync(Tenant, Machine, id, CancellationToken.None);

        var answer = world.Service().Report(Tenant, Machine, id, new DirectorRestartProgressReport
        {
            State = DirectorRestartRequestState.Abandoned,
            Progress = "the drain stopped: seat e1d7291b never answered",
            WorkspaceId = "soren-north-restart-2026-09-07",
        });

        Assert.Equal(200, answer.Status);
        var dto = (DirectorRestartRequestDto)answer.Body;
        Assert.Equal(DirectorRestartRequestState.Abandoned, dto.State);
        Assert.Contains("e1d7291b", dto.StateReason);
        Assert.Equal("soren-north-restart-2026-09-07", dto.WorkspaceId);
    }
}
