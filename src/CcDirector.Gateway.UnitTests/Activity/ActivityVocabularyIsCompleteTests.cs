using System.Reflection;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Gateway.Tests.Activity;

/// <summary>
/// EVERY DECLARED ACTIVITY WORD IS IN ITS OWN LEGAL LIST - the general guard, for every event and every cause,
/// not only the one that was caught.
///
/// THE DEFECT THIS EXISTS FOR. <c>ActivityEventStore</c> refuses any event type or cause outside
/// <c>ActivityEventTypes.All</c> and <c>ActivityCauses.All</c>. Declaring the constant is the obvious half of
/// adding a word; adding it to the list at the bottom of the file is the half that is easy to miss, and missing
/// it FAILS SILENTLY: the seat catches the store's rejection and logs it, so the feature works, the colours are
/// right, the focused tests are green - and the durable row nobody looks at until months later does not exist.
/// Slice F of the Wingman-on-every-turn mission shipped one event type and three causes that way.
///
/// It is cheap - two reflection passes over two static classes - and it protects every future word rather than
/// the four that were found. Adding a constant to either class and forgetting its list now goes RED here, naming
/// the constant.
///
/// PARKED SUITE. Gateway.UnitTests runs under -Parked.
/// </summary>
public sealed class ActivityVocabularyIsCompleteTests
{
    /// <summary>Every <c>public const string</c> on a vocabulary class, by its own name.</summary>
    private static IReadOnlyDictionary<string, string> DeclaredWords(Type vocabulary) =>
        vocabulary
            .GetFields(BindingFlags.Public | BindingFlags.Static | BindingFlags.FlattenHierarchy)
            .Where(f => f.IsLiteral && !f.IsInitOnly && f.FieldType == typeof(string))
            .ToDictionary(f => f.Name, f => (string)f.GetRawConstantValue()!, StringComparer.Ordinal);

    [Fact]
    public void EveryDeclaredEventType_IsInActivityEventTypesAll()
    {
        var declared = DeclaredWords(typeof(ActivityEventTypes));
        Assert.NotEmpty(declared);

        var missing = declared
            .Where(d => !ActivityEventTypes.All.Contains(d.Value, StringComparer.Ordinal))
            .Select(d => $"{d.Key} (\"{d.Value}\")")
            .ToArray();

        Assert.True(missing.Length == 0,
            "These event types are declared but absent from ActivityEventTypes.All, so ActivityEventStore " +
            "refuses every one of them and no durable row is ever written: " + string.Join(", ", missing));
    }

    [Fact]
    public void EveryDeclaredCause_IsInActivityCausesAll()
    {
        var declared = DeclaredWords(typeof(ActivityCauses));
        Assert.NotEmpty(declared);

        var missing = declared
            .Where(d => !ActivityCauses.All.Contains(d.Value, StringComparer.Ordinal))
            .Select(d => $"{d.Key} (\"{d.Value}\")")
            .ToArray();

        Assert.True(missing.Length == 0,
            "These causes are declared but absent from ActivityCauses.All, so ActivityEventStore refuses every " +
            "one of them and no durable row is ever written: " + string.Join(", ", missing));
    }

    [Fact]
    public void NeitherLegalList_CarriesAWordNothingDeclares()
    {
        // The other direction, and it is not symmetry for its own sake: a list entry with no constant behind it is
        // a word the store accepts that nothing can spell, which is how a typo survives a rename.
        var events = DeclaredWords(typeof(ActivityEventTypes)).Values.ToHashSet(StringComparer.Ordinal);
        var causes = DeclaredWords(typeof(ActivityCauses)).Values.ToHashSet(StringComparer.Ordinal);

        Assert.All(ActivityEventTypes.All, e => Assert.Contains(e, events));
        Assert.All(ActivityCauses.All, c => Assert.Contains(c, causes));
    }

    [Fact]
    public void NoWordIsListedTwice()
    {
        Assert.Equal(ActivityEventTypes.All.Count, ActivityEventTypes.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(ActivityCauses.All.Count, ActivityCauses.All.Distinct(StringComparer.Ordinal).Count());
    }
}
