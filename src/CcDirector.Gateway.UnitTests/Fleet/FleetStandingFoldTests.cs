using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Fleet;
using CcDirector.Gateway.Pairing;
using CcDirector.Gateway.Tests.Data;
using CcDirector.Gateway.Util;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace CcDirector.Gateway.Tests.Fleet;

/// <summary>
/// The owner's lessons and preferences as the Fleet Manager page draws them (issue #3559, part 4). Pure fold tests,
/// with sentences asserted whole because the page renders them verbatim, and one test through the real page route
/// over the real store.
/// </summary>
public sealed class FleetStandingFoldTests
{
    private static readonly DateTime Kept = new(2026, 10, 5, 22, 30, 0, DateTimeKind.Utc);
    private const string FleetManager = "60000000-0000-4000-8000-000000000001";

    private static FleetPreferenceDto Lesson(string id, string text, string by, DateTime? confirmed, string? mistake = null,
        DateTime? kept = null) => new()
    {
        Id = id, Kind = "lesson", Text = text, CreatedBy = by, CreatedAtUtc = kept ?? Kept,
        ConfirmedByOwnerAtUtc = confirmed, Mistake = mistake,
    };

    private static FleetPreferenceDto Preference(string id, string text) => new()
    {
        Id = id, Kind = "preference", Text = text, CreatedBy = FleetManager, CreatedAtUtc = Kept.AddDays(-3),
    };

    [Fact]
    public void Fold_LessonsFirstThenPreferences_EachRowWithItsDateWhoKeptItAndWhetherItIsConfirmed()
    {
        var dto = FleetStandingFold.Fold(
            new[]
            {
                Lesson("l-owner", "Never message every session when none is stuck.", "owner", Kept, "messaged all 36 sessions"),
                Lesson("l-fm", "Ask before a release.", FleetManager, null),
            },
            new[] { Preference("p-1", "Merge docs on green.") },
            TimeZoneInfo.Utc, fleetManagerRunning: true);

        Assert.Equal("Lessons and preferences (3)", dto.ShowLabel);
        Assert.Equal("1 lesson waits for you to confirm.", dto.WaitingNote);
        Assert.Equal("Lessons", dto.Lessons.Title);
        Assert.Equal("Your corrections. Only confirmed lessons are given to the Fleet Manager - 1 of at most 20.", dto.Lessons.Note);

        // The one waiting for a press comes first.
        var waiting = dto.Lessons.Rows[0];
        Assert.Equal(("l-fm", "Ask before a release.", FleetStandingRowDto.ToneWaiting), (waiting.Id, waiting.Text, waiting.Tone));
        Assert.Equal("Kept 5 October 2026 by the Fleet Manager", waiting.KeptLine);
        Assert.Equal("Kept by the Fleet Manager - confirm? Until you do, it is not given to a later Fleet Manager.", waiting.StatusLine);
        Assert.True(waiting.Confirm.Offered);
        Assert.Equal("Confirm", waiting.Confirm.Label);

        var confirmed = dto.Lessons.Rows[1];
        Assert.Equal("Never message every session when none is stuck.", confirmed.Text);
        Assert.Equal("What went wrong: messaged all 36 sessions", confirmed.MistakeLine);
        Assert.Equal("Kept 5 October 2026 by you", confirmed.KeptLine);
        Assert.Equal("Confirmed 5 October 2026 - every Fleet Manager obeys it", confirmed.StatusLine);
        Assert.False(confirmed.Confirm.Offered);
        Assert.True(confirmed.Edit.Offered && confirmed.Remove.Offered);
        Assert.Equal("The Fleet Manager is told the new words, and every Fleet Manager after it obeys them.", confirmed.Edit.Note);
        Assert.Null(waiting.Edit.Note);
        Assert.Equal("Remove this lesson?", confirmed.Remove.ConfirmTitle);
        // The row carries its own limit and the words a failed removal is reported with: the page decides neither.
        Assert.Equal((500, "remove this lesson"), (confirmed.MaxLength, confirmed.RemoveAction));

        var pref = Assert.Single(dto.Preferences.Rows);
        Assert.Equal(("Merge docs on green.", "Kept 2 October 2026 by the Fleet Manager", (string?)null, FleetStandingRowDto.TonePreference),
            (pref.Text, pref.KeptLine, pref.StatusLine, pref.Tone));
        Assert.False(pref.Confirm.Offered);
        Assert.True(pref.Edit.Offered && pref.Remove.Offered);
        Assert.Equal((2000, "remove this preference"), (pref.MaxLength, pref.RemoveAction));
    }

    [Theory]
    [InlineData(true, "Kept. The Fleet Manager is told at its next free moment, and every Fleet Manager after it obeys it.")]
    [InlineData(false, "Kept. No Fleet Manager is running now; the next one is given it when it starts.")]
    public void Fold_TheSentenceAfterKeepingALesson_ClaimsOnlyWhatIsTrue(bool running, string sentence)
    {
        // Review of part 4: with no Fleet Manager running, "it is told" would be untrue.
        var dto = FleetStandingFold.Fold(Array.Empty<FleetPreferenceDto>(), Array.Empty<FleetPreferenceDto>(), TimeZoneInfo.Utc, running);

        Assert.Equal(sentence, dto.KeptSentence);
    }

    [Fact]
    public void Fold_TheDateIsWrittenInTheAccountsTimeZone()
    {
        // 22:30 UTC on 5 October is already 6 October in Tokyo.
        var tokyo = TimeZoneInfo.CreateCustomTimeZone("test+9", TimeSpan.FromHours(9), "test+9", "test+9");

        var row = FleetStandingFold.Fold(new[] { Lesson("l", "x", "owner", Kept) }, Array.Empty<FleetPreferenceDto>(), tokyo, fleetManagerRunning: true)
            .Lessons.Rows[0];

        Assert.Equal("Kept 6 October 2026 by you", row.KeptLine);
    }

    [Fact]
    public void Fold_NothingKept_SaysSoInEachPart_AndOffersTheMistakeAction()
    {
        var dto = FleetStandingFold.Fold(Array.Empty<FleetPreferenceDto>(), Array.Empty<FleetPreferenceDto>(), TimeZoneInfo.Utc,
            fleetManagerRunning: true);

        Assert.True(dto.Mistake.Offered);
        Assert.Equal("That was a mistake", dto.Mistake.Label);
        Assert.Equal(500, dto.MaxLessonLength);
        Assert.Null(dto.WaitingNote);
        Assert.Equal("Lessons and preferences (0)", dto.ShowLabel);
        Assert.StartsWith("No lessons yet.", dto.Lessons.EmptyText);
        Assert.StartsWith("No standing preferences.", dto.Preferences.EmptyText);
    }

    [Fact]
    public void Fold_AtTheMostConfirmedLessons_OffersNeitherAnotherLessonNorAConfirm_AndSaysHowToMakeRoom()
    {
        // The page never offers a button the Gateway would refuse (FleetPreferenceStore caps confirmed lessons at 20).
        var lessons = Enumerable.Range(0, FleetPreferenceStore.MaxConfirmedLessons)
            .Select(i => Lesson("c-" + i, "lesson " + i, "owner", Kept))
            .Append(Lesson("w", "waiting", FleetManager, null))
            .ToList();

        var dto = FleetStandingFold.Fold(lessons, Array.Empty<FleetPreferenceDto>(), TimeZoneInfo.Utc, fleetManagerRunning: true);

        // Review of part 4: only a removal makes room - an edit keeps a lesson's confirmation.
        const string Full = "20 lessons are confirmed, the most an account may hold. Remove one below to make room.";
        Assert.False(dto.Mistake.Offered);
        Assert.Equal(Full, dto.Mistake.Note);
        var waiting = dto.Lessons.Rows[0];
        Assert.Equal("w", waiting.Id);
        Assert.False(waiting.Confirm.Offered);
        Assert.Equal(Full, waiting.Confirm.Note);
    }

    [Fact]
    public void TheStandingRoute_CarriesThisAccountsLessonsAndPreferences_FromTheRealStore_InOneRead()
    {
        using var harness = new GatewayDbTestHarness();
        var tenant = new TenantId("acct-standing");
        var other = new TenantId("acct-other");
        var store = new FleetPreferenceStore(harness.Open());
        store.Add(tenant, "Merge docs on green.", FleetManager, Kept);
        store.AddLesson(tenant, "Never message every session when none is stuck.", null, "owner", confirmed: true, Kept);
        store.AddLesson(tenant, "Ask before a release.", null, FleetManager, confirmed: false, Kept);
        store.AddLesson(other, "Not this account's", null, "owner", confirmed: true, Kept);
        var reads = 0;
        var sources = new FleetStandingSources(
            Rows: t => { reads++; return store.ListBoth(t); },
            TimeZone: _ => TimeZoneInfo.Utc,
            FleetManagerRunning: _ => false);

        var result = FleetManagerPageEndpoints.ReadStanding(new DefaultHttpContext(), _ => tenant, sources);

        var standing = Assert.IsType<Microsoft.AspNetCore.Http.HttpResults.JsonHttpResult<FleetStandingDto>>(result).Value!;
        Assert.Equal(1, reads);
        Assert.Equal(new[] { "Ask before a release.", "Never message every session when none is stuck." },
            standing.Lessons.Rows.Select(r => r.Text).ToArray());
        Assert.Equal("Merge docs on green.", Assert.Single(standing.Preferences.Rows).Text);
        Assert.Equal("1 lesson waits for you to confirm.", standing.WaitingNote);
        Assert.Equal("Kept. No Fleet Manager is running now; the next one is given it when it starts.", standing.KeptSentence);
    }

    [Fact]
    public void TheStandingRoute_RefusesEverySessionKey_AndReadsNothing()
    {
        var reads = 0;
        var sources = new FleetStandingSources(
            Rows: _ => { reads++; return (Array.Empty<FleetPreferenceDto>(), Array.Empty<FleetPreferenceDto>()); },
            TimeZone: _ => TimeZoneInfo.Utc,
            FleetManagerRunning: _ => true);
        var ctx = new DefaultHttpContext();
        ctx.Items[AuthMiddleware.AuthenticatedSessionItemKey] =
            new SessionCredentialIdentity(Guid.Parse(FleetManager), new TenantId("acct-standing"), "dir-a");

        var result = FleetManagerPageEndpoints.ReadStanding(ctx, _ => new TenantId("acct-standing"), sources);

        Assert.Equal(StatusCodes.Status403Forbidden,
            Assert.IsAssignableFrom<Microsoft.AspNetCore.Http.IStatusCodeHttpResult>(result).StatusCode);
        Assert.Equal(0, reads);
    }

    [Fact]
    public void ThePageAnswer_NoLongerCarriesTheList()
    {
        // Review of part 4: the page answer is polled every few seconds wherever the Cockpit is open; the list is not.
        Assert.Null(typeof(FleetManagerPageDto).GetProperty("Standing"));
    }
}
