using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Factory;
using CcDirector.Gateway.Factory.Registry;
using CcDirector.Gateway.Tests.Data;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CcDirector.Gateway.Tests.Factory.Registry;

/// <summary>
/// Issue #3650 - "we cannot have factory agents without a factory". The link between a schedule and its factory seat
/// lives on the schedule and is checked against the registry on every write (points 1 and 2), registration cannot
/// drop a seat that is still running (point 3), and a seat's schedule list is derived from the links (point 4). Each
/// refusal is proven to refuse AND to store nothing, beside the happy path that is accepted.
/// </summary>
public sealed class FactoryScheduleLinkTests : IAsyncLifetime
{
    private static readonly TenantId T = TenantId.Local;
    private static readonly DateTime Now = new(2026, 10, 8, 4, 0, 0, DateTimeKind.Utc);
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    private readonly GatewayDbTestHarness _h = new();
    private GatewayDatabase _db = null!;
    private CronJobStore _schedules = null!;
    private FactoryRegistryStore _registry = null!;
    private WebApplication _app = null!;
    private HttpClient _http = null!;

    public async Task InitializeAsync()
    {
        _db = _h.Open();
        _schedules = new CronJobStore(_db, _h.LegacyPath(Guid.NewGuid().ToString("N") + ".json"));
        _registry = new FactoryRegistryStore(_db);

        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Urls.Add("http://127.0.0.1:0");
        // No credential on these requests, so the factory-naming gate treats them as the account's own token and
        // lets the factory through: what is under test here is the registry check, not who may name a factory.
        CronJobEndpoints.Map(_app, _schedules, sessionFactoryOf: _ => CcDirector.Gateway.History.SessionFactoryLookup.NotKnown,
            findFactory: (_, id) => _registry.Find(T, id));
        await _app.StartAsync();
        _http = new HttpClient { BaseAddress = new Uri(_app.Urls.First() + "/") };
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _app.DisposeAsync();
        _h.Dispose();
    }

    private static RegisterFactoryRequest Devthrottle(params string[] seats) => new()
    {
        Factory = "devthrottle",
        Title = "DevThrottle",
        Folder = @"D:\ReposFred\devthrottle-factory\factory",
        Computer = "SOREN_NORTH",
        Seats = seats.Select(s => new FactorySeatManifest { Id = s, Name = s, Role = s, BriefFile = $"agents/{s}.yaml" }).ToList(),
    };

    private static CronJobDto Schedule(string? factory, string? seat, string name = "Mail Desk - morning") => new()
    {
        Name = name,
        ScheduleKind = "recurring",
        CronExpression = "0 7 * * *",
        TimeZoneId = "UTC",
        Factory = factory,
        Seat = seat,
        Target = new CronJobTarget { Machine = "SOREN_NORTH" },
        Action = new CronJobAction { RepoPath = @"D:\ReposFred\devthrottle-factory\factory", Seed = "/mail-desk" },
    };

    private async Task<(HttpStatusCode Status, string Error, CronJobDto? Job)> Post(CronJobDto job)
    {
        var response = await _http.PostAsJsonAsync("cron/jobs", job);
        return await Read(response);
    }

    private async Task<(HttpStatusCode Status, string Error, CronJobDto? Job)> Put(string id, CronJobDto job)
    {
        var response = await _http.PutAsJsonAsync($"cron/jobs/{id}", job);
        return await Read(response);
    }

    private static async Task<(HttpStatusCode, string, CronJobDto?)> Read(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            return (response.StatusCode, JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!, null);
        return (response.StatusCode, "", JsonSerializer.Deserialize<CronJobDto>(body, Json));
    }

    // ---------- point 1: the link is on the schedule, checked against the registry ----------

    [Fact]
    public async Task Create_NamingAFactoryThatIsNotRegistered_IsRefusedWithTheFix_AndNothingIsStored()
    {
        var (status, error, _) = await Post(Schedule("money-saver", "daily-report"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("No factory 'money-saver' is registered", error);
        Assert.Contains("cc-devthrottle factory register --manifest", error);
        Assert.Empty(_schedules.ListAll());
    }

    [Fact]
    public async Task Create_NamingASeatTheFactoryDoesNotHave_IsRefusedNamingItsSeats_AndNothingIsStored()
    {
        _registry.Register(T, Devthrottle("ceo", "onboarding"), "the owner (test)", Now);

        var (status, error, _) = await Post(Schedule("devthrottle", "mail-desk"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("'mail-desk' is not a seat of DevThrottle (devthrottle); its seats are: ceo, onboarding", error);
        Assert.Contains("Add the seat to the factory's manifest and register it", error);
        Assert.Empty(_schedules.ListAll());
    }

    [Fact]
    public async Task Create_NamingAFactoryButNoSeat_IsRefused_AndNothingIsStored()
    {
        _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now);

        var (status, error, _) = await Post(Schedule("devthrottle", seat: null));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("names the factory 'devthrottle' but no seat", error);
        Assert.Empty(_schedules.ListAll());
    }

    [Fact]
    public async Task Create_NamingASeatButNoFactory_IsRefused_AndNothingIsStored()
    {
        var (status, error, _) = await Post(Schedule(factory: null, seat: "mail-desk"));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("names the seat 'mail-desk' but no factory", error);
        Assert.Empty(_schedules.ListAll());
    }

    // ---------- point 2: the seat exists first, and then the schedule is accepted ----------

    [Fact]
    public async Task Create_ForARegisteredSeat_IsAccepted_AndTheSeatListsIt()
    {
        _registry.Register(T, Devthrottle("ceo", "mail-desk"), "the owner (test)", Now);

        var (status, _, job) = await Post(Schedule("DevThrottle", "Mail-Desk"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Equal("devthrottle", job!.Factory);
        Assert.Equal("mail-desk", job.Seat);
        var seat = _registry.Find(T, "devthrottle")!.Seats.Single(s => s.Id == "mail-desk");
        Assert.Equal(new[] { job.Id }, seat.Schedules);
    }

    [Fact]
    public async Task Create_WithNoFactoryAndNoSeat_IsStillAPlainJob()
    {
        var (status, _, job) = await Post(Schedule(factory: null, seat: null, name: "Daily email assistant"));

        Assert.Equal(HttpStatusCode.Created, status);
        Assert.Null(job!.Factory);
        Assert.Null(job.Seat);
    }

    [Fact]
    public async Task Update_ThatSaysNothingAboutTheSeat_KeepsIt_AndMovingToAnUnknownSeatIsRefused()
    {
        _registry.Register(T, Devthrottle("ceo", "mail-desk"), "the owner (test)", Now);
        var (_, _, job) = await Post(Schedule("devthrottle", "mail-desk"));

        var (kept, _, edited) = await Put(job!.Id, Schedule(factory: null, seat: null, name: "Mail Desk - renamed"));
        Assert.Equal(HttpStatusCode.OK, kept);
        Assert.Equal(("devthrottle", "mail-desk"), (edited!.Factory, edited.Seat));

        var (refused, error, _) = await Put(job.Id, Schedule("devthrottle", "outreach"));
        Assert.Equal(HttpStatusCode.BadRequest, refused);
        Assert.Contains("'outreach' is not a seat of DevThrottle", error);
        Assert.Equal("mail-desk", _schedules.Get(job.Id)!.Seat);
    }

    [Fact]
    public async Task Update_ThatSwitchesOffASchedule_WhoseSeatIsGone_Lands_AndOneThatKeepsItOnIsRefused()
    {
        // Review finding 1: the Cockpit's toggle and `schedule disable` re-send the stored definition, so a link that
        // stopped validating must not stop the owner switching the schedule off.
        _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now);
        ScheduleLinkSeed.Link(_db, T, "devthrottle", "fired", "cj_gone");
        var off = Schedule(factory: null, seat: null);
        off.Enabled = false;

        var (switchedOff, _, job) = await Put("cj_gone", off);
        Assert.Equal(HttpStatusCode.OK, switchedOff);
        Assert.False(job!.Enabled);
        Assert.Equal(("devthrottle", "fired"), (job.Factory, job.Seat));

        var (keptOn, error, _) = await Put("cj_gone", Schedule(factory: null, seat: null));
        Assert.Equal(HttpStatusCode.BadRequest, keptOn);
        Assert.Contains("'fired' is not a seat of DevThrottle", error);
        Assert.False(_schedules.Get("cj_gone")!.Enabled);
    }

    [Fact]
    public async Task Update_ThatMovesAScheduleToAnotherFactory_MustNameTheSeat()
    {
        // Review finding 4: a seat id belongs to its factory; carrying "ceo" into another factory is a seat nobody named.
        _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now);
        var other = Devthrottle("ceo");
        other.Factory = "money-saver";
        other.Title = "Money Saver";
        _registry.Register(T, other, "the owner (test)", Now);
        var (_, _, job) = await Post(Schedule("devthrottle", "ceo"));

        var (status, error, _) = await Put(job!.Id, Schedule("money-saver", seat: null));

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("names the factory 'money-saver' but no seat", error);
        Assert.Equal("devthrottle", _schedules.Get(job.Id)!.Factory);
    }

    // ---------- point 3: registration cannot drop a running seat ----------

    [Fact]
    public async Task Register_WhileArchived_CannotDropASeatWhoseSchedulesTheArchiveSwitchedOff()
    {
        // Review finding 2: Restore switches exactly the archived schedules back on, so their seat must still exist.
        _registry.Register(T, Devthrottle("ceo", "mail-desk"), "the owner (test)", Now);
        var (_, _, job) = await Post(Schedule("devthrottle", "mail-desk"));
        _schedules.SetEnabled(T, job!.Id, enabled: false);
        _registry.Archive(T, "devthrottle", "owner (test)", new[] { job.Id }, Now);

        var ex = Assert.Throws<FactoryViewValidationException>(() =>
            _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now.AddMinutes(1)));

        Assert.Contains($"seat 'mail-desk' still runs {job.Id}", ex.Message);
        Assert.Contains("archive switched off count as running", ex.Message);
    }

    [Fact]
    public async Task Register_ThatLeavesOutASeatWithEnabledSchedules_IsRefusedNamingThem_AndTheSeatStays()
    {
        _registry.Register(T, Devthrottle("ceo", "mail-desk"), "the owner (test)", Now);
        var (_, _, job) = await Post(Schedule("devthrottle", "mail-desk"));

        var ex = Assert.Throws<FactoryViewValidationException>(() =>
            _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now.AddMinutes(1)));

        Assert.Contains($"seat 'mail-desk' still runs {job!.Id} (Mail Desk - morning)", ex.Message);
        Assert.Contains("cc-devthrottle schedule disable", ex.Message);
        Assert.Contains(_registry.Find(T, "devthrottle")!.Seats, s => s.Id == "mail-desk");
    }

    [Fact]
    public async Task Register_ThatLeavesOutASeatWhoseSchedulesAreSwitchedOff_IsAccepted()
    {
        _registry.Register(T, Devthrottle("ceo", "mail-desk"), "the owner (test)", Now);
        var (_, _, job) = await Post(Schedule("devthrottle", "mail-desk"));
        _schedules.SetEnabled(T, job!.Id, enabled: false);

        var registered = _registry.Register(T, Devthrottle("ceo"), "the owner (test)", Now.AddMinutes(1));

        Assert.Equal(new[] { "ceo" }, registered.Seats.Select(s => s.Id));
    }

    // ---------- point 4: the seat's list is derived, and a manifest cannot claim a schedule ----------

    [Fact]
    public void Register_NamingAScheduleThatDoesNotPointAtTheSeat_IsRefusedWithTheLinkCommand()
    {
        ScheduleLinkSeed.Link(_db, T, factory: null, seat: null, "cj_plain");
        var manifest = Devthrottle("ceo");
        manifest.Seats[0].Schedules.Add("cj_plain");

        var ex = Assert.Throws<FactoryViewValidationException>(() => _registry.Register(T, manifest, "s", Now));

        Assert.Contains("cc-devthrottle schedule link cj_plain --factory devthrottle --seat ceo", ex.Message);
        Assert.Null(_registry.Find(T, "devthrottle"));
    }

    [Fact]
    public void Register_NamingAScheduleOfAnotherSeat_IsRefused()
    {
        ScheduleLinkSeed.Link(_db, T, "devthrottle", "onboarding", "cj_onb");
        var manifest = Devthrottle("ceo", "onboarding");
        manifest.Seats[0].Schedules.Add("cj_onb");

        var ex = Assert.Throws<FactoryViewValidationException>(() => _registry.Register(T, manifest, "s", Now));

        Assert.Contains("which runs the seat 'onboarding'", ex.Message);
    }

    [Fact]
    public void TheSeatsSchedules_AreTheOnesThatPointAtIt_WhateverTheManifestLeftOut()
    {
        ScheduleLinkSeed.Link(_db, T, "devthrottle", "ceo", "cj_b");
        ScheduleLinkSeed.Link(_db, T, "devthrottle", "ceo", "cj_a");
        ScheduleLinkSeed.Link(_db, T, "devthrottle", "mail-desk", "cj_m");

        var registered = _registry.Register(T, Devthrottle("ceo", "mail-desk"), "s", Now);

        Assert.Equal(new[] { "cj_a", "cj_b" }, registered.Seats.Single(s => s.Id == "ceo").Schedules);
        Assert.Equal(new[] { "cj_m" }, registered.Seats.Single(s => s.Id == "mail-desk").Schedules);
        Assert.Equal(registered.Seats.Select(s => s.Schedules), _registry.List(T).Single().Seats.Select(s => s.Schedules));
    }

    // ---------- the rule on its own ----------

    [Fact]
    public void Check_RefusesASeatIdThatCannotBeOne()
    {
        var refusal = FactoryScheduleLink.Check("devthrottle", "Mail Desk!", _ => null, out var seat);
        Assert.StartsWith("The seat 'Mail Desk!' is refused", refusal);
        Assert.Null(seat);
    }
}
