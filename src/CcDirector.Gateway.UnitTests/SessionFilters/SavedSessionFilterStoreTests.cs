using CcDirector.Core.Tenancy;
using CcDirector.Gateway.SessionFilters;
using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests.SessionFilters;

/// <summary>
/// The saved session filters on an account (issue #4009): a filter round-trips, a name is unique within an account
/// ignoring case, every refusal says why, and one account can neither see nor change another's filters.
/// </summary>
public sealed class SavedSessionFilterStoreTests : IDisposable
{
    private static readonly TenantId A = new("acct-a");
    private static readonly TenantId B = new("acct-b");
    private static readonly DateTime Now = new(2026, 10, 15, 12, 0, 0, DateTimeKind.Utc);

    private readonly GatewayDbTestHarness _h = new();
    private readonly SavedSessionFilterStore _store;

    public SavedSessionFilterStoreTests()
    {
        _store = new SavedSessionFilterStore(_h.Open());
    }

    public void Dispose() => _h.Dispose();

    private static SavedSessionFilterInput Input(string name, string colour = "blue", string[]? repos = null, string[]? machines = null)
        => new(name, colour, repos ?? new[] { "devthrottle" }, machines ?? Array.Empty<string>());

    [Fact]
    public void Create_ValidInput_RoundTripsThroughList()
    {
        var result = _store.Create(A, Input("Product", "green", new[] { "devthrottle", "website" }, new[] { "DEVLINUX" }), Now);

        Assert.Equal(SavedSessionFilterOutcome.Saved, result.Outcome);
        var listed = Assert.Single(_store.List(A));
        Assert.Equal(result.Filter!.Id, listed.Id);
        Assert.Equal("Product", listed.Name);
        Assert.Equal("green", listed.Colour);
        Assert.Equal("#22C55E", listed.ColourHex);
        Assert.Equal(new[] { "devthrottle", "website" }, listed.Repos);
        Assert.Equal(new[] { "DEVLINUX" }, listed.Machines);
        Assert.Equal(Now, listed.CreatedAtUtc);
        Assert.Equal(DateTimeKind.Utc, listed.UpdatedAtUtc.Kind);
    }

    [Fact]
    public void Create_TrimsNameAndValues_AndDropsDuplicatesAndBlanks()
    {
        var result = _store.Create(A, new SavedSessionFilterInput("  Client  ", " AMBER ", new[] { " acme ", "acme", "", "  " }, null), Now);

        Assert.Equal(SavedSessionFilterOutcome.Saved, result.Outcome);
        Assert.Equal("Client", result.Filter!.Name);
        Assert.Equal("amber", result.Filter.Colour);
        Assert.Equal(new[] { "acme" }, result.Filter.Repos);
        Assert.Empty(result.Filter.Machines);
    }

    [Fact]
    public void Create_NameDiffersOnlyByCase_IsRefusedAsTaken()
    {
        _store.Create(A, Input("Product"), Now);

        var second = _store.Create(A, Input("PRODUCT"), Now);

        Assert.Equal(SavedSessionFilterOutcome.NameTaken, second.Outcome);
        Assert.Contains("already exists", second.Error);
        Assert.Single(_store.List(A));
    }

    [Theory]
    [InlineData("", "blue", "needs a name")]
    [InlineData("   ", "blue", "needs a name")]
    [InlineData("Product", "magenta", "is not a filter colour")]
    [InlineData("Product", "", "is not a filter colour")]
    public void Create_InvalidNameOrColour_IsRefusedWithTheReason(string name, string colour, string expected)
    {
        var result = _store.Create(A, Input(name, colour), Now);

        Assert.Equal(SavedSessionFilterOutcome.Invalid, result.Outcome);
        Assert.Contains(expected, result.Error);
        Assert.Empty(_store.List(A));
    }

    [Fact]
    public void Create_NameTooLong_IsRefused()
    {
        var result = _store.Create(A, Input(new string('x', SavedSessionFilterStore.MaxNameLength + 1)), Now);

        Assert.Equal(SavedSessionFilterOutcome.Invalid, result.Outcome);
        Assert.Contains("at most", result.Error);
    }

    [Fact]
    public void Create_SelectsNothing_IsRefused()
    {
        var result = _store.Create(A, new SavedSessionFilterInput("Everything", "blue", Array.Empty<string>(), Array.Empty<string>()), Now);

        Assert.Equal(SavedSessionFilterOutcome.Invalid, result.Outcome);
        Assert.Contains("at least one repo or machine", result.Error);
    }

    [Fact]
    public void Create_TooManyValuesInOneFacet_IsRefused()
    {
        var repos = Enumerable.Range(0, SavedSessionFilterStore.MaxValuesPerFacet + 1).Select(i => $"repo-{i}").ToArray();

        var result = _store.Create(A, Input("Big", repos: repos), Now);

        Assert.Equal(SavedSessionFilterOutcome.Invalid, result.Outcome);
    }

    [Fact]
    public void Create_BeyondTheAccountLimit_IsRefused()
    {
        for (var i = 0; i < SavedSessionFilterStore.MaxFiltersPerAccount; i++)
            Assert.Equal(SavedSessionFilterOutcome.Saved, _store.Create(A, Input($"f{i}"), Now).Outcome);

        var over = _store.Create(A, Input("one too many"), Now);

        Assert.Equal(SavedSessionFilterOutcome.TooMany, over.Outcome);
        Assert.Contains("Delete one", over.Error);
    }

    [Fact]
    public void Update_RenameAndRecolour_KeepsTheIdentifier()
    {
        var created = _store.Create(A, Input("Product"), Now).Filter!;

        var updated = _store.Update(A, created.Id, Input("Products", "purple", new[] { "devthrottle", "website" }), Now.AddMinutes(5));

        Assert.Equal(SavedSessionFilterOutcome.Saved, updated.Outcome);
        var listed = Assert.Single(_store.List(A));
        Assert.Equal(created.Id, listed.Id);
        Assert.Equal("Products", listed.Name);
        Assert.Equal("purple", listed.Colour);
        Assert.Equal(new[] { "devthrottle", "website" }, listed.Repos);
        Assert.Equal(Now, listed.CreatedAtUtc);
        Assert.Equal(Now.AddMinutes(5), listed.UpdatedAtUtc);
    }

    [Fact]
    public void Update_ToItsOwnNameInADifferentCase_IsAllowed()
    {
        var created = _store.Create(A, Input("product"), Now).Filter!;

        var updated = _store.Update(A, created.Id, Input("Product"), Now);

        Assert.Equal(SavedSessionFilterOutcome.Saved, updated.Outcome);
        Assert.Equal("Product", updated.Filter!.Name);
    }

    [Fact]
    public void Update_ToAnotherFiltersName_IsRefusedAsTaken()
    {
        _store.Create(A, Input("Product"), Now);
        var client = _store.Create(A, Input("Client"), Now).Filter!;

        var result = _store.Update(A, client.Id, Input("product"), Now);

        Assert.Equal(SavedSessionFilterOutcome.NameTaken, result.Outcome);
    }

    [Fact]
    public void Update_UnknownIdentifier_IsNotFound()
    {
        var result = _store.Update(A, "0123456789abcdef0123456789abcdef", Input("Product"), Now);

        Assert.Equal(SavedSessionFilterOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void Delete_RemovesTheFilter_AndASecondDeleteFindsNothing()
    {
        var created = _store.Create(A, Input("Product"), Now).Filter!;

        Assert.True(_store.Delete(A, created.Id));
        Assert.Empty(_store.List(A));
        Assert.False(_store.Delete(A, created.Id));
    }

    [Fact]
    public void List_SortsByNameIgnoringCase()
    {
        _store.Create(A, Input("beta"), Now);
        _store.Create(A, Input("Alpha"), Now);
        _store.Create(A, Input("gamma"), Now);

        Assert.Equal(new[] { "Alpha", "beta", "gamma" }, _store.List(A).Select(f => f.Name));
    }

    [Fact]
    public void AnotherAccount_CannotSeeChangeOrDeleteTheFilter()
    {
        var mine = _store.Create(A, Input("Product"), Now).Filter!;

        Assert.Empty(_store.List(B));
        Assert.Equal(SavedSessionFilterOutcome.NotFound, _store.Update(B, mine.Id, Input("Hijacked"), Now).Outcome);
        Assert.False(_store.Delete(B, mine.Id));
        Assert.Equal("Product", Assert.Single(_store.List(A)).Name);
    }

    [Fact]
    public void TwoAccounts_MayUseTheSameName()
    {
        Assert.Equal(SavedSessionFilterOutcome.Saved, _store.Create(A, Input("Product"), Now).Outcome);
        Assert.Equal(SavedSessionFilterOutcome.Saved, _store.Create(B, Input("Product"), Now).Outcome);
    }

    [Fact]
    public void Colours_EveryKeyIsLowerCaseAndUnique_AndEveryHexIsSixDigits()
    {
        var keys = SavedSessionFilterStore.Colours.Select(c => c.Key).ToList();

        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.All(SavedSessionFilterStore.Colours, c =>
        {
            Assert.Equal(c.Key.ToLowerInvariant(), c.Key);
            Assert.Matches("^#[0-9A-F]{6}$", c.Hex);
        });
    }
}
