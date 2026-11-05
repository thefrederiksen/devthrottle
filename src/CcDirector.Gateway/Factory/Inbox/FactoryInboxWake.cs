using System.Collections.Concurrent;
using CcDirector.Core.Sessions;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Contracts;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Factory.Definition;
using CcDirector.Gateway.Factory.Triggers;
using CcDirector.Gateway.Fleet;

namespace CcDirector.Gateway.Factory.Inbox;

/// <summary>Everything the wake needs from the Gateway around it, as one seam.</summary>
public interface IFactoryInboxWakeEnvironment
{
    /// <summary>One registered factory of the account, or null.</summary>
    RegisteredFactoryDto? Factory(TenantId tenant, string factory);

    /// <summary>The factory's published definition, or null when it is not defined. Throws
    /// <see cref="InvalidOperationException"/> with the reason when the published version cannot be read
    /// (<see cref="FactorySeatStart.ReadPublished"/>).</summary>
    PublishedFactoryDefinition? Published(TenantId tenant, string factory);

    /// <summary>The account's fresh pushed roster, with the Director that pushed each row.</summary>
    IReadOnlyList<(string DirectorId, SessionDto Session)> Roster(TenantId tenant);

    /// <summary>What the account's Directors last pushed for one session, however stale, or null when none holds it.</summary>
    (string DirectorId, SessionDto Session)? LastKnown(TenantId tenant, string sessionId);

    /// <summary>Whether the Gateway's record says the session has ended: its history row has an ending, or the roster shows
    /// it exited. A session that is merely not reported (its Director is not connected, or has not pushed since a Gateway
    /// restart) has NOT ended.</summary>
    bool HasEnded(TenantId tenant, string sessionId);

    /// <summary>A Director of the account that is connected now and runs on <paramref name="machine"/>, or null. The wake
    /// starts nothing on another computer, and never launches one.</summary>
    string? DirectorOn(TenantId tenant, string machine);

    /// <summary>Start one session on the machine, through the one resolve-then-create path schedules and triggers use.</summary>
    Task<TriggerStartAttempt> StartAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct);

    /// <summary>Type one line into a session and press Enter - only if the Director finds it waiting for a prompt at that
    /// moment, with no draft of the owner's in it (the Fleet Manager's guarded prompt send).</summary>
    Task<FleetManagerPromptSend> RingAsync(TenantId tenant, string directorId, string sessionId, string text, CancellationToken ct);

    /// <summary>Wait. The wake's only clock for waiting, so a test runs the gather window without sleeping.</summary>
    Task DelayAsync(TimeSpan delay, CancellationToken ct);

    DateTime NowUtc();
}

/// <summary>What one look did for one seat, for the log and the tests.</summary>
public enum FactoryInboxWakeAct
{
    /// <summary>Nothing waits for this seat.</summary>
    Nothing,

    /// <summary>Messages wait, and their gather window is still open.</summary>
    WindowOpen,

    /// <summary>The seat was started now.</summary>
    Started,

    /// <summary>A start was claimed and failed; the claim is released and the factory waits, saying why.</summary>
    StartFailed,

    /// <summary>A start was sent and its outcome is not known; the claim is held for the start grace.</summary>
    StartOutcomeUnknown,

    /// <summary>Another writer took the start's claim first, so this look started nothing.</summary>
    ClaimTakenByAnother,

    /// <summary>A start is already in flight, or a started session has not been reported yet.</summary>
    Starting,

    /// <summary>A live session of the seat was rung, once.</summary>
    Rung,

    /// <summary>A live session of the seat was not rung: it was working, held the owner's draft, or could not be reached.
    /// Looked at again shortly.</summary>
    RingDeferred,

    /// <summary>A live session of the seat has already been told of everything that waits.</summary>
    AlreadyTold,

    /// <summary>Nothing could be started; the factory waits, and the thread says why.</summary>
    Waiting,
}

/// <summary>What one look at one factory did, and when it wants to look again (null when nothing is owed).</summary>
public sealed record FactoryInboxWakeLook(string Factory, FactoryInboxWakeAct Secretary, FactoryInboxWakeAct Boss, DateTime? LookAgainAtUtc)
{
    public static FactoryInboxWakeLook None(string factory) => new(factory, FactoryInboxWakeAct.Nothing, FactoryInboxWakeAct.Nothing, null);
}

/// <summary>
/// THE WAKE (Factory Inbox mission, phase 2; MISSION section 5, "What wakes whom"). Nobody polls the inbox: the Gateway
/// writes every message and every bundle, so the write is the event, and this decides who is woken.
///
///  - A MESSAGE ARRIVES: it opens a <see cref="Window"/> gather window for its factory, or joins the open one. The window
///    is not a timer's memory: it is read from the store, as the oldest message still waiting plus 60 seconds.
///  - THE WINDOW CLOSES AND NO SECRETARY IS ALIVE: ONE Secretary starts on the Boss's computer
///    (<see cref="FactoryInboxSecretary"/>), recorded with the phase 1 mark (factory, seat <c>secretary</c>, seat kit).
///  - THE WINDOW CLOSES AND A SECRETARY IS ALIVE: it is rung with one line, only while it waits for a prompt - never while
///    it works or holds the owner's draft. The Director makes that check at the moment it would type; a refused ring is
///    not counted, and the look comes back shortly.
///  - A BUNDLE IS HANDED: a live Boss of the factory - a night-start Boss, a talk, or one this started - is rung on the
///    same terms, never doubled; with none alive, ONE Boss starts at once, as itself: the night-start seat kit (brief,
///    memory, skills), not the rules-only kit of the Boss start door.
///  - NO COMPUTER: nothing starts, the factory's wake row says why, and the thread shows it; the heartbeat looks again,
///    and the start goes on once a Director runs on that computer. Never on another computer.
///
/// ONE START PER FACTORY IS A DATABASE FACT. A start's claim is a conditional update on the factory's wake row
/// (<see cref="FactoryInboxWakeStore.TryUpdate"/>), taken before the start is sent, and held while the start runs and
/// for <see cref="StartGrace"/> after it while the started session is not reported yet. A Gateway restarted in the
/// middle of either reads the claim and starts nothing more; one restarted inside a window reads the window from the
/// store and starts exactly one Secretary after it. In this process a per-factory lock keeps two looks apart.
///
/// WHO IS ALIVE is read from the pushed roster: a session of the factory with the seat and a SEAT kit that has not
/// exited - a child that inherited the seat (rules-only) is not the seat - or the session this last started, by its id,
/// as a trigger remembers its last session.
///
/// A NIGHT-START BOSS RUNG MID-RUN (the open item of MISSION section 10, settled by
/// <c>FactoryInboxWakeTests.NightStartBoss_RungMidRun_RingWaitsUntilIdleAndLandsOnce</c>): while it works the roster says
/// so and nothing is asked; if the roster is a moment behind, the Director refuses the ring because the session is not
/// waiting for a prompt. Either way nothing is typed mid-turn, the bundle stays unread, and the look comes back in
/// <see cref="LookAgain"/>. When its turn ends and it waits for a prompt the ring lands, once, and is recorded, so it is
/// never rung again for that bundle. A night-start run that closes itself at the end of its turn instead is no longer
/// alive, and the next look starts ONE Boss for the bundle.
/// </summary>
public sealed class FactoryInboxWake
{
    /// <summary>The gather window: messages arriving within it of the first are filed by one Secretary start or one ring.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    /// <summary>How long a start's claim is held while the started session has not been reported, as a trigger's lock is.</summary>
    public static readonly TimeSpan StartGrace = TriggerService.StartGrace;

    /// <summary>How soon a look comes back after a ring that was not typed, or while a start is still being reported.</summary>
    public static readonly TimeSpan LookAgain = TimeSpan.FromSeconds(15);

    /// <summary>The heartbeat the Gateway sweeps every account's factories with: the backstop for a Director that comes
    /// back, a Secretary that ended with messages unfiled, and a Gateway that restarted.</summary>
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly FactoryInboxWakeStore _wakes;
    private readonly IFactoryInboxWakeEnvironment _env;
    private readonly CancellationToken _lifetime;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new(StringComparer.Ordinal);

    /// <param name="lifetime">The Gateway's own lifetime, which every look and start runs on - never a request's.</param>
    public FactoryInboxWake(FactoryInboxWakeStore wakes, IFactoryInboxWakeEnvironment env, CancellationToken lifetime)
    {
        _wakes = wakes ?? throw new ArgumentNullException(nameof(wakes));
        _env = env ?? throw new ArgumentNullException(nameof(env));
        _lifetime = lifetime;
    }

    /// <summary>
    /// A message was logged: look at its factory when its gather window closes. Returns the scheduled look, which the
    /// route never waits on. The look reads the window from the store, so a later message joining the window, or a
    /// restart before it closes, changes nothing.
    /// </summary>
    public Task<FactoryInboxWakeLook> MessageSent(TenantId tenant, string factory, DateTime sentAtUtc)
    {
        FileLog.Write($"[FactoryInboxWake] MessageSent: tenant={tenant.ToLogString()} factory={factory} - looking at {sentAtUtc.Add(Window):O}");
        return LookLaterAsync(tenant, factory, sentAtUtc.Add(Window));
    }

    /// <summary>A Secretary handed a bundle: look at its factory now, to ring or start its Boss.</summary>
    public Task<FactoryInboxWakeLook> BundleHanded(TenantId tenant, string factory)
    {
        FileLog.Write($"[FactoryInboxWake] BundleHanded: tenant={tenant.ToLogString()} factory={factory}");
        return LookLaterAsync(tenant, factory, _env.NowUtc());
    }

    /// <summary>The heartbeat for one account: look at every factory with something waiting. Never throws.</summary>
    public async Task SweepAsync(TenantId tenant, CancellationToken ct)
    {
        IReadOnlyList<string> factories;
        try
        {
            factories = _wakes.FactoriesWithWork(tenant);
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryInboxWake] SweepAsync FAILED to read the work of {tenant.ToLogString()}: {ex.Message}");
            return;
        }
        foreach (var factory in factories)
        {
            if (ct.IsCancellationRequested) return;
            await LookSafeAsync(tenant, factory).ConfigureAwait(false);
        }
    }

    private async Task<FactoryInboxWakeLook> LookLaterAsync(TenantId tenant, string factory, DateTime dueUtc)
    {
        try
        {
            var wait = dueUtc - _env.NowUtc();
            if (wait > TimeSpan.Zero)
                await _env.DelayAsync(wait, _lifetime).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            FileLog.Write($"[FactoryInboxWake] the look at {factory} was cut by the Gateway stopping; the next Gateway's heartbeat looks instead");
            return FactoryInboxWakeLook.None(factory);
        }
        return await LookSafeAsync(tenant, factory).ConfigureAwait(false);
    }

    /// <summary>A look as an entry point: nothing awaits a scheduled look, so a failure is logged, never thrown.</summary>
    private async Task<FactoryInboxWakeLook> LookSafeAsync(TenantId tenant, string factory)
    {
        try
        {
            var look = await LookAsync(tenant, factory, _lifetime).ConfigureAwait(false);
            if (look.LookAgainAtUtc is { } again && again > _env.NowUtc().Add(HeartbeatInterval))
                _ = LookLaterAsync(tenant, factory, again);
            return look;
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryInboxWake] look at {factory} FAILED: {ex.GetType().Name}: {ex.Message}");
            return FactoryInboxWakeLook.None(factory);
        }
    }

    /// <summary>
    /// LOOK AT ONE FACTORY NOW: read its inbox and its wake row, and start or ring the Secretary and the Boss as they are
    /// owed. Everything is decided from the store at this moment.
    /// </summary>
    public async Task<FactoryInboxWakeLook> LookAsync(TenantId tenant, string factory, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(factory);
        var gate = _locks.GetOrAdd($"{tenant.Value}/{factory}", _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var registered = _env.Factory(tenant, factory);
            if (registered is null || registered.ArchivedAtUtc is not null || string.IsNullOrWhiteSpace(registered.BossSeat))
            {
                FileLog.Write($"[FactoryInboxWake] LookAsync: {factory} is {(registered is null ? "not registered" : registered.ArchivedAtUtc is not null ? "archived" : "Boss-less")} - nothing is woken");
                return FactoryInboxWakeLook.None(factory);
            }

            var facts = _wakes.Facts(tenant, registered.Factory);
            if (facts.ToFile.Count == 0 && facts.Unread.Count == 0)
            {
                ClearWaiting(tenant, registered.Factory);
                return FactoryInboxWakeLook.None(registered.Factory);
            }

            var (secretary, secretaryAgain) = await SecretaryAsync(tenant, registered, facts, ct).ConfigureAwait(false);
            var (boss, bossAgain) = await BossAsync(tenant, registered, facts, ct).ConfigureAwait(false);
            var again = Earliest(secretaryAgain, bossAgain);
            FileLog.Write($"[FactoryInboxWake] LookAsync: {registered.Factory} toFile={facts.ToFile.Count} unread={facts.Unread.Count} secretary={secretary} boss={boss} lookAgain={again?.ToString("O") ?? "none"}");
            return new FactoryInboxWakeLook(registered.Factory, secretary, boss, again);
        }
        finally
        {
            gate.Release();
        }
    }

    // ---------------------------------------------------------------- the Secretary

    private async Task<(FactoryInboxWakeAct Act, DateTime? Again)> SecretaryAsync(TenantId tenant, RegisteredFactoryDto registered,
        FactoryInboxWakeFacts facts, CancellationToken ct)
    {
        if (facts.ToFile.Count == 0) return (FactoryInboxWakeAct.Nothing, null);
        var now = _env.NowUtc();
        var factory = registered.Factory;
        var row = _wakes.Row(tenant, factory, now);
        var sighting = Find(tenant, factory, FactoryInboxSeats.Secretary, row.SecretarySessionId, row.SecretaryStartedAtUtc, now);

        if (sighting.State == SeatState.Starting)
            return (FactoryInboxWakeAct.Starting, Earliest(now.Add(LookAgain), null));

        var sent = facts.ToFile.Where(m => m.State == FactoryInboxWords.States.Sent).ToList();
        if (sighting.State == SeatState.Alive)
        {
            if (row.SecretaryWaiting is not null)
                row = ClearSeatWaiting(tenant, factory, row, FactoryInboxWords.WaitingFor.Secretary, now);
            // Told of everything sent up to SecretaryToldUpToUtc, by its opening or a ring: a ring is owed only for a
            // message sent after it, and only once that message's window has closed.
            var told = row.SecretarySessionId is not null && SameId(row.SecretarySessionId, sighting.SessionId) ? row.SecretaryToldUpToUtc : null;
            var untold = sent.Where(m => told is null || m.SentAtUtc > told).ToList();
            if (untold.Count == 0) return (FactoryInboxWakeAct.AlreadyTold, null);
            var closes = untold[0].SentAtUtc.Add(Window);
            if (now < closes) return (FactoryInboxWakeAct.WindowOpen, closes);
            var rung = await RingAsync(tenant, factory, sighting, FactoryInboxSecretary.RingLine(sent.Count), ct).ConfigureAwait(false);
            if (!rung) return (FactoryInboxWakeAct.RingDeferred, now.Add(LookAgain));
            var newest = sent.Max(m => m.SentAtUtc);
            // Recorded on the row as the session that was rung, so a later look does not ring it again for these.
            _wakes.TryUpdate(tenant, factory, row.Version, w =>
            {
                w.SecretarySessionId = sighting.SessionId;
                w.SecretaryToldUpToUtc = newest;
            }, now);
            return (FactoryInboxWakeAct.Rung, null);
        }

        // No Secretary alive: everything not yet handed on waits for one - the new messages, and any a Secretary that has
        // ended received and did not hand on. The window is the oldest of them plus the gather window.
        var opens = facts.ToFile[0].SentAtUtc;
        var windowCloses = opens.Add(Window);
        if (now < windowCloses) return (FactoryInboxWakeAct.WindowOpen, windowCloses);
        return await StartSecretaryAsync(tenant, registered, row, facts, ct).ConfigureAwait(false);
    }

    private async Task<(FactoryInboxWakeAct Act, DateTime? Again)> StartSecretaryAsync(TenantId tenant, RegisteredFactoryDto registered,
        FactoryInboxWakeEntity row, FactoryInboxWakeFacts facts, CancellationToken ct)
    {
        var factory = registered.Factory;
        var now = _env.NowUtc();
        PublishedFactoryDefinition? published;
        try
        {
            published = _env.Published(tenant, factory);
        }
        catch (InvalidOperationException ex)
        {
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Secretary, FactoryInboxWords.Waiting.DefinitionUnreadable, ex.Message);
        }
        if (published is null)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Secretary, FactoryInboxWords.Waiting.NotDefined, null);
        var withSecretary = FactoryInboxSecretary.WithSecretary(published.Definition);
        var seat = withSecretary is null ? null : FactoryInboxSecretary.SeatOf(published.Definition);
        if (withSecretary is null || seat is null)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Secretary, FactoryInboxWords.Waiting.DefinitionUnreadable,
                $"version {published.Version} names no Boss seat it holds");

        var machine = seat.MachineOrDefault(published.Definition.Defaults);
        if (_env.DirectorOn(tenant, machine) is not { } director)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Secretary, FactoryInboxWords.Waiting.NoDirector, machine);

        // THE CLAIM, taken before anything is sent: whoever updates the version it read starts the Secretary.
        if (!_wakes.TryUpdate(tenant, factory, row.Version, w =>
            {
                w.SecretarySessionId = null;
                w.SecretaryStartedAtUtc = now;
                w.SecretaryToldUpToUtc = null;
            }, now))
            return (FactoryInboxWakeAct.ClaimTakenByAnother, now.Add(LookAgain));

        var sent = facts.ToFile.Where(m => m.State == FactoryInboxWords.States.Sent).ToList();
        var told = facts.ToFile.Max(m => m.SentAtUtc);
        var request = FactorySeatStart.Build(new PublishedFactoryDefinition(published.Version, withSecretary), seat,
            FactoryInboxSecretary.SessionName(registered), FactoryInboxSecretary.Opening(registered, facts.ToFile.Count)).Request;
        Automated(request, director);
        FileLog.Write($"[FactoryInboxWake] starting the Secretary of {factory} on {machine} (director {director}), v{published.Version}, {facts.ToFile.Count} to file ({sent.Count} new)");
        var attempt = await StartSafeAsync(tenant, machine, request, ct).ConfigureAwait(false);
        return Started(tenant, factory, FactoryInboxWords.WaitingFor.Secretary, attempt, told);
    }

    // ---------------------------------------------------------------- the Boss

    private async Task<(FactoryInboxWakeAct Act, DateTime? Again)> BossAsync(TenantId tenant, RegisteredFactoryDto registered,
        FactoryInboxWakeFacts facts, CancellationToken ct)
    {
        if (facts.Unread.Count == 0) return (FactoryInboxWakeAct.Nothing, null);
        var now = _env.NowUtc();
        var factory = registered.Factory;
        var bossSeat = registered.BossSeat!.Trim();
        var row = _wakes.Row(tenant, factory, now);
        var sighting = Find(tenant, factory, bossSeat, row.BossSessionId, row.BossStartedAtUtc, now);

        if (sighting.State == SeatState.Starting)
            return (FactoryInboxWakeAct.Starting, now.Add(LookAgain));
        if (sighting.State == SeatState.Alive)
        {
            if (row.BossWaiting is not null)
                row = ClearSeatWaiting(tenant, factory, row, FactoryInboxWords.WaitingFor.Boss, now);
            var told = row.BossSessionId is not null && SameId(row.BossSessionId, sighting.SessionId) ? row.BossToldUpToUtc : null;
            var untold = facts.Unread.Where(b => told is null || b.HandedAtUtc > told).ToList();
            if (untold.Count == 0) return (FactoryInboxWakeAct.AlreadyTold, null);
            var rung = await RingAsync(tenant, factory, sighting, FactoryInboxSecretary.BossRingLine(facts.Unread.Count), ct).ConfigureAwait(false);
            if (!rung) return (FactoryInboxWakeAct.RingDeferred, now.Add(LookAgain));
            var newest = facts.Unread.Max(b => b.HandedAtUtc);
            _wakes.TryUpdate(tenant, factory, row.Version, w =>
            {
                w.BossSessionId = sighting.SessionId;
                w.BossToldUpToUtc = newest;
            }, now);
            return (FactoryInboxWakeAct.Rung, null);
        }

        // None alive: ONE Boss starts at once, as itself - the night-start seat kit.
        PublishedFactoryDefinition? published;
        try
        {
            published = _env.Published(tenant, factory);
        }
        catch (InvalidOperationException ex)
        {
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Boss, FactoryInboxWords.Waiting.DefinitionUnreadable, ex.Message);
        }
        if (published is null)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Boss, FactoryInboxWords.Waiting.NotDefined, null);
        var seat = published.Seat(bossSeat);
        var registeredSeat = registered.Seats.FirstOrDefault(s => string.Equals(s.Id, bossSeat, StringComparison.OrdinalIgnoreCase));
        if (seat is null || registeredSeat is null)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Boss, FactoryInboxWords.Waiting.DefinitionUnreadable,
                seat is null ? FactorySeatStart.SeatNotInVersion(published, bossSeat) : $"the Boss seat '{bossSeat}' is not registered");
        var machine = seat.MachineOrDefault(published.Definition.Defaults);
        if (_env.DirectorOn(tenant, machine) is not { } director)
            return Wait(tenant, factory, row, FactoryInboxWords.WaitingFor.Boss, FactoryInboxWords.Waiting.NoDirector, machine);

        if (!_wakes.TryUpdate(tenant, factory, row.Version, w =>
            {
                w.BossSessionId = null;
                w.BossStartedAtUtc = now;
                w.BossToldUpToUtc = null;
            }, now))
            return (FactoryInboxWakeAct.ClaimTakenByAnother, now.Add(LookAgain));

        var toldUpTo = facts.Unread.Max(b => b.HandedAtUtc);
        var request = FactorySeatStart.Build(published, seat, FactoryInboxSecretary.BossSessionName(registered),
            FactoryInboxSecretary.BossOpening(registered, registeredSeat, published.BriefOf(seat), facts.Unread.Count)).Request;
        Automated(request, director);
        FileLog.Write($"[FactoryInboxWake] starting the Boss of {factory} ({seat.Seat}) on {machine} (director {director}), v{published.Version}, {facts.Unread.Count} unread bundles");
        var attempt = await StartSafeAsync(tenant, machine, request, ct).ConfigureAwait(false);
        return Started(tenant, factory, FactoryInboxWords.WaitingFor.Boss, attempt, toldUpTo);
    }

    // ---------------------------------------------------------------- shared

    private enum SeatState { None, Starting, Alive }

    private sealed record Sighting(SeatState State, string? DirectorId, string? SessionId, string? ActivityState);

    /// <summary>
    /// Is the seat alive in this factory? A session of the factory with this seat and a seat kit that has not exited, in
    /// the fresh roster - the one this last started first; else the one this last started, by what its Director last
    /// pushed; else a start this holds that has not been reported yet, inside the grace.
    /// </summary>
    private Sighting Find(TenantId tenant, string factory, string seat, string? rememberedSessionId, DateTime? claimedAtUtc, DateTime now)
    {
        var live = _env.Roster(tenant)
            .Where(r => r.Session is { } s && !FleetManagerSessions.IsGone(s)
                        && string.Equals(s.Factory?.Trim(), factory, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.FactorySeat?.Trim(), seat, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(s.FactoryKit?.Trim(), FactoryKits.Seat, StringComparison.Ordinal))
            .OrderBy(r => SameId(r.Session.SessionId, rememberedSessionId) ? 0 : 1)
            .ThenBy(r => r.Session.SessionId, StringComparer.Ordinal)
            .ToList();
        if (live.Count > 0)
            return new Sighting(SeatState.Alive, live[0].DirectorId, live[0].Session.SessionId, live[0].Session.ActivityState);

        var inGrace = claimedAtUtc is { } claimed && now - DateTime.SpecifyKind(claimed, DateTimeKind.Utc) < StartGrace;
        if (rememberedSessionId is not null)
        {
            var last = _env.LastKnown(tenant, rememberedSessionId);
            // Absence is not death: a session whose Director is only disconnected is still alive. It cannot be rung until
            // its Director is back, and nothing else is started meanwhile.
            if (last is { } known)
                return FleetManagerSessions.IsGone(known.Session)
                    ? new Sighting(SeatState.None, null, null, null)
                    : new Sighting(SeatState.Alive, known.DirectorId, rememberedSessionId, known.Session.ActivityState);
            // Not reported at all - seconds old, or the Gateway restarted and its Director has not pushed yet. Only a
            // recorded ending lets a second one start; until then it is alive and cannot be rung.
            if (!_env.HasEnded(tenant, rememberedSessionId))
                return inGrace
                    ? new Sighting(SeatState.Starting, null, rememberedSessionId, null)
                    : new Sighting(SeatState.Alive, null, rememberedSessionId, null);
            return new Sighting(SeatState.None, null, null, null);
        }
        return inGrace ? new Sighting(SeatState.Starting, null, null, null) : new Sighting(SeatState.None, null, null, null);
    }

    /// <summary>
    /// Ring a live seat with one line, only when it waits for a prompt. The roster saying it works means nothing is asked;
    /// otherwise the Director decides, at the moment it would type, and refuses a session that works or holds the owner's
    /// draft. True only when the Director typed it.
    /// </summary>
    private async Task<bool> RingAsync(TenantId tenant, string factory, Sighting seat, string line, CancellationToken ct)
    {
        if (seat.DirectorId is null || seat.SessionId is null) return false;
        if (!WaitsForAPrompt(seat.ActivityState))
        {
            FileLog.Write($"[FactoryInboxWake] {factory}: not ringing {seat.SessionId} - it is {seat.ActivityState ?? "in no known state"}; looking again shortly");
            return false;
        }
        FleetManagerPromptSend sent;
        try
        {
            sent = await _env.RingAsync(tenant, seat.DirectorId, seat.SessionId, line, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            FileLog.Write($"[FactoryInboxWake] {factory}: ringing {seat.SessionId} FAILED: {ex.Message}");
            return false;
        }
        FileLog.Write($"[FactoryInboxWake] {factory}: rang {seat.SessionId} on {seat.DirectorId}: {sent} (\"{line}\")");
        return sent == FleetManagerPromptSend.Accepted;
    }

    /// <summary>Whether the pushed state is one a ring may be asked for: the turn has ended and the agent waits for a prompt.</summary>
    internal static bool WaitsForAPrompt(string? activityState)
        => string.Equals(activityState, nameof(ActivityState.WaitingForInput), StringComparison.OrdinalIgnoreCase)
           || string.Equals(activityState, nameof(ActivityState.Idle), StringComparison.OrdinalIgnoreCase);

    private async Task<TriggerStartAttempt> StartSafeAsync(TenantId tenant, string machine, NewSessionRequest request, CancellationToken ct)
    {
        try
        {
            return await _env.StartAsync(tenant, machine, request, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Cut by the Gateway stopping, the create may already be on its way: the claim is held, as for any unknown.
            return TriggerStartAttempt.Unknown("the Gateway stopped while the session was being started.");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[FactoryInboxWake] the start on {machine} threw {ex.GetType().Name}: {ex.Message}");
            return TriggerStartAttempt.Failed(ex.Message);
        }
    }

    /// <summary>
    /// Record what a start this claimed came to. Started: the session is remembered and told of everything up to
    /// <paramref name="toldUpTo"/>. Unknown: the claim is held for the grace, so nothing else starts while the Director may
    /// still create it. Failed: the claim is released and the factory waits, saying why, until a later look starts it.
    /// The claim is this writer's, so the row is written whatever its version now is.
    /// </summary>
    private (FactoryInboxWakeAct Act, DateTime? Again) Started(TenantId tenant, string factory, FactoryInboxWords.WaitingFor who,
        TriggerStartAttempt attempt, DateTime toldUpTo)
    {
        var now = _env.NowUtc();
        var secretary = who == FactoryInboxWords.WaitingFor.Secretary;
        if (!string.IsNullOrEmpty(attempt.SessionId))
        {
            WriteOwned(tenant, factory, w =>
            {
                if (secretary)
                {
                    w.SecretarySessionId = attempt.SessionId;
                    w.SecretaryToldUpToUtc = toldUpTo;
                    w.SecretaryWaiting = null;
                    w.SecretaryWaitingDetail = null;
                }
                else
                {
                    w.BossSessionId = attempt.SessionId;
                    w.BossToldUpToUtc = toldUpTo;
                    w.BossWaiting = null;
                    w.BossWaitingDetail = null;
                }
            }, now);
            FileLog.Write($"[FactoryInboxWake] {factory}: started the {who} {attempt.SessionId}");
            return (FactoryInboxWakeAct.Started, null);
        }
        if (attempt.OutcomeUnknown)
        {
            FileLog.Write($"[FactoryInboxWake] {factory}: the {who}'s start has an unknown outcome ({attempt.Error}); the claim is held for {StartGrace.TotalMinutes} minutes");
            return (FactoryInboxWakeAct.StartOutcomeUnknown, now.Add(LookAgain));
        }
        var error = attempt.Error ?? "the machine did not return a session";
        WriteOwned(tenant, factory, w =>
        {
            if (secretary)
            {
                w.SecretaryStartedAtUtc = null;
                w.SecretaryWaiting = FactoryInboxWords.Waiting.StartFailed;
                w.SecretaryWaitingDetail = Clip(error);
            }
            else
            {
                w.BossStartedAtUtc = null;
                w.BossWaiting = FactoryInboxWords.Waiting.StartFailed;
                w.BossWaitingDetail = Clip(error);
            }
        }, now);
        FileLog.Write($"[FactoryInboxWake] {factory}: the {who} did not start: {error}");
        return (FactoryInboxWakeAct.StartFailed, now.Add(LookAgain));
    }

    /// <summary>Write a change to a row whose claim this writer holds: read the version now and write on it.</summary>
    private void WriteOwned(TenantId tenant, string factory, Action<FactoryInboxWakeEntity> change, DateTime now)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var row = _wakes.Row(tenant, factory, now);
            if (_wakes.TryUpdate(tenant, factory, row.Version, change, now)) return;
        }
        throw new InvalidOperationException($"The wake row of '{factory}' kept changing underneath its own claim's write.");
    }

    /// <summary>Nothing could be started: record why, so the thread says it, and look again on the heartbeat.</summary>
    private (FactoryInboxWakeAct Act, DateTime? Again) Wait(TenantId tenant, string factory, FactoryInboxWakeEntity row,
        FactoryInboxWords.WaitingFor who, string code, string? detail)
    {
        var now = _env.NowUtc();
        var clipped = detail is null ? null : Clip(detail);
        var same = who == FactoryInboxWords.WaitingFor.Secretary
            ? row.SecretaryWaiting == code && row.SecretaryWaitingDetail == clipped
            : row.BossWaiting == code && row.BossWaitingDetail == clipped;
        if (!same)
        {
            _wakes.TryUpdate(tenant, factory, row.Version, w =>
            {
                if (who == FactoryInboxWords.WaitingFor.Secretary)
                {
                    w.SecretaryWaiting = code;
                    w.SecretaryWaitingDetail = clipped;
                }
                else
                {
                    w.BossWaiting = code;
                    w.BossWaitingDetail = clipped;
                }
            }, now);
            FileLog.Write($"[FactoryInboxWake] {factory}: the {who} waits: {FactoryInboxWords.WaitingLabel(who, code, clipped)}");
        }
        return (FactoryInboxWakeAct.Waiting, now.Add(HeartbeatInterval));
    }

    /// <summary>A seat is alive again: its waiting sentence is cleared. Returns the row as it now is.</summary>
    private FactoryInboxWakeEntity ClearSeatWaiting(TenantId tenant, string factory, FactoryInboxWakeEntity row,
        FactoryInboxWords.WaitingFor who, DateTime now)
    {
        _wakes.TryUpdate(tenant, factory, row.Version, w =>
        {
            if (who == FactoryInboxWords.WaitingFor.Secretary)
            {
                w.SecretaryWaiting = null;
                w.SecretaryWaitingDetail = null;
            }
            else
            {
                w.BossWaiting = null;
                w.BossWaitingDetail = null;
            }
        }, now);
        return _wakes.Row(tenant, factory, now);
    }

    /// <summary>Nothing waits any more: a waiting sentence left on the row is cleared.</summary>
    private void ClearWaiting(TenantId tenant, string factory)
    {
        if (_wakes.Find(tenant, factory) is not { } row) return;
        if (row.SecretaryWaiting is null && row.BossWaiting is null) return;
        _wakes.TryUpdate(tenant, factory, row.Version, w =>
        {
            w.SecretaryWaiting = null;
            w.SecretaryWaitingDetail = null;
            w.BossWaiting = null;
            w.BossWaitingDetail = null;
        }, _env.NowUtc());
    }

    /// <summary>A start the Gateway makes by itself: pinned to the Director it found on the computer, recorded as an
    /// automated start from a Gateway trigger, and never closed for it - the seat closes itself when it is done.</summary>
    private static void Automated(NewSessionRequest request, string director)
    {
        request.Director = director;
        request.Origin = SessionOriginKinds.Schedule;
        request.OriginSurface = SessionOriginSurfaces.Trigger;
        request.ControllerSessionId = null;
        request.ParentSessionId = null;
        request.AutoDismiss = false;
    }

    private static DateTime? Earliest(DateTime? a, DateTime? b)
        => a is null ? b : b is null ? a : (a < b ? a : b);

    private static bool SameId(string? a, string? b)
        => !string.IsNullOrEmpty(a) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string Clip(string s) => s.Length <= 1000 ? s : s[..997] + "...";
}
