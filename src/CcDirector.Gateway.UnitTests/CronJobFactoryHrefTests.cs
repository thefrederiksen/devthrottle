using CcDirector.Gateway.Api;
using Xunit;

namespace CcDirector.Gateway.Tests;

// Where the Schedule page sends a factory schedule's Edit (the owner, 2026-10-09): its factory's Seats page - and
// nowhere when the factory is not registered, so that schedule keeps its own Edit and Delete on the Schedule page.
public class CronJobFactoryHrefTests
{
    private static readonly HashSet<string> Registered = new(StringComparer.Ordinal) { "clickfunnels", "warm forward" };

    [Fact]
    public void FactoryHrefOf_RegisteredFactory_IsItsSeatsPage()
    {
        Assert.Equal("/factories/clickfunnels/seats", CronJobEndpoints.FactoryHrefOf("clickfunnels", Registered));
    }

    [Fact]
    public void FactoryHrefOf_FactoryIdNeedingEscaping_IsEscaped()
    {
        Assert.Equal("/factories/warm%20forward/seats", CronJobEndpoints.FactoryHrefOf("warm forward", Registered));
    }

    [Fact]
    public void FactoryHrefOf_UnregisteredFactory_IsNull()
    {
        Assert.Null(CronJobEndpoints.FactoryHrefOf("gone", Registered));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void FactoryHrefOf_NoFactory_IsNull(string? factory)
    {
        Assert.Null(CronJobEndpoints.FactoryHrefOf(factory, Registered));
    }
}
