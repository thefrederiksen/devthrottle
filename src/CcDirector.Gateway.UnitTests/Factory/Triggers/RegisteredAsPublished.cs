using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Factory.Definition;

namespace CcDirector.Gateway.Tests.Factory.Triggers;

/// <summary>
/// The registration a publish writes for a definition, for tests whose published definition is held in memory rather
/// than published into a database: the factory id, title and Boss seat, and each seat's id, name and role. A
/// check-started seat is told who it is from this registration, so a trigger test with a published definition needs it.
/// </summary>
internal static class RegisteredAsPublished
{
    public static RegisteredFactoryDto Of(PublishedFactoryDefinition published)
    {
        var d = published.Definition;
        var dto = new RegisteredFactoryDto { Factory = d.Factory, Title = d.Title, BossSeat = d.BossSeat };
        foreach (var seat in d.Seats)
            dto.Seats.Add(new RegisteredFactorySeatDto { Id = seat.Seat, Name = seat.Name, Role = seat.Role });
        return dto;
    }
}
