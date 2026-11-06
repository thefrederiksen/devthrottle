using CcDirector.Gateway.Stats.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace CcDirector.Gateway.Stats.Data;

/// <summary>
/// The Gateway statistics store as an Entity Framework model: the sixteen tables that
/// <see cref="GatewayStatsDatabase"/> creates by hand at SQLite schema version 5, carried forward UNCHANGED
/// so one implementation serves SQLite (self-host) and PostgreSQL (hosted) from the same code.
///
/// That "one implementation, two providers" is the whole point of the port, and it is what makes a
/// provider-parametrised contract suite mean anything: it is one implementation run twice, not two
/// implementations compared.
///
/// THE NAMES ARE NOT NEGOTIABLE. Every table and column name is the version 5 name, in snake_case, and each
/// is pinned EXPLICITLY with ToTable / HasColumnName rather than left to a naming convention. A convention
/// is a rule that has to keep holding; an existing self-host gateway-stats.db is already on disk with these
/// exact names, and a convention change - or one property renamed by a well-meaning hand - would strand it.
/// Pinning them removes the rule.
///
/// WHAT IS DELIBERATELY ABSENT, so nobody adds it back as an improvement:
///
///  - NO navigation properties and NO foreign keys. Version 5 has none between these tables, the identity
///    map that relates them is held in memory, and adding them would change delete and insert ordering.
///  - NO global query filter on the tenant. Two of the sixteen tables (<c>repo_session</c>,
///    <c>agent_session</c>) genuinely have no tenant column at version 5 - they are partitioned indirectly
///    through their per-tenant surrogate ids - so a uniform filter cannot exist here. Scoping is the read and
///    write paths' job, and the contract suite asserts it, including asserting the accessors that currently
///    return every tenant's rows so that whenever that is changed the test turns red and NAMES the change
///    rather than passing silently.
///  - NO constraint anywhere that asks a database to decide whether two DIFFERENT display spellings are the
///    same identity. That decision is case-INSENSITIVE and stays in the aggregator's mirror; see
///    <see cref="RepoIdentityEntity"/>. The unique index each identity table DOES carry is on the exact
///    (tenant, spelling) pair, which is a duplicate under every comparer and asks no collation question -
///    it is what lets a mint read back which id won instead of assuming it minted one.
///
/// Threading: used through a pooled IDbContextFactory - a fresh context per operation, never shared across
/// threads.
/// </summary>
public sealed class GatewayStatsDbContext : DbContext
{
    /// <summary>The PostgreSQL schema this context owns. Its migration history table lives in the same
    /// schema (<c>gateway_stats.__EFMigrationsHistory</c>), so the statistics chain never shares a history
    /// table, a transaction or a startup gate with the main <c>gateway</c> schema's chain. SQLite is
    /// schemaless and uses none of this.</summary>
    public const string PostgresSchema = "gateway_stats";

    /// <summary>
    /// The database-level default on the <c>tenant</c> column of the eight delta and identity tables.
    ///
    /// It is in the MODEL, not only in the baseline's raw data definition language, and that distinction is
    /// what a review caught. Schema version 5 added this column with
    /// <c>ALTER TABLE ... ADD COLUMN tenant TEXT NOT NULL DEFAULT 'local'</c>, so the default is part of the
    /// shape of every self-host file on disk. Writing the correct text in the baseline while leaving the
    /// model silent about it hid the divergence from the baseline's output WITHOUT correcting the chain's
    /// target model - and the model is what a LATER migration is scaffolded against. The first migration
    /// needing a table rebuild would have diffed against a snapshot that does not know about this default and
    /// quietly dropped it.
    ///
    /// The eight tables that carry it are exactly the ones version 5 reached by ALTER TABLE. The high-water,
    /// membership and meta tables were REBUILT with the tenant in their primary key and have no default, so
    /// they must not be given one here.
    /// </summary>
    public const string TenantColumnDefault = "local";

    public GatewayStatsDbContext(DbContextOptions<GatewayStatsDbContext> options) : base(options)
    {
    }

    /// <summary>Observed human input deltas (<c>stat_delta</c>), append only.</summary>
    public DbSet<StatDeltaEntity> StatDeltas => Set<StatDeltaEntity>();

    /// <summary>Observed token-spend increases (<c>token_delta</c>), append only.</summary>
    public DbSet<TokenDeltaEntity> TokenDeltas => Set<TokenDeltaEntity>();

    /// <summary>Observed per-agent tally deltas (<c>agent_delta</c>), append only.</summary>
    public DbSet<AgentDeltaEntity> AgentDeltas => Set<AgentDeltaEntity>();

    /// <summary>Observed agent-to-agent deltas (<c>agent_driven_delta</c>), append only.</summary>
    public DbSet<AgentDrivenDeltaEntity> AgentDrivenDeltas => Set<AgentDrivenDeltaEntity>();

    /// <summary>Repository surrogate id to first-seen display spelling (<c>repo_identity</c>).</summary>
    public DbSet<RepoIdentityEntity> RepoIdentities => Set<RepoIdentityEntity>();

    /// <summary>Agent surrogate id to first-seen display spelling (<c>agent_identity</c>).</summary>
    public DbSet<AgentIdentityEntity> AgentIdentities => Set<AgentIdentityEntity>();

    /// <summary>Model surrogate id to first-seen display spelling (<c>model_identity</c>).</summary>
    public DbSet<ModelIdentityEntity> ModelIdentities => Set<ModelIdentityEntity>();

    /// <summary>Checkout surrogate id to first-seen display spelling (<c>checkout_identity</c>).</summary>
    public DbSet<CheckoutIdentityEntity> CheckoutIdentities => Set<CheckoutIdentityEntity>();

    /// <summary>Per-session per-bucket high-water counts (<c>session_highwater</c>).</summary>
    public DbSet<SessionHighwaterEntity> SessionHighwater => Set<SessionHighwaterEntity>();

    /// <summary>Per-session cumulative token high-water counts (<c>token_highwater</c>).</summary>
    public DbSet<TokenHighwaterEntity> TokenHighwater => Set<TokenHighwaterEntity>();

    /// <summary>Per-session agent-driven high-water counts (<c>agent_driven_highwater</c>).</summary>
    public DbSet<AgentDrivenHighwaterEntity> AgentDrivenHighwater => Set<AgentDrivenHighwaterEntity>();

    /// <summary>The all-time set of voice-mode sessions (<c>wingman_session</c>).</summary>
    public DbSet<WingmanSessionEntity> WingmanSessions => Set<WingmanSessionEntity>();

    /// <summary>The set of sessions already back-filled to their agent (<c>agents_seeded</c>).</summary>
    public DbSet<AgentsSeededEntity> AgentsSeeded => Set<AgentsSeededEntity>();

    /// <summary>The all-time repository-to-session membership set (<c>repo_session</c>). No tenant column -
    /// see <see cref="RepoSessionEntity"/>.</summary>
    public DbSet<RepoSessionEntity> RepoSessions => Set<RepoSessionEntity>();

    /// <summary>The all-time agent-to-session membership set (<c>agent_session</c>). No tenant column - see
    /// <see cref="AgentSessionEntity"/>.</summary>
    public DbSet<AgentSessionEntity> AgentSessions => Set<AgentSessionEntity>();

    /// <summary>Runtime scalars keyed by (tenant, name) (<c>meta</c>).</summary>
    public DbSet<MetaEntity> Meta => Set<MetaEntity>();

    /// <summary>All-time concurrency peaks, one row per tenant (<c>concurrency_peak</c>).</summary>
    public DbSet<ConcurrencyPeakEntity> ConcurrencyPeaks => Set<ConcurrencyPeakEntity>();

    /// <summary>The per-hour fleet activity log (<c>concurrency_hour</c>).</summary>
    public DbSet<ConcurrencyHourEntity> ConcurrencyHours => Set<ConcurrencyHourEntity>();

    /// <summary>The raw members of each hour's distinct sets (<c>concurrency_hour_member</c>) - restart
    /// durability for the in-memory dedup sets, nothing else.</summary>
    public DbSet<ConcurrencyHourMemberEntity> ConcurrencyHourMembers => Set<ConcurrencyHourMemberEntity>();

    /// <summary>Five-minute machine resource windows (<c>resource_window</c>), kept 90 days (Mission: Resource Monitor).</summary>
    public DbSet<ResourceWindowEntity> ResourceWindows => Set<ResourceWindowEntity>();

    /// <summary>Each session's share of a five-minute window (<c>resource_session_window</c>), kept 90 days.</summary>
    public DbSet<ResourceSessionWindowEntity> ResourceSessionWindows => Set<ResourceSessionWindowEntity>();

    /// <summary>Hourly machine roll-ups of the resource windows (<c>resource_hour</c>), kept two years.</summary>
    public DbSet<ResourceHourEntity> ResourceHours => Set<ResourceHourEntity>();

    /// <summary>Hourly session roll-ups of the resource windows (<c>resource_session_hour</c>), kept two years.</summary>
    public DbSet<ResourceSessionHourEntity> ResourceSessionHours => Set<ResourceSessionHourEntity>();

    /// <summary>
    /// The check constraint name for one table's non-empty tenant guard. Named per table because a
    /// PostgreSQL constraint name is unique within its schema, and named deterministically so a migration
    /// and the model always agree on it.
    /// </summary>
    public static string TenantNotEmptyConstraint(string table) => $"ck_{table}_tenant_not_empty";

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // ---- Delta tables - append only -----------------------------------------------------------

        modelBuilder.Entity<StatDeltaEntity>(b =>
        {
            b.ToTable("stat_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Modality).HasColumnName("modality").IsRequired();
            b.Property(e => e.Surface).HasColumnName("surface").IsRequired();
            b.Property(e => e.IsVoice).HasColumnName("is_voice").IsRequired();
            b.Property(e => e.RepoId).HasColumnName("repo_id").IsRequired();
            b.Property(e => e.Wingman).HasColumnName("wingman").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            // Nullable exactly where version 5 is nullable, and nowhere else. Both reached the table by ALTER
            // TABLE (model_id at version 2, checkout_id at version 4), and model_id's nullability is load
            // bearing at runtime as well - see the entity.
            b.Property(e => e.ModelId).HasColumnName("model_id");
            b.Property(e => e.CheckoutId).HasColumnName("checkout_id");
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            // Index names are pinned to the version 5 names with HasDatabaseName. Entity Framework would
            // otherwise mint IX_stat_delta_hour_utc, which is a DIFFERENT index from the ix_stat_delta_hour
            // already on every self-host file - so an adopted store would carry both the old index under its
            // old name and, on the next migration, a new one beside it.
            b.HasIndex(e => e.HourUtc).HasDatabaseName("ix_stat_delta_hour");
            b.HasIndex(e => new { e.Tenant, e.HourUtc }).HasDatabaseName("ix_stat_delta_tenant_hour");
        });

        modelBuilder.Entity<TokenDeltaEntity>(b =>
        {
            b.ToTable("token_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.HourUtc).HasColumnName("hour_utc").IsRequired();
            b.Property(e => e.ModelId).HasColumnName("model_id");
            b.Property(e => e.InputTokens).HasColumnName("input_tokens").IsRequired();
            b.Property(e => e.OutputTokens).HasColumnName("output_tokens").IsRequired();
            b.Property(e => e.CacheReadTokens).HasColumnName("cache_read_tokens").IsRequired();
            b.Property(e => e.CacheCreationTokens).HasColumnName("cache_creation_tokens").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => e.HourUtc).HasDatabaseName("ix_token_delta_hour");
            b.HasIndex(e => new { e.Tenant, e.HourUtc }).HasDatabaseName("ix_token_delta_tenant_hour");
        });

        modelBuilder.Entity<AgentDeltaEntity>(b =>
        {
            b.ToTable("agent_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.IsVoice).HasColumnName("is_voice").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        modelBuilder.Entity<AgentDrivenDeltaEntity>(b =>
        {
            b.ToTable("agent_driven_delta");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
        });

        // ---- Identity tables - surrogate id to FIRST-SEEN display spelling -------------------------
        //
        // THE DATABASE STILL DOES NOT DECIDE IDENTITY. It never case-folds, never compares two DIFFERENT
        // spellings, and is never grouped or ordered by a display column; the StringComparer.OrdinalIgnoreCase
        // in the aggregator's mirror remains the only thing that decides whether two strings are one
        // repository. RepoIdentityEntity carries that reasoning and it is unchanged.
        //
        // What IS enforced, per table, is a unique index on (tenant, display) - the EXACT byte-for-byte pair.
        // Two rows with the identical spelling under one tenant are a duplicate under EVERY comparer, the
        // case-insensitive one included, so forbidding them asks no question about collation at all. Both
        // providers compare these columns byte-ordinally (SQLite BINARY; the "C" collation pinned below on
        // PostgreSQL), so the index means the same thing on both.
        //
        // It exists because a surrogate id must be MINTED ONCE and READ BACK, never assumed. Without a unique
        // index there is no conflict target, so two hosted containers minting "owner/repo" for one tenant at
        // the same moment each get their OWN id and that tenant's turns split silently across two rows. With
        // it, the second writer's insert conflicts, the statement returns the id that WON, and both writers
        // file under it. Spellings that differ only by case still mint separate ids - that is the mirror's
        // business, exactly as before, and is called out again in GatewayStatsWriter.

        modelBuilder.Entity<RepoIdentityEntity>(b =>
        {
            b.ToTable("repo_identity");
            b.HasKey(e => e.RepoId);
            b.Property(e => e.RepoId).HasColumnName("repo_id").ValueGeneratedOnAdd();
            b.Property(e => e.RepoDisplay).HasColumnName("repo_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => new { e.Tenant, e.RepoDisplay }).IsUnique().HasDatabaseName("ux_repo_identity_tenant_display");
        });

        modelBuilder.Entity<AgentIdentityEntity>(b =>
        {
            b.ToTable("agent_identity");
            b.HasKey(e => e.AgentId);
            b.Property(e => e.AgentId).HasColumnName("agent_id").ValueGeneratedOnAdd();
            b.Property(e => e.AgentDisplay).HasColumnName("agent_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => new { e.Tenant, e.AgentDisplay }).IsUnique().HasDatabaseName("ux_agent_identity_tenant_display");
        });

        modelBuilder.Entity<ModelIdentityEntity>(b =>
        {
            b.ToTable("model_identity");
            b.HasKey(e => e.ModelId);
            b.Property(e => e.ModelId).HasColumnName("model_id").ValueGeneratedOnAdd();
            b.Property(e => e.ModelDisplay).HasColumnName("model_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => new { e.Tenant, e.ModelDisplay }).IsUnique().HasDatabaseName("ux_model_identity_tenant_display");
        });

        modelBuilder.Entity<CheckoutIdentityEntity>(b =>
        {
            b.ToTable("checkout_identity");
            b.HasKey(e => e.CheckoutId);
            b.Property(e => e.CheckoutId).HasColumnName("checkout_id").ValueGeneratedOnAdd();
            b.Property(e => e.CheckoutDisplay).HasColumnName("checkout_display").IsRequired();
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired()
                .HasDefaultValue(TenantColumnDefault);
            b.HasIndex(e => new { e.Tenant, e.CheckoutDisplay }).IsUnique().HasDatabaseName("ux_checkout_identity_tenant_display");
        });

        // ---- High-water tables - the read-modify-write paths ---------------------------------------
        //
        // The tenant is the FIRST key column on all three, as version 5 rebuilt them. Without it in the key,
        // two tenants pushing the same bare session id collide and one silently overwrites the other.
        //
        // Every one of them carries previous_* columns beside its counts (schema version 6). They hold what
        // the row held immediately BEFORE the last raise, written by the raise statement itself so that one
        // atomic statement can return both halves and the writer can append exactly the difference the
        // database made. Nothing reads them as a statistic; see SessionHighwaterEntity and GatewayStatsWriter.

        modelBuilder.Entity<SessionHighwaterEntity>(b =>
        {
            b.ToTable("session_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId, e.Modality, e.Surface });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Modality).HasColumnName("modality").IsRequired();
            b.Property(e => e.Surface).HasColumnName("surface").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.PreviousTurns).HasColumnName("previous_turns").IsRequired();
            b.Property(e => e.PreviousChars).HasColumnName("previous_chars").IsRequired();
            b.Property(e => e.Generation).HasColumnName("generation").IsRequired();
        });

        modelBuilder.Entity<TokenHighwaterEntity>(b =>
        {
            b.ToTable("token_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.InputTokens).HasColumnName("input_tokens").IsRequired();
            b.Property(e => e.OutputTokens).HasColumnName("output_tokens").IsRequired();
            b.Property(e => e.CacheReadTokens).HasColumnName("cache_read_tokens").IsRequired();
            b.Property(e => e.CacheCreationTokens).HasColumnName("cache_creation_tokens").IsRequired();
            b.Property(e => e.PreviousInputTokens).HasColumnName("previous_input_tokens").IsRequired();
            b.Property(e => e.PreviousOutputTokens).HasColumnName("previous_output_tokens").IsRequired();
            b.Property(e => e.PreviousCacheReadTokens).HasColumnName("previous_cache_read_tokens").IsRequired();
            b.Property(e => e.PreviousCacheCreationTokens).HasColumnName("previous_cache_creation_tokens").IsRequired();
            b.Property(e => e.Generation).HasColumnName("generation").IsRequired();
        });

        modelBuilder.Entity<AgentDrivenHighwaterEntity>(b =>
        {
            b.ToTable("agent_driven_highwater");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
            b.Property(e => e.Turns).HasColumnName("turns").IsRequired();
            b.Property(e => e.Chars).HasColumnName("chars").IsRequired();
            b.Property(e => e.PreviousTurns).HasColumnName("previous_turns").IsRequired();
            b.Property(e => e.PreviousChars).HasColumnName("previous_chars").IsRequired();
            b.Property(e => e.Generation).HasColumnName("generation").IsRequired();
        });

        // ---- Membership tables - all-time distinct sets, never pruned ------------------------------

        modelBuilder.Entity<WingmanSessionEntity>(b =>
        {
            b.ToTable("wingman_session");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        modelBuilder.Entity<AgentsSeededEntity>(b =>
        {
            b.ToTable("agents_seeded");
            b.HasKey(e => new { e.Tenant, e.SessionId });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        // repo_session and agent_session have NO tenant column at version 5. That is carried forward exactly:
        // they are partitioned INDIRECTLY through their per-tenant surrogate ids. Adding a tenant column is a
        // behaviour change and is outside this port's scope - see RepoSessionEntity.
        modelBuilder.Entity<RepoSessionEntity>(b =>
        {
            b.ToTable("repo_session");
            b.HasKey(e => new { e.RepoId, e.SessionId });
            b.Property(e => e.RepoId).HasColumnName("repo_id").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        modelBuilder.Entity<AgentSessionEntity>(b =>
        {
            b.ToTable("agent_session");
            b.HasKey(e => new { e.AgentId, e.SessionId });
            b.Property(e => e.AgentId).HasColumnName("agent_id").IsRequired();
            b.Property(e => e.SessionId).HasColumnName("session_id").IsRequired();
        });

        // ---- Scalar table --------------------------------------------------------------------------

        modelBuilder.Entity<MetaEntity>(b =>
        {
            b.ToTable("meta");
            b.HasKey(e => new { e.Tenant, e.Name });
            b.Property(e => e.Tenant).HasColumnName("tenant").IsRequired();
            b.Property(e => e.Name).HasColumnName("name").IsRequired();
            b.Property(e => e.Value).HasColumnName("value").IsRequired();
        });

        // ---- Provider-specific model shape ---------------------------------------------------------
        //
        // Guarded by Database.IsNpgsql() so the SQLite path is 100% unchanged - a self-host file is already on
        // disk in exactly this shape and its snapshot must not move. IsNpgsql() is available at model-build
        // time because both the runtime pooled factory and the design-time factory construct this context with
        // a concrete provider already selected. This is a provider CONDITIONAL, never a try/catch fallback.
        if (Database.IsNpgsql())
        {
            // 1. This context's OWN schema. SQLite is schemaless (one file, no schema namespace) and needs
            //    none; on Postgres every table goes under gateway_stats, matching the migrations history table
            //    (gateway_stats.__EFMigrationsHistory) the database and the design-time factory pin. It is
            //    deliberately NOT the main context's "gateway" schema: separate schema, separate history
            //    table, separate connection pool, so the statistics chain can never share a transaction or a
            //    startup gate with the chain that gates the deploy.
            modelBuilder.HasDefaultSchema(PostgresSchema);

            // 2. Byte-ordinal collation on EVERY text column. SQLite compares all text with its default BINARY
            //    collation (memcmp), which is what every key, index, range and ordering in the version 5 store
            //    is built on; PostgreSQL's default collation is LOCALE-based. Pinning "C" makes both providers
            //    compare these columns identically, which is what output parity on the same rows requires.
            //
            //    Applied to EVERY text column rather than only the key columns, as ONE uniform rule, because
            //    the alternative is a rule someone has to keep remembering per column. hour_utc alone shows
            //    why: it is not a key, but every hourly and working-day projection RANGES and ORDERS on it as
            //    text, and the ARCHIVE marker sorts against real hour keys. The display columns are never
            //    compared by the database at all, so pinning them too costs nothing and removes the question.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
                foreach (var property in entityType.GetProperties())
                    if (property.ClrType == typeof(string))
                        property.SetCollation("C");

        }

        ConcurrencyStatsModel.Configure(modelBuilder);
        // Mission: Resource Monitor. Before the tenant guard below, so the four tables get their constraint under their
        // real names, for the reason that guard's comment gives.
        ResourceSummaryStatsModel.Configure(modelBuilder);

        // SQLITE IS UNTOUCHED BY ALL OF THIS, exactly as it is by the block above: a self-host
        // gateway-stats.db is already on disk carrying the tenant default, and removing it there would
        // force a table rebuild on every existing file to buy nothing on a single-tenant install.
        if (Database.IsNpgsql())
        {
            // THE TENANT GUARD IS APPLIED HERE, AFTER ConcurrencyStatsModel.Configure, AND THE ORDER IS
            // LOAD-BEARING. It used to sit inside the PostgreSQL block above, which runs BEFORE the
            // concurrency entities are given their table names - so entityType.GetTableName() returned the
            // DbSet-derived default for those three, and their constraints were created as
            // ck_ConcurrencyPeaks_tenant_not_empty rather than ck_concurrency_peak_tenant_not_empty. The
            // constraints still guarded the right tables, so nothing was unprotected - but the name did not
            // match what TenantNotEmptyConstraint() returns, which makes the helper wrong for those tables and
            // would send anyone reading a violation to a constraint that does not exist under that name.
            // Found by the migration-transition proof, which tried to drop the constraints by their computed
            // names and could not.
            // 3. NO TENANT DEFAULT, AND NO EMPTY TENANT. This is the multi-tenant schema, and it must not
            //    accept a row whose owner was not stated.
            //
            //    THE DEFAULT stays on SQLite and goes here, and the asymmetry is the point rather than an
            //    inconsistency. Self-host has exactly one tenant, so a "local" default there is harmless by
            //    construction, and removing it would force a table rebuild on every statistics file already
            //    on disk to buy nothing. On the shared hosted schema the same default is FAIL-OPEN: a writer
            //    that omits the tenant column has its row quietly filed under Local instead of being
            //    refused, which is silent mispartitioning of one customer's numbers into another partition.
            //
            //    THE CHECK CONSTRAINT closes the other half, and without it dropping the default would be
            //    half a fix. The tenant property initialises to an empty string, so a write that forgets to
            //    set it does not send NULL and collide with the missing default - it sends '' and is stored
            //    as a perfectly valid value naming nobody. Unattributed is unattributed either way, so the
            //    schema refuses both: absent (no default, NOT NULL) and empty (this constraint).
            //
            //    Applied to every table that HAS a tenant column, including the ones that never carried a
            //    default, because the empty-string hole was never about the default.
            foreach (var entityType in modelBuilder.Model.GetEntityTypes())
            {
                var tenant = entityType.FindProperty(nameof(Entities.StatDeltaEntity.Tenant));
                if (tenant is null || tenant.ClrType != typeof(string)) continue;

                tenant.SetDefaultValue(null);

                var table = entityType.GetTableName();
                if (table is null) continue;

                // AN ALLOWLIST OF THE CHARACTERS A TENANT CAN CONTAIN - not a denylist of the ones it
                // cannot. This is the third predicate here and the first one of the right SHAPE, and the
                // reason the previous two failed is worth more than either of them.
                //
                // WHAT WENT WRONG THREE TIMES. `tenant <> ''` refused the empty string only; an inspector
                // inserted THREE SPACES and read them back. `btrim("tenant") <> ''` closed that for
                // SPACES, because PostgreSQL's one-argument btrim strips the space character and nothing
                // else; a tab walked through. `"tenant" ~ '[^[:space:]]'` closed tab and newline, and an
                // inspector then enumerated all twenty-five characters .NET calls whitespace, found FOUR
                // that POSIX does not - U+0085, U+00A0, U+2007, U+202F - and inserted chr(160) as a real
                // row's tenant, reading it back at length 1.
                //
                // Each attempt narrowed the gap and none closed it, because the shape was wrong: a denylist
                // has to enumerate every bad value, and we were trying to mirror .NET's idea of whitespace
                // with a different engine's idea of it. Those two sets do not agree and Unicode keeps
                // supplying more members. An allowlist inverts the burden - every whitespace spelling,
                // including ones nobody has thought of yet, is refused as a SIDE EFFECT of not being an
                // allowed character.
                //
                // THE DERIVATION, from the code rather than from assumption. A tenant is not free text; it
                // is a minted value with exactly three provenances:
                //   - TenantId.Local  = "local"                    (self-host, and the column default)
                //   - TenantId.System = "system"                   (reserved, hosted non-account rows)
                //   - a real account  = Guid.NewGuid().ToString()  (TenantRegistry.MintOrLookupBySubject)
                // The only other production construction is SkillStore's library partition, which is
                // whichever of Local or System was ambient, so it adds no new spelling. The union of
                // characters across all three is lower-case letters, digits and the hyphen, and .NET's
                // Guid "D" format is lower-case hex with hyphens, so nothing minted can fall outside it.
                //
                // The pattern is anchored at both ends, so it constrains EVERY character rather than
                // asserting that one acceptable character exists somewhere - the mistake the previous
                // version made in miniature.
                //
                // THIS IS DELIBERATELY NARROWER THAN TenantId, AND IF YOU ARE READING THIS BECAUSE OF A
                // CONSTRAINT VIOLATION, START HERE. TenantId only TRIMS its input - it does not lower-case
                // it and it does not restrict the character set - so `new TenantId("Alice")` is a perfectly
                // legal tenant that this column will REFUSE. That gap is intentional and was accepted with
                // its eyes open, on three grounds: no production path can produce such a value (the four
                // construction sites are Local, System, the Guid mint, and SkillStore's library partition,
                // and every one of them yields lower-case); the failure mode is LOUD and lands at
                // development time as a named constraint violation on insert, never as silent
                // mispartitioning or a wrong-tenant read; and the alternative - matching TenantId exactly -
                // is the denylist that failed three times.
                //
                // So a violation here does NOT mean the schema is broken. It means a writer used a tenant
                // spelling production has never produced. Fix the caller if the value came from a test or a
                // new code path that should be minting properly; widen this pattern only if a real
                // production mint has started producing a spelling it excludes - and if you widen it, widen
                // the allowlist, never soften it back into "not whitespace".
                entityType.AddCheckConstraint(TenantNotEmptyConstraint(table), "\"tenant\" ~ '^[a-z0-9-]+$'");
            }
        }
    }
}
