using System.Collections.Concurrent;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Voice;

namespace CcDirector.Gateway.Api;

/// <summary>
/// THE GATEWAY DRIVES A HELD DELIVERY TO ITS END (Voice Delivery mission, phase 5).
///
/// Phase 4, case 2d: under full load the Director's tunnel dropped, the Gateway rightly held the recording as "still
/// delivering" - and then nothing retried it for 7 minutes 44 seconds, because the only retry lived in a Cockpit tab the
/// browser had frozen in the background. When it finally ran, the recording was 565 seconds old and was shown back: never
/// doubled, never delivered, for a recording sent to a live agent. The Delivery Lead's ruling: once the Gateway holds a
/// recording's words and its delivery id, the Gateway drives that delivery to its end itself.
///
/// This is that one driver. It attempts every delivery the Gateway owns and has not finished - a dictation taken over
/// by its complete call, and a "Send anyway" answered "still delivering" - through <see cref="DictationDelivery"/>, the
/// same core and single-flight the client's own call uses. It is woken by exactly three things, each named on the
/// <see cref="DeliveryDecisions.GatewayDrive"/> line it writes:
///
///  1. THE DIRECTOR'S TUNNEL COMING BACK (<see cref="DeliveryDecisions.DriveDirectorConnected"/>): the first session
///     snapshot a new connection of that Director delivers. Not the registry's "Director added" event - that fires only
///     for a Director the registry did not already hold, so a tunnel that drops and reconnects inside the eviction
///     horizon raises nothing. And not the Hello alone: Hello marks the Director's cached sessions stale until the new
///     connection pushes its own snapshot, so an attempt woken by Hello would find its session unreachable every time.
///     The first snapshot of a connection is the moment the session can be reached again - see
///     <see cref="Streaming.PushedSessionStore.SessionsArrivedOnNewConnection"/>.
///  2. A STEADY TICK (<see cref="DeliveryDecisions.DriveTick"/>), which attempts any delivery whose next attempt is due.
///     The wait between attempts rises from about 2 seconds to 15; the tick looks every <see cref="TickInterval"/>, which
///     is short so the short waits are kept, and it only reads memory unless something is due. It is also what applies
///     the time rules to a delivery whose Director never comes back.
///  3. THE GATEWAY STARTING (<see cref="DeliveryDecisions.DriveGatewayStarted"/>): every owned delivery still held on
///     disk is picked up from its durable record and attempted. Nothing in memory is needed, so a restart never forgets
///     one.
///
/// PER TENANT. Each attempt runs inside its own tenant's scope, against that tenant's own partition - the tenant is the
/// partition the delivery was found in or taken over in, never guessed. It does NOT use the tenant census the periodic
/// sweeps use, on purpose: that census lists the tenants with a connected Director, and a tenant whose Director never
/// returns is exactly the one whose time rules this must still apply.
///
/// NO FALLBACK. A delivery the driver cannot read is written up - in the log and on its decision record - and left
/// alone; it is never guessed at.
/// </summary>
internal sealed class HeldDeliveryDriver : IDisposable
{
    /// <summary>The wait after the first held attempt; it doubles each time, up to <see cref="LongestWait"/>.</summary>
    internal static readonly TimeSpan ShortestWait = TimeSpan.FromSeconds(2);
    /// <summary>The longest wait between two attempts of one held delivery.</summary>
    internal static readonly TimeSpan LongestWait = TimeSpan.FromSeconds(15);

    private readonly VoiceUploadStore _uploads;
    private readonly Func<TenantId, IDisposable> _enterScope;
    private readonly Func<TenantId, string, string?> _directorOfSession;
    private readonly bool _hosted;
    private readonly ConcurrentDictionary<string, Held> _held = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private DictationDelivery? _delivery;
    private Prompts.TypedPromptStore? _typedPrompts;
    private Func<TypedPromptDelivery>? _typedDelivery;
    private Timer? _timer;
    private int _disposed;

    private sealed class Held
    {
        public required TenantId Tenant { get; init; }
        public required string UploadId { get; init; }
        public required HeldDeliveryKind Kind { get; init; }
        public int HeldAttempts;
        public DateTimeOffset NextDueUtc;
    }

    /// <param name="uploads">The dictation staging, any partition: every attempt works in <c>ForTenant</c> of it.</param>
    /// <param name="enterScope">Enters one tenant's scope, so the commands an attempt sends reach that tenant's Director.</param>
    /// <param name="directorOfSession">Which Director holds a session, for the director-connected wake; null when none does.</param>
    /// <param name="hosted">True on the hosted Gateway, where the base partition is not an account's and is not scanned.</param>
    public HeldDeliveryDriver(VoiceUploadStore uploads, Func<TenantId, IDisposable> enterScope,
        Func<TenantId, string, string?> directorOfSession, bool hosted)
    {
        _uploads = uploads;
        _enterScope = enterScope;
        _directorOfSession = directorOfSession;
        _hosted = hosted;
    }

    /// <summary>How often the tick looks for a delivery whose next attempt is due.</summary>
    public TimeSpan TickInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The attempt piece, handed over when the dictation routes are mapped. Required before <see cref="StartAsync"/>.</summary>
    public void Attach(DictationDelivery delivery)
    {
        if (_delivery is not null && !ReferenceEquals(_delivery, delivery))
            throw new InvalidOperationException("the held-delivery driver is already attached to a dictation delivery");
        _delivery = delivery;
        FileLog.Write("[HeldDeliveryDriver] attached to the dictation delivery core");
    }

    /// <summary>
    /// The held typed prompts (Voice Delivery phase 5, contract section 7, T4): their store (base handle, any partition)
    /// and the one-attempt piece. From then on a typed prompt the prompt route holds is driven here, on the same three
    /// wake-ups as a dictation. The attempt piece is handed over as a factory because the Gateway builds it on first use.
    /// </summary>
    public void AttachTypedPrompts(Prompts.TypedPromptStore store, Func<TypedPromptDelivery> delivery)
    {
        if (_typedPrompts is not null && !ReferenceEquals(_typedPrompts, store))
            throw new InvalidOperationException("the held-delivery driver is already attached to a typed prompt store");
        _typedPrompts = store;
        _typedDelivery = delivery;
        FileLog.Write("[HeldDeliveryDriver] attached to the typed prompt store");
    }

    /// <summary>How many deliveries the driver holds right now (for tests and the log).</summary>
    public int HeldCount => _held.Count;

    /// <summary>
    /// A delivery the Gateway just took over: from now on this driver finishes it. Its first attempt by the driver is due
    /// after the shortest wait - the owner's own call is making the first attempt right now. The start-up pass passes
    /// <c>startupPass: true</c>: it drives the delivery immediately itself, so its next attempt is not due until that
    /// attempt has run and set the wait (the tick must not steal a delivery the pass is about to drive, and name the
    /// wrong wake-up on its decision line).
    /// </summary>
    public void Track(TenantId tenant, string uploadId, HeldDeliveryKind kind, bool startupPass = false)
    {
        if (kind == HeldDeliveryKind.TypedPrompt && _typedPrompts is null)
            throw new InvalidOperationException("a typed prompt was handed to the held-delivery driver before its store was attached");
        var uid = (kind == HeldDeliveryKind.TypedPrompt
                ? Prompts.TypedPromptStore.NormalizeDeliveryId(uploadId)
                : VoiceUploadStore.NormalizeUploadId(uploadId))
            ?? throw new ArgumentException($"'{uploadId}' is not a {kind} id", nameof(uploadId));
        var now = Now();
        _held.AddOrUpdate(Key(tenant, uid, kind),
            _ => new Held { Tenant = tenant, UploadId = uid, Kind = kind,
                NextDueUtc = startupPass ? DateTimeOffset.MaxValue : now + ShortestWait },
            (_, existing) => existing);
        FileLog.Write($"[HeldDeliveryDriver] tracking {kind} upload={uid} tenant={tenant.ToLogString()}");
    }

    /// <summary>
    /// The Gateway started: find every owned, unfinished delivery on disk, in every partition, and attempt each one now
    /// (<see cref="DeliveryDecisions.DriveGatewayStarted"/>). THE TICK STARTS FIRST (Voice Delivery phase 5, review
    /// round, the review's finding 3): the pass is serial and can be slow - each attempt can wait out a thirty-second
    /// send and a thirty-second question - and while it works, a delivery newly taken over by a complete call must get
    /// its first driver attempt on the ordinary cadence, not queued behind every held record the pass is still working
    /// through. The pass's own deliveries are not due to the tick: the pass drives them now, and each attempt sets the
    /// next wait when it stays held. A delivery that cannot be read is written up and not driven.
    /// </summary>
    public async Task StartAsync()
    {
        if (_delivery is null)
            throw new InvalidOperationException("the held-delivery driver was started before the dictation routes attached their delivery core");
        _timer = new Timer(_ => _ = TickAsync(), null, TickInterval, TickInterval);
        var found = 0;
        foreach (var tenant in PartitionTenants())
        {
            var store = _uploads.ForTenant(tenant);
            foreach (var held in store.HeldDeliveries())
            {
                if (held.Problem is { } problem)
                {
                    FileLog.Write($"[HeldDeliveryDriver] upload {held.UploadId} (tenant {tenant.ToLogString()}) NOT driven: {problem}");
                    store.RecordDecision(held.UploadId, DeliveryDecisions.GatewayDriveRefused,
                        new DeliveryDecisionFacts { Error = problem, Trigger = DeliveryDecisions.DriveGatewayStarted });
                    continue;
                }
                Track(tenant, held.UploadId, held.Kind, startupPass: true);
                found++;
            }
        }
        if (_typedPrompts is not null)
        {
            // Every held typed prompt on disk. One that cannot be read is tracked too: its attempt writes it up
            // (gateway-drive-refused) and it is dropped - the typed attempt piece is the one reader of its record.
            foreach (var tenant in _typedPrompts.TenantsWithPartitions())
            {
                // The base partition is not an account's on the hosted Gateway, exactly as for the dictation staging.
                if (_hosted && tenant.Equals(TenantId.Local)) continue;
                foreach (var (deliveryId, _) in _typedPrompts.ForTenant(tenant).HeldDeliveries())
                {
                    Track(tenant, deliveryId, HeldDeliveryKind.TypedPrompt, startupPass: true);
                    found++;
                }
            }
        }
        FileLog.Write($"[HeldDeliveryDriver] started: {found} held deliver{(found == 1 ? "y" : "ies")} found on disk; tick every {TickInterval.TotalSeconds:0.###}s");
        await DriveAsync(_held.Values.ToList(), DeliveryDecisions.DriveGatewayStarted);
    }

    /// <summary>
    /// A Director's tunnel came back and its sessions arrived: attempt now every held delivery of that tenant whose
    /// session that Director holds. Runs off the caller's thread - the caller is the Director's own push.
    /// </summary>
    public void OnSessionsArrived(TenantId tenant, string directorId)
    {
        if (Volatile.Read(ref _disposed) != 0 || _delivery is null) return;
        var mine = _held.Values.Where(h => h.Tenant.Equals(tenant)).ToList();
        if (mine.Count == 0) return;
        _ = Task.Run(async () =>
        {
            try
            {
                var theirs = new List<Held>();
                foreach (var held in mine)
                {
                    var sid = SessionOf(held);
                    if (sid is not null && string.Equals(_directorOfSession(tenant, sid), directorId, StringComparison.OrdinalIgnoreCase))
                        theirs.Add(held);
                }
                FileLog.Write($"[HeldDeliveryDriver] director {directorId} (tenant {tenant.ToLogString()}) is back: driving {theirs.Count} held deliver{(theirs.Count == 1 ? "y" : "ies")}");
                await DriveAsync(theirs, DeliveryDecisions.DriveDirectorConnected);
            }
            catch (Exception ex)
            {
                FileLog.Write($"[HeldDeliveryDriver] director-connected pass for {directorId} FAILED: {ex.Message}");
            }
        });
    }

    /// <summary>One tick: attempt every held delivery whose next attempt is due. Exposed so a test can tick on demand.</summary>
    public async Task TickAsync()
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        try
        {
            var now = Now();
            var due = _held.Values.Where(h => h.NextDueUtc <= now).ToList();
            if (due.Count > 0) await DriveAsync(due, DeliveryDecisions.DriveTick);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HeldDeliveryDriver] tick FAILED: {ex.Message}");
        }
    }

    private async Task DriveAsync(IReadOnlyList<Held> batch, string trigger)
    {
        foreach (var held in batch)
            await DriveOneAsync(held, trigger);
    }

    private async Task DriveOneAsync(Held held, string trigger)
    {
        var key = Key(held.Tenant, held.UploadId, held.Kind);
        // One attempt of one delivery at a time from this driver; the single-flight in the attempt piece keeps a client
        // call and a driver attempt apart as well.
        if (!_running.TryAdd(key, 0)) return;
        try
        {
            string result;
            bool stillHeld;
            using (_enterScope(held.Tenant))
            {
                if (held.Kind == HeldDeliveryKind.TypedPrompt)
                {
                    var typed = await _typedDelivery!().DriveOnceAsync(held.Tenant, _typedPrompts!.ForTenant(held.Tenant),
                        held.UploadId, trigger);
                    stillHeld = typed == TypedDriveResult.Held;
                    result = typed.ToString();
                }
                else
                {
                    var store = _uploads.ForTenant(held.Tenant);
                    var voice = held.Kind == HeldDeliveryKind.Dictation
                        ? await _delivery!.DriveDictationAsync(held.Tenant, store, held.UploadId, trigger)
                        : await _delivery!.DriveSendAnywayAsync(held.Tenant, store, held.UploadId, trigger);
                    stillHeld = voice == DriveResult.Held;
                    result = voice.ToString();
                }
            }
            if (stillHeld)
            {
                held.HeldAttempts++;
                held.NextDueUtc = Now() + WaitAfter(held.HeldAttempts);
                return;
            }
            _held.TryRemove(key, out _);
            FileLog.Write($"[HeldDeliveryDriver] {held.Kind} upload={held.UploadId}: {result}; no longer driven");
        }
        catch (Exception ex)
        {
            // An attempt that threw has written what it could; it is held and tried again after the wait, and the log
            // says why. Nothing is guessed about it.
            held.HeldAttempts++;
            held.NextDueUtc = Now() + WaitAfter(held.HeldAttempts);
            FileLog.Write($"[HeldDeliveryDriver] {held.Kind} upload={held.UploadId} ({trigger}) FAILED: {ex.Message}; tried again in {WaitAfter(held.HeldAttempts).TotalSeconds:0}s");
        }
        finally
        {
            _running.TryRemove(key, out _);
        }
    }

    /// <summary>The wait after the <paramref name="heldAttempts"/>-th attempt that stayed held: 2, 4, 8, then 15 seconds.</summary>
    internal static TimeSpan WaitAfter(int heldAttempts)
    {
        var seconds = ShortestWait.TotalSeconds * Math.Pow(2, Math.Max(0, heldAttempts - 1));
        return TimeSpan.FromSeconds(Math.Min(seconds, LongestWait.TotalSeconds));
    }

    // The session a held delivery is for, read from its durable record: the dictation's owned fields, or the "Send anyway"
    // delivery's own. Null when it cannot be read - the attempt itself then writes that up.
    private string? SessionOf(Held held)
    {
        var store = _uploads.ForTenant(held.Tenant);
        try
        {
            return held.Kind switch
            {
                HeldDeliveryKind.Dictation => store.Read(held.UploadId).Record?.Owned?.SessionId,
                HeldDeliveryKind.SendAnyway => store.ReadSendAnyway(held.UploadId)?.SessionId,
                HeldDeliveryKind.TypedPrompt => _typedPrompts!.ForTenant(held.Tenant).Read(held.UploadId).Record?.SessionId,
                _ => throw new InvalidOperationException($"unknown held delivery kind {held.Kind}"),
            };
        }
        catch (Exception ex)
        {
            FileLog.Write($"[HeldDeliveryDriver] upload={held.UploadId}: its session could not be read ({ex.Message})");
            return null;
        }
    }

    // Every partition that can hold a delivery: the base partition on self-host (Local), and each account's own under
    // the partition container. The base partition is not an account's on the hosted Gateway, so it is not scanned there.
    private IEnumerable<TenantId> PartitionTenants()
    {
        if (!_hosted) yield return TenantId.Local;
        var container = Path.Combine(_uploads.ForTenant(TenantId.Local).Root, VoiceUploadStore.TenantPartitionDirectoryName);
        if (!Directory.Exists(container)) yield break;
        foreach (var dir in Directory.EnumerateDirectories(container))
        {
            var name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            // Only the one spelling a partition is ever created under; anything else is not a partition.
            if (Guid.TryParseExact(name, "D", out var parsed) && string.Equals(name, parsed.ToString("D"), StringComparison.Ordinal))
                yield return new TenantId(name);
        }
    }

    private DateTimeOffset Now() => (_delivery?.Clock ?? TimeProvider.System).GetUtcNow();

    private static string Key(TenantId tenant, string uploadId, HeldDeliveryKind kind) => $"{tenant.Value}|{uploadId}|{kind}";

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _timer?.Dispose();
        FileLog.Write("[HeldDeliveryDriver] stopped");
    }
}
