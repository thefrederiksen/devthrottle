using CcDirectorClient.Voice;
using Xunit;

namespace CcDirectorClient.Tests;

/// <summary>
/// Persistence + resume-decision tests for the in-flight voice turn (issue #406). The store
/// keeps exactly one turn (latest wins) so a reply the Gateway already cached (~10-minute TTL)
/// is not lost when the app is killed/backgrounded/crashes mid-turn: a turn within the TTL
/// window is resumed (reusing the #405 poll loop), a turn past it is discarded with a clear
/// message rather than polling a guaranteed 404.
///
/// An in-memory <see cref="IKeyValueStore"/> stands in for MAUI Preferences so the round-trip
/// is exercised off-device; the TTL clock is passed in so "within" vs "past" is deterministic.
/// </summary>
public class InFlightTurnStoreTests
{
    private const string Sid = "session-1";
    private const string Tid = "turn-abc";

    /// <summary>An in-memory key/value store - the off-device stand-in for MAUI Preferences.</summary>
    private sealed class FakeKeyValueStore : IKeyValueStore
    {
        private readonly Dictionary<string, string> _map = new();
        public int SetCalls { get; private set; }
        public int RemoveCalls { get; private set; }

        public string? Get(string key) => _map.TryGetValue(key, out var v) ? v : null;
        public void Set(string key, string value) { _map[key] = value; SetCalls++; }
        public void Remove(string key) { _map.Remove(key); RemoveCalls++; }
    }

    private static DateTimeOffset At(int minute) => new(2026, 6, 13, 12, minute, 0, TimeSpan.Zero);

    // ===== persistence round-trip ===========================================

    [Fact]
    public void SaveThenLoad_RoundTripsAllFields()
    {
        // Arrange
        var store = new InFlightTurnStore(new FakeKeyValueStore());
        var submittedAt = At(0);

        // Act
        store.Save(new InFlightVoiceTurn(Sid, Tid, submittedAt));
        var loaded = store.Load();

        // Assert
        Assert.NotNull(loaded);
        Assert.Equal(Sid, loaded.SessionId);
        Assert.Equal(Tid, loaded.TurnId);
        Assert.Equal(submittedAt, loaded.SubmittedAt);
    }

    [Fact]
    public void Load_WhenNothingSaved_ReturnsNull()
    {
        var store = new InFlightTurnStore(new FakeKeyValueStore());
        Assert.Null(store.Load());
    }

    [Fact]
    public void Save_LatestWins_OnlyOneTurnTracked()
    {
        // Arrange - a new submit must replace the older abandoned turn (only one is tracked).
        var kv = new FakeKeyValueStore();
        var store = new InFlightTurnStore(kv);
        store.Save(new InFlightVoiceTurn(Sid, "turn-old", At(0)));

        // Act
        store.Save(new InFlightVoiceTurn("session-2", "turn-new", At(1)));
        var loaded = store.Load();

        // Assert - the latest turn is the one that survives; there is no second slot.
        Assert.NotNull(loaded);
        Assert.Equal("session-2", loaded.SessionId);
        Assert.Equal("turn-new", loaded.TurnId);
    }

    [Fact]
    public void Clear_RemovesTheTurn()
    {
        var kv = new FakeKeyValueStore();
        var store = new InFlightTurnStore(kv);
        store.Save(new InFlightVoiceTurn(Sid, Tid, At(0)));

        store.Clear();

        Assert.Null(store.Load());
        Assert.Equal(1, kv.RemoveCalls);
    }

    [Fact]
    public void Load_CorruptBlob_ReturnsNull_NotCrash()
    {
        // A garbage persisted value means "no usable in-flight turn", never an exception.
        var kv = new FakeKeyValueStore();
        kv.Set(InFlightTurnStore.PrefKey, "{ this is not valid json");
        var store = new InFlightTurnStore(kv);

        Assert.Null(store.Load());
    }

    [Fact]
    public void Load_BlobMissingTurnId_ReturnsNull()
    {
        // Parses as JSON but is unusable (no turn id) -> treated as nothing in flight.
        var kv = new FakeKeyValueStore();
        kv.Set(InFlightTurnStore.PrefKey, "{\"SessionId\":\"s\",\"TurnId\":\"\",\"SubmittedAt\":\"2026-06-13T12:00:00+00:00\"}");
        var store = new InFlightTurnStore(kv);

        Assert.Null(store.Load());
    }

    // ===== resume-within-TTL vs discard-past-TTL ============================

    [Fact]
    public void IsWithinTtl_FreshTurn_IsResumable()
    {
        // Submitted 3 minutes ago, TTL 10 minutes -> still within the cached window: resume it.
        var turn = new InFlightVoiceTurn(Sid, Tid, At(0));
        Assert.True(turn.IsWithinTtl(At(3), InFlightTurnStore.ResumeWindow));
    }

    [Fact]
    public void IsWithinTtl_AtTheBoundary_IsStillResumable()
    {
        // Exactly at the TTL edge is still resumable (inclusive) - a one-second-old race should
        // not be thrown away on a boundary rounding.
        var turn = new InFlightVoiceTurn(Sid, Tid, At(0));
        Assert.True(turn.IsWithinTtl(At(0).Add(InFlightTurnStore.ResumeWindow), InFlightTurnStore.ResumeWindow));
    }

    [Fact]
    public void IsWithinTtl_PastTtl_IsDiscarded()
    {
        // Submitted 11 minutes ago, TTL 10 minutes -> the Gateway has dropped the cached reply,
        // so this turn must be discarded (and a clear message shown) rather than polled into a 404.
        var turn = new InFlightVoiceTurn(Sid, Tid, At(0));
        Assert.False(turn.IsWithinTtl(At(11), InFlightTurnStore.ResumeWindow));
    }

    [Fact]
    public void ResumeWindow_AlignsWithGatewayJobTtl()
    {
        // The resume window is the ~10-minute Gateway job TTL the issue calls out; pin it so a
        // future change to one side is a visible, deliberate edit.
        Assert.Equal(TimeSpan.FromMinutes(10), InFlightTurnStore.ResumeWindow);
    }

    [Fact]
    public void SavedTurn_LoadedAndJudgedAgainstTtl_GatesResume()
    {
        // End-to-end of the persistence + decision the resume path relies on: persist on submit,
        // reload on next launch, and decide resume-vs-discard purely from the stored SubmittedAt.
        var store = new InFlightTurnStore(new FakeKeyValueStore());
        store.Save(new InFlightVoiceTurn(Sid, Tid, At(0)));

        var reloaded = store.Load();
        Assert.NotNull(reloaded);

        // Within the window on the next launch -> resume.
        Assert.True(reloaded.IsWithinTtl(At(5), InFlightTurnStore.ResumeWindow));
        // A later launch past the window -> discard.
        Assert.False(reloaded.IsWithinTtl(At(20), InFlightTurnStore.ResumeWindow));
    }
}
