using CcDirector.Core.Licenses;
using CcDirector.Gateway.Contracts;
using Xunit;

namespace CcDirector.Core.UnitTests.Licenses;

/// <summary>License Switching contract 2.4 (one key holder per machine and user) and 3.6 (switch results kept 24 hours
/// on disk).</summary>
public sealed class LicenseKeyHolderAndResultStoreTests : IDisposable
{
    private const string SwitchId = "6f1c2a9e4b7d4e0f9a3c5b8d2e1f7a40";
    private readonly TempFolder _folder = new();
    private readonly FakeClock _clock = new();

    public void Dispose() => _folder.Dispose();

    [Fact]
    public void TryAcquire_SecondDirectorOnTheSameFolder_IsRefusedUntilTheFirstLetsGo()
    {
        using var first = new LicenseKeyHolderLock(_folder.Path);
        using var second = new LicenseKeyHolderLock(_folder.Path);

        Assert.True(first.TryAcquire());
        Assert.False(second.TryAcquire());
        Assert.False(first.TryAcquire());   // true only on the call that took it

        first.Dispose();
        Assert.True(second.TryAcquire());
        Assert.True(second.IsHeld);
    }

    private LicenseSwitchResult Result(DateTime finished) => new()
    {
        SwitchId = SwitchId,
        Outcome = LicenseSwitchOutcomes.Switched,
        PreviousActiveAccountEmail = "account-a@example.com",
        ActiveAccountEmailAfter = "account-b@example.com",
        StartedAtUtc = finished,
        FinishedAtUtc = finished,
    };

    [Fact]
    public void Find_AfterARestart_ReadsTheResultFromDisk()
    {
        new LicenseSwitchResultStore(_folder.Path, _clock.UtcNow).Save(Result(_clock.Now));

        var found = new LicenseSwitchResultStore(_folder.Path, _clock.UtcNow).Find(SwitchId);

        Assert.Equal("account-b@example.com", found!.ActiveAccountEmailAfter);
    }

    [Fact]
    public void Find_OlderThanRetention_IsNotFoundAndPruneDeletesIt()
    {
        var store = new LicenseSwitchResultStore(_folder.Path, _clock.UtcNow);
        store.Save(Result(_clock.Now));
        File.WriteAllText(Path.Combine(_folder.Path, "not-ours.json"), "{}");
        _clock.Advance(LicenseContract.SwitchResultRetention + TimeSpan.FromSeconds(1));

        Assert.Null(store.Find(SwitchId));
        Assert.Equal(1, store.Prune());
        Assert.False(File.Exists(Path.Combine(_folder.Path, SwitchId + ".json")));
        Assert.True(File.Exists(Path.Combine(_folder.Path, "not-ours.json")));   // only what the store names is removed
    }

    [Theory]
    [InlineData("../../escape")]
    [InlineData("6F1C2A9E4B7D4E0F9A3C5B8D2E1F7A40")]
    public void Find_SwitchIdOfTheWrongShape_Throws(string switchId)
    {
        var store = new LicenseSwitchResultStore(_folder.Path, _clock.UtcNow);

        Assert.Throws<ArgumentException>(() => store.Find(switchId));
    }
}
