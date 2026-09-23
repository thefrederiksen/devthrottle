using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace CcDirector.Gateway.Data;

/// <summary>
/// Owns the Gateway's EF Core database (gateway.db): builds the pooled context factory, applies the EF
/// migrations at startup, and hands out a tenant-scoped context per operation. One instance for the whole
/// Gateway, constructed by the host and shared by every store that has moved onto the EF data layer.
///
/// Fail-loud, no fallback (mission rule, matching <see cref="Stats.GatewayStatsDatabase"/>): if the database
/// cannot be opened or migrated, this throws with a clear message and the Gateway does not start half-blind.
/// It never falls back to the old JSON stores - coming up empty or silently reverting is the failure mode
/// this mission exists to end.
///
/// Threading: the Gateway is a single process and single writer. Contexts come from a pooled factory (a
/// fresh context per operation, never shared across threads) and each store keeps its own write lock, which
/// together preserve the single-writer invariant while letting WAL readers run.
/// </summary>
public sealed class GatewayDatabase : IDisposable
{
    /// <summary>The environment variable that, when set to a non-whitespace connection string, switches the
    /// Gateway database from the local SQLite file onto PostgreSQL (single-tenant hosted Gateway). Unset means
    /// the local install: the SQLite file behavior is unchanged.</summary>
    public const string PostgresConnectionEnvVar = "CC_GATEWAY_DB_CONNECTION";

    // ---- opening PostgreSQL under deploy contention (issue #2383) --------------------------------
    // On 2 August 2026 a deploy stopped the LIVE SITE for 38.5 seconds. Not because the swap was slow -
    // the swap measured 0.0s, as every healthy deploy does - but because the container App Service
    // starts after a swap got ONE refused PostgreSQL connection and treated it as terminal. The port
    // bind sits behind this open, so nothing ever listened; the platform waited out its 230-second
    // container start limit, found no listening port, and STOPPED THE SITE, killing the healthy
    // container that had been serving throughout. Four minutes later the same image on the same worker
    // opened the same database in 1.6 seconds.
    //
    // The refusal is contention, and it is structural rather than bad luck: a swap briefly runs FOUR
    // Gateway containers (the warmed one, the old one, and the two new ones), each with two Npgsql
    // pools, against a session-mode pooler in front of a server with max_connections=60. Post-swap
    // boots had been blowing their deadlines on every deploy for days before one finally crossed from
    // slow into refused.
    //
    // So: a refused connection is no longer proof the database is gone. The open is retried with
    // backoff for a bounded window. This is NOT a fallback and does not weaken the fail-loud contract -
    // it never reverts to SQLite, never starts without a database, and still throws at the end of the
    // window. It only stops one refusal during the noisiest ninety seconds of a deploy from being
    // mistaken for a dead database.
    //
    // WHAT THE RETRY DOES NOT DO, because an earlier version of this comment claimed it did and the
    // claim was false: it does not make a dead database fail the container. The throw at the end of the
    // window does not escape startup - GatewayService.StartAsync catches every startup exception, logs
    // it and does not rethrow - so on its own this retry only changes how long the container takes to
    // reach a silent, portless hang. What actually ends the process is GatewayWorker seeing the failed
    // state afterwards and exiting; see MustTerminate there. This window's only job is to ride out
    // transient contention, and it is INTENDED to stay well inside the platform's 230-second start limit
    // so it does not delay that exit into the platform's own timeout: 90 seconds of retry plus the rest
    // of boot is intended to leave headroom, and the measured recovery case resolved in far less.
    //
    // Intended, not guaranteed, and the difference is real. The deadline is only tested AFTER an attempt
    // returns, so it bounds how many attempts are made and not how long one takes. An attempt runs
    // GetPendingMigrations and Migrate synchronously, and Migrate takes a database-wide lock with no lock
    // timeout and no command timeout (see the note further down), so a single attempt can block past both
    // this window and the platform limit. That residual case is tracked separately as issue #2395; it is
    // not addressed here, and nothing in this file should be read as ruling it out.
    //
    // THE NINETY SECONDS ABOVE WAS CHOSEN ON A PREMISE THAT IS FALSE, AND 12 AUGUST DISPROVED IT (#2585).
    //
    // The reasoning was: give up well inside the platform's 230-second start limit, so the container exits
    // on OUR terms rather than being timed out by the platform. That is worth doing only if exiting early
    // is better than being timed out. It is not. The platform's own log shows it treats the two
    // identically - a container that EXITS during site startup and one that never binds both end at
    // "Failed to start site. Revert by stopping site." - and stopping the site tears down the healthy
    // container that was serving traffic beside it. That is where the outage comes from, either way.
    //
    // So giving up early buys nothing and costs the remaining budget. On 12 August the container exited at
    // 103.1 seconds (90 of retry plus boot) and the site was stopped, throwing away roughly 127 seconds in
    // which the same database, on the same image, opened cleanly minutes later. Production was dark for
    // 46.7 seconds.
    //
    // The window is therefore sized against the budget it actually has to fit inside, with headroom for
    // the rest of boot and for binding the port afterwards: the healthy boot that day bound and answered
    // the platform probe 21 seconds after the container started, database open included.
    //
    // THE COST, stated because an earlier version of this comment claimed there was none and that was
    // simply false (found in review). GatewayDatabase's catch takes EVERY exception once the connection
    // string has parsed, so a wrong password, an unreachable host, a missing or failed migration and a
    // provider fault all traverse this window too - not just the transient contention it was written for.
    // Every one of those now takes about eighty seconds longer to report and to restart. An operator who
    // has mistyped a connection string waits nearly three minutes for the error instead of ninety seconds.
    //
    // That is a real cost and it is accepted deliberately, because the two sides are not comparable: the
    // slow side costs an operator eighty seconds while they are already debugging a broken configuration,
    // and the fast side costs every user a live outage on an ordinary deploy. A misconfiguration is
    // noticed and fixed once; the deploy path runs every time we ship.
    //
    // What is NOT a cost: a database that is genuinely gone still fails, just later, and the site was
    // going to be stopped in that case regardless. And a Migrate that HANGS is unaffected either way - the
    // deadline is only tested after an attempt returns, so a blocked attempt never reaches it (see #2395).
    //
    // THIS IS A MITIGATION, NOT THE FIX. The fix is to bind the port BEFORE the database work so that
    // site startup never depends on PostgreSQL at all, which is #2383's first recommendation and is still
    // unbuilt. This constant only widens the window in which a slow database can recover before the
    // platform is asked to judge the startup. Do not read it as closing #2585.
    private static readonly TimeSpan PostgresOpenRetryWindow = TimeSpan.FromSeconds(170);
    private const int PostgresOpenFirstDelayMs = 1_000;
    private const int PostgresOpenMaxDelayMs = 10_000;

    /// <summary>
    /// Maximum Npgsql pool size for the Gateway store when the connection string does not set one.
    /// Npgsql's default is 100; a Gateway container holds about four backends at rest, and the server
    /// behind the pooler allows sixty connections in total. Four containers times two unbounded pools
    /// is headroom nothing needs and is the pressure that produced the refusal above. An explicit value
    /// in the connection string always wins - this is a ceiling for the unconfigured case, not an
    /// override of the operator.
    /// </summary>
    internal const int DefaultMaxPoolSize = 10;

    private readonly string _path;
    private readonly bool _usePostgres;
    private readonly ITenantContext _tenant;
    // Assigned by Open(), not by the constructor: the hosted Gateway defers the connect so its listener
    // can bind first. Null until Open() succeeds, which is exactly what IsOpen reports.
    private ServiceProvider? _provider;
    private IDbContextFactory<GatewayDbContext>? _factory;

    // The pool-bounded Postgres connection string, parsed and validated in the constructor and used by
    // Open(). Holds credentials, so it is never logged - see RedactConnectionTarget for what is.
    private readonly string? _boundedConn;
    // The SQLite connection string Open() built, kept so Dispose() can release THIS database's pool and
    // no other. Null on the Postgres path and until the SQLite open runs.
    private string? _sqliteConnectionString;
    private bool _disposed;

    /// <summary>The database file path (SQLite), for logging. On the Postgres path this is NOT a file - it
    /// holds a credential-redacted description of the connection target instead.</summary>
    public string Path => _path;

    /// <summary>
    /// Return <paramref name="connectionString"/> with an explicit maximum pool size, unless it already
    /// carries one. Npgsql defaults to 100 connections per pool; the hosted server behind the pooler
    /// allows sixty in total, and a deploy briefly runs four containers with two pools each. Capping the
    /// unconfigured case removes that pressure without taking the decision away from an operator who
    /// stated a value on purpose.
    ///
    /// Pure and internal so it can be tested without a database. It NEVER logs and never returns anything
    /// to a caller that logs - the value is a credentialed connection string.
    /// </summary>
    internal static string WithBoundedPool(string connectionString, int maxPoolSize)
    {
        // "Did the operator state a pool size?" must be asked of the RAW string, not of the Npgsql
        // builder. NpgsqlConnectionStringBuilder pre-populates every keyword it knows with that
        // keyword's default, so ContainsKey("Maximum Pool Size") is TRUE even for a connection string
        // that never mentions pooling - which quietly turned this entire cap into a no-op. Its own test
        // caught that (expected 10, got Npgsql's default 100); this is the repair.
        //
        // A plain DbConnectionStringBuilder holds ONLY the keys the string actually carries, so it can
        // answer the question honestly. Npgsql accepts both spellings and ignores spaces, so both are
        // checked with spacing and underscores removed.
        var stated = new System.Data.Common.DbConnectionStringBuilder { ConnectionString = connectionString };
        foreach (string key in stated.Keys)
        {
            var normalised = key.Replace(" ", "").Replace("_", "");
            if (normalised.Equals("maximumpoolsize", StringComparison.OrdinalIgnoreCase)
                || normalised.Equals("maxpoolsize", StringComparison.OrdinalIgnoreCase))
                return connectionString;
        }

        return new NpgsqlConnectionStringBuilder(connectionString) { MaxPoolSize = maxPoolSize }.ConnectionString;
    }

    /// <summary>
    /// A log-safe description of a database open failure.
    ///
    /// The generic exception message is NEVER used: a malformed connection string is echoed back by the
    /// parser, so stringifying the exception can leak credentials into a log. But when the server itself
    /// answered, its <see cref="PostgresException.SqlState"/> and <see cref="PostgresException.MessageText"/>
    /// are the server's own error code and text - they are produced by PostgreSQL, cannot contain the
    /// client's connection string, and are the difference between "we know it said too_many_connections"
    /// and the bare word "PostgresException", which is all the 2 August outage left behind.
    ///
    /// Pure and internal so the redaction contract can be tested directly.
    /// </summary>
    internal static string DescribeFailure(Exception ex)
    {
        return ex is PostgresException pg
            ? $"{nameof(PostgresException)} SqlState={pg.SqlState} MessageText={pg.MessageText}"
            : ex.GetType().Name;
    }

    /// <param name="tenant">The ambient tenant context. On the local install this is
    /// <see cref="SingleTenantContext"/> and every row is the "local" tenant.</param>
    /// <param name="dbPath">The SQLite database file. Defaults to <see cref="CcStorage.GatewayDb"/>. Ignored
    /// when <see cref="PostgresConnectionEnvVar"/> is set (the Postgres path never touches a file).</param>
    /// <param name="deferOpen">
    /// When true the constructor VALIDATES configuration and stops there, and the caller must call
    /// <see cref="Open"/> to connect and migrate. The hosted Gateway passes true so its listener can
    /// bind BEFORE any database work: connecting and migrating used to sit in front of the bind, and a
    /// slow database therefore pushed the bind past the platform's container-start deadline, at which
    /// point the platform stopped the SITE - tearing down the healthy container that was serving beside
    /// it. That is the 2 and 12 August 2026 outages (#2383, #2585); this parameter is what lets the
    /// order be fixed rather than the wait merely shortened.
    ///
    /// Configuration faults still fail in the CONSTRUCTOR either way - an unset-but-blank connection
    /// string, or one that cannot be parsed. Those are misconfiguration, they cannot be recovered by
    /// waiting, and a Gateway must not bind a port to serve errors forever because of one. Only the
    /// part that can succeed later - reaching the server - is deferred.
    /// </param>
    public GatewayDatabase(ITenantContext tenant, string? dbPath = null, bool deferOpen = false)
    {
        _tenant = tenant ?? throw new ArgumentNullException(nameof(tenant));

        // Provider selection is config-driven and fail-loud: when CC_GATEWAY_DB_CONNECTION carries a
        // connection string the Gateway runs on Postgres; otherwise it is the local SQLite file. There is NO
        // fallback between the two - a configured-but-broken Postgres throws below, it never silently reverts
        // to SQLite (which would hide a real cloud misconfiguration behind a healthy-looking local database).
        //
        // The three cases are distinct and blank is NOT the same as unset: the variable being UNSET (null)
        // means "local install, use SQLite", but the variable being SET to a blank/whitespace value is a
        // misconfiguration - the operator meant to point at Postgres and left the value empty - so it fails
        // loud here rather than silently picking SQLite (which would be exactly the hidden fallback the
        // no-fallback rule forbids).
        var pgConn = Environment.GetEnvironmentVariable(PostgresConnectionEnvVar);
        if (pgConn is not null && string.IsNullOrWhiteSpace(pgConn))
            throw new InvalidOperationException(
                PostgresConnectionEnvVar + " is set but blank; set a real PostgreSQL connection string or " +
                "unset it to use local SQLite. The Gateway will not fall back.");

        _usePostgres = pgConn is not null;

        if (_usePostgres)
        {
            // The _path field is SQLite-specific; on the Postgres path it holds a redacted target (host +
            // database only) for logging, never the file path and never the credentials.
            _path = RedactConnectionTarget(pgConn!);

            // Bound the connection pool before anything opens it (issue #2383). See DefaultMaxPoolSize.
            //
            // INSIDE A REDACTING BOUNDARY, because this parses a credentialed string and the caller writes
            // whatever escapes straight to disk: GatewayService.StartAsync catches every startup exception
            // and logs ex.Message verbatim. Npgsql's parser echoes the offending KEYWORD back in its
            // message - measured: a connection string carrying "SUPERSECRET=x" throws
            // "Couldn't set supersecret (Parameter 'supersecret')" - so a garbled string whose credential
            // fragment lands in keyword position puts that fragment in the log. The value position does not
            // echo; the keyword position does, and a mangled connection string is exactly the case where a
            // secret ends up somewhere it was never meant to be. (A case-SENSITIVE check for the leak misses
            // it, because the keyword is lowercased on the way out. That nearly hid this.)
            //
            // The original exception is deliberately NOT kept as an InnerException: the whole point is that
            // its message must not survive to be written by anything downstream. The type name is preserved
            // in the redacted message, which is what a diagnosis actually needs.
            try
            {
                _boundedConn = WithBoundedPool(pgConn!, DefaultMaxPoolSize);
            }
            catch (Exception ex)
            {
                FileLog.Write("[GatewayDatabase] Open FAILED: the connection string could not be parsed "
                    + $"({ex.GetType().Name}); its text is withheld because the parser echoes part of it.");
                throw new InvalidOperationException(
                    $"The Gateway PostgreSQL connection string could not be parsed ({ex.GetType().Name}). " +
                    "Its text is deliberately not reproduced here - the parser's own message can echo part of " +
                    "the string, which may carry credentials. Check " + PostgresConnectionEnvVar + " and restart.");
            }
        }
        else
        {
            _path = string.IsNullOrWhiteSpace(dbPath) ? CcStorage.GatewayDb() : dbPath!;
        }

        // Default is the historical behaviour: construct and connect in one step. Every caller that
        // does not say otherwise - the desktop pairing store, and every test that asserts this
        // constructor throws on an unreachable or unmigratable database - keeps exactly that.
        if (!deferOpen)
            Open();
    }

    /// <summary>
    /// Connect and migrate. Idempotent: a second call after a successful open does nothing, so a caller
    /// that cannot easily tell whether the constructor already opened may call it safely.
    ///
    /// SEPARATED FROM CONSTRUCTION so the hosted Gateway can bind its listener first. Everything about
    /// the failure behaviour is unchanged - the same bounded retry window, the same fail-loud throw when
    /// the window is spent - because the outage was never caused by giving up too early. It was caused
    /// by doing this work while the platform was still waiting for a port.
    /// </summary>
    public void Open()
    {
        if (_factory is not null)
            return;

        if (_usePostgres)
        {
            FileLog.Write($"[GatewayDatabase] Open: provider=Postgres target={_path}");

            // Retry the open for a bounded window rather than treating one refusal as a dead database.
            // Every attempt builds a FRESH provider: a provider whose pool was created against a refusing
            // server is not reusable, and disposing it between attempts is what stops a retry loop from
            // leaking pooled connections into the very contention it is waiting out.
            var deadline = DateTime.UtcNow + PostgresOpenRetryWindow;
            var delayMs = PostgresOpenFirstDelayMs;
            for (var attempt = 1; ; attempt++)
            {
                // Build the provider into a LOCAL first and only publish it to the readonly fields after Migrate()
                // succeeds. If Migrate throws, the constructor never completes, so Dispose() can never run - the
                // catch disposes the local provider itself, otherwise its pooled connections would leak.
                ServiceProvider? provider = null;
                try
                {
                    var services = new ServiceCollection();
                    services.AddPooledDbContextFactory<GatewayDbContext>(o => o.WithGatewayInterceptors().UseNpgsql(_boundedConn!, npg =>
                    {
                        npg.MigrationsAssembly("CcDirector.Gateway.Migrations.Postgres");
                        npg.MigrationsHistoryTable("__EFMigrationsHistory", "gateway");
                    }));
                    provider = services.BuildServiceProvider();
                    var factory = provider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();

                    using (var ctx = factory.CreateDbContext())
                    {
                        // Apply the Postgres migration set (its own assembly + gateway-schema history table). No
                        // PRAGMA journal_mode here - WAL is a SQLite-only setting and Postgres has its own WAL.
                        //
                        // ASK FIRST (issue #2203). Migrate() takes an EXCLUSIVE database-wide advisory lock before
                        // it looks at whether there is anything to apply, and it holds that lock for the whole
                        // call with no lock timeout and no command timeout. Every deploy runs two Gateway
                        // containers against this one database, so the second one waits on the first: measured at
                        // 43 seconds on a deploy carrying NO schema change, against 7 seconds with nothing else
                        // running. That wait sits in front of the port bind, and when it pushes the bind past the
                        // platform's 230-second startup deadline the platform stops the SITE - which is the
                        // user-visible outage this issue is about.
                        //
                        // GetPendingMigrations() takes no lock: it reads the history table and compares. On a
                        // code-only deploy - most deploys - the answer is "none" and we skip the locking call
                        // entirely. This is NOT a fallback and it does not weaken the contract: when there ARE
                        // migrations we still call Migrate() and still fail loudly if it throws. It only removes
                        // a lock acquisition that had nothing to do.
                        var pending = ctx.Database.GetPendingMigrations().ToList();
                        if (pending.Count == 0)
                        {
                            FileLog.Write("[GatewayDatabase] Migrate: no pending migrations - skipping Migrate() and its database-wide lock");
                        }
                        else
                        {
                            FileLog.Write($"[GatewayDatabase] Migrate: {pending.Count} pending migration(s), applying: {string.Join(", ", pending)}");
                            ctx.Database.Migrate();
                            FileLog.Write($"[GatewayDatabase] Migrate: applied {pending.Count} migration(s)");
                        }
                    }

                    _provider = provider;
                    _factory = factory;

                    FileLog.Write($"[GatewayDatabase] Open: ready, provider=Postgres target={_path}, attempt={attempt}");
                    return;
                }
                catch (Exception ex)
                {
                    provider?.Dispose();
                    // The provider's exception message can carry the raw connection string (a malformed one is
                    // echoed back by the parser), so it is NEVER interpolated into the log or the thrown message.
                    // DescribeFailure adds the SERVER's own error code and text when there is one - neither can
                    // contain the connection string - so the next failure is diagnosable instead of being
                    // recorded as the single word "PostgresException", which is what left the root cause of the
                    // 2 August outage permanently unknowable. The full exception is preserved as the
                    // InnerException without us stringifying it anywhere.
                    var detail = DescribeFailure(ex);
                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                    {
                        FileLog.Write($"[GatewayDatabase] Open FAILED: provider=Postgres target={_path} "
                            + $"after {attempt} attempt(s) over {PostgresOpenRetryWindow.TotalSeconds:F0}s: {detail}");
                        throw new InvalidOperationException(
                            $"The Gateway PostgreSQL database ('{_path}') could not be opened or migrated ({detail}) " +
                            $"after {attempt} attempt(s) over {PostgresOpenRetryWindow.TotalSeconds:F0} seconds. " +
                            "PostgreSQL is configured via " + PostgresConnectionEnvVar + ", so the Gateway will NOT " +
                            "fall back to SQLite or to the old JSON stores. Fix the connection string, the server, or " +
                            "the schema and restart the Gateway.", ex);
                    }

                    var delay = TimeSpan.FromMilliseconds(Math.Min(delayMs, (int)remaining.TotalMilliseconds));
                    FileLog.Write($"[GatewayDatabase] Open attempt {attempt} failed, retrying in {delay.TotalSeconds:F1}s "
                        + $"({remaining.TotalSeconds:F0}s of the window left): provider=Postgres target={_path}: {detail}");
                    Thread.Sleep(delay);
                    delayMs = Math.Min(delayMs * 2, PostgresOpenMaxDelayMs);
                }
            }
        }

        FileLog.Write($"[GatewayDatabase] Open: provider=Sqlite path={_path}");
        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            // Foreign Keys=True enforces foreign keys on every pooled connection (it is per-connection, so
            // it rides on the connection string rather than a one-off pragma).
            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = _path,
                ForeignKeys = true,
            }.ToString();
            _sqliteConnectionString = connectionString;

            var services = new ServiceCollection();
            services.AddPooledDbContextFactory<GatewayDbContext>(o => o.WithGatewayInterceptors().UseSqlite(connectionString));
            _provider = services.BuildServiceProvider();
            _factory = _provider.GetRequiredService<IDbContextFactory<GatewayDbContext>>();

            using (var ctx = _factory.CreateDbContext())
            {
                // Apply the EF migration set. This creates the schema on a fresh file and migrates an older
                // one forward - the EF equivalent of the stats database's PRAGMA user_version steps.
                ctx.Database.Migrate();
                // Write-ahead logging: a reader never blocks the single writer. journal_mode=WAL is persisted
                // in the database header, so setting it once here applies to every future pooled connection.
                // This is a SQLite-only pragma and never runs on the Postgres path above.
                ctx.Database.ExecuteSqlRaw("PRAGMA journal_mode=WAL;");
            }

            FileLog.Write($"[GatewayDatabase] Open: ready, provider=Sqlite path={_path}");
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayDatabase] Open FAILED: provider=Sqlite path={_path}: {ex.Message}");
            throw new InvalidOperationException(
                $"The Gateway SQLite database at '{_path}' could not be opened or migrated: {ex.Message}. " +
                "The Gateway will not fall back to the old JSON stores. Fix the database file " +
                "(or move it aside to start a fresh one) and restart the Gateway.", ex);
        }
    }

    /// <summary>
    /// Build a credential-free description of a Postgres connection for logging: host and database ONLY, never
    /// the username or password. The connection string is parsed with the Npgsql builder (which correctly
    /// handles quoted values and passwords containing ';' or '='); only Host and Database are read back. Any
    /// parse trouble degrades to a fixed literal, and the builder's own ToString() is never called, so a
    /// password can never reach the log through this method.
    /// </summary>
    /// <remarks>Internal (not private) only so the credential-redaction guarantee can be unit-tested directly
    /// via InternalsVisibleTo; it is not part of the public surface.</remarks>
    internal static string RedactConnectionTarget(string connectionString)
    {
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connectionString);
            return $"postgres host={builder.Host} database={builder.Database}";
        }
        catch
        {
            return "postgres (target redacted)";
        }
    }

    /// <summary>
    /// A fresh context scoped to the current tenant. Resolves the ambient tenant, fails loud on an invalid
    /// one (so a default(TenantId) never reaches a query - mirroring PushedSessionStore.DirectorsFor), and
    /// stamps the context so the global query filter and every write scope to it. The caller disposes it
    /// (it returns to the pool).
    /// </summary>
    /// <summary>
    /// True once <see cref="Open"/> has connected and migrated. False between construction and Open on the
    /// deferred path - the window in which the hosted Gateway is LISTENING but cannot yet serve data, which
    /// is exactly what /healthz reports as not-ready so the deploy's warm-up waits instead of swapping onto
    /// a Gateway that would answer errors.
    /// </summary>
    public bool IsOpen => _factory is not null;

    // Every context factory goes through this. Before Open() the fields are null, and a bare null-deref
    // would surface as a NullReferenceException somewhere far from the cause; a caller that arrives during
    // the open window deserves to be told what is actually happening.
    private IDbContextFactory<GatewayDbContext> RequireOpen()
        => _factory ?? throw new InvalidOperationException(
            "The Gateway database is not open yet. The listener binds before the database is connected so "
            + "that a slow database cannot stop the site from starting; requests that need data must wait "
            + "for /healthz to report ready.");

    public GatewayDbContext CreateContext()
    {
        var tenant = _tenant.Current;
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        var ctx = RequireOpen().CreateDbContext();
        ctx.ActiveTenant = tenant.Value;
        return ctx;
    }

    /// <summary>
    /// A fresh context scoped to an EXPLICITLY-supplied tenant, never the ambient one. Issue #2017 (the
    /// per-tenant settings resolver) and its MTR runtime-threading follow-up must scope to the caller/owner
    /// tenant the ROUTE resolved (via <c>ResolveReadTenant</c>), not to any AsyncLocal ambient tenant - the
    /// coordination boundary is explicit: no hidden ambient or static tenant inference, and on hosted a blank
    /// or unresolved tenant must fail closed and never become <see cref="TenantId.Local"/>. This method makes
    /// that explicit: it fails loud on an invalid tenant (so a <c>default(TenantId)</c> never reaches a query),
    /// then stamps the context so the global query filter and every write scope to that exact tenant. The
    /// caller disposes it (it returns to the pool).
    /// </summary>
    /// <exception cref="ArgumentException">The tenant is not valid.</exception>
    public GatewayDbContext CreateContext(TenantId tenant)
    {
        if (!tenant.IsValid)
            throw new ArgumentException("A valid TenantId is required.", nameof(tenant));

        var ctx = RequireOpen().CreateDbContext();
        ctx.ActiveTenant = tenant.Value;
        return ctx;
    }

    /// <summary>
    /// A fresh context that is NOT scoped to any tenant - for the GLOBAL mapping tables ONLY (the
    /// <c>tenants</c> account-subject -> tenant-id table), which carry no <c>tenant_id</c> column and no
    /// query filter. It leaves <see cref="GatewayDbContext.ActiveTenant"/> null, so any tenant-SCOPED entity
    /// read through it fails CLOSED - the global filter compares <c>tenant_id == null</c> and matches no row -
    /// which is exactly the deny-by-default we want if this context is ever misused on a scoped table. It
    /// exists because the tenant registry must mint or look up a tenant BEFORE any tenant is resolved, so it
    /// cannot go through <see cref="CreateContext"/> (which fails loud on an unresolved tenant). The caller
    /// disposes it (it returns to the pool).
    ///
    /// It EXPLICITLY sets <see cref="GatewayDbContext.ActiveTenant"/> to null. The factory is a POOL - a
    /// returned context keeps its custom <c>ActiveTenant</c> (pooling resets EF's own state, not arbitrary
    /// properties), so a context previously handed out by <see cref="CreateContext"/> could come back here
    /// still stamped with that prior tenant. Without this reset a scoped-entity read through the "unscoped"
    /// context would silently filter by a leftover tenant instead of failing closed - so the null is set here,
    /// exactly as <see cref="CreateContext"/> always sets the real tenant.
    /// </summary>
    public GatewayDbContext CreateUnscopedContext()
    {
        var ctx = RequireOpen().CreateDbContext();
        ctx.ActiveTenant = null;
        return ctx;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Null when the database was constructed with deferOpen and Open() never ran (or threw).
        _provider?.Dispose();
        // Release THIS database's pooled SQLite connections so its file can be deleted. SQLite-only: the
        // Postgres path has no local file to release and Npgsql pooling is managed by the provider.
        //
        // Only this database's pool, never ClearAllPools(). The pool is PROCESS-WIDE, so clearing all of it
        // disposed the native handle of a connection another database in the same process was opening at
        // that moment - an ObjectDisposedException inside SqliteConnection.Open() on a database nobody had
        // closed. Parallel tests hit it (HostedEntitlementGateTests, the v2.9.2 release gate), and the running
        // Gateway can too whenever one database is disposed while another serves.
        if (_sqliteConnectionString is not null)
        {
            using var poolKey = new SqliteConnection(_sqliteConnectionString);
            SqliteConnection.ClearPool(poolKey);
        }
        FileLog.Write($"[GatewayDatabase] Dispose: closed {_path}");
    }
}
