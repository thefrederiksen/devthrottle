using System.Reflection;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Wingman;
using Xunit;

namespace CcDirector.Gateway.Tests.Wingman;

/// <summary>
/// The verdict copy keeps every field. The copy is a hand-written member list, and a list like that goes stale the day
/// the record gains a field - so this fills EVERY public settable property by reflection and requires the copy to carry
/// each one. A field added to the record without being added to the copy fails here by name.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class TurnVerdictDtoCopyTests
{
    [Fact]
    public void Of_CarriesEveryPublicPropertyOfTheVerdict()
    {
        var original = new TurnVerdictDto();
        var properties = typeof(TurnVerdictDto).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.CanWrite)
            .ToList();
        Assert.NotEmpty(properties);

        foreach (var p in properties)
            p.SetValue(original, DistinctValueFor(p.PropertyType, p.Name));

        var copy = TurnVerdictDtoCopy.Of(original);

        Assert.NotSame(original, copy);
        foreach (var p in properties)
            Assert.True(Equals(p.GetValue(original), p.GetValue(copy)), $"TurnVerdictDtoCopy.Of does not copy {p.Name}");
    }

    private static object DistinctValueFor(Type type, string name)
    {
        if (type == typeof(string)) return "value-of-" + name;
        if (type == typeof(bool)) return true;
        if (type == typeof(int)) return 3 + name.Length;
        if (type == typeof(DateTime) || type == typeof(DateTime?)) return new DateTime(2026, 9, 15, 10, name.Length % 60, 0, DateTimeKind.Utc);
        if (type == typeof(TurnVerdictMenuDto)) return new TurnVerdictMenuDto { Question = "a question" };
        if (type == typeof(List<TurnVerdictOptionDto>)) return new List<TurnVerdictOptionDto> { new() { Key = "an option" } };
        throw new InvalidOperationException(
            $"TurnVerdictDto.{name} has a type this test does not know how to fill ({type.Name}); teach it, so the copy stays checked.");
    }
}
