using CcDirector.Gateway.Api;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// The schedule editor's factory and seat picker (the owner, 2026-10-10). The choices are exactly what a schedule write
/// accepts (FactoryScheduleLink): registered factories and their registered seats, with an archived factory left out
/// because a write that switches one of its schedules on is refused.
/// </summary>
public sealed class CronSeatChoicesTests
{
    private static RegisteredFactoryDto Factory(string id, string title, DateTime? archived = null, params (string Id, string Name)[] seats) => new()
    {
        Factory = id,
        Title = title,
        ArchivedAtUtc = archived,
        Seats = seats.Select(s => new RegisteredFactorySeatDto { Id = s.Id, Name = s.Name }).ToList(),
    };

    [Fact]
    public void Fold_ListsLiveFactoriesByTitle_WithTheirSeatsInManifestOrder()
    {
        var choices = CronSeatChoicesEndpoint.Fold(new[]
        {
            Factory("warmforward", "WarmForward Factory", null, ("ceo", "Nora Hale"), ("value-hunter", "value-hunter")),
            Factory("clickfunnels", "ClickFunnels Factory", null, ("boss", "")),
        });

        Assert.Equal("No factory (Personal)", choices.NoneLabel);
        Assert.Equal(new[] { "clickfunnels", "warmforward" }, choices.Factories.Select(f => f.Factory));
        Assert.Equal(new[] { "ClickFunnels Factory", "WarmForward Factory" }, choices.Factories.Select(f => f.Title));
        var warm = choices.Factories[1];
        Assert.Equal(new[] { "ceo", "value-hunter" }, warm.Seats.Select(s => s.Id));
        // A named seat shows its name and its id; a seat whose name is its id, or has none, shows the id once.
        Assert.Equal(new[] { "Nora Hale (ceo)", "value-hunter" }, warm.Seats.Select(s => s.Label));
        Assert.Equal("boss", choices.Factories[0].Seats.Single().Label);
    }

    [Fact]
    public void Fold_LeavesOutArchivedFactories_AndFactoriesWithNoSeat()
    {
        var choices = CronSeatChoicesEndpoint.Fold(new[]
        {
            Factory("old", "Old Factory", new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc), ("boss", "Boss")),
            Factory("empty", "Empty Factory"),
            Factory("live", "", null, ("boss", "Boss")),
        });

        // An untitled factory is listed by its id.
        Assert.Equal("live", choices.Factories.Single().Title);
    }
}
