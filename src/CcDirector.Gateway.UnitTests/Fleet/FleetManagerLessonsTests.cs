using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The Fleet Manager's lessons (issue #3559): the one block every Fleet Manager is given, what a plain start's first
/// prompt and a delivered marked event carry, and the store's caps.
/// </summary>
public sealed class FleetManagerLessonsTests : IDisposable
{
    private static readonly TenantId Tenant = new("acct-lessons");
    private static readonly DateTime Now = new(2026, 10, 5, 12, 0, 0, DateTimeKind.Utc);
    private const string Correction =
        "Never queue a message to every session when none is stuck.\nCheck first, and if nothing is stuck, do nothing.";

    private readonly GatewayDbTestHarness _harness = new();

    public void Dispose() => _harness.Dispose();

    private FleetPreferenceStore NewStore() => new(_harness.Open());

    [Fact]
    public void FirstPrompt_OfAPlainStart_CarriesEveryConfirmedLessonVerbatim_AndNoUnconfirmedOne()
    {
        var store = NewStore();
        store.AddLesson(Tenant, Correction, "sent carry on now to all 36 sessions", "owner", confirmed: true, Now);
        store.AddLesson(Tenant, "second lesson, the owner's", null, "owner", confirmed: true, Now.AddMinutes(1));
        store.AddLesson(Tenant, "UNCONFIRMED - written by a session", null, "fm-session", confirmed: false, Now.AddMinutes(2));

        var request = FleetManagerPlacementService.BuildStartRequest("ClaudeCode",
            lessons: FleetManagerLessons.Build(store.ConfirmedLessons(Tenant)));

        Assert.StartsWith(FleetManagerLessons.Heading, request.PrePrompt);
        Assert.Contains("<<<" + Correction + ">>>", request.PrePrompt);
        Assert.Contains("<<<second lesson, the owner's>>>", request.PrePrompt);
        Assert.Contains("what went wrong: sent carry on now to all 36 sessions", request.PrePrompt);
        Assert.Contains("kept 2026-10-05", request.PrePrompt);
        Assert.DoesNotContain("UNCONFIRMED", request.PrePrompt);
        Assert.EndsWith(FleetManagerPlacementService.FirstPrompt, request.PrePrompt);
    }

    [Fact]
    public void FirstPrompt_WithNoLessons_IsTheFirstPromptExactly_AndAReplacementIsNeverGivenThem()
    {
        Assert.Equal(FleetManagerPlacementService.FirstPrompt,
            FleetManagerPlacementService.BuildStartRequest("ClaudeCode", lessons: null).PrePrompt);
        Assert.Equal(FleetManagerPlacementService.WaitForMarkPrompt,
            FleetManagerPlacementService.BuildStartRequest("ClaudeCode", waitForMark: true, lessons: "[Fleet Manager lessons] x").PrePrompt);
    }

    [Fact]
    public void Build_RefusesAnUnconfirmedLessonOrAPreference_RatherThanInjectingIt()
    {
        var store = NewStore();
        var unconfirmed = store.AddLesson(Tenant, "not yet", null, "fm-session", confirmed: false, Now);
        var preference = store.Add(Tenant, "a preference", "owner", Now);

        Assert.Throws<ArgumentException>(() => FleetManagerLessons.Build(new[] { unconfirmed }));
        Assert.Throws<ArgumentException>(() => FleetManagerLessons.Build(new[] { preference }));
        Assert.Null(FleetManagerLessons.Build(Array.Empty<FleetPreferenceDto>()));
    }

    [Fact]
    public void MarkedEvent_IsBuiltAtDelivery_WithTheLessonsAsTheyAreThen()
    {
        var store = NewStore();
        var marked = new FleetManagerEventDto
        {
            Id = Guid.NewGuid().ToString(),
            Kind = FleetManagerEventStore.KindMarked,
            SessionId = "fm-new",
            SessionName = "Fleet Manager",
            AddressedTo = "fm-new",
            Detail = FleetManagerEventStore.MarkedDetail,
        };

        // The promotion stored the event; the owner keeps a lesson afterwards; then the event is delivered.
        store.AddLesson(Tenant, Correction, null, "owner", confirmed: true, Now);
        var text = FleetManagerEventPrompt.Build(new[] { marked }, 0, FleetManagerLessons.Build(store.ConfirmedLessons(Tenant)));

        Assert.StartsWith(FleetManagerEventPrompt.FirstLinePrefix, text);
        Assert.Contains(FleetManagerEventStore.MarkedDetail, text);
        Assert.Contains("<<<" + Correction + ">>>", text);
    }

    [Fact]
    public void LessonEvent_IsWrittenBeforeEveryOtherEvent_WithTheOwnersWordsBetweenMarkers()
    {
        var died = new FleetManagerEventDto { Id = "e-1", Kind = FleetManagerEventStore.KindDied, SessionId = "w-1", SessionName = "worker" };
        var lesson = new FleetManagerEventDto { Id = "e-2", Kind = FleetManagerEventStore.KindLesson, Words = Correction, Detail = "woke everyone" };

        var text = FleetManagerEventPrompt.Build(new[] { died, lesson });

        Assert.Contains("1 lesson kept by the owner", text.Split('\n')[0]);
        Assert.True(text.IndexOf("event 1 of 2: e-2", StringComparison.Ordinal) >= 0);
        Assert.True(text.IndexOf("e-2", StringComparison.Ordinal) < text.IndexOf("e-1", StringComparison.Ordinal));
        Assert.Contains("<<<" + Correction + ">>>", text);
        Assert.Contains("what went wrong: woke everyone", text);
    }

    [Fact]
    public void Confirm_AtTheCap_IsRefused_AndNothingIsPushedOut()
    {
        var store = NewStore();
        for (var i = 0; i < FleetPreferenceStore.MaxConfirmedLessons; i++)
            store.AddLesson(Tenant, $"lesson {i}", null, "owner", confirmed: true, Now.AddMinutes(i));
        var waiting = store.AddLesson(Tenant, "kept by the Fleet Manager", null, "fm-session", confirmed: false, Now.AddHours(1));

        var ex = Assert.Throws<ArgumentException>(() => store.Confirm(Tenant, Guid.Parse(waiting.Id), Now.AddHours(2)));
        Assert.Contains("no older lesson was pushed out", ex.Message);
        Assert.Equal(FleetPreferenceStore.MaxConfirmedLessons, store.ConfirmedLessons(Tenant).Count);
        Assert.Null(store.Find(Tenant, Guid.Parse(waiting.Id))!.ConfirmedByOwnerAtUtc);
    }

    [Fact]
    public void ExistingPreferences_ReadExactlyAsBefore_AndAreNeverLessons()
    {
        var store = NewStore();
        store.Add(Tenant, "merge docs changes on green", "owner", Now);
        store.AddLesson(Tenant, Correction, null, "owner", confirmed: true, Now);

        var pref = Assert.Single(store.List(Tenant));
        Assert.Equal("preference", pref.Kind);
        Assert.Null(pref.Mistake);
        Assert.Null(pref.ConfirmedByOwnerAtUtc);
        var json = System.Text.Json.JsonSerializer.Serialize(pref, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
        Assert.DoesNotContain("mistake", json);
        Assert.DoesNotContain("confirmedByOwnerAtUtc", json);
    }
}
