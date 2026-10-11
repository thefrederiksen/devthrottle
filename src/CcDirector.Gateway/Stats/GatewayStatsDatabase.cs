using CcDirector.Core.Storage;
using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using Microsoft.Data.Sqlite;

namespace CcDirector.Gateway.Stats;

/// <summary>
/// The Gateway's statistics database: one SQLite file holding what used to be a set of hand-rolled JSON
/// documents that were rewritten in full on every counter move. Owns the connection, the schema, and the
/// schema version.
///
/// Why this exists at all (mission "SQLite on the Gateway", Phase 1). The JSON stores cost real money three
/// ways: every new question the owner asked cost a new dictionary, a new store field, and a deploy; a shape
/// change had no version to migrate from, so it either quarantined the file and lost the numbers (pull
/// request #1376 wiped the all-time concurrency peak this way) or, worse, deserialized to defaults and came
/// up silently with zeros; and a counter move rewrote the whole document on a request path. A narrow table
/// of rows answers a new question with a GROUP BY, PRAGMA user_version gives a real migration instead of
/// quarantine-and-lose, and a counter move costs work bounded by what changed rather than by how much
/// history is stored.
///
/// Schema versioning is the point, so it is here from day one. <see cref="SchemaVersion"/> is the version
/// this build understands; the file carries its own in PRAGMA user_version. An older file is migrated
/// forward inside a transaction. A NEWER file - written by a build that knows something this one does not -
/// is a LOUD failure, never a downgrade attempt: silently opening it is how a store loses data, which is the
/// exact failure this mission exists to end.
///
/// No Entity Framework, no Dapper. Raw <see cref="SqliteConnection"/> with CREATE TABLE IF NOT EXISTS,
/// matching the house pattern already in Core (Communications/Services/DatabaseService.cs:47-60).
///
/// Threading: the Gateway is a single process and therefore a single writer, so there is no cross-process
/// write contention. One long-lived connection is held open and every caller reaches it under the owning
/// aggregator's lock, which is also what keeps an unchanged roster poll from paying a connection open.
/// </summary>
public sealed class GatewayStatsDatabase : IDisposable
{
    /// <summary>The schema version this build understands. Bump it and add a migration step; never reshape
    /// an existing table in place without one.</summary>
    public const int SchemaVersion = 7;

    /// <summary>
    /// The oldest schema version this build can migrate FORWARD without losing data. Below it the store is
    /// retired aside unread (<see cref="RetireIncompatibleStore"/>): version 4 changed what repo_id MEANS
    /// (local path -> "owner/repo" repo name), and the Gateway cannot re-key a stored path, so a pre-v4 store
    /// has no faithful forward migration. A v4 store DOES: version 5 (MTR-08) only ADDS a tenant column and
    /// backfills every existing row to the single self-host tenant, so v4 is migrated, never retired. This is
    /// a FIXED boundary, deliberately NOT <see cref="SchemaVersion"/> - raising the schema version must not
    /// silently start retiring stores that have a faithful migration.
    /// </summary>
    private const int OldestForwardMigratableVersion = 4;

    /// <summary>The meta key holding when the model dimension started recording - an ISO-8601 UTC stamp
    /// written once, by the migration that added the dimension. See <see cref="MigrateToVersion2"/> for why
    /// a reader cannot interpret a null model_id without it.</summary>
    public const string ModelsSinceKey = "models_since_utc";

    /// <summary>The marker written into hour_utc and session_id when a pruned row is folded into an archive
    /// row. Cannot collide with a real hour key ("yyyy-MM-ddTHH") or a real session id.
    ///
    /// The rule that goes with it, because leaving it implicit is how this design breaks: all-time aggregate
    /// queries INCLUDE archive rows - that is the whole point of archiving, the totals must not shrink when
    /// detail is pruned - while hourly and working-day queries MUST exclude this marker, or the series grows
    /// a phantom bucket that was never a real hour.</summary>
    public const string ArchiveMarker = "ARCHIVE";

    private readonly string _path;
    private readonly SqliteConnection _connection;

    /// <summary>The version <see cref="Migrate"/> runs to and stamps. <see cref="SchemaVersion"/> on every
    /// production path; lower only through the internal test-seam constructor, which exists so the adoption
    /// fixtures can build a genuine version 5 file by running the real steps.</summary>
    private readonly int _targetVersion;

    private bool _disposed;

    /// <summary>The open connection. Callers hold the owning aggregator's lock.</summary>
    public SqliteConnection Connection => _connection;

    /// <summary>The database file path, for logging and for the fail-loud messages.</summary>
    public string Path => _path;

    /// <param name="path">The database file. Defaults to gateway-stats.db under the cc-director storage
    /// root, beside the stores it replaces.</param>
    public GatewayStatsDatabase(string? path = null)
        : this(path, SchemaVersion)
    {
    }

    /// <summary>
    /// TEST SEAM: open (or create) the store and migrate it only as far as <paramref name="targetVersion"/>,
    /// running the REAL migration steps and stamping the version they reach.
    ///
    /// It exists for one purpose: the adoption fixtures must produce a GENUINE version 5 file - the state
    /// every real self-host machine that predates the Entity Framework chain is in - and no public code path
    /// can produce one any more, because the public constructor migrates every file to
    /// <see cref="SchemaVersion"/>. The fixture rule those tests live by is that a fixture is built by
    /// RUNNING shipped code, never hand-written and never synthesised from the new model's idea of the old
    /// schema; stopping the real chain at 5 is the only way to satisfy that rule once the shipped version has
    /// moved on. Internal, reached from the tests through InternalsVisibleTo, and never called in production:
    /// every production caller uses the public constructor and therefore the current version.
    /// </summary>
    internal GatewayStatsDatabase(string? path, int targetVersion)
    {
        if (targetVersion is < 1 or > SchemaVersion)
            throw new ArgumentOutOfRangeException(nameof(targetVersion),
                $"A statistics store can only be built at versions 1 through {SchemaVersion}; " +
                $"{targetVersion} is not a version this build's migration steps can produce.");

        _targetVersion = targetVersion;
        _path = string.IsNullOrWhiteSpace(path)
            ? System.IO.Path.Combine(CcStorage.Root(), "gateway-stats.db")
            : path!;

        FileLog.Write($"[GatewayStatsDatabase] Open: path={_path}");

        // The no-SQLite-on-hosted rule, enforced at the exact line that broke the 2026-07-30 deploy. This is
        // the statistics FILE, and a hosted Gateway keeps none: its store is PostgreSQL. Refused BEFORE the
        // directory is created, so a hosted container does not even leave an empty directory behind on a
        // share. The refusal is not caught here - the caller decides what an unavailable statistics surface
        // means, and GatewayHost.OpenInputStats already treats it as non-fatal and names it in the log.
        CcDirector.Gateway.Data.HostedSqliteGuard.EnsureNotHosted($"gateway statistics database at '{_path}'");

        try
        {
            var dir = System.IO.Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            RetireIncompatibleStore();

            _connection = new SqliteConnection($"Data Source={_path}");
            _connection.Open();

            // Write-ahead logging: a reader never blocks the single writer. Single process, single writer,
            // so there is nothing to contend with.
            Execute("PRAGMA journal_mode=WAL");
            Execute("PRAGMA foreign_keys=ON");

            Migrate();

            FileLog.Write($"[GatewayStatsDatabase] Open: ready at version {_targetVersion}, path={_path}");
        }
        catch (Exception ex)
        {
            // No fallback to the JSON store, ever. A database that will not open is a loud failure with a
            // clear message; coming up empty or quietly reverting to JSON is the failure mode this mission
            // exists to end.
            FileLog.Write($"[GatewayStatsDatabase] Open FAILED: path={_path}: {ex.Message}");
            throw new InvalidOperationException(
                $"The Gateway statistics database at '{_path}' could not be opened: {ex.Message}. " +
                "The Gateway will not fall back to the old JSON stores. Fix the database file " +
                "(or move it aside to start a fresh one) and restart the Gateway.", ex);
        }
    }

    private void Migrate()
    {
        var current = QueryUserVersion();
        FileLog.Write($"[GatewayStatsDatabase] Migrate: file version={current}, target version={_targetVersion}");

        if (current == _targetVersion)
            return;

        if (current > _targetVersion)
        {
            // A file written by a newer build. Opening it anyway would be a downgrade against a shape this
            // build does not know - the fastest way to lose the owner's numbers.
            throw new InvalidOperationException(
                $"The Gateway statistics database at '{_path}' is at schema version {current}, but this " +
                $"build only understands version {_targetVersion}. This database was written by a newer " +
                "build of DevThrottle. Upgrade the Gateway rather than running an older build against it.");
        }

        // Every migration step runs inside one transaction together with its version stamp, so a crash
        // mid-migration can never leave a half-migrated file claiming to be fully migrated. Each step is
        // gated on the TARGET as well as the current version: on every production path the target is
        // SchemaVersion and the gates are inert, and the internal test seam stops the same real steps at an
        // earlier version so the adoption fixtures can build a genuine version 5 file.
        using var tx = _connection.BeginTransaction();

        if (current < 1 && _targetVersion >= 1)
            MigrateToVersion1(tx);

        if (current < 2 && _targetVersion >= 2)
            MigrateToVersion2(tx);

        if (current < 3 && _targetVersion >= 3)
            MigrateToVersion3(tx);

        if (current < 4 && _targetVersion >= 4)
            MigrateToVersion4(tx);

        if (current < 5 && _targetVersion >= 5)
            MigrateToVersion5(tx);

        if (current < 6 && _targetVersion >= 6)
            MigrateToVersion6(tx);

        if (current < 7 && _targetVersion >= 7)
            MigrateToVersion7(tx);

        Execute($"PRAGMA user_version={_targetVersion}", tx);
        tx.Commit();

        FileLog.Write($"[GatewayStatsDatabase] Migrate: {current} -> {_targetVersion} applied");
    }

    // Version 1: the input-store schema (mission Phase 1).
    //
    // The transaction is threaded explicitly through every statement here. Schema commands on this same
    // connection do enlist in the surrounding transaction without it - the Codex reviewer verified that
    // with a rollback probe rather than taking it on trust - but passing it makes the atomicity visible at
    // each call site instead of relying on a reader knowing that rule.
    private void MigrateToVersion1(SqliteTransaction tx)
    {
        // One row per observed delta, recorded from the cutover FORWARD only.
        //
        // Historical rows are NEVER synthesized here, and there is no history to synthesize them from.
        // The owner ruled that the old numbers are not carried across, so gateway-input-stats.json is
        // renamed aside UNREAD on first run and every number in this database starts at the cutover. There
        // are no baseline tables and there is no import.
        //
        // Worth knowing why the import could not have been faithful even if it had been wanted: the old
        // JSON held three independent projections of history - by hour, by repository, by agent - each
        // already collapsed on a different dimension. Which hour went with which repository with which
        // agent was never written down, so the cross-product could not be recovered from the disk at all.
        // Synthesizing rows would have invented data, and summing the invented rows on one dimension would
        // have disagreed with the real totals on another.
        //
        // repo_id and agent_id are SURROGATE INTEGERS. Not a repository or agent string in any form - not
        // raw, and not folded.
        //
        // The dictionaries these replace group with StringComparer.OrdinalIgnoreCase
        // (GatewayInputStatsAggregator.cs:55 and :61), while SQLite's default text comparison is
        // case-sensitive BINARY, so a plain GROUP BY over a raw string column would SPLIT what the current
        // code MERGES. The obvious repair - store a FOLDED string and group on that - cannot actually be
        // built: it needs a normalizing function, and none exists. StringComparer.OrdinalIgnoreCase is a
        // COMPARER, not a normalizer; there is no Fold(x) exactly equivalent to it. ToLowerInvariant is a
        // different function (it can even change a string's length, at U+0130) and ToUpperInvariant is only
        // close enough that it would "almost certainly never" bite - which is not this mission's standard.
        //
        // With a surrogate id there is no normalizer, because there is no folded string. A
        // Dictionary<string, long> built with StringComparer.OrdinalIgnoreCase resolves a display spelling
        // to an id in memory - that dictionary IS today's comparer, the same object with the same
        // semantics - so parity holds BY CONSTRUCTION rather than by care. SQLite never compares a
        // repository string, so its collation cannot be wrong about one.
        //
        // The principle, which generalises past this column: prefer the design where the mistake is
        // IMPOSSIBLE over the design where the mistake is merely avoided. A rule you have to remember is
        // weaker than a schema that cannot express the error. This deletes the rule rather than obeying it.
        //
        // is_voice, not LOWER(modality)='voice': the voice test at :366 is case-INSENSITIVE while _totals
        // at :32 is case-SENSITIVE. Storing the flag preserves that asymmetry exactly and removes any
        // dependence on a SQL collation.
        //
        // wingman is NOT modality='voice': :425-427 folds a session's ENTIRE turn delta into the wingman
        // count whenever the session has voice mode on, and that delta includes TYPED turns. A turn typed
        // while voice mode is on is a wingman turn. This column records SessionDto.VoiceMode as observed at
        // fold time. Without it, post-cutover wingman turns could not be derived from rows at all.
        Execute(@"
            CREATE TABLE IF NOT EXISTS stat_delta (
                id           INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_utc     TEXT    NOT NULL,
                session_id   TEXT    NOT NULL,
                modality     TEXT    NOT NULL,
                surface      TEXT    NOT NULL,
                is_voice     INTEGER NOT NULL,
                repo_id      INTEGER NOT NULL,
                wingman      INTEGER NOT NULL,
                turns        INTEGER NOT NULL,
                chars        INTEGER NOT NULL
            )", tx);
        Execute("CREATE INDEX IF NOT EXISTS ix_stat_delta_hour ON stat_delta(hour_utc)", tx);

        // Operational state for live sessions: the last per-bucket counts seen, so only the INCREASE is
        // folded. This is what makes re-reading a roster safe and what lets counts survive a Director or
        // Gateway restart without double-counting. Its semantics are preserved exactly from
        // GatewayInputStatsAggregator.cs:336-354. Cleared for one session by Forget.
        Execute(@"
            CREATE TABLE IF NOT EXISTS session_highwater (
                session_id TEXT    NOT NULL,
                modality   TEXT    NOT NULL,
                surface    TEXT    NOT NULL,
                turns      INTEGER NOT NULL,
                chars      INTEGER NOT NULL,
                PRIMARY KEY (session_id, modality, surface)
            )", tx);

        // The all-time distinct-session sets. These are DELIBERATELY never pruned - the code says so at
        // :45-47, :51-54, and :57-61 - and they are a requirement to preserve, not a bug to fix.
        //
        // They are NOT COUNT(DISTINCT session_id) over stat_delta: that is exact only while every
        // contributing row is still present, so it stops being exact the moment pruning starts, and it
        // cannot see a pre-cutover session at all.
        Execute("CREATE TABLE IF NOT EXISTS wingman_session (session_id TEXT PRIMARY KEY)", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS repo_session (
                repo_id    INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (repo_id, session_id)
            )", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_session (
                agent_id   INTEGER NOT NULL,
                session_id TEXT    NOT NULL,
                PRIMARY KEY (agent_id, session_id)
            )", tx);

        // Surrogate id to FIRST-SEEN display spelling. First-seen wins is exactly what a .NET Dictionary
        // with an OrdinalIgnoreCase comparer does - it keeps the key it was first given - so the spelling
        // the pages display does not change.
        //
        // These columns are, from SQLite's point of view, write-only: they are read once at startup to
        // rebuild the in-memory identity map, and SQLite is never asked to compare or group by them. That is
        // the whole point - the only component that decides whether two repository strings are equal is the
        // same StringComparer.OrdinalIgnoreCase that decides it today.
        //
        // Deliberately NO UNIQUE constraint on the display column: uniqueness here is case-INSENSITIVE and
        // SQLite could only enforce it case-SENSITIVELY (BINARY), which would be the wrong question asked
        // authoritatively. The in-memory map is what guarantees one id per distinct-ignoring-case spelling.
        Execute(@"
            CREATE TABLE IF NOT EXISTS repo_identity (
                repo_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                repo_display TEXT    NOT NULL
            )", tx);
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_identity (
                agent_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_display TEXT    NOT NULL
            )", tx);

        // ---- The per-agent tally. Its OWN table, and this is not a stylistic choice. ----
        //
        // The agent tally is NOT derivable from stat_delta, because AttributeToAgentLocked has two callers
        // and only one of them feeds the totals. The ordinary delta path attributes the same delta the
        // totals get (GatewayInputStatsAggregator.cs:460), but the first-fold back-fill attributes a
        // session's PRIOR high-water (:395) - turns that are ALREADY in the totals from before the agent
        // tally existed.
        //
        // So carrying agent_id on stat_delta has NO correct behaviour once the back-fill fires: writing a
        // row for a back-fill inflates the totals, because those turns are already counted there, and not
        // writing one leaves the agent tally short. Two wrong answers and no right one is not a trade-off,
        // it is a schema that cannot express the situation.
        //
        // The cost is real and was accepted deliberately: stat_delta cannot answer turns-by-agent-by-hour.
        // Carrying agent_id would ADVERTISE a cross-product the code does not maintain, and answering that
        // question from it would silently omit every back-fill attribution - which is "what the historical
        // data cannot tell us" written fresh into a brand new schema.
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_delta (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id INTEGER NOT NULL,
                is_voice INTEGER NOT NULL,
                turns    INTEGER NOT NULL,
                chars    INTEGER NOT NULL
            )", tx);

        // ---- The agent-to-agent lane (issue #1636). A SEPARATE TABLE, deliberately. ----
        //
        // These are turns OTHER AGENTS drove into a session, and the code is explicit that they "never enter
        // _totals, _hourly or the buckets, because the human voice-versus-typed numbers must stay about the
        // human" (GatewayInputStatsAggregator.cs:102-105). Putting them in stat_delta behind a lane flag
        // would make that a RULE every human aggregate query has to remember - the archive-marker problem
        // again, and this one fails SILENTLY: the owner's voice-versus-typed share would quietly start
        // including agent traffic and nothing would look wrong. In their own table they CANNOT be summed
        // into the human totals by accident.
        //
        // The shapes also disagree, which is the second reason: the agent-driven high-water is keyed by
        // SESSION ALONE, while the human high-water is keyed by session AND modality AND surface. One table
        // would force one of them to lie about its own key.
        //
        // No hour, no repository, no modality: this lane feeds only the per-agent tally and a global pair,
        // so carrying columns nothing populates would be a dimension nothing emits.
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_driven_delta (
                id       INTEGER PRIMARY KEY AUTOINCREMENT,
                agent_id INTEGER NOT NULL,
                turns    INTEGER NOT NULL,
                chars    INTEGER NOT NULL
            )", tx);

        // High-watered exactly like the human buckets - only the increase counts, and a reported count that
        // DROPPED (a Director restarted this session id) is fresh activity from zero.
        Execute(@"
            CREATE TABLE IF NOT EXISTS agent_driven_highwater (
                session_id TEXT    PRIMARY KEY,
                turns      INTEGER NOT NULL,
                chars      INTEGER NOT NULL
            )", tx);

        // Sessions whose already-counted turns have been attributed to their agent (issue #1633).
        //
        // THIS IS LIVE BEHAVIOUR, NOT MIGRATION SCAFFOLDING, and the distinction nearly cost the owner's
        // agent numbers. On a fresh database the first-fold back-fill contributes nothing, because a new
        // session's high-water is empty - so this table looks like dead weight. But session_highwater
        // PERSISTS across a Gateway restart, and without this set the first fold after a restart would
        // back-fill every live session a SECOND time and double every agent's turns. The aggregator says so
        // itself at GatewayInputStatsAggregator.cs:80-81. It survives because of what it DOES, not because
        // of what its name suggests it was for.
        Execute("CREATE TABLE IF NOT EXISTS agents_seeded (session_id TEXT PRIMARY KEY)", tx);


        // Runtime scalars, keyed by name. Its only occupant today is agents_since_utc - when the per-agent
        // breakdown started counting, stamped on the first observation by StampAgentsSinceLocked and never
        // moved after that.
        //
        // It lives here rather than in a table of its own because it is not a statistic: it is a fact about
        // when a statistic began. It is also NOT history - it is stamped at runtime by the running product -
        // which is why it survived the deletion of everything that carried the old numbers across.
        Execute(@"
            CREATE TABLE IF NOT EXISTS meta (
                name  TEXT PRIMARY KEY,
                value TEXT NOT NULL
            )", tx);
    }

    // Version 2: the model dimension - which model produced each turn.
    //
    // THIS IS THE FIRST REAL MIGRATION, and that is half its value. Version 1 shipped the machinery
    // (PRAGMA user_version, a transaction per step, a loud refusal to open a newer file) against a single
    // version, where nothing exercised it. This step is the proof: an existing database at version 1 - the
    // owner's, carrying real turns - gains a column and a table and keeps every row it had.
    //
    // model_id is NULLABLE, alone among the dimensions, and the nullability is the design rather than a
    // concession. SessionDto.CurrentModel is RECORDS-ONLY: the owning Director stamps it from the agent's
    // own records at each turn-end and reports null until the tool has actually recorded a model. So a
    // session's first turn folds before any record of its model exists, and it folds that way FOREVER -
    // this store records forward only and never revisits a written row. A NOT NULL column would have to
    // invent a value for that turn; a null says the true thing.
    //
    // Deliberately NOT the empty-string identity that repo_id and agent_id use for "the Director did not
    // say". An empty display spelling would appear in model_identity as a model, be ranked among models, and
    // read as a model named nothing. Absence is not a value here, so it is stored as the absence of one.
    //
    // ALTER TABLE ADD COLUMN, not a table rebuild: SQLite appends the column and reads NULL for every
    // existing row, which is exactly the state those rows are in - folded before the dimension existed.
    private void MigrateToVersion2(SqliteTransaction tx)
    {
        Execute("ALTER TABLE stat_delta ADD COLUMN model_id INTEGER", tx);

        // Surrogate id to first-seen display spelling, exactly as repo_identity and agent_identity: the
        // in-memory OrdinalIgnoreCase map decides identity and SQLite is never asked to compare a model
        // string. The reasoning is identical and is written out at length on repo_identity above - most of
        // all the part about there being no normalizer equivalent to the comparer, which is why no folded
        // string is stored.
        //
        // Model names need it as much as repositories do: CurrentModel is free text with unbounded
        // cardinality and casing by convention only.
        Execute(@"
            CREATE TABLE IF NOT EXISTS model_identity (
                model_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                model_display TEXT    NOT NULL
            )", tx);

        // When the model dimension began, stamped HERE rather than at the first fold, because the migration
        // is the moment it began and it is the only moment that is exactly knowable.
        //
        // Without this a null model_id is UNREADABLE, and that is the whole reason it exists. Two different
        // facts both store NULL: a row folded BEFORE this migration ran, which predates the dimension
        // entirely and could never have carried a model; and a row folded AFTER it, whose session had
        // genuinely not recorded a model yet. Same null, different meanings, and a page that cannot tell
        // them apart would report the owner's entire history as "model unknown" as though the data were
        // missing rather than never collected. Compared against a row's hour_utc, this separates them.
        //
        // agents_since_utc is the same idea one dimension earlier, and its comment on the meta table above -
        // "not a statistic: a fact about when a statistic began" - describes this exactly.
        //
        // INSERT OR IGNORE, not INSERT: this stamp is written once and never moved. If a database somehow
        // already carries the key, the ORIGINAL is the true beginning and overwriting it with a later time
        // would silently reclassify real model rows as predating the dimension.
        Execute("INSERT OR IGNORE INTO meta(name, value) VALUES ($n, $v)", tx,
            ("$n", ModelsSinceKey),
            ("$v", DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)));
    }

    // Version 3: the token dimension - how many tokens the work actually cost.
    //
    // SPEND, NOT OCCUPANCY, ENFORCED BY THE SCHEMA. Four columns and every one is a cumulative, additive
    // count: input, output, cache-read and cache-creation tokens. Context-window occupancy is deliberately
    // absent - it is a gauge (how full the window is at a point in time), it goes up AND down, and summing
    // it is meaningless. The wire carries it (SessionDto.TokenTotals.ContextTokens) for the live gauge, but
    // it MUST NOT enter a delta table where a SUM would be taken, so it is not here. A future hand tempted
    // to "just add context too" would be adding a number that lies the moment it is aggregated.
    //
    // No modality and no surface, unlike stat_delta. Tokens are the model's WORK, not the human's input
    // channel: a turn costs the same tokens whether the human typed it or spoke it, and the token total
    // arrives per session as one cumulative figure that cannot be split across voice/typed buckets. Adding
    // those columns would advertise a division the data cannot make - the agent_id-on-stat_delta mistake in
    // a new shape.
    //
    // model_id is carried and NULLABLE, exactly as on stat_delta and for the same reason: the spend
    // attributes to the model the session was RECORDED running at fold time, and that is null until the
    // agent's records name one. Unlike stat_delta, a token row's null model has ONLY ONE meaning - "not
    // recorded yet" - because every token row is written after this migration, so none can predate the model
    // dimension (version 2, already applied by the time this runs). No since-stamp is needed to read it.
    private void MigrateToVersion3(SqliteTransaction tx)
    {
        // One row per observed token increase, attributed to the hour and the session's current model. High-
        // watered per session (see token_highwater) so only the GROWTH since the last poll is folded, never
        // the running total - the same increment discipline as stat_delta's turns and characters.
        Execute(@"
            CREATE TABLE IF NOT EXISTS token_delta (
                id                    INTEGER PRIMARY KEY AUTOINCREMENT,
                hour_utc              TEXT    NOT NULL,
                model_id              INTEGER,
                input_tokens          INTEGER NOT NULL,
                output_tokens         INTEGER NOT NULL,
                cache_read_tokens     INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL
            )", tx);
        Execute("CREATE INDEX IF NOT EXISTS ix_token_delta_hour ON token_delta(hour_utc)", tx);

        // The last cumulative token counts seen for each live session, so only the INCREASE folds. Mirrors
        // session_highwater exactly: a reported count that DROPPED (a Director restarted the session with a
        // fresh conversation) is fresh spend from zero, not a negative. Cleared for one session by Forget.
        //
        // All four counts are running sums over the whole transcript (SessionUsageDto sums them across every
        // assistant line), so they only grow within one conversation - which is what makes the high-water
        // increment correct. Context occupancy is NOT here: it is not cumulative and high-watering it would
        // be meaningless.
        Execute(@"
            CREATE TABLE IF NOT EXISTS token_highwater (
                session_id            TEXT    PRIMARY KEY,
                input_tokens          INTEGER NOT NULL,
                output_tokens         INTEGER NOT NULL,
                cache_read_tokens     INTEGER NOT NULL,
                cache_creation_tokens INTEGER NOT NULL
            )", tx);
    }

    // Version 4: the checkout dimension - the LOCAL working directory each turn was driven in - landing
    // together with a meaning change to the repository dimension it sits beside.
    //
    // repo_id USED to be keyed by the session's local working-directory path; from this version it is keyed
    // by the session's "owner/repo" repo name (SessionDto.RepoName), so one repository's worktrees and its
    // per-machine clones collapse into a single row on the Repos page instead of one row each. The path is
    // NOT thrown away in the process - it becomes its own dimension here, so the store still records exactly
    // which checkout every turn ran in (the owner's ask: keep the checkout AND the repo name). Grouping and
    // ranking are by repo_id (the repo name); checkout_id is retained detail, read back as the set of
    // checkouts that rolled into a repo.
    //
    // Because the meaning of an EXISTING repo_id row changed and the Gateway cannot re-key a stored path to a
    // repo name (it has no filesystem to resolve it, and the path may be another machine's), a pre-version-4
    // store is not migrated forward at all: it is retired aside UNREAD before this database is opened
    // (RetireIncompatibleStore), and this store starts empty. So this step only ever runs as part of building
    // a FRESH database (0 -> 4); it is written as a proper migration all the same, so the schema machinery
    // stays honest and a fresh build lands the column and table exactly once.
    //
    // checkout_id is added by ALTER TABLE and is therefore nullable at the column level (SQLite reads NULL
    // for any row written before the column existed). No live fold ever writes a NULL: every stat_delta row
    // is written by this build, which always carries a checkout (RepoPath is always set), and every pre-v4
    // row was retired aside. The nullable column is the honest shape for an ALTER, not a state the fold
    // produces - the same as model_id in version 2.
    private void MigrateToVersion4(SqliteTransaction tx)
    {
        Execute("ALTER TABLE stat_delta ADD COLUMN checkout_id INTEGER", tx);

        // Surrogate id to first-seen display spelling, exactly as repo_identity, agent_identity and
        // model_identity: the in-memory OrdinalIgnoreCase map decides identity and SQLite is never asked to
        // compare a path string. The full reasoning - most of all why no folded string is stored, because
        // there is no normalizer equivalent to the comparer - is written out on repo_identity in
        // MigrateToVersion1 and is not repeated here.
        Execute(@"
            CREATE TABLE IF NOT EXISTS checkout_identity (
                checkout_id      INTEGER PRIMARY KEY AUTOINCREMENT,
                checkout_display TEXT    NOT NULL
            )", tx);
    }

    // Version 5: the TENANT dimension (MTR-08, production-readiness census rows 49-67) - the owning tenant of
    // every recorded fact, so a hosted Gateway that folds several accounts' pushes into this one store keeps
    // each account's tally, membership and identity PHYSICALLY separate instead of coalescing them.
    //
    // WHY A FORWARD MIGRATION AND NOT A RETIRE. Unlike version 4 (which changed what repo_id MEANS and had no
    // faithful re-key), this only ADDS a column. Every row already in the store was written by a single-tenant
    // build, so it belongs to exactly one tenant: the self-host tenant. Backfilling each existing row to
    // TenantId.Local is therefore not a guess - it is the true owner. No numbers are lost and no data is
    // invented.
    //
    // TWO SHAPES OF CHANGE:
    //   - ADD COLUMN with a NOT NULL DEFAULT of the local tenant, for the delta tables and the identity
    //     tables. SQLite backfills every existing row to 'local' in one statement, and the column keeps
    //     NOT NULL going forward (the fold always stamps a tenant).
    //   - A TABLE REBUILD for the five membership/high-water tables and meta, because the tenant must join
    //     their PRIMARY KEY and SQLite cannot alter a primary key in place. Without the tenant in the key,
    //     two tenants pushing the same bare session id would collide on the key and one would silently
    //     overwrite the other's high-water - the exact suppression this fix closes. Each rebuild copies every
    //     existing row under the local tenant, so a self-host store keeps all of its rows.
    //
    // Runs inside the migration transaction (see Migrate()), so a crash mid-rebuild rolls the whole thing back
    // rather than leaving a half-keyed store claiming to be version 5.
    private void MigrateToVersion5(SqliteTransaction tx)
    {
        var local = TenantId.Local.Value;

        // The delta and identity tables: the tenant is a plain column (not part of any primary key), so an
        // ADD COLUMN with a NOT NULL default backfills every existing row to the self-host tenant in place.
        foreach (var table in new[]
                 {
                     "stat_delta", "token_delta", "agent_delta", "agent_driven_delta",
                     "repo_identity", "agent_identity", "model_identity", "checkout_identity",
                 })
            Execute($"ALTER TABLE {table} ADD COLUMN tenant TEXT NOT NULL DEFAULT '{local}'", tx);

        // Read speed: the tenant-scoped aggregate reads all filter by tenant, and the working-day series also
        // by hour. A composite index keeps those from scanning another tenant's rows.
        Execute("CREATE INDEX IF NOT EXISTS ix_stat_delta_tenant_hour ON stat_delta(tenant, hour_utc)", tx);
        Execute("CREATE INDEX IF NOT EXISTS ix_token_delta_tenant_hour ON token_delta(tenant, hour_utc)", tx);

        // The membership / high-water tables and meta: the tenant must join the PRIMARY KEY, so each is
        // rebuilt with the tenant in the key and every existing row copied under the local tenant.
        RebuildWithTenant(tx, "session_highwater",
            @"CREATE TABLE session_highwater (
                  tenant     TEXT    NOT NULL,
                  session_id TEXT    NOT NULL,
                  modality   TEXT    NOT NULL,
                  surface    TEXT    NOT NULL,
                  turns      INTEGER NOT NULL,
                  chars      INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id, modality, surface)
              )",
            "tenant, session_id, modality, surface, turns, chars",
            "session_id, modality, surface, turns, chars", local);

        RebuildWithTenant(tx, "agent_driven_highwater",
            @"CREATE TABLE agent_driven_highwater (
                  tenant     TEXT    NOT NULL,
                  session_id TEXT    NOT NULL,
                  turns      INTEGER NOT NULL,
                  chars      INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",
            "tenant, session_id, turns, chars",
            "session_id, turns, chars", local);

        RebuildWithTenant(tx, "token_highwater",
            @"CREATE TABLE token_highwater (
                  tenant                TEXT    NOT NULL,
                  session_id            TEXT    NOT NULL,
                  input_tokens          INTEGER NOT NULL,
                  output_tokens         INTEGER NOT NULL,
                  cache_read_tokens     INTEGER NOT NULL,
                  cache_creation_tokens INTEGER NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",
            "tenant, session_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens",
            "session_id, input_tokens, output_tokens, cache_read_tokens, cache_creation_tokens", local);

        RebuildWithTenant(tx, "wingman_session",
            @"CREATE TABLE wingman_session (
                  tenant     TEXT NOT NULL,
                  session_id TEXT NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",
            "tenant, session_id", "session_id", local);

        RebuildWithTenant(tx, "agents_seeded",
            @"CREATE TABLE agents_seeded (
                  tenant     TEXT NOT NULL,
                  session_id TEXT NOT NULL,
                  PRIMARY KEY (tenant, session_id)
              )",
            "tenant, session_id", "session_id", local);

        // meta becomes per (tenant, name). agents_since_utc is per tenant from here on; models_since_utc is a
        // schema fact and simply rides the local tenant's row (it is read tenant-agnostically).
        RebuildWithTenant(tx, "meta",
            @"CREATE TABLE meta (
                  tenant TEXT NOT NULL,
                  name   TEXT NOT NULL,
                  value  TEXT NOT NULL,
                  PRIMARY KEY (tenant, name)
              )",
            "tenant, name, value", "name, value", local);
    }

    // Version 6: let the DATABASE tell a writer what it changed, instead of the writer inferring it.
    //
    // THE RULE THIS VERSION EXISTS TO MAKE POSSIBLE: never learn what you changed from your own prior belief -
    // learn it from the response of whatever arbitrates. Two things follow, and both are additive.
    //
    //  1. THE previous_* COLUMNS on the three high-water tables. A writer used to compute a session's growth
    //     against its OWN in-memory mirror and append that growth to the shared delta ledger, while the stored
    //     watermark was arbitrated by the database. Those two cannot both be authoritative: two hosted
    //     containers each measuring growth from their own stale mirror append MORE in total than the watermark
    //     ever moved, so the all-time totals drift upward with every interleave. Now the raise statement parks
    //     the pre-raise value in these columns and returns it beside the new one, so a writer appends exactly
    //     the difference IT made. The sum of appended deltas equals the movement of the watermark by
    //     construction rather than by hope. Nothing reads them as a statistic.
    //
    //  2. A UNIQUE INDEX on each identity table's (tenant, display) pair. The database still never decides
    //     whether two DIFFERENT spellings are one identity - that is case-insensitive and stays in the
    //     aggregator's mirror. This forbids only the byte-for-byte duplicate, which is a duplicate under every
    //     comparer, and it exists so a mint can be an upsert that RETURNS the winning id instead of an insert
    //     that assumes it minted one. Creating it cannot fail on an existing self-host file: the mirror is
    //     loaded from these tables at startup and every insert goes through it, so one process could never have
    //     written the same spelling twice under one tenant, and the self-host store has only ever had one
    //     process.
    //
    // Both are ADD-only, so no table is rebuilt and no row is rewritten. A version 5 row's previous_* columns
    // land at zero, which reads as "this row has never been raised by a returning statement yet" - the first
    // raise after the upgrade then reports its whole reported count as the difference. That is the same
    // arithmetic a fresh row gets and it is the honest answer: the previous value genuinely is not recorded.
    private void MigrateToVersion6(SqliteTransaction tx)
    {
        Execute("ALTER TABLE session_highwater ADD COLUMN previous_turns INTEGER NOT NULL DEFAULT 0", tx);
        Execute("ALTER TABLE session_highwater ADD COLUMN previous_chars INTEGER NOT NULL DEFAULT 0", tx);

        Execute("ALTER TABLE agent_driven_highwater ADD COLUMN previous_turns INTEGER NOT NULL DEFAULT 0", tx);
        Execute("ALTER TABLE agent_driven_highwater ADD COLUMN previous_chars INTEGER NOT NULL DEFAULT 0", tx);

        Execute("ALTER TABLE token_highwater ADD COLUMN previous_input_tokens INTEGER NOT NULL DEFAULT 0", tx);
        Execute("ALTER TABLE token_highwater ADD COLUMN previous_output_tokens INTEGER NOT NULL DEFAULT 0", tx);
        Execute("ALTER TABLE token_highwater ADD COLUMN previous_cache_read_tokens INTEGER NOT NULL DEFAULT 0", tx);
        Execute("ALTER TABLE token_highwater ADD COLUMN previous_cache_creation_tokens INTEGER NOT NULL DEFAULT 0", tx);

        Execute("CREATE UNIQUE INDEX ux_repo_identity_tenant_display ON repo_identity(tenant, repo_display)", tx);
        Execute("CREATE UNIQUE INDEX ux_agent_identity_tenant_display ON agent_identity(tenant, agent_display)", tx);
        Execute("CREATE UNIQUE INDEX ux_model_identity_tenant_display ON model_identity(tenant, model_display)", tx);
        Execute("CREATE UNIQUE INDEX ux_checkout_identity_tenant_display ON checkout_identity(tenant, checkout_display)", tx);
    }

    // Version 7: give each high-water row an INCARNATION, so a straggling reading from a session's previous
    // life cannot be counted as growth in its current one.
    //
    // THE DEFECT THIS CLOSES IS PERMANENT AND SCALES, which is why it is worth a column. A Director restarting
    // a session id makes its reported tally drop to zero and count again; the store adopts that as a reset. If
    // a second writer is in flight with a reading from BEFORE the reset, that reading arrives looking exactly
    // like ordinary growth - it is higher than the freshly-reset row - and is counted a second time. Measured:
    // 100 banked turns, a reset to 5, a straggling observation of 110, and the ledger ends at 210 against
    // honest activity of 115. Nothing rewrites an appended delta, so the 95 is wrong FOREVER, and its size
    // grows with the pre-reset watermark. A Director restart after a counter reset is an ordinary event, not
    // an edge case.
    //
    // The store can SEE a reset happen, so it can count them: generation advances by one each time a reset is
    // adopted, a writer sends the generation it last saw, and a reading whose belief is from an older
    // generation changes nothing. No producer change and no wire-contract change - the evidence was already
    // here, it just had nowhere to be recorded. See GatewayStatsWriter for the full rule.
    //
    // ADD-only, like version 6: every existing row starts at generation 0, which is exactly right - whatever
    // life it is counting, that is the first one this store has knowingly seen.
    private void MigrateToVersion7(SqliteTransaction tx)
    {
        foreach (var table in new[] { "session_highwater", "agent_driven_highwater", "token_highwater" })
            Execute($"ALTER TABLE {table} ADD COLUMN generation INTEGER NOT NULL DEFAULT 0", tx);
    }

    // Rebuild one table so the tenant can join its primary key: create the new shape, copy every existing row
    // under the local tenant, drop the old table, rename the new one into its place. The copy stamps the
    // local-tenant literal as the first selected column, which is why <paramref name="oldColumns"/> lists the
    // ORIGINAL columns (no tenant) and <paramref name="newColumns"/> lists them WITH tenant first.
    private void RebuildWithTenant(SqliteTransaction tx, string table, string createNewSql,
        string newColumns, string oldColumns, string local)
    {
        var tmp = table + "_v5";
        // createNewSql names `table`; build it under the temp name, then rename after the drop.
        Execute(createNewSql.Replace($"CREATE TABLE {table} ", $"CREATE TABLE {tmp} "), tx);
        Execute($"INSERT INTO {tmp}({newColumns}) SELECT '{local}', {oldColumns} FROM {table}", tx);
        Execute($"DROP TABLE {table}", tx);
        Execute($"ALTER TABLE {tmp} RENAME TO {table}", tx);
    }

    // Retire an incompatible pre-version-4 statistics store aside UNREAD, exactly as the legacy JSON store was
    // retired (GatewayInputStatsAggregator.RetireLegacyJsonStore). Runs once, before the database is opened.
    //
    // Why the old numbers are not carried across: version 4 changed what repo_id MEANS - the session's local
    // working-directory path became its "owner/repo" repo name. An existing row is keyed by a path, and the
    // Gateway cannot re-key it to a repo name: it has no filesystem to resolve the path against, and the path
    // may belong to another machine entirely. There is no faithful forward migration, so - as with the JSON
    // store and by the same owner ruling - the file is renamed aside (never deleted) and this store starts empty.
    //
    // The schema version is read straight from the SQLite header (a 4-byte big-endian integer at offset 60)
    // rather than by opening the file: no connection, no file lock, nothing to release before the rename.
    // Self-idempotent - the fresh database this build then creates is stamped version 4, so the next startup
    // reads 4 from the header and retires nothing.
    private void RetireIncompatibleStore()
    {
        if (!File.Exists(_path)) return;

        int version;
        try
        {
            Span<byte> header = stackalloc byte[64];
            int read;
            using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                read = fs.Read(header);
            // Too small to be a real database, or not a SQLite file at all: leave it in place and let the
            // normal open path either build the schema (empty file) or fail loudly (a genuinely bad file).
            if (read < 64) return;
            if (System.Text.Encoding.ASCII.GetString(header.Slice(0, 16)) != "SQLite format 3\0") return;
            version = (header[60] << 24) | (header[61] << 16) | (header[62] << 8) | header[63];
        }
        catch (Exception ex)
        {
            FileLog.Write($"[GatewayStatsDatabase] RetireIncompatibleStore: could not read header of {_path}: " +
                          $"{ex.Message}; leaving it in place for the normal open path");
            return;
        }

        // A store at version 4 or beyond has a faithful forward migration (v5 only adds a tenant column), so
        // it is NOT retired - it is migrated. Version 0 is not a real stamped store. Only a genuine version
        // 1..3 file is the incompatible store this retires (its repo_id is a path this build cannot re-key).
        if (version <= 0 || version >= OldestForwardMigratableVersion) return;

        var aside = _path + ".superseded-" +
            DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture);
        MoveAsideIfExists(_path, aside);
        MoveAsideIfExists(_path + "-wal", aside + "-wal");
        MoveAsideIfExists(_path + "-shm", aside + "-shm");
        FileLog.Write($"[GatewayStatsDatabase] RetireIncompatibleStore: the repository dimension changed from " +
                      $"local path to repo name at schema v{OldestForwardMigratableVersion}; renamed the superseded store " +
                      $"(v{version}) to {aside} UNREAD; starting empty");
    }

    private static void MoveAsideIfExists(string from, string to)
    {
        if (File.Exists(from)) File.Move(from, to);
    }

    private int QueryUserVersion()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "PRAGMA user_version";
        var result = cmd.ExecuteScalar();
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private void Execute(string sql, SqliteTransaction? tx = null)
    {
        using var cmd = _connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Execute with bound parameters. Values reach SQLite as parameters, never as text pasted into
    /// the statement - the same rule the aggregator's own Execute follows.</summary>
    private void Execute(string sql, SqliteTransaction? tx, params (string Name, object Value)[] parameters)
    {
        using var cmd = _connection.CreateCommand();
        if (tx is not null) cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value);
        cmd.ExecuteNonQuery();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _connection.Close();
        // Closing a pooled connection returns its native handle to the pool, where it keeps the file open for
        // the life of the process; the owner of this database (the Gateway, or a test) expects the file to be
        // free once it has disposed it. Only this file's pool is cleared, never every pool in the process, for
        // the reason GatewayDatabase.Dispose gives.
        SqliteConnection.ClearPool(_connection);
        _connection.Dispose();
        FileLog.Write($"[GatewayStatsDatabase] Dispose: closed {_path}");
    }
}
