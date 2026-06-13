namespace CcDirectorClient.Voice;

/// <summary>
/// Persists the single in-flight voice turn (issue #406) so it survives an app restart,
/// a background past the foreground service, or a crash. Exactly ONE turn is tracked at a
/// time (latest wins): <see cref="Save"/> overwrites any previous turn under one key, so a
/// new submit always replaces an older abandoned turn. The store is backed by an
/// <see cref="IKeyValueStore"/> seam (MAUI <c>Preferences</c> in the app, an in-memory fake
/// in tests) so the persistence round-trip is exercised off-device.
///
/// The TTL window aligns with the Gateway's cached-result lifetime (the <c>GatewayTurnJobStore</c>
/// ~10-minute TTL): a persisted turn within the window can still be resumed; one past it is a
/// guaranteed 404 and is discarded by the caller with a clear message rather than polled.
/// </summary>
public sealed class InFlightTurnStore
{
    /// <summary>The single Preferences key the in-flight turn is stored under.</summary>
    public const string PrefKey = "inflight_voice_turn";

    /// <summary>
    /// The window inside which a persisted turn is still worth resuming, aligned with the
    /// Gateway job TTL (~10 minutes). A turn older than this is discarded, not polled.
    /// </summary>
    public static readonly TimeSpan ResumeWindow = TimeSpan.FromMinutes(10);

    private readonly IKeyValueStore _store;

    public InFlightTurnStore(IKeyValueStore store)
    {
        if (store is null) throw new ArgumentNullException(nameof(store));
        _store = store;
    }

    /// <summary>
    /// Persist <paramref name="turn"/> as THE in-flight turn, replacing any previously stored
    /// one (latest wins - only one turn is ever tracked).
    /// </summary>
    public void Save(InFlightVoiceTurn turn)
    {
        if (turn is null) throw new ArgumentNullException(nameof(turn));
        ClientLog.Write($"[InFlightTurnStore] Save: sid={turn.SessionId}, turnId={turn.TurnId}, submittedAt={turn.SubmittedAt:O}");
        _store.Set(PrefKey, turn.ToJson());
    }

    /// <summary>
    /// The persisted in-flight turn, or null when none is stored or the stored blob is
    /// unusable (absent/corrupt). A corrupt blob returns null - the caller clears it.
    /// </summary>
    public InFlightVoiceTurn? Load()
    {
        var turn = InFlightVoiceTurn.FromJson(_store.Get(PrefKey));
        if (turn is null)
            ClientLog.Write("[InFlightTurnStore] Load: no usable in-flight turn");
        else
            ClientLog.Write($"[InFlightTurnStore] Load: sid={turn.SessionId}, turnId={turn.TurnId}, submittedAt={turn.SubmittedAt:O}");
        return turn;
    }

    /// <summary>Drop the persisted in-flight turn (reached a terminal stage, or was cancelled/discarded).</summary>
    public void Clear()
    {
        ClientLog.Write("[InFlightTurnStore] Clear");
        _store.Remove(PrefKey);
    }
}
