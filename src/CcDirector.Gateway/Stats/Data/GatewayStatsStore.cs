using CcDirector.Core.Storage;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// What the statistics surface should say about itself, folded ONCE here and read verbatim by whoever
/// renders it. A client never re-derives this and never guesses: it renders what the Gateway decided.
/// </summary>
/// <param name="IsAvailable">Whether the statistics store is usable at all.</param>
/// <param name="Reason">The named reason it is not, or <see cref="StatsStoreUnavailableReason.None"/>.</param>
/// <param name="ReasonCode">The stable, machine-readable spelling of <paramref name="Reason"/> - what a
/// surface keys off and what an operator greps a log for. Never re-spelled per surface.</param>
/// <param name="Detail">A one-line operator-facing explanation, naming the environment variable involved
/// when one is. Safe to log and to serve: never a connection string and never a credential.</param>
/// <param name="Source">Where the connection came from - and in particular whether it was DERIVED or came
/// from an EXPLICIT override. On the surface deliberately: silent following is only dangerous while it is
/// silent.</param>
/// <param name="Target">A credential-free description of what was selected.</param>
public sealed record StatsStoreAvailability(
    bool IsAvailable,
    StatsStoreUnavailableReason Reason,
    string ReasonCode,
    string Detail,
    StatsConnectionSource Source,
    string Target);

/// <summary>
/// THE FAILURE-DOMAIN BOUNDARY around the Gateway's statistics store. It owns the statistics context's
/// connection, its provider selection and its migration chain - and it CONTAINS every failure in all three,
/// so that none of them can stop the Gateway from starting or from serving.
///
/// WHY THIS EXISTS, AND IT IS NOT A HYPOTHETICAL. On 2026-07-30 the hosted Gateway answered HTTP 500 to
/// every client for thirty-two minutes because a statistics fault propagated out of the roster handler.
/// Removing SQLite from the hosted Gateway must not replace that with a NEW way for statistics to take the
/// whole Gateway down - a statistics database that cannot be reached at startup would do exactly that, one
/// layer earlier and even more completely, because the process would never bind its port at all.
///
/// THIS IS A BOUNDARY, NOT A FALLBACK, AND THE DIFFERENCE IS THE WHOLE POINT. A fallback HIDES a fault by
/// quietly serving something else; a boundary CONTAINS a fault and NAMES it. There is no substitute store
/// here, no alternative path, and no invented data - not a zero, not an empty series, not a stale figure.
/// The statistics surface is simply off, loudly, with the reason named, and everything else keeps working.
/// If anybody ever adds "and if PostgreSQL is down, use a file", that is the fallback this class exists
/// instead of.
///
/// WHAT IS DELIBERATELY NOT TOUCHED. The main <see cref="CcDirector.Gateway.Data.GatewayDatabase"/> keeps its current
/// fatal-on-failure startup behaviour. That is correct and it is load-bearing: it carries the roster, the
/// devices and the tenants, and a Gateway that came up without it would serve wrong answers rather than no
/// answers. Only statistics are contained.
///
/// THE TWO MIGRATION CHAINS NEVER SHARE A TRANSACTION OR A STARTUP GATE. Separate context, separate
/// connection, separate pool, separate schema, separate history table - and separately, this chain does not
/// gate anything. Coupling them would recreate the shared failure domain that separating the contexts
/// existed to avoid, so it is worth saying plainly: this store must never be constructed inside the main
/// database's try block, and its result must never be an input to whether the Gateway starts.
///
/// THE NAMED STATES, AND THE DISTINCTION IS AN ARCHITECT RULING. NOT CONFIGURED, UNREACHABLE and INCOMPLETE
/// SCHEMA are different reasons in the log, on the health surface and in the failure state, because they send
/// the person fixing them to three different places: a setting, a database or network, and the store's own
/// disk. A deploy that simply forgot a variable would otherwise present identically to a database outage, and
/// a half-built schema would present as an outage while the database sits there perfectly healthy - either
/// way the next incident is spent looking somewhere the fault is not.
///
/// Threading: contexts come from a pooled factory (a fresh one per operation, never shared across threads),
/// exactly as the main database hands them out.
/// </summary>
public sealed class GatewayStatsStore : IDisposable
{
    /// <summary>The observer identifier this store reports its own health under.</summary>
    public const string ObserverName = "statistics-store";

    /// <summary>
    /// The longest the statistics store may spend opening and migrating before startup gives up on it and
    /// carries on WITHOUT it.
    ///
    /// A bound rather than a wait, because "non-fatal" is not the same as "harmless". A hosted Gateway has a
    /// platform startup deadline measured in low hundreds of seconds, and that deadline sits in front of the
    /// port bind: a statistics migration that merely BLOCKS long enough would take the site down just as
    /// completely as one that threw, and it would do it without a single exception in the log. So the
    /// containment is on the clock as well as on the exception.
    ///
    /// IT BOUNDS STARTUP, NOT THE STORE'S LIFETIME, and that distinction is the whole design. Exceeding it
    /// does NOT abandon the store: the attempt keeps running and PUBLISHES when it finishes, so a slow store
    /// costs the first seconds of ONE BOOT rather than everything after it.
    ///
    /// WHY THE NUMBER CAME DOWN RATHER THAN UP. The obvious response to "the inner call took longer than the
    /// deadline" is to wait longer. That was the wrong instinct and it is worth writing down why: once a late
    /// arrival can publish, WAITING BUYS NOTHING. A longer wait only delays the port bind on every boot -
    /// including the overwhelming majority where the store is healthy - to buy an earlier statistics surface
    /// in the rare slow one, which the late arrival now delivers anyway. So the deadline should be as SHORT
    /// as the inner bound allows.
    ///
    /// DERIVED FROM THE INNER BOUND, NOT PICKED. The adoption step bounds its own write wait, and this must
    /// sit outside that with margin or the inner bound is useless - the caller would stop waiting first and
    /// report a generic failure over a named one, which is precisely the operator misdirection this work
    /// exists to remove. The relationship is what matters and it is ASSERTED BY A TEST rather than kept as a
    /// pair of literals that agree today: see <c>StatsStoreDeadlineRelationshipTests</c>. Any measured number
    /// will move, so a test that pins two constants would go stale the first time either does.
    /// </summary>
    public static readonly TimeSpan OpenDeadline = TimeSpan.FromSeconds(8);

    /// <summary>
    /// How long to wait before each successive attempt to reopen a store that COULD NOT BE REACHED, with the
    /// last value repeating for as long as the process runs.
    ///
    /// WHY THIS EXISTS. The open used to happen exactly once. A store that was merely SLOW was covered - the
    /// attempt outlives the startup deadline and publishes when it finishes - but a store that was briefly
    /// UNREACHABLE was not: the failure latched, and statistics stayed dead for the life of the container
    /// with no retry anywhere. That is not a theoretical gap. On 2 September 2026 a deploy ran production and
    /// staging together for four minutes; each container opens its own pool, the pooler refused the incoming
    /// container's statistics connection, and Your Throttle answered 503 for hours while every turn the owner
    /// drove went unrecorded - on a database that was healthy the whole time and answered on the next
    /// connection anyone made to it.
    ///
    /// A TRANSIENT FAILURE MUST NOT BE PERMANENT, and one reachability test is not evidence about the next
    /// minute. So a store that could not be reached is retried until it can be, and the surface comes back on
    /// its own with no restart - the same promise the late arrival already makes for a slow store, kept for an
    /// unreachable one too.
    ///
    /// BACKED OFF, AND THEN STEADY RATHER THAN STOPPED. The early attempts are close together because the
    /// common case is a pooler that refused one connection during a deploy and will accept the next one
    /// seconds later. It settles at one attempt a minute and stays there: a database can come back at any
    /// hour, an attempt is a single connection that costs nothing measurable beside the roster traffic
    /// already flowing, and a store that gives up after N tries is the same permanent failure this exists to
    /// remove, just with a longer fuse. Nothing here retries a store that is UNCONFIGURED, one that failed
    /// inside our OWN code, or one whose adoption verdict says this build cannot use it - see
    /// <see cref="ScheduleReopen"/> for why each of those is a different answer, not a slower one.
    /// </summary>
    internal static readonly IReadOnlyList<TimeSpan> ReopenBackoff = new[]
    {
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(40),
        TimeSpan.FromSeconds(60),
    };

    private readonly StatsFailureCounters _health = new(ObserverName);
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private ServiceProvider? _provider;
    private StatsStoreAvailability _availability;
    private IDbContextFactory<GatewayStatsDbContext>? _factory;
    private bool _disposed;
    private bool _reopening;
    private readonly TimeProvider _time;
    private int _reopenWaits;
    private int _reopenAttempts;

    /// <summary>
    /// What the statistics surface should say about itself. Folded once, here.
    ///
    /// READ UNDER THE LOCK because a LATE ARRIVAL can change it: an open that exceeded the startup deadline
    /// keeps running and publishes when it finishes, so this is not write-once. See <see cref="OpenDeadline"/>.
    /// </summary>
    public StatsStoreAvailability Availability
    {
        get { lock (_gate) return _availability; }
    }

    /// <summary>This store's own health, in the shape the statistics failure surface consumes.</summary>
    public IStatsFailureState Health => _health;

    /// <summary>
    /// How many backoff waits the reopen loop has BEGUN. The loop arms its timer before this moves, so a test that
    /// drives a fake clock waits for it to move and then advances - the advance reaches a timer that exists. An
    /// advance made before the timer is armed is simply lost, and a test built that way passes or fails on
    /// scheduling luck.
    /// </summary>
    internal int ReopenWaits => Volatile.Read(ref _reopenWaits);

    /// <summary>
    /// How many reopen attempts have FINISHED, successful or not. It moves after the attempt has published whatever
    /// it had to publish, so a reader that sees it move also sees the availability that attempt left behind.
    /// </summary>
    internal int ReopenAttempts => Volatile.Read(ref _reopenAttempts);

    /// <summary>Whether a reopen loop is running. A store with nothing configured never starts one.</summary>
    internal bool ReopenScheduled
    {
        get { lock (_gate) return _reopening; }
    }

    /// <summary>
    /// The pooled statistics context factory, or NULL when the store is unavailable.
    ///
    /// Nullable on purpose, so a consumer cannot use it without having decided what to do when there is no
    /// store. That decision is always the same and it is never a substitute store: record a DROP on the
    /// consumer's own failure state and carry on. The type system asking the question is what stops the
    /// answer from being forgotten on the one path that runs during an incident.
    ///
    /// It can go from null to non-null ONCE, when a late arrival publishes. It never goes the other way, so a
    /// caller that has obtained a factory keeps a usable one; only Dispose ends that.
    /// </summary>
    public IDbContextFactory<GatewayStatsDbContext>? Factory
    {
        get { lock (_gate) return _factory; }
    }

    /// <summary>Whether the statistics store is usable.</summary>
    public bool IsAvailable => Availability.IsAvailable;

    /// <summary>
    /// Build the statistics store from the environment: the optional
    /// <see cref="StatsConnectionSelection.StatsConnectionEnvVar"/> override, else derivation from
    /// <see cref="CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar"/>, else the local self-host file.
    /// </summary>
    /// <param name="sqlitePath">The self-host statistics file. Defaults to gateway-stats.db under the
    /// storage root - the same file the hand-rolled store has always used. Never opened on a hosted
    /// Gateway.</param>
    /// <param name="hosted">Whether this is a hosted Gateway - the owning host's <see cref="GatewayHost.Hosted"/>.
    /// REQUIRED (see <see cref="GatewayHostOptions"/>). A hosted Gateway NEVER opens a statistics file.</param>
    public static GatewayStatsStore FromEnvironment(bool hosted, string? sqlitePath = null)
    {
        var path = string.IsNullOrWhiteSpace(sqlitePath)
            ? Path.Combine(CcStorage.Root(), "gateway-stats.db")
            : sqlitePath!;

        // Hosted is read from BOTH signals, and either one is enough. The host's hosted flag comes from a runtime
        // environment variable that a slot swap or a config restore can drop; IsHostedImage is part of the
        // published artifact and cannot be. Taking either means a hosted container that lost its environment
        // variable still refuses to open a statistics file, which is the direction that matters: the cost of
        // being wrong towards "hosted" is an unavailable statistics surface with a named reason, and the cost
        // of being wrong the other way is a hosted Gateway writing a database onto ephemeral or shared disk.
        var isHosted = hosted || GatewayHostedMode.IsHostedImage;

        var choice = StatsConnectionSelection.Resolve(
            Environment.GetEnvironmentVariable(StatsConnectionSelection.StatsConnectionEnvVar),
            Environment.GetEnvironmentVariable(CcDirector.Gateway.Data.GatewayDatabase.PostgresConnectionEnvVar),
            isHosted,
            path);

        return new GatewayStatsStore(choice);
    }

    /// <summary>
    /// Build the statistics store over an already-chosen connection.
    /// </summary>
    /// <param name="choice">The chosen connection, or the named reason there is not one.</param>
    /// <param name="time">The clock the reopen backoff waits on. Production passes nothing and gets the system
    /// clock. A test passes a fake clock and advances it, so the backoff is proven without being sat out: the
    /// schedule is seconds to a minute per step, and four tests once slept through it for 55 seconds.</param>
    /// <exception cref="ArgumentNullException"><paramref name="choice"/> is null. A caller contract
    /// violation - a programming error, not a state a deployment can be in - so it throws.</exception>
    public GatewayStatsStore(StatsConnectionChoice choice, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(choice);
        _time = time ?? TimeProvider.System;

        if (!choice.IsConfigured)
        {
            // NOT CONFIGURED. Non-fatal, and LOUD: logged once at startup with the variable named, so an
            // operator reading the log finds the setting rather than starting a network investigation.
            _availability = Unavailable(choice.Reason, choice.Detail, choice.Source, choice.Target);
            _health.RecordFailure(choice.Detail);
            FileLog.Write(
                $"[GatewayStatsStore] Open: statistics are UNAVAILABLE, reason={Availability.ReasonCode}: " +
                $"{choice.Detail} The Gateway starts and serves normally without them.");
            return;
        }

        FileLog.Write(
            $"[GatewayStatsStore] Open: source={choice.Source} target={choice.Target} - " +
            "the statistics migration and its connection failures are NON-FATAL to startup.");

        ServiceProvider? provider = null;
        try
        {
            provider = BuildProvider(choice);
            var factory = provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();

            // Open and migrate WITHIN A BOUND. "Non-fatal" is not the same as "harmless": a hosted Gateway
            // has a platform startup deadline sitting in front of the port bind, so a statistics database
            // that accepts a connection and then never answers would take the site down just as completely
            // as one that threw - and would do it without a single exception in the log. The containment is
            // therefore on the clock as well as on the exception.
            //
            // Task.WhenAny rather than Task.Wait(timeout) so a FAULTED attempt does not surface here as an
            // AggregateException: the real exception is unwrapped by GetResult below and reaches the catch
            // with its own type name, which is what the operator-facing message reports.
            var work = Task.Run(() => OpenAndMigrate(factory, choice));
            var finished = Task.WhenAny(work, Task.Delay(OpenDeadline)).GetAwaiter().GetResult() == work;
            if (!finished)
            {
                // TIMED OUT - AND THE ATTEMPT KEEPS RUNNING AND WILL PUBLISH WHEN IT FINISHES.
                //
                // The deadline bounds STARTUP, not the store's lifetime. That distinction is the whole fix:
                // an abandoned-and-discarded attempt made a store that was merely SLOW unavailable for the
                // LIFE OF THE PROCESS, because only the code below assigned the factory and this path never
                // reached it. Now boot proceeds without statistics exactly as before, the attempt continues,
                // and when it succeeds it publishes - so a slow store costs the first seconds of ONE BOOT
                // instead of everything after it. The pathological case does not become less likely; it
                // stops existing.
                //
                // Still ABANDONED rather than cancelled: a migration in flight must not be torn out from
                // under itself.
                var late = provider;
                provider = null;
                _ = work.ContinueWith(t => PublishLateArrival(t, late, choice), TaskScheduler.Default);

                var timedOut =
                    $"The statistics database ({choice.Target}) did not answer within " +
                    $"{OpenDeadline.TotalSeconds:0} seconds, so the Gateway finished starting without it. " +
                    "This is NOT a sign that the database is unreachable - the attempt is STILL RUNNING, and " +
                    "if it succeeds the statistics surface will come up on its own with no restart needed. " +
                    "Wait and re-check before investigating anything. The Gateway is serving normally and " +
                    "the rest of it is unaffected.";
                _availability = Unavailable(
                    StatsStoreUnavailableReason.DidNotAnswerInTime, timedOut, choice.Source, choice.Target);
                _health.RecordFailure(timedOut);
                FileLog.Write(
                    $"[GatewayStatsStore] Open TIMED OUT (CONTAINED) after {OpenDeadline.TotalSeconds:0} " +
                    $"second(s): source={choice.Source} target={choice.Target}. Statistics are UNAVAILABLE, " +
                    $"reason={Availability.ReasonCode}. The Gateway starts and serves normally without them.");
                return;
            }

            var prepared = work.GetAwaiter().GetResult();
            if (!prepared.IsUsable)
            {
                // The store was reached and interrogated, and it is not one this build can use - an
                // adoption verdict from the self-host path, with its own named reason. Nothing has been
                // changed on disk. Released here rather than held: an unusable store's pooled connections
                // are of no use to anybody.
                provider.Dispose();
                _availability = Unavailable(prepared.Reason, prepared.Detail, choice.Source, choice.Target);
                _health.RecordFailure(prepared.Detail);
                FileLog.Write(
                    $"[GatewayStatsStore] Open: statistics are UNAVAILABLE, reason={Availability.ReasonCode}: " +
                    $"{prepared.Detail}");
                return;
            }

            _provider = provider;
            _factory = factory;
            _availability = new StatsStoreAvailability(
                IsAvailable: true,
                Reason: StatsStoreUnavailableReason.None,
                ReasonCode: "available",
                Detail: choice.Detail,
                Source: choice.Source,
                Target: choice.Target);

            FileLog.Write(
                $"[GatewayStatsStore] Open: ready, source={choice.Source} target={choice.Target}");
        }
        catch (Exception ex)
        {
            // UNREACHABLE. THIS CATCH IS THE BOUNDARY, and it is the reason this class exists: every way the
            // statistics database can fail to open or migrate arrives here, and none of them leaves.
            //
            // The provider is disposed rather than leaked - the constructor is not completing successfully,
            // so nothing else will ever dispose it, and its pooled connections would be held for the life of
            // the process.
            //
            // The exception's MESSAGE is never used. A provider echoes a malformed connection string back in
            // its message, so putting it on a health surface or in a log would publish a credential; the
            // exception TYPE plus the already-redacted target is enough to diagnose, and the full exception
            // is not stringified anywhere.
            provider?.Dispose();

            // OUR FAULT OR THEIRS. A boundary that catches everything cannot, by itself, tell a database
            // that is not there from a bug in this file - and if it guesses "database", every programming
            // error in here gets a plausible infrastructure label and sends the reader somewhere the fault
            // is not. So the failure is CLASSIFIED before it is named.
            var theirs = IsStorageFailure(ex);

            var detail = theirs
                ? $"The statistics database ({choice.Target}) could not be opened or migrated " +
                  $"({ex.GetType().Name}). The settings name a database, so this is a database or network " +
                  "problem rather than a missing setting. The Gateway is retrying it until it answers, so " +
                  "the statistics surface will come up on its own with no restart needed. The Gateway is " +
                  "serving normally and the rest of it is unaffected."
                : "Statistics are unavailable because something in DevThrottle's own code failed while " +
                  $"opening the statistics store ({ex.GetType().Name}). This is a fault in DevThrottle, NOT " +
                  "a problem with your database, your network or your settings - checking those will not " +
                  "help, and reporting it to us will. The details are in the Gateway log. The Gateway is " +
                  "serving normally and the rest of it is unaffected.";

            _availability = Unavailable(
                theirs ? StatsStoreUnavailableReason.Unreachable : StatsStoreUnavailableReason.InternalError,
                detail, choice.Source, choice.Target);
            _health.RecordFailure(detail);

            // THEIRS IS RETRIED, OURS IS NOT. A database or network that refused this connection may accept
            // the next one; a fault in our own code throws identically every time, and looping on it would
            // bury the report that gets it fixed. See ScheduleReopen.
            if (theirs) ScheduleReopen(choice);

            // The STACK is logged for our own faults and not for theirs. For a storage failure the type and
            // the redacted target are the whole diagnosis and a stack is noise; for a fault in our code the
            // stack IS the diagnosis, and without it the operator's report reaches us saying only that
            // something failed. The exception MESSAGE is still never used on either path - a provider echoes
            // a malformed connection string back in its message, and putting that on a surface or in a log
            // would publish a credential.
            FileLog.Write(theirs
                ? $"[GatewayStatsStore] Open FAILED (CONTAINED): source={choice.Source} " +
                  $"target={choice.Target}: {ex.GetType().Name}. Statistics are UNAVAILABLE, " +
                  $"reason={Availability.ReasonCode}. The Gateway starts and serves normally without them."
                : $"[GatewayStatsStore] Open FAILED (CONTAINED) - INTERNAL ERROR, THIS IS OUR BUG: " +
                  $"source={choice.Source} target={choice.Target}: {ex.GetType().FullName}. Statistics are " +
                  $"UNAVAILABLE, reason={Availability.ReasonCode}. The Gateway starts and serves normally " +
                  $"without them. Stack:{Environment.NewLine}{ex.StackTrace}");
        }
    }

    /// <summary>
    /// THE LATE ARRIVAL. An open that outlived the startup deadline has finished; publish it if it succeeded,
    /// release it if it did not.
    ///
    /// This runs on a thread pool thread, long after the constructor returned and the Gateway began serving,
    /// so everything it touches is behind the same lock the readers use. It is the only place other than the
    /// constructor that may make the store available.
    ///
    /// DISPOSE RACES IT AND MUST WIN. If the store was disposed while this attempt was still running there is
    /// nothing left to publish into, so the provider is released and nothing is assigned - otherwise a
    /// disposed store would quietly become available again and hand out contexts over a provider nobody owns.
    /// </summary>
    private void PublishLateArrival(
        Task<StatsStoreAdoptionResult> attempt, ServiceProvider late, StatsConnectionChoice choice)
    {
        StatsStoreAdoptionResult? prepared = null;
        string? failure = null;

        // Read the outcome OUTSIDE the lock: it can throw, and a fault must not be observed while holding a
        // lock the readers need.
        if (attempt.IsCompletedSuccessfully)
            prepared = attempt.Result;
        else
            failure = attempt.Exception?.GetBaseException().GetType().Name ?? "unknown";

        lock (_gate)
        {
            if (_disposed)
            {
                late.Dispose();
                FileLog.Write(
                    "[GatewayStatsStore] Late arrival DISCARDED: the store was disposed while the open was " +
                    "still running, so there is nothing to publish into.");
                return;
            }

            if (prepared is { IsUsable: true })
            {
                _provider = late;
                _factory = late.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();
                _availability = new StatsStoreAvailability(
                    IsAvailable: true,
                    Reason: StatsStoreUnavailableReason.None,
                    ReasonCode: "available",
                    Detail: choice.Detail,
                    Source: choice.Source,
                    Target: choice.Target);

                FileLog.Write(
                    $"[GatewayStatsStore] LATE ARRIVAL PUBLISHED: the statistics store finished opening " +
                    $"after the {OpenDeadline.TotalSeconds:0}-second startup deadline and is now AVAILABLE " +
                    $"({choice.Target}). No restart was needed.");
                return;
            }

            // It finished and it is not usable - either an unusable store with its own named reason, or a
            // throw. Released rather than held: an unusable store's pooled connections are of no use, and
            // the availability already on the surface stays as it is unless there is something better to say.
            late.Dispose();

            var detail = prepared?.Detail ??
                $"The statistics store ({choice.Target}) failed after the startup deadline ({failure}). " +
                "The Gateway is retrying it until it answers, and the statistics surface will come up on " +
                "its own with no restart needed.";
            var reason = prepared?.Reason ?? StatsStoreUnavailableReason.Unreachable;
            _availability = Unavailable(reason, detail, choice.Source, choice.Target);
            _health.RecordFailure(detail);

            FileLog.Write(
                $"[GatewayStatsStore] Late arrival FAILED (CONTAINED): {_availability.ReasonCode}: {detail}");

            // A THROW is a reachability failure and is retried; an adoption VERDICT is not. prepared is null
            // exactly when the attempt faulted, which is the case this incident came from.
            if (prepared is null) ScheduleReopen(choice);
        }
    }

    /// <summary>
    /// Start retrying an open that failed because the store COULD NOT BE REACHED, until it can be.
    ///
    /// ONLY UNREACHABLE IS RETRIED, and the three states this deliberately excludes are excluded because
    /// retrying them is not a slower version of the right answer, it is the wrong answer repeated:
    ///
    ///  - NOT CONFIGURED. No setting names a database. There is nothing to connect to, and a thousand
    ///    attempts do not produce a connection string. The operator has to set it, and the log already
    ///    names the variable.
    ///  - INTERNAL ERROR. Something in OUR code threw while opening the store. A defect is not a timing
    ///    artefact; it will throw again on every attempt, and turning it into a permanent background loop
    ///    buries the one report that would get it fixed.
    ///  - AN ADOPTION VERDICT. The store was reached and interrogated, and this build says it cannot use
    ///    what it found. That is a considered answer about the store's CONTENTS, and it does not change
    ///    because we ask a second time.
    ///
    /// At most ONE loop ever runs, and it is the only thing other than the constructor and the late arrival
    /// that may make the store available. It ends on success or on Dispose - never on a retry count; see
    /// <see cref="ReopenBackoff"/> for why stopping would reintroduce exactly the failure it exists to fix.
    /// </summary>
    /// <param name="choice">The chosen connection to keep trying. The same one the failed attempt used - a
    /// reopen never re-resolves the configuration, so it cannot quietly start using a different database
    /// than the one the operator's log says the Gateway chose.</param>
    private void ScheduleReopen(StatsConnectionChoice choice)
    {
        // Called from inside the lock on the late-arrival path and from outside it in the constructor, so it
        // takes the lock itself and tolerates already holding it (a Monitor is re-entrant per thread).
        lock (_gate)
        {
            if (_disposed || _reopening || _factory is not null) return;
            _reopening = true;
        }

        FileLog.Write(
            $"[GatewayStatsStore] Reopen SCHEDULED: source={choice.Source} target={choice.Target}. The store " +
            "could not be reached, so it will be retried until it answers. No restart is needed.");

        _ = Task.Run(() => ReopenLoop(choice));
    }

    /// <summary>
    /// The reopen loop. Waits, opens, and publishes the first attempt that succeeds.
    ///
    /// It runs on a thread pool thread for the life of the process, so EVERY attempt is contained: a throw
    /// escaping here would be an unobserved task exception and would end the loop silently, which is the
    /// failure mode this whole type exists to prevent. Nothing it catches leaves.
    /// </summary>
    private async Task ReopenLoop(StatsConnectionChoice choice)
    {
        for (var attempt = 0; ; attempt++)
        {
            var wait = ReopenBackoff[Math.Min(attempt, ReopenBackoff.Count - 1)];
            try
            {
                // On the injected clock. The timer is armed when Task.Delay returns, and only then is the wait
                // counted - see ReopenWaits for why that order is load-bearing.
                var waiting = Task.Delay(wait, _time, _stop.Token);
                Interlocked.Increment(ref _reopenWaits);
                await waiting.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;   // Disposed. There is nothing left to publish into.
            }

            ServiceProvider? provider = null;
            try
            {
                provider = BuildProvider(choice);
                var factory = provider.GetRequiredService<IDbContextFactory<GatewayStatsDbContext>>();

                // NOT bounded on the clock here, and that is the difference from the constructor. The startup
                // deadline exists because the platform is waiting on the port bind; nothing is waiting on
                // this. A slow attempt takes as long as it takes, and the next one starts after it.
                var prepared = await Task.Run(() => OpenAndMigrate(factory, choice)).ConfigureAwait(false);

                if (!prepared.IsUsable)
                {
                    // The store answered, with a verdict. That is not a reachability failure, and asking a
                    // second time asks the same question - so the loop ends with the verdict on the surface.
                    provider.Dispose();
                    lock (_gate)
                    {
                        _reopening = false;
                        if (_disposed) return;
                        _availability = Unavailable(
                            prepared.Reason, prepared.Detail, choice.Source, choice.Target);
                        _health.RecordFailure(prepared.Detail);
                    }

                    FileLog.Write(
                        "[GatewayStatsStore] Reopen STOPPED: the store answered and this build cannot use it " +
                        $"({prepared.Reason}): {prepared.Detail}");
                    return;
                }

                lock (_gate)
                {
                    if (_disposed)
                    {
                        provider.Dispose();
                        return;
                    }

                    _provider = provider;
                    _factory = factory;
                    _availability = new StatsStoreAvailability(
                        IsAvailable: true,
                        Reason: StatsStoreUnavailableReason.None,
                        ReasonCode: "available",
                        Detail: choice.Detail,
                        Source: choice.Source,
                        Target: choice.Target);
                    _reopening = false;
                }

                FileLog.Write(
                    $"[GatewayStatsStore] REOPENED on attempt {attempt + 1}: the statistics store is now " +
                    $"AVAILABLE ({choice.Target}). No restart was needed.");
                return;
            }
            catch (Exception ex)
            {
                // Contained, exactly as the constructor's boundary is. The exception MESSAGE is never used -
                // a provider echoes a malformed connection string back in it - so the type and the already
                // redacted target are what gets logged, and the loop waits and tries again.
                provider?.Dispose();
                FileLog.Write(
                    $"[GatewayStatsStore] Reopen attempt {attempt + 1} FAILED (CONTAINED): " +
                    $"target={choice.Target}: {ex.GetType().Name}. Waiting and retrying.");
            }
            finally
            {
                // After the attempt has published, on every path out of it - see ReopenAttempts.
                Interlocked.Increment(ref _reopenAttempts);
            }
        }
    }

    /// <summary>
    /// A fresh statistics context. The caller disposes it (it returns to the pool).
    /// </summary>
    /// <exception cref="InvalidOperationException">The statistics store is unavailable. Explicit, with the
    /// named reason in the message - never a context over a substitute store.</exception>
    public GatewayStatsDbContext CreateContext()
    {
        if (Factory is null)
            throw new InvalidOperationException(
                $"The statistics store is unavailable ({Availability.ReasonCode}): {Availability.Detail}");
        return Factory.CreateDbContext();
    }

    /// <summary>
    /// Record that a statistics observation was deliberately NOT attempted because the store is unavailable.
    /// Callers on the roster path use this instead of attempting a write that cannot succeed, so a refusal
    /// is visible as its own number rather than as silence or as a failure storm.
    /// </summary>
    public void RecordDrop() => _health.RecordDrop();

    /// <summary>Record that this store actually stored something, for the health surface.</summary>
    public void RecordSuccessfulWrite(DateTimeOffset whenUtc) => _health.RecordSuccessfulWrite(whenUtc);

    /// <summary>Maximum Npgsql pool size for the statistics store when the connection string does not set
    /// one. Lower than the main store's ceiling because statistics are the lighter workload; see
    /// <see cref="CcDirector.Gateway.Data.GatewayDatabase.DefaultMaxPoolSize"/> for why either exists.</summary>
    internal const int StatsMaxPoolSize = 5;

    /// <summary>
    /// Build the pooled context factory for the chosen provider.
    ///
    /// The PostgreSQL path pins BOTH the migrations assembly and this context's OWN history table, in this
    /// context's OWN schema. Neither is a default: without the history table pin the statistics chain would
    /// record itself in the main context's <c>gateway.__EFMigrationsHistory</c>, which is the two chains
    /// sharing a table - the exact coupling separating the contexts existed to avoid.
    /// </summary>
    private static ServiceProvider BuildProvider(StatsConnectionChoice choice)
    {
        var services = new ServiceCollection();

        if (choice.IsPostgres)
        {
            // Bound this pool too (issue #2383). This is the SECOND Npgsql pool in every Gateway container -
            // adding it roughly doubled the connections each container asks for, and a deploy briefly runs
            // four containers against a session-mode pooler in front of a server that allows sixty
            // connections in total. Unbounded, both pools default to a hundred each. Statistics are the
            // lighter of the two workloads, so its ceiling is lower than the main store's. An explicit value
            // in the connection string still wins.
            //
            // ConnectionString is null only when the source is NotConfigured, which IsPostgres excludes. If
            // that invariant is ever broken this fails loud here rather than opening something unbounded.
            var bounded = CcDirector.Gateway.Data.GatewayDatabase.WithBoundedPool(
                choice.ConnectionString
                    ?? throw new InvalidOperationException(
                        $"Statistics store source is {choice.Source} (PostgreSQL) but carries no connection string."),
                StatsMaxPoolSize);
            services.AddPooledDbContextFactory<GatewayStatsDbContext>(o =>
                o.UseNpgsql(bounded, npg =>
                {
                    npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                    npg.MigrationsHistoryTable("__EFMigrationsHistory", GatewayStatsDbContext.PostgresSchema);
                }));
        }
        else
        {
            // Self-host. The directory is created here because the SQLite provider will not create it, and a
            // first run on a clean machine has no storage root yet.
            var dataSource = new SqliteConnectionStringBuilder(choice.ConnectionString).DataSource;
            var dir = Path.GetDirectoryName(dataSource);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            services.AddPooledDbContextFactory<GatewayStatsDbContext>(o => o.UseSqlite(choice.ConnectionString));
        }

        return services.BuildServiceProvider();
    }

    /// <summary>
    /// The actual open: adopt an existing self-host store if there is one to adopt, then apply the chain.
    /// </summary>
    private static StatsStoreAdoptionResult OpenAndMigrate(
        IDbContextFactory<GatewayStatsDbContext> factory, StatsConnectionChoice choice)
    {
        using var context = factory.CreateDbContext();

        // Self-host only. Every self-host machine that has ever opened the statistics page has a file written
        // before this chain existed - sixteen tables and no migration history - and adoption is what makes
        // the chain understand it. A PostgreSQL statistics store has never existed without a history table,
        // so there is nothing there to adopt and asking would be a caller contract violation.
        if (!choice.IsPostgres)
        {
            var adoption = GatewayStatsSqliteAdoption.Adopt(context);
            if (!adoption.IsUsable)
                return adoption;
        }

        // Ask BEFORE applying. GetPendingMigrations reads the history table and compares; Migrate takes an
        // exclusive database-wide advisory lock before it looks at whether there is anything to do, and holds
        // it for the whole call. On PostgreSQL that lock is on the database the Gateway's OWN chain also
        // migrates, so calling Migrate unconditionally would make the statistics chain WAIT on the main one
        // during every deploy - two chains that share no table, no transaction and no gate, queueing behind
        // one lock anyway. On a code-only deploy the answer here is "none" and the lock is never taken.
        //
        // This is not a weakened contract: when there ARE migrations they are applied exactly as before, and
        // a failure is contained exactly as before. It only removes a lock acquisition with nothing to do.
        var pending = context.Database.GetPendingMigrations().ToList();

        // A HALF-BUILT SCHEMA, DIAGNOSED BEFORE IT IS WALKED INTO. The history records nothing as applied
        // while tables this store owns are already there, so the chain is about to create a table that
        // exists and fail. That failure would be CONTAINED either way - the catch around this work is the
        // boundary and it holds - but it would be contained as UNREACHABLE, whose sentence sends the
        // operator to the database and the network while both are perfectly healthy and the fault is on the
        // store's own disk. So the state is checked and NAMED rather than caught and mis-named.
        //
        // This is a diagnosis and not a second boundary: it changes which reason is reported and nothing
        // else. If it ever fails to spot the state, the chain throws and the catch still contains it.
        //
        // Left exactly as found. Repair means deciding what to do about tables holding somebody's numbers,
        // and a startup path that quietly reshapes a store is how numbers disappear without anybody knowing
        // which build did it.
        // THE CONDITION IS "THE BASELINE IS NOT RECORDED", not "nothing is recorded". It used to be the
        // second, which was accidentally correct only because the chain has exactly ONE migration today: with
        // one migration the applied set is either empty or holds the baseline, so the two conditions agree.
        // They stop agreeing the moment a second migration joins the chain, and then a store whose history
        // records something OTHER than the baseline walks straight past this check. A condition that is right
        // for a reason unrelated to what it means is a defect waiting for an ordinary commit to arm it.
        var baseline = context.Database.GetMigrations().FirstOrDefault();
        var applied = context.Database.GetAppliedMigrations().ToList();
        var baselineRecorded =
            !string.IsNullOrEmpty(baseline) && applied.Contains(baseline!, StringComparer.Ordinal);

        if (pending.Count > 0 && !baselineRecorded)
        {
            var alreadyThere = ExistingModelTables(context, choice);
            if (alreadyThere.Count > 0)
            {
                var detail = HalfBuiltDetail(choice, alreadyThere,
                    $"its migration history records {applied.Count} migration(s) and NOT the baseline " +
                    $"'{baseline}', yet {alreadyThere.Count} table(s) it owns already exist " +
                    $"({string.Join(", ", alreadyThere)})");
                FileLog.Write($"[GatewayStatsStore] Migrate: REFUSED, half-built schema (pre-check): {detail}");
                return new StatsStoreAdoptionResult(
                    StatsStoreAdoptionOutcome.NotAdoptable,
                    StatsStoreUnavailableReason.StoreSchemaIncomplete,
                    detail);
            }
        }

        if (pending.Count == 0)
        {
            FileLog.Write("[GatewayStatsStore] Migrate: no pending statistics migrations - skipping " +
                          "Migrate() and its database-wide lock");
        }
        else
        {
            FileLog.Write($"[GatewayStatsStore] Migrate: {pending.Count} pending statistics migration(s), " +
                          $"applying: {string.Join(", ", pending)}");
            try
            {
                context.Database.Migrate();
            }
            catch (Exception ex)
            {
                // RECOGNISE IT RATHER THAN ONLY PREDICT IT. The pre-check above can never be COMPLETE about a
                // state left by a process that DIED: being complete would mean enumerating every object each
                // pending migration would create, and the next unusual death produces a shape nobody
                // enumerated. So the half-built store is also recognised WHEN IT HAPPENS.
                //
                // A duplicate-object failure is definitionally "the schema already contains what this
                // migration is creating", which IS a half-built store. And it is the dangerous case for the
                // fault classifier, because the classifier is CORRECT about it and still wrong about the
                // world: a duplicate-table error genuinely is a provider exception, so the rule calls it the
                // operator's fault and reports UNREACHABLE - sending somebody to check a network over a
                // schema that is sitting half-built on their disk. Recognised here, before the boundary ever
                // sees it.
                if (!IsDuplicateObjectFailure(ex, choice, context, out var duplicateSignal))
                    throw;

                var alreadyThere = ExistingModelTables(context, choice);
                var detail = HalfBuiltDetail(choice, alreadyThere,
                    $"applying migration(s) {string.Join(", ", pending)} failed because the schema already " +
                    $"contains what they create ({duplicateSignal})");
                FileLog.Write(
                    $"[GatewayStatsStore] Migrate: REFUSED, half-built schema (recognised on failure, " +
                    $"{duplicateSignal}): {detail}");
                return new StatsStoreAdoptionResult(
                    StatsStoreAdoptionOutcome.NotAdoptable,
                    StatsStoreUnavailableReason.StoreSchemaIncomplete,
                    detail);
            }

            FileLog.Write($"[GatewayStatsStore] Migrate: applied {pending.Count} statistics migration(s)");
        }

        return new StatsStoreAdoptionResult(
            StatsStoreAdoptionOutcome.AlreadyTracked, StatsStoreUnavailableReason.None,
            "The statistics store is open and its schema is up to date.");
    }

    /// <summary>
    /// PostgreSQL SQLSTATEs that mean "the thing this statement is creating is already there".
    ///
    /// CODES AND NOT MESSAGE TEXT. A message is localised, reworded between server versions and different per
    /// object kind; a SQLSTATE is part of the PostgreSQL protocol contract and does not move. Matching on the
    /// words "already exists" would be the message sniffing this class deliberately avoids everywhere else.
    /// </summary>
    private static readonly Dictionary<string, string> PostgresDuplicateObjectStates = new(StringComparer.Ordinal)
    {
        ["42P07"] = "duplicate_table",     // also index, view, sequence - Postgres calls them all relations
        ["42701"] = "duplicate_column",
        ["42710"] = "duplicate_object",    // constraints, types, and the rest
        ["42P06"] = "duplicate_schema",
        ["42723"] = "duplicate_function",
        ["42P04"] = "duplicate_database",
    };

    /// <summary>
    /// Whether a PostgreSQL failure carries a duplicate-object SQLSTATE anywhere in its chain.
    ///
    /// Separated from the provider-aware method so it is reachable by a test WITHOUT a PostgreSQL server:
    /// the whole point of keying off SQLSTATE rather than message text is that the code can be asserted
    /// directly, and a recogniser nothing can exercise until someone stands up a server is a recogniser
    /// nobody has watched work.
    ///
    /// The inner chain is walked because Entity Framework wraps provider exceptions during migration.
    /// </summary>
    public static bool IsPostgresDuplicateObjectFailure(Exception? exception, out string signal)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is Npgsql.PostgresException pg
                && PostgresDuplicateObjectStates.TryGetValue(pg.SqlState, out var name))
            {
                signal = $"SQLSTATE {pg.SqlState} {name}";
                return true;
            }
        }

        signal = string.Empty;
        return false;
    }

    /// <summary>
    /// Whether a failed migration failed BECAUSE the schema already holds what it was creating - the
    /// signature of a half-built store, recognised after the fact rather than predicted in advance.
    /// </summary>
    /// <param name="signal">What identified it, for the log: the SQLSTATE on PostgreSQL, or the observed
    /// state on SQLite.</param>
    /// <remarks>
    /// THE TWO PROVIDERS ARE NOT SYMMETRIC AND THE DIFFERENCE IS STATED RATHER THAN PAPERED OVER.
    ///
    /// PostgreSQL answers precisely: <c>PostgresException.SqlState</c> carries a dedicated code per duplicate
    /// object kind, so the recognition is exact and is protocol contract rather than inference.
    ///
    /// SQLITE HAS NO SUCH CODE, and that is a finding rather than an inconvenience. "table x already exists"
    /// is plain <c>SQLITE_ERROR</c> (result code 1) - the same code SQLite returns for most statement
    /// failures - and no extended result code distinguishes it. The ONLY thing in a SQLite exception that
    /// identifies a duplicate object is the message text, which is exactly what must not be relied on.
    ///
    /// So the SQLite arm does not read the exception at all: it RE-OBSERVES THE STORE. If a migration failed
    /// and this model's tables are already present, the store is half built - which is a statement about
    /// what is on disk rather than an inference from an error string, and it is checkable by looking. It is
    /// deliberately NOT presented as an equivalent of the SQLSTATE: it is weaker (a migration that failed for
    /// an unrelated reason against a store whose tables happen to exist reads as half-built), and it is
    /// narrower than the PostgreSQL arm on purpose, since on SQLite the adoption step ahead of it has already
    /// refused the ordinary shapes of this state.
    /// </remarks>
    private static bool IsDuplicateObjectFailure(
        Exception exception, StatsConnectionChoice choice, GatewayStatsDbContext context, out string signal)
    {
        signal = string.Empty;

        if (choice.IsPostgres)
            return IsPostgresDuplicateObjectFailure(exception, out signal);

        // SQLite: no distinguishing code exists, so the STORE is re-read instead of the exception.
        var present = ExistingModelTables(context, choice);
        if (present.Count == 0)
            return false;

        signal = $"SQLite reports no duplicate-object code, so the store was re-read: {present.Count} " +
                 "table(s) this model owns are already present";
        return true;
    }

    /// <summary>
    /// The operator-facing sentence for a half-built store. One place, so the pre-check and the
    /// recognise-on-failure path cannot drift into describing the same state two different ways.
    /// </summary>
    private static string HalfBuiltDetail(
        StatsConnectionChoice choice, List<string> alreadyThere, string finding) =>
        $"The statistics store ({choice.Target}) has a HALF-BUILT SCHEMA: {finding}. That is what a process " +
        "stopped part-way through a migration leaves behind. The database is reachable and the settings are " +
        "correct, so this is NOT a network or connection problem - it is the store on disk, and it has NOT " +
        "been changed in any way. Statistics are unavailable; the Gateway is serving normally and the rest " +
        "of it is unaffected." +
        (alreadyThere.Count > 0 ? $" Tables already present: {string.Join(", ", alreadyThere)}." : string.Empty);

    /// <summary>
    /// Whether a failure caught by the boundary is a STORAGE failure - the operator's database, network or
    /// file - as opposed to a fault in DevThrottle's own code.
    ///
    /// NOT A TYPE-NAME WHITELIST, because a list of spellings rots: it silently reclassifies the day a
    /// provider renames an exception or a new provider is added, and nothing fails when it does. This asks
    /// the .NET type SYSTEM instead, using the base types that are the framework's OWN contract for
    /// "a data provider or the transport underneath it failed":
    ///
    ///  - <see cref="System.Data.Common.DbException"/> is the base every ADO.NET provider derives its own
    ///    exceptions from - <c>NpgsqlException</c> and <c>SqliteException</c> both do - so a new provider is
    ///    classified correctly without this method being edited.
    ///  - <see cref="System.Net.Sockets.SocketException"/> and <see cref="IOException"/> are the transport
    ///    and the local file underneath it.
    ///  - <see cref="TimeoutException"/> is a wait that expired against something outside this process.
    ///
    /// The whole INNER CHAIN is walked, because Entity Framework routinely wraps a provider exception in one
    /// of its own; a genuine outage that arrived wrapped must not be called our bug.
    ///
    /// WHAT THIS RULE CANNOT CLASSIFY, named rather than silently bucketed, because there are real cases in
    /// the middle and pretending otherwise is how a classifier earns unwarranted trust:
    ///
    ///  1. OUR bug thrown while a provider exception is already in flight - our code failing inside a catch,
    ///     chaining theirs - reads as THEIRS, because their exception is in the chain.
    ///  2. <see cref="InvalidOperationException"/> from Entity Framework with no inner provider exception
    ///     reads as OURS. That is right far more often than not - it is the shape of a model or mapping
    ///     mistake - but a genuinely malformed connection string can surface the same way.
    ///  3. <see cref="IOException"/> reads as THEIRS, and a bug in our own path handling surfaces as one too.
    ///
    /// The bias is deliberate and it is toward NOT crying wolf about the operator's infrastructure: every
    /// case above that lands wrong lands on the side of a truthful-but-vaguer message rather than sending
    /// somebody to audit a healthy network.
    /// </summary>
    public static bool IsStorageFailure(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is System.Data.Common.DbException
                or System.Net.Sockets.SocketException
                or IOException
                or TimeoutException)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Which of THIS MODEL'S tables already exist in the store, read from the provider's own catalog.
    ///
    /// The expected set comes from the MODEL rather than from a list written out here, so it cannot drift
    /// from the schema the baseline migration actually creates - a hand-written list would go stale the first
    /// time a table is added and would then report a half-built store as healthy.
    ///
    /// The catalog query is provider-specific because there is no portable one, and the PostgreSQL side is
    /// scoped to this context's OWN schema deliberately: the statistics schema shares a database with the
    /// Gateway's own, so a query that asked "does this database have tables" would answer yes on every
    /// healthy hosted Gateway and report a half-built statistics store that does not exist.
    /// </summary>
    private static List<string> ExistingModelTables(GatewayStatsDbContext context, StatsConnectionChoice choice)
    {
        var expected = context.Model.GetEntityTypes()
            .Select(t => t.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .ToHashSet(StringComparer.Ordinal);

        var connection = context.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
            context.Database.OpenConnection();

        using var command = connection.CreateCommand();
        if (choice.IsPostgres)
        {
            command.CommandText =
                "SELECT table_name FROM information_schema.tables WHERE table_schema = @schema";
            var schema = command.CreateParameter();
            schema.ParameterName = "@schema";
            schema.Value = GatewayStatsDbContext.PostgresSchema;
            command.Parameters.Add(schema);
        }
        else
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table'";
        }

        var present = new List<string>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var name = reader.GetString(0);
            if (expected.Contains(name))
                present.Add(name);
        }

        present.Sort(StringComparer.Ordinal);
        return present;
    }

    private static StatsStoreAvailability Unavailable(
        StatsStoreUnavailableReason reason, string detail, StatsConnectionSource source, string target) =>
        new(IsAvailable: false, reason, CodeFor(reason), detail, source, target);

    /// <summary>
    /// The stable, machine-readable spelling of a reason. Written out rather than derived from the enum name
    /// so that renaming a member in C# cannot silently change a string an operator greps for or a surface
    /// keys off.
    ///
    /// A MEMBER WITH NO CODE HERE IS A PROGRAMMING ERROR AND THROWS - and this comment used to claim it
    /// "fails to compile", which was simply false and was worth more than the mistake it hid. A C# switch
    /// expression over an enum does NOT fail to compile on a missing member; it throws at RUN TIME. Worse,
    /// on the one path that matters the throw lands inside the boundary catch above, which reports it as
    /// UNREACHABLE - so a missing code does not crash loudly, it silently mis-names somebody's disk problem
    /// as a network problem. That is precisely the failure the named reasons exist to prevent, arriving
    /// through the mechanism meant to guarantee them.
    ///
    /// It happened: worker 2 added two adoption reasons on its own branch and this map did not know them.
    /// So the guarantee is now MECHANICAL rather than a comment asking to be believed -
    /// <c>StatsStoreReasonCodeTests</c> walks every member of the enum and fails when one has no code or
    /// shares another's. A rule that depends on somebody remembering is not a rule.
    /// </summary>
    public static string CodeFor(StatsStoreUnavailableReason reason) => reason switch
    {
        StatsStoreUnavailableReason.None => "available",
        StatsStoreUnavailableReason.NotConfigured => "not_configured",
        StatsStoreUnavailableReason.Unreachable => "unreachable",
        StatsStoreUnavailableReason.StoreSchemaIncomplete => "store_schema_incomplete",
        StatsStoreUnavailableReason.IncompatibleSchemaVersion => "incompatible_schema_version",
        StatsStoreUnavailableReason.NotAStatisticsStore => "not_a_statistics_store",
        StatsStoreUnavailableReason.StoreUnreadable => "store_unreadable",
        StatsStoreUnavailableReason.StoreIsNewerThanThisBuild => "store_is_newer_than_this_build",
        StatsStoreUnavailableReason.StoreLockedByAnotherProcess => "store_locked_by_another_process",
        StatsStoreUnavailableReason.DidNotAnswerInTime => "did_not_answer_in_time",
        StatsStoreUnavailableReason.InternalError => "internal_error",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason,
            "A statistics unavailability reason with no stable code. Add one here - a surface cannot key " +
            "off a reason it has no spelling for."),
    };

    /// <summary>
    /// Close the store.
    ///
    /// UNDER THE SAME LOCK AS THE LATE ARRIVAL, because they race. Setting <c>_disposed</c> inside the lock is
    /// what lets <see cref="PublishLateArrival"/> see that there is nothing left to publish into and release
    /// its provider instead - without that, a store disposed while an open was still running could quietly
    /// become available again afterwards and hand out contexts over a provider nobody owns.
    /// </summary>
    public void Dispose()
    {
        StatsStoreAvailability availability;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _stop.Cancel();
            _provider?.Dispose();
            _provider = null;
            _factory = null;
            availability = _availability;
        }

        // Release the underlying SQLite connections so a test can delete the file. SQLite-only: the
        // PostgreSQL path has no local file and its pooling is the provider's to manage. Outside the lock:
        // it is process-wide and has no business being held under this store's gate.
        if (availability.Source == StatsConnectionSource.SqliteFile)
            SqliteConnection.ClearAllPools();

        // Outside the lock, and after the cancel above has already stopped the reopen loop from publishing.
        _stop.Dispose();
        FileLog.Write($"[GatewayStatsStore] Dispose: closed {availability.Target}");
    }
}
