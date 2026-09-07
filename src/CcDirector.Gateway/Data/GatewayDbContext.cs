using CcDirector.Gateway.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace CcDirector.Gateway.Data;

/// <summary>
/// The Gateway's EF Core data-access context (Hosted Gateway mission, Step 1b). One model runs on SQLite
/// locally and (in a later phase) Postgres in the cloud, so the model is kept provider-AGNOSTIC: no
/// SQLite-only or Postgres-only construct appears here, and the common-subset discipline is baked into the
/// model conventions so every store that moves onto this layer inherits it.
///
/// This context is used through a pooled <c>IDbContextFactory</c> - a fresh context per operation, never
/// shared across threads - and each store keeps its own write lock, preserving the Gateway's single-writer
/// invariant. Because pooling reuses instances, the context takes only its options; the ambient tenant is
/// supplied per operation via <see cref="ActiveTenant"/>, which the store sets from the
/// <see cref="Core.Tenancy.ITenantContext"/> before reading or writing.
///
/// The common-subset conventions (applied in <see cref="OnModelCreating"/>):
///  - Timestamps are stored/read as UTC <see cref="DateTime"/> (never DateTimeOffset - EF's SQLite converter
///    has a cross-timezone bug); a converter forces UTC on the way in and marks the Kind UTC on the way out.
///  - A bare <see cref="decimal"/> mapping is FORBIDDEN (SQLite has no real decimal and EF falls to double,
///    which would silently break the "round up, never undercount" money rule). Cron has none; the guard
///    protects later money-bearing stores.
///  - GUID keys are generated in code, never by a database default.
///  - Every table carries a <c>tenant_id</c> column and a global query filter scoping reads to
///    <see cref="ActiveTenant"/>, so a tenant never reads another tenant's rows. On the single-tenant local
///    install this is always "local" and behavior is identical to a store with no tenant column.
/// </summary>
public sealed class GatewayDbContext : DbContext
{
    /// <summary>
    /// The tenant the current unit of work is scoped to (the raw <see cref="Core.Tenancy.TenantId.Value"/>).
    /// Set by the store from the ambient tenant context before every read or write; the global query filter
    /// compares each row's <c>tenant_id</c> against it. Null only on a context that has not been scoped yet -
    /// the store always sets it, and it fails loud there on an invalid tenant, so a null never reaches a query.
    /// </summary>
    public string? ActiveTenant { get; set; }

    public GatewayDbContext(DbContextOptions<GatewayDbContext> options) : base(options)
    {
    }

    /// <summary>
    /// Set by <see cref="Rules.SessionRuleStore.Promote"/> for the one save that moves a rule to live,
    /// naming the rule a person asked about. Null the rest of the time, which is what makes a rule
    /// changing state through any other route a refusal - see <see cref="GuardRuleWrites"/>.
    /// </summary>
    internal Guid? PromotionInEffect { get; set; }

    /// <summary>
    /// True for exactly as long as a save that has been through <see cref="GuardRuleWrites"/> is issuing
    /// its statements. <see cref="RuleTableWriteInterceptor"/> reads it to tell a gated write from a bulk
    /// statement that never met the gate at all.
    /// </summary>
    internal bool SavingThroughTheGate { get; private set; }

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        GuardRuleWrites();
        SavingThroughTheGate = true;
        try { return base.SaveChanges(acceptAllChangesOnSuccess); }
        finally { SavingThroughTheGate = false; }
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(
        bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        GuardRuleWrites();
        SavingThroughTheGate = true;
        try { return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken); }
        finally { SavingThroughTheGate = false; }
    }


    /// <summary>
    /// THE WRITE GATE FOR SESSION RULES, AND IT IS HERE BECAUSE HERE IS WHERE IT CANNOT BE GONE ROUND.
    ///
    /// The store's own source used to claim that nothing reached <c>session_rules</c> without passing
    /// <see cref="Rules.RuleCallValidator"/>. It was not true. The entity, its setters, the DbSet and the
    /// context factory are all public, so any Gateway caller could build a rule with an arbitrary call
    /// document, an arbitrary tenant and <c>State = "live"</c>, add it and save it - meeting neither the
    /// validator nor dry run. An independent inspection found the claim and the code disagreeing, and a
    /// FALSE STRUCTURAL CLAIM IN A COMMENT IS WORSE THAN AN HONEST ABSENCE, because the next author
    /// trusts it.
    ///
    /// So the claim was made true rather than deleted. Every route to the table - the store, the DbSet, a
    /// context built by hand - ends at <c>SaveChanges</c>, and this runs there. What it holds:
    ///
    ///  - a rule's calls are valid against the shipped check registry, on every write;
    ///  - a NEW rule is in dry run, whoever wrote it (owner ruling 14, the bound that puts a person
    ///    between an instruction and its first real use);
    ///  - a rule moving to live carries a promotion a person asked for (<see cref="PromotionInEffect"/>);
    ///  - a rule belongs to the tenant this context is scoped to, so a write cannot plant a row in
    ///    another account's partition - which reading is already scoped against, but writing was not.
    ///
    /// It is a GATE and not a wall: a rule the store built passes straight through it, and there is a test
    /// that says so, because every refusal test above would also pass on a gate that refused everything.
    /// </summary>
    /// <exception cref="Rules.RuleRejectedException">A rule write does not hold, with the reason.</exception>
    private void GuardRuleWrites()
    {
        foreach (var entry in ChangeTracker.Entries<SessionRuleEntity>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var rule = entry.Entity;

            if (!string.Equals(rule.TenantId, ActiveTenant, StringComparison.Ordinal))
                throw new Rules.RuleRejectedException(
                    $"this rule says it belongs to '{rule.TenantId}', and this connection belongs to " +
                    $"'{ActiveTenant}'. A rule is written into the account that is writing it.");

            // EVERYTHING A RULE HAS TO BE, from the one place that says so. This used to check the call
            // document alone, so a rule with no instruction, no screen description, no trigger words and
            // no ceilings passed the gate while the store refused every one of them.
            Rules.SessionRuleRecordRules.CheckRule(rule, Rules.RulePrimitiveRegistry.Default);

            if (entry.State == EntityState.Added && !string.Equals(rule.State, DryRunState, StringComparison.Ordinal))
                throw new Rules.RuleRejectedException(
                    $"a new rule is always created in dry run, and this one says '{rule.State}'. Dry run is " +
                    "what puts a person between a standing instruction and the first time it types " +
                    "anything, so there is no route by which a rule can start live.");

            if (entry.State == EntityState.Modified
                && string.Equals(rule.State, LiveState, StringComparison.Ordinal)
                && PromotionInEffect != rule.Id)
                throw new Rules.RuleRejectedException(
                    "a rule is moved out of dry run by a person, and this write carried no evidence that a " +
                    "person asked. Nothing that runs on its own can promote a rule.");
        }

        // THE RECORD IS GATED TOO, AND IT WAS NOT. The firing table had no gate at all, so an invented row
        // saying nothing could be written straight through the context and read back later as evidence of
        // something that never happened. The record is the product; a row nobody can trust is worse than no
        // row, because a reader trusts it.
        foreach (var entry in ChangeTracker.Entries<SessionRuleFiringEntity>())
        {
            if (entry.State != EntityState.Added && entry.State != EntityState.Modified) continue;
            var firing = entry.Entity;

            if (!string.Equals(firing.TenantId, ActiveTenant, StringComparison.Ordinal))
                throw new Rules.RuleRejectedException(
                    $"this firing says it belongs to '{firing.TenantId}', and this connection belongs to " +
                    $"'{ActiveTenant}'. A record is written into the account it happened in.");

            // The rule's stored state, read through this same scoped context, because "may this firing say
            // it typed something" is a question about the rule and not about the row being written.
            var state = SessionRules.AsNoTracking()
                .Where(r => r.Id == firing.RuleId)
                .Select(r => r.State)
                .FirstOrDefault();

            Rules.SessionRuleRecordRules.CheckFiring(
                firing, Rules.RulePrimitiveRegistry.Default, state, DryRunState);
        }
    }

    // Derived from the states themselves, never typed out again: a second copy of a wire value is a
    // second thing to keep in step, and this one decides whether a rule may act.
    private static readonly string DryRunState =
        Rules.RuleWireNames.ToWireName(nameof(Rules.RuleState.DryRun));
    private static readonly string LiveState =
        Rules.RuleWireNames.ToWireName(nameof(Rules.RuleState.Live));

    /// <summary>Cron job definitions (<c>cron_jobs</c>).</summary>
    public DbSet<CronJobEntity> CronJobs => Set<CronJobEntity>();

    /// <summary>Cron run history (<c>cron_runs</c>), one row per fire.</summary>
    public DbSet<CronRunEntity> CronRuns => Set<CronRunEntity>();

    /// <summary>Named work lists (<c>worklists</c>).</summary>
    public DbSet<WorkListEntity> WorkLists => Set<WorkListEntity>();

    /// <summary>Work-list item references (<c>worklist_items</c>), ordered per list.</summary>
    public DbSet<WorkListItemEntity> WorkListItems => Set<WorkListItemEntity>();

    /// <summary>Workflow head records (<c>workflows</c>) - identity and lifecycle, never content.</summary>
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();

    /// <summary>Immutable workflow content versions (<c>workflow_versions</c>).</summary>
    public DbSet<WorkflowVersionEntity> WorkflowVersions => Set<WorkflowVersionEntity>();

    /// <summary>Helper files belonging to a workflow version (<c>workflow_files</c>).</summary>
    public DbSet<WorkflowFileEntity> WorkflowFiles => Set<WorkflowFileEntity>();

    /// <summary>Workflow runs (<c>workflow_runs</c>) - the governance outcome spine (issue #1771).</summary>
    public DbSet<WorkflowRunEntity> WorkflowRuns => Set<WorkflowRunEntity>();

    /// <summary>Skill head records (<c>skills</c>) - identity and lifecycle, never content. The
    /// central skill library (devthrottle_internal issue 995): a separate register from workflows,
    /// sharing their storage shape.</summary>
    public DbSet<SkillEntity> Skills => Set<SkillEntity>();

    /// <summary>Immutable skill content versions (<c>skill_versions</c>).</summary>
    public DbSet<SkillVersionEntity> SkillVersions => Set<SkillVersionEntity>();

    /// <summary>Supporting files belonging to a skill version (<c>skill_files</c>).</summary>
    public DbSet<SkillFileEntity> SkillFiles => Set<SkillFileEntity>();

    /// <summary>Pending session snoozes (<c>snoozes</c>), keyed by session id.</summary>
    public DbSet<SnoozeEntity> Snoozes => Set<SnoozeEntity>();

    /// <summary>The append-only governance event ledger (<c>governance_events</c>) - immutable session/run
    /// state transitions, the duration spine of issue #1771.</summary>
    public DbSet<GovernanceEventEntity> GovernanceEvents => Set<GovernanceEventEntity>();

    /// <summary>Web Push subscriptions (<c>push_subscriptions</c>), keyed by browser push endpoint.</summary>
    public DbSet<PushSubscriptionEntity> PushSubscriptions => Set<PushSubscriptionEntity>();

    /// <summary>The wingman instructions state document (<c>wingman_instructions</c>), one row per tenant.</summary>
    public DbSet<WingmanInstructionEntity> WingmanInstructions => Set<WingmanInstructionEntity>();

    /// <summary>Per-session token effort and honest spend (<c>session_spend</c>) - raw tokens + billing-mode
    /// label, the driver-normalized spend of issue #1771 (spine item 3).</summary>
    public DbSet<SessionSpendEntity> SessionSpend => Set<SessionSpendEntity>();

    /// <summary>Account-level hosted-AI service dollars (<c>account_hosted_ai_spend</c>) mirrored from the
    /// cloud credit-debit ledger - real metered dollars, never pinned to a session or run (issue #1771).</summary>
    public DbSet<AccountHostedAiSpendEntity> AccountHostedAiSpend => Set<AccountHostedAiSpendEntity>();

    /// <summary>Mission WHY notes (<c>mission_notes</c>), keyed by the normalized mission name.</summary>
    public DbSet<MissionNoteEntity> MissionNotes => Set<MissionNoteEntity>();

    /// <summary>Workspaces (<c>workspaces</c>, issue #2722) - a named set of seats, authored by hand or
    /// captured from a running Director. Stored here rather than on the machine because it must be readable
    /// when that machine is down.</summary>
    public DbSet<WorkspaceEntity> Workspaces => Set<WorkspaceEntity>();

    /// <summary>Stored dictation transcripts (<c>dictation_transcripts</c>, issue #509) - the raw and cleaned
    /// transcript per utterance, per tenant, write-only, for later mistranscription mining (devthrottle #2075).</summary>
    public DbSet<DictationTranscriptEntity> DictationTranscripts => Set<DictationTranscriptEntity>();

    /// <summary>The latest repo-state snapshot per (tenant, director, repository) (<c>repo_state</c>, issue
    /// #2118) - the branches and worktrees the morning report's hygiene recommendations read. Overwritten on
    /// each push; never appended.</summary>
    public DbSet<RepoStateEntity> RepoState => Set<RepoStateEntity>();

    /// <summary>The latest skill-placement outcome per Director and agent family
    /// (<c>skill_placement_state</c>) - whether the skills this Gateway serves can actually be READ on the
    /// machines it serves them to. Overwritten on each report; never appended.</summary>
    public DbSet<SkillPlacementStateEntity> SkillPlacementState => Set<SkillPlacementStateEntity>();

    /// <summary>Dismissed dictionary suggestions (<c>dictation_suggestion_dismissals</c>, devthrottle #2075) -
    /// one row per term the tenant told us to stop suggesting, with a snapshot of the evidence so the
    /// "Dismissed terms" screen can still explain why it was once offered. Tenant-scoped.</summary>
    public DbSet<DictationSuggestionDismissalEntity> DictationSuggestionDismissals => Set<DictationSuggestionDismissalEntity>();

    /// <summary>Model verdicts on mined dictionary-suggestion candidates (<c>dictation_suggestion_verdicts</c>,
    /// devthrottle #2115) - one row per tenant per term, judged at most once ever; scans read stored verdicts
    /// instead of re-asking the screening model. Tenant-scoped.</summary>
    public DbSet<DictationSuggestionVerdictEntity> DictationSuggestionVerdicts => Set<DictationSuggestionVerdictEntity>();

    /// <summary>The stored result of each tenant's most recent dictionary-suggestion scan
    /// (<c>dictation_suggestion_scans</c>, devthrottle #2115) - one row per tenant; the badge and the
    /// Dictionary page read this row instead of triggering mining. Tenant-scoped.</summary>
    public DbSet<DictationSuggestionScanEntity> DictationSuggestionScans => Set<DictationSuggestionScanEntity>();

    /// <summary>The append-only governance audit trail (<c>governance_audit_events</c>) - structured
    /// intervention and permission/sandbox decisions, never inferred from transcripts (issue #1771).</summary>
    public DbSet<GovernanceAuditEventEntity> GovernanceAuditEvents => Set<GovernanceAuditEventEntity>();

    /// <summary>The append-only activity ledger (<c>activity_events</c>) - submission, terminal-output,
    /// state-transition, transcript and snooze lifecycle evidence, per tenant, retained 30 days (the
    /// trustworthy-Working-start plan, docs/PLAN-trustworthy-working-start-2026-07-24.md).</summary>
    public DbSet<ActivityEventEntity> ActivityEvents => Set<ActivityEventEntity>();

    /// <summary>The durable per-session work-history record (<c>session_history</c>, issue #2194) - one row
    /// per fleet session, written while the session RUNS so a power cut still leaves a usable record, with
    /// the Gateway-ruled ending and the summary sealed at close or generated from the prompt log.</summary>
    public DbSet<SessionHistoryEntity> SessionHistory => Set<SessionHistoryEntity>();

    /// <summary>The durable repository catalog used by machine-scoped session creation search.</summary>
    public DbSet<KnownRepositoryEntity> KnownRepositories => Set<KnownRepositoryEntity>();

    /// <summary>Cached per-repository per-day roll-up paragraphs (<c>session_history_rollups</c>, issue
    /// #2194) - computed once in the background sweep, never on page load.</summary>
    public DbSet<SessionHistoryRollupEntity> SessionHistoryRollups => Set<SessionHistoryRollupEntity>();

    /// <summary>The stored conversation (<c>session_turns</c>): one row per pushed message, the turn-push
    /// mission's single source for Chat, the transcript view, and the wingman.</summary>
    public DbSet<SessionTurnEntity> SessionTurns => Set<SessionTurnEntity>();

    /// <summary>One row per session (<c>session_turn_heads</c>): its current generation, the contiguous
    /// watermark, and the per-session facts a history read needs.</summary>
    public DbSet<SessionTurnHeadEntity> SessionTurnHeads => Set<SessionTurnHeadEntity>();

    /// <summary>Per-tenant setting overrides (<c>tenant_settings</c>, issue #2017) - the per-tenant home the
    /// AI / voice / car-mode / notification settings needed before they could be served on the hosted Gateway.
    /// Tenant-scoped: an absent row means "no override" and the typed resolver returns the operator global
    /// default, never another tenant's value.</summary>
    public DbSet<TenantSettingEntity> TenantSettings => Set<TenantSettingEntity>();

    /// <summary>Per-tenant on/off choices for BUILT-IN workflows (<c>workflow_tenant_overrides</c>, the Shared
    /// Workflow Library phase 2). The built-ins live once in the shared library partition and are read-only to
    /// tenants, so a tenant's switch flip lands here and the read paths fold it over the library value. An
    /// absent row means "no choice made" - the workflow serves with the library's shipped state.</summary>
    public DbSet<WorkflowTenantOverrideEntity> WorkflowTenantOverrides => Set<WorkflowTenantOverrideEntity>();

    /// <summary>Per-tenant on/off choices for BUILT-IN skills (<c>skill_tenant_overrides</c>). The
    /// built-ins live once in the shared library partition and are read-only to tenants, so a
    /// tenant's choice lands here; an absent row means "no choice made".</summary>
    public DbSet<SkillTenantOverrideEntity> SkillTenantOverrides => Set<SkillTenantOverrideEntity>();

    /// <summary>The account-to-tenant mapping (<c>tenants</c>), keyed by the verified Supabase subject. This
    /// is the GLOBAL mapping table (Hosted Multi-Tenancy increment 1) - deliberately NOT tenant-scoped, so it
    /// has no <c>tenant_id</c> column and no query filter; it is the table the filter's tenant values come
    /// from. Unused on the single-tenant local install.</summary>
    public DbSet<TenantEntity> Tenants => Set<TenantEntity>();

    /// <summary>
    /// The paid-entitlement records the payment side writes and this Gateway only READS. Excluded from
    /// migrations - see the entity for why this one table is not ours to create.
    /// </summary>
    public DbSet<EntitlementEntity> Entitlements => Set<EntitlementEntity>();

    /// <summary>
    /// The free-trial ledger (<c>account_trials</c>, issue #2117) - the Gateway's OWN record of which accounts
    /// were granted the 14-day Pro trial the public pricing page promises, and when each trial ends. GLOBAL
    /// like <see cref="Tenants"/>: keyed by the verified account subject and read before any tenant exists, so
    /// it carries no <c>tenant_id</c> and no query filter. This one IS ours to create and write, unlike
    /// <see cref="Entitlements"/> - a trial is not a payment, so no payment webhook produces it.
    /// </summary>
    public DbSet<AccountTrialEntity> AccountTrials => Set<AccountTrialEntity>();

    /// <summary>
    /// The administrator trial-extension ledger (<c>trial_extensions</c>) - one append-only row per human
    /// decision to move a trial's end date later, recording who, for whom, from what to what, and why. GLOBAL
    /// like <see cref="AccountTrials"/>, and deliberately kept in the SAME schema as the table it describes:
    /// the extension and its audit row are written in one transaction by the one role that owns both, so
    /// neither can exist without the other.
    /// </summary>
    public DbSet<TrialExtensionEntity> TrialExtensions => Set<TrialExtensionEntity>();

    /// <summary>
    /// The turn-log switches (<c>turn_log_switches</c>) - which accounts and machines have turn-end capture
    /// switched on, and who decided that. GLOBAL rather than tenant-scoped: an administrator switches capture
    /// on for an account that is not their own, and the recorder reads the switch before it is inside any
    /// account's partition, so a tenant filter here would make the setting unwritable by the person entitled
    /// to write it and unreadable by the only thing that consumes it.
    /// </summary>
    public DbSet<TurnLogSwitchEntity> TurnLogSwitches => Set<TurnLogSwitchEntity>();

    /// <summary>The durable per-device-key credential records (<c>device_credentials</c>, MTR-14) - the per-row
    /// replacement for the shared <c>devices.json</c> file. A GLOBAL table (no <c>tenant_id</c> query filter):
    /// a presented key is resolved by its hash before any tenant is known, and the tenant is read off the
    /// matched record. The runtime read/write cutover is MTR-14B; MTR-14A only creates this schema.</summary>
    public DbSet<DeviceCredentialEntity> DeviceCredentials => Set<DeviceCredentialEntity>();

    /// <summary>The one-time <c>devices.json</c> import markers (<c>device_import_markers</c>, MTR-14A) - the
    /// idempotency record for the legacy-registry migration. GLOBAL, like <see cref="DeviceCredentials"/>.</summary>
    public DbSet<DeviceImportMarkerEntity> DeviceImportMarkers => Set<DeviceImportMarkerEntity>();

    /// <summary>The per-session Gateway credentials (<c>session_keys</c>, Remove-the-network-port mission
    /// phase 1b) - the record that lets an agent inside a session call the Gateway as itself. A GLOBAL table
    /// for the same reason as <see cref="DeviceCredentials"/>: a presented key is resolved by its hash before
    /// any tenant is known, and the tenant is read off the matched row.</summary>
    public DbSet<SessionKeyEntity> SessionKeys => Set<SessionKeyEntity>();

    /// <summary>Standing instructions the account gave in English (<c>session_rules</c>, the Session Rules
    /// mission). A rule holds the sentence itself plus what a model derived from it - never code.</summary>
    /// <remarks>INTERNAL on purpose. A rule is written through the rule store, which is where a rule is
    /// checked and where dry run is enforced; a DbSet other assemblies could reach is a second write
    /// surface that no comment can make safe. Inside this assembly the gate above, the interceptor, and a
    /// structural test over the built code are what stand in the way.</remarks>
    internal DbSet<SessionRuleEntity> SessionRules => Set<SessionRuleEntity>();

    /// <summary>The record of every rule firing (<c>session_rule_firings</c>) - screen, understanding,
    /// decision, reason, which verified checks ran, what was typed, and what happened next.</summary>
    /// <remarks>INTERNAL, for the same reason as <see cref="SessionRules"/>: the record is the product.</remarks>
    internal DbSet<SessionRuleFiringEntity> SessionRuleFirings => Set<SessionRuleFiringEntity>();

    /// <summary>Serializer options for the skill metadata column. One shared instance: constructing
    /// fresh options per call defeats the serializer's internal caching.</summary>
    private static readonly System.Text.Json.JsonSerializerOptions MetadataJsonOptions = new();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<CronJobEntity>(b =>
        {
            b.ToTable("cron_jobs");
            // COMPOSITE primary key (tenant_id, Id) - Hosted Multi-Tenancy. CronJobStore mints a SHORT id
            // (cj_ + 24 bits) whose uniqueness it checks THROUGH the tenant query filter (per tenant), and the
            // legacy import preserves caller-supplied short ids, so two tenants can hold the same id; scoping
            // the key by tenant lets each tenant own its id space (the store already treats it as per-tenant).
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // The nested target/action are bulky sub-documents: map each as an owned type serialized to a
            // JSON column rather than its own table (the reusable "sub-doc -> JSON in a column" pattern).
            b.OwnsOne(e => e.Target, o => o.ToJson());
            b.OwnsOne(e => e.Action, o => o.ToJson());
        });

        modelBuilder.Entity<CronRunEntity>(b =>
        {
            b.ToTable("cron_runs");
            b.HasKey(e => e.Id);
            // Newest-first within a job is served by ordering on Sequence DESC; index the lookup+order path.
            b.HasIndex(e => new { e.JobId, e.Sequence });
        });

        modelBuilder.Entity<WorkListEntity>(b =>
        {
            b.ToTable("worklists");
            b.HasKey(e => e.Id);
            // Items are an ORDERED child table (worklist_items) with a cascade so a list's items go with it.
            b.HasMany(e => e.Items).WithOne().HasForeignKey(i => i.WorkListId).OnDelete(DeleteBehavior.Cascade);
            // Deliberately NO database-level name-unique index. Name uniqueness is case-insensitive and must
            // match the legacy Dictionary(OrdinalIgnoreCase) exactly, and no stored string transform
            // reproduces StringComparer.OrdinalIgnoreCase (ToUpperInvariant over-merges U+017F onto 'S'), so a
            // unique index over a fold column could not preserve the behaviour and could brick an import. The
            // store enforces uniqueness in code via OrdinalIgnoreCase under its single-writer lock - exactly
            // what the old Dictionary did (which also had no database constraint).
        });

        modelBuilder.Entity<WorkListItemEntity>(b =>
        {
            b.ToTable("worklist_items");
            b.HasKey(e => e.Id);
            // Ordered read + the per-list lookup path (Reorder / RemoveItem) go through (WorkListId, Position).
            b.HasIndex(e => new { e.WorkListId, e.Position });
        });

        modelBuilder.Entity<WorkflowEntity>(b =>
        {
            b.ToTable("workflows");
            // COMPOSITE primary key (tenant_id, Id) - Hosted Multi-Tenancy. The built-in workflow seed uses a
            // FIXED id per built-in (Id = definition.Id), so with Id ALONE as the key two tenants seeding the
            // same built-in collide at the database (the SYSTEM seed vs leftover 'local' built-ins crashed
            // startup). Scoping the key by tenant lets each tenant - and the reserved SYSTEM tenant - hold its
            // own copy of a built-in, and makes an existing single-tenant gateway upgrade automatically (the
            // 'local' and SYSTEM built-ins coexist, the tenant-filtered seed is idempotent).
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // The owner's switch defaults ON at the DATABASE level too, so the migration that adds
            // the column backfills every pre-existing workflow as in-force - upgrading must never
            // silently switch a fleet's workflows off.
            b.Property(e => e.Enabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<WorkflowVersionEntity>(b =>
        {
            b.ToTable("workflow_versions");
            b.HasKey(e => e.Id);
            // A workflow's versions are unique per number; lookups go through (WorkflowId, Version). The
            // uniqueness must be PER TENANT (Hosted Multi-Tenancy): a built-in's WorkflowId + Version=1 is the
            // same across tenants, so a GLOBAL unique index would collide when the SYSTEM seed and a leftover
            // 'local' built-in both hold v1. Scoping the unique index by tenant lets each tenant hold its own
            // v1 of a built-in. (The PK stays the random-Guid Id; only the uniqueness rule is tenant-scoped.)
            b.HasIndex(e => new { e.TenantId, e.WorkflowId, e.Version }).IsUnique();
            // Steps and outcome criteria are bounded sub-documents: owned types serialized to a JSON
            // column each (the cron store's "sub-doc -> JSON in a column" pattern).
            b.OwnsMany(e => e.Steps, o => o.ToJson());
            b.OwnsMany(e => e.OutcomeCriteria, o => o.ToJson());
        });

        modelBuilder.Entity<WorkflowFileEntity>(b =>
        {
            b.ToTable("workflow_files");
            b.HasKey(e => e.Id);
            // Files are read per version; indexed but deliberately NOT a foreign key (independent
            // lifecycle, matching cron_runs -> cron_jobs).
            b.HasIndex(e => e.VersionId);
        });

        // ---- the central skill library (devthrottle_internal issue 995) ----------------------------
        // A separate register from workflows, sharing their storage shape: head row, immutable content
        // versions, supporting files, and a per-tenant switch over the read-only built-ins.

        modelBuilder.Entity<SkillEntity>(b =>
        {
            b.ToTable("skills");
            // COMPOSITE primary key (tenant_id, Id), for the same reason workflows use one: the
            // built-in seed uses a FIXED id per built-in, so with Id ALONE two tenants seeding the
            // same built-in collide at the database.
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // The owner's switch defaults ON at the DATABASE level too, so the migration that adds
            // the column backfills every pre-existing skill as available - upgrading must never
            // silently switch a fleet's skills off.
            b.Property(e => e.Enabled).HasDefaultValue(true);
        });

        modelBuilder.Entity<SkillVersionEntity>(b =>
        {
            b.ToTable("skill_versions");
            b.HasKey(e => e.Id);
            // Unique PER TENANT, not globally: a built-in's SkillId + Version=1 is the same across
            // tenants, so a global unique index would collide between the shared library seed and a
            // single-tenant gateway's own built-ins.
            b.HasIndex(e => new { e.TenantId, e.SkillId, e.Version }).IsUnique();
            // Triggers are a bounded sub-document: a primitive collection serialized to a JSON column.
            b.PrimitiveCollection(e => e.Triggers);
            // The Agent Skills standard's metadata map. A string-keyed map has no primitive-collection
            // equivalent, so it converts to a JSON string - provider-agnostic, which is the rule for
            // this model. The comparer is required, or EF cannot tell that a mutated dictionary is
            // dirty and the edit would silently not save.
            b.Property(e => e.Metadata)
                // An explicit database default of an EMPTY JSON OBJECT, so the migration backfills
                // every pre-existing version row with something that PARSES. Without it the column
                // backfills as an empty string, which is not JSON, and every skill written before this
                // upgrade would fail to read - an upgrade that breaks the existing rows is worse than
                // the missing feature.
                .HasDefaultValue(new Dictionary<string, string>())
                .HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, MetadataJsonOptions),
                    v => System.Text.Json.JsonSerializer
                        .Deserialize<Dictionary<string, string>>(v, MetadataJsonOptions)
                        ?? new Dictionary<string, string>(),
                    new ValueComparer<Dictionary<string, string>>(
                        (left, right) => left != null && right != null
                            ? left.Count == right.Count && !left.Except(right).Any()
                            : left == right,
                        v => v.OrderBy(e => e.Key, StringComparer.Ordinal)
                            .Aggregate(0, (acc, e) => HashCode.Combine(acc, e.Key.GetHashCode(), e.Value.GetHashCode())),
                        v => new Dictionary<string, string>(v)));
        });

        modelBuilder.Entity<SkillFileEntity>(b =>
        {
            b.ToTable("skill_files");
            b.HasKey(e => e.Id);
            // Read per version; indexed but deliberately NOT a foreign key (independent lifecycle,
            // matching workflow_files).
            b.HasIndex(e => e.VersionId);
            // Every file written before binary support was text, so the backfill says exactly that
            // rather than leaving a blank that each reader has to interpret for itself.
            b.Property(e => e.Encoding).HasDefaultValue("utf8");
        });

        modelBuilder.Entity<WorkflowRunEntity>(b =>
        {
            b.ToTable("workflow_runs");
            b.HasKey(e => e.Id);
            // The reporting cuts: per-workflow and per-mission run lists. Indexed, never foreign
            // keys - a run outlives archives and mission cleanup.
            b.HasIndex(e => e.WorkflowId);
            b.HasIndex(e => e.MissionId);
            b.OwnsMany(e => e.CriteriaResults, o => o.ToJson());
            b.OwnsMany(e => e.ProofLinks, o => o.ToJson());
            b.OwnsMany(e => e.Participants, o => o.ToJson());
        });

        modelBuilder.Entity<SnoozeEntity>(b =>
        {
            b.ToTable("snoozes");
            // SessionId is the natural key - an ordinally-compared GUID string - so there is no surrogate id.
            // SQLite's default BINARY text collation compares it ordinally, matching the legacy
            // Dictionary(StringComparer.Ordinal). The armed-vs-deferred invariant is kept in the store, not as
            // a database CHECK (provider-divergent), matching today.
            //
            // COMPOSITE primary key (tenant_id, SessionId) - Hosted Multi-Tenancy. The session id is
            // CALLER-SUPPLIED (it arrives on the snooze request from a Director), so it is a value one tenant
            // hands us, never something the Gateway mints and guarantees globally unique. The store upserts by
            // reading THROUGH the tenant query filter and adding when it finds nothing, so on a SessionId-only
            // key a second tenant presenting an id the first tenant already holds finds nothing, inserts, and
            // hits a PRIMARY KEY violation: a cross-tenant SQUAT (the second tenant cannot use that id at all)
            // and an EXISTENCE ORACLE (the failure tells it the first tenant holds it). Scoping the key by
            // tenant gives each tenant its own session-id space, which is what the store already assumes.
            b.HasKey(e => new { e.TenantId, e.SessionId });
        });

        modelBuilder.Entity<GovernanceEventEntity>(b =>
        {
            b.ToTable("governance_events");
            b.HasKey(e => e.Id);
            // The duration-rollup read paths: a session's transitions over time, a run's, and the weekly
            // whole-week scan across every subject. Each composite LEADS WITH tenant_id because the global
            // query filter prepends "tenant_id = @t" to every query - a SessionId/RunId/OccurredUtc-leading
            // index would force a cross-tenant scan under Phase B multi-tenant load, so these are the
            // tenant-leading forms that actually serve the filtered query. (ApplyTenantScope adds a
            // standalone tenant_id index too; these are the query-serving composites.)
            // SessionId/RunId are value references, never foreign keys - the ledger outlives the run and
            // the session it records.
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.RunId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.OccurredUtc });
        });

        modelBuilder.Entity<PushSubscriptionEntity>(b =>
        {
            b.ToTable("push_subscriptions");
            // Endpoint is the natural key - a per-subscription URL compared ordinally (SQLite's default BINARY
            // collation), matching the legacy Dictionary(StringComparer.Ordinal) - so there is no surrogate
            // id. There is no per-user column: the legacy shape had none.
            //
            // COMPOSITE primary key (tenant_id, Endpoint) - Hosted Multi-Tenancy. The endpoint is the browser's
            // push URL, entirely CALLER-SUPPLIED, and it is the whole key. The store upserts by reading THROUGH
            // the tenant query filter and adding when it finds nothing, so on an Endpoint-only key a second
            // tenant presenting an endpoint the first tenant already holds finds nothing, inserts, and hits a
            // PRIMARY KEY violation: a cross-tenant SQUAT plus an EXISTENCE ORACLE, exactly as for snoozes.
            // Scoping the key by tenant lets each tenant own its own subscription for a given endpoint.
            b.HasKey(e => new { e.TenantId, e.Endpoint });
        });

        modelBuilder.Entity<WingmanInstructionEntity>(b =>
        {
            b.ToTable("wingman_instructions");
            b.HasKey(e => e.Id);
            // The document has no external id; the store keeps one row per tenant under its write lock.
            // The saved versions are a bounded sub-document: an owned collection serialized to a JSON column
            // (the cron/workflow "sub-doc -> JSON in a column" pattern), preserving each field and the order.
            b.OwnsMany(e => e.Versions, o => o.ToJson());
        });

        modelBuilder.Entity<SessionSpendEntity>(b =>
        {
            b.ToTable("session_spend");
            // SessionId is the natural key - one row per session per tenant - so there is no surrogate id.
            //
            // COMPOSITE primary key (tenant_id, SessionId) - Hosted Multi-Tenancy, the snoozes-table reasoning
            // exactly: the session id is CALLER-SUPPLIED (it arrives on the record-spend request), the store
            // upserts through the tenant query filter, so a SessionId-only key would let one tenant SQUAT an id
            // for every other tenant and would leak the fact that it holds it.
            b.HasKey(e => new { e.TenantId, e.SessionId });
            // The reporting cut is a last-observed time window, tenant-leading for the global filter.
            b.HasIndex(e => new { e.TenantId, e.LastObservedUtc });
        });

        modelBuilder.Entity<SessionTurnEntity>(b =>
        {
            b.ToTable("session_turns");
            // COMPOSITE primary key led by tenant_id, like session_history: the session id, the generation
            // and the ordinal all arrive on the push stream from the Director, so a key that did not lead
            // with the tenant would let one tenant squat another's rows. This key is also what makes a
            // re-sent batch idempotent - the same (session, generation, ordinal) cannot be stored twice.
            b.HasKey(e => new { e.TenantId, e.SessionId, e.Generation, e.Ordinal });
            b.Property(e => e.SessionId).HasMaxLength(64);
            b.Property(e => e.Generation).HasMaxLength(64);
            b.Property(e => e.DirectorId).HasMaxLength(64);
            b.Property(e => e.Role).HasMaxLength(16);
            // Retention cuts on received-at, tenant-leading for the global filter.
            b.HasIndex(e => new { e.TenantId, e.ReceivedAtUtc });
        });

        // ---- the Session Rules mission: standing instructions and the record of every firing -------

        modelBuilder.Entity<SessionRuleEntity>(b =>
        {
            b.ToTable("session_rules");
            b.HasKey(e => e.Id);
            // The rules list is read per tenant, newest first; tenant-leading for the global filter.
            b.HasIndex(e => new { e.TenantId, e.CreatedUtc });
            b.Property(e => e.State).HasMaxLength(32);
            // The trigger words are a bounded sub-document: a primitive collection, like skill triggers.
            b.PrimitiveCollection(e => e.TriggerWords);
            // The derived calls are TYPED sub-documents serialized to a JSON column (the cron store's
            // "sub-doc -> JSON in a column" pattern). This is the shape that makes ruling 15 structural
            // rather than a promise: the column holds a name and argument VALUES, checked against a real
            // primitive signature before the row is written, and there is nowhere for a program to sit.
            b.OwnsMany(e => e.Calls, o =>
            {
                o.ToJson();
                o.OwnsMany(c => c.Arguments, a => a.PrimitiveCollection(x => x.Values));
            });
        });

        modelBuilder.Entity<SessionRuleFiringEntity>(b =>
        {
            b.ToTable("session_rule_firings");
            b.HasKey(e => e.Id);
            b.Property(e => e.SessionId).HasMaxLength(64);
            b.Property(e => e.Decision).HasMaxLength(64);
            // "What did this rule do?" and "what happened on this session?" are the two reads.
            b.HasIndex(e => new { e.TenantId, e.RuleId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.OwnsMany(e => e.PrimitiveRuns, o => o.ToJson());
        });

        modelBuilder.Entity<SessionTurnHeadEntity>(b =>
        {
            b.ToTable("session_turn_heads");
            b.HasKey(e => new { e.TenantId, e.SessionId });
            b.Property(e => e.SessionId).HasMaxLength(64);
            b.Property(e => e.Generation).HasMaxLength(64);
            b.Property(e => e.GenerationSource).HasMaxLength(1024);
            // Optimistic concurrency on the head: two Gateways (a deploy swap has two processes for a moment)
            // each read the head, decide a generation switch or a new watermark, and write. Without a token the
            // later commit wins even when its decision was made on stale facts; with it the loser gets a
            // DbUpdateConcurrencyException, re-reads, and decides again (SessionTurnStore.Append retries once).
            b.Property(e => e.Revision).IsConcurrencyToken();
            b.Property(e => e.DirectorId).HasMaxLength(64);
            b.Property(e => e.Agent).HasMaxLength(32);
            // Hello asks "every session this Director has pushed"; retention cuts on updated-at.
            b.HasIndex(e => new { e.TenantId, e.DirectorId });
            b.HasIndex(e => new { e.TenantId, e.UpdatedAtUtc });
        });

        modelBuilder.Entity<SessionHistoryEntity>(b =>
        {
            b.ToTable("session_history");
            // COMPOSITE primary key (tenant_id, SessionId) - the session_spend reasoning exactly: the
            // session id is CALLER-SUPPLIED (it arrives on the push stream), the recorder upserts through
            // the tenant query filter, so a SessionId-only key would let one tenant squat an id for every
            // other tenant and would leak the fact that it holds it.
            b.HasKey(e => new { e.TenantId, e.SessionId });
            // The range read ("what was worked on between these days") cuts on last-seen; the interrupted
            // sweep cuts on the same column for open rows. Tenant-leading for the global filter.
            b.HasIndex(e => new { e.TenantId, e.LastSeenUtc });
            // The recorder's per-Director reconcile ("which open rows does this Director still push")
            // and the director-stopped farewell both cut by Director.
            b.HasIndex(e => new { e.TenantId, e.DirectorId });
        });

        modelBuilder.Entity<KnownRepositoryEntity>(b =>
        {
            b.ToTable("known_repositories");
            // The identity is minted by the Gateway rather than supplied by a tenant, so it is safe as a
            // global key. Machine and path stay out of the primary key to avoid provider-specific index-size
            // ceilings; the store's single-writer lock enforces normalized deduplication.
            b.HasKey(e => e.Id);
            b.Property(e => e.Id);
            b.Property(e => e.MachineKey).HasMaxLength(1024);
            b.Property(e => e.PathKey).HasMaxLength(1024);
            b.Property(e => e.MachineName).HasMaxLength(1024);
            b.Property(e => e.Path).HasMaxLength(1024);
            b.Property(e => e.Name).HasMaxLength(1024);
            b.HasIndex(e => new { e.TenantId, e.MachineKey, e.LastUsedUtc });
        });

        modelBuilder.Entity<SessionHistoryRollupEntity>(b =>
        {
            b.ToTable("session_history_rollups");
            // COMPOSITE primary key (tenant_id, RepoKey, DayUtc): one cached paragraph per repository
            // group per day per tenant. RepoKey is caller-derived (a repo name or path off the push
            // stream), so it is scoped by tenant like every caller-supplied natural key.
            b.HasKey(e => new { e.TenantId, e.RepoKey, e.DayUtc });
            // Retention prune cuts on the day, tenant-leading.
            b.HasIndex(e => new { e.TenantId, e.DayUtc });
        });

        modelBuilder.Entity<AccountHostedAiSpendEntity>(b =>
        {
            b.ToTable("account_hosted_ai_spend");
            b.HasKey(e => e.Id);
            // The dedup lookup (kind + amount + transaction-time) and the weekly window sum, both tenant-
            // leading for the global filter. NOT unique on the dedup tuple on purpose: two genuinely distinct
            // debits of the same amount at the same instant are rare but real, and a unique index would throw
            // on one; the store de-duplicates in code and discloses the (rare) undercount instead of crashing.
            b.HasIndex(e => new { e.TenantId, e.Kind, e.AmountMicros, e.TransactionCreatedUtc });
            b.HasIndex(e => new { e.TenantId, e.TransactionCreatedUtc });
        });

        modelBuilder.Entity<MissionNoteEntity>(b =>
        {
            b.ToTable("mission_notes");
            // Key is the natural key - the already-normalized (trim + lower-cased) mission name - compared
            // ordinally by SQLite's default BINARY collation, matching the legacy Dictionary(StringComparer.
            // Ordinal) keyed by that same normalized value. It is the primary key directly, no surrogate. No
            // case-fold question arises: the value is folded BEFORE it becomes the key, so lookup is a plain
            // ordinal equality (unlike the work-list NAME key, which folds at comparison time).
            //
            // COMPOSITE primary key (tenant_id, Key) - Hosted Multi-Tenancy. The mission NAME is namespaced per
            // tenant, so with Key ALONE as the key two tenants naming a mission the same thing would collide at
            // the database. Scoping the key by tenant lets each tenant own its own note for a given name.
            b.HasKey(e => new { e.TenantId, e.Key });
        });

        modelBuilder.Entity<WorkspaceEntity>(b =>
        {
            b.ToTable("workspaces");
            // COMPOSITE primary key (tenant_id, Id) - the established per-tenant key pattern. The id is a
            // slug the author mints, so without the tenant in the key two tenants naming a workspace
            // "morning-fleet" would collide at the database.
            b.HasKey(e => new { e.TenantId, e.Id });
            // The list reads every workspace for one tenant, newest first; the index leads with tenant_id so
            // it rides the global query filter's "tenant_id = @t" prefix rather than scanning.
            b.HasIndex(e => new { e.TenantId, e.UpdatedUtc });
        });

        modelBuilder.Entity<DictationTranscriptEntity>(b =>
        {
            b.ToTable("dictation_transcripts");
            // COMPOSITE primary key (tenant_id, Id) - the established per-tenant key pattern (issue #509). The
            // store mints a random GUID Id per row, per tenant, so scoping the key by tenant lets each tenant
            // own its own id space (and two tenants can never collide on a row id). Id is never caller-supplied.
            b.HasKey(e => new { e.TenantId, e.Id });
            b.Property(e => e.Id);
            // Retention (delete rows older than 30 days, and any beyond the newest 10,000 per tenant) and any
            // future read scan by time - both lead with tenant_id so they ride the global query filter's
            // "tenant_id = @t" prefix rather than forcing a cross-tenant scan.
            b.HasIndex(e => new { e.TenantId, e.TimestampUtc });
        });

        modelBuilder.Entity<RepoStateEntity>(b =>
        {
            b.ToTable("repo_state");
            // COMPOSITE primary key (tenant_id, DirectorId, RepoPath): both parts of the natural key are
            // CALLER-SUPPLIED, so the tenant must be in the key. Two accounts can genuinely register the same
            // repository path on identically-named machines; without tenant_id in the key one account's push
            // would fail to insert over the other's row and learn from the failure that it exists.
            b.HasKey(e => new { e.TenantId, e.DirectorId, e.RepoPath });
            // The report reads one tenant's whole repo-state set at once, ordered by freshness; index the
            // tenant-leading path so it rides the global query filter's "tenant_id = @t" prefix rather than
            // forcing a cross-tenant scan.
            b.HasIndex(e => new { e.TenantId, e.ReceivedAtUtc });
        });

        modelBuilder.Entity<SkillPlacementStateEntity>(b =>
        {
            b.ToTable("skill_placement_state");
            // COMPOSITE primary key (tenant_id, DirectorId, AgentKind), for the same reason repo_state uses
            // one: both non-tenant parts are CALLER-supplied, and two accounts can genuinely run
            // identically-named Directors. Without tenant_id in the key one account's report would fail to
            // insert over the other's row and learn from the failure that the row exists.
            b.HasKey(e => new { e.TenantId, e.DirectorId, e.AgentKind });
            // The Cockpit reads one tenant's whole placement set at once; index the tenant-leading path so
            // it rides the global query filter's "tenant_id = @t" prefix rather than scanning cross-tenant.
            b.HasIndex(e => new { e.TenantId, e.ReceivedAtUtc });
        });

        modelBuilder.Entity<DictationSuggestionDismissalEntity>(b =>
        {
            b.ToTable("dictation_suggestion_dismissals");
            // COMPOSITE primary key (tenant_id, Term) - the per-tenant key pattern (mirroring mission_notes /
            // tenant_settings). Term is the NORMALIZED canonical spelling, namespaced per tenant, so with Term
            // alone two tenants dismissing the same word would collide at the database. Scoping the key by
            // tenant lets each tenant own its own dismissal for a given term; the store upserts by reading
            // THROUGH the tenant query filter. Term is derived (lower-cased alphanumerics), compared ordinally
            // (SQLite BINARY / Postgres "C"), matching how the miner folds a spelling before excluding it.
            b.HasKey(e => new { e.TenantId, e.Term });
            // The "Dismissed terms" screen lists newest-first; index the tenant-leading order path so it rides
            // the global query filter's "tenant_id = @t" prefix rather than forcing a cross-tenant scan.
            b.HasIndex(e => new { e.TenantId, e.DismissedAtUtc });
        });

        modelBuilder.Entity<DictationSuggestionVerdictEntity>(b =>
        {
            b.ToTable("dictation_suggestion_verdicts");
            // COMPOSITE primary key (tenant_id, Term) - the per-tenant key pattern, mirroring the dismissal
            // table exactly: Term is the NORMALIZED canonical spelling (lower-cased alphanumerics), derived
            // not caller-supplied, compared ordinally (SQLite BINARY / Postgres "C"). One verdict per tenant
            // per term is the whole point - the upsert reads through the tenant query filter.
            b.HasKey(e => new { e.TenantId, e.Term });
        });

        modelBuilder.Entity<DictationSuggestionScanEntity>(b =>
        {
            b.ToTable("dictation_suggestion_scans");
            // ONE row per tenant (the latest scan result), so the tenant id alone is the primary key.
            b.HasKey(e => e.TenantId);
        });

        modelBuilder.Entity<ActivityEventEntity>(b =>
        {
            b.ToTable("activity_events");
            // COMPOSITE primary key (tenant_id, EventId): the EventId is CALLER-SUPPLIED (the producer mints
            // it once before its outbox, so a retried batch replays the same identity and the append is
            // idempotent). A caller-supplied key must carry the tenant in the primary key - one tenant
            // replaying an id another tenant holds must neither collide (a squat) nor learn of the
            // collision (an existence oracle).
            b.HasKey(e => new { e.TenantId, e.EventId });
            b.Property(e => e.EventId);
            // The read paths, each tenant-leading so they ride the global filter's "tenant_id = @t" prefix:
            // a session's timeline (diagnosis + the 30-day retention scan on OccurredUtc), a Director's
            // sequence range (retry/reorder checks), and a shadow-analysis scan by event type over a window.
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.DirectorId, e.DirectorSequence });
            b.HasIndex(e => new { e.TenantId, e.EventType, e.OccurredUtc });
        });

        modelBuilder.Entity<TenantSettingEntity>(b =>
        {
            b.ToTable("tenant_settings");
            // COMPOSITE primary key (tenant_id, Key) - issue #2017, mirroring mission_notes. The setting Key is
            // namespaced per tenant, so with Key ALONE two tenants overriding the same setting would collide at
            // the database. Scoping the key by tenant lets each tenant own its own value for a given key. Key is
            // a fixed identifier from TenantSettingKeys, compared ordinally (SQLite BINARY / Postgres "C").
            b.HasKey(e => new { e.TenantId, e.Key });
        });

        modelBuilder.Entity<WorkflowTenantOverrideEntity>(b =>
        {
            b.ToTable("workflow_tenant_overrides");
            // COMPOSITE primary key (tenant_id, WorkflowId) - the Shared Workflow Library phase 2, mirroring
            // tenant_settings. The workflow id is namespaced per tenant (every tenant may hold a choice for
            // the same built-in), so scoping the key by tenant is what keeps two tenants' choices for
            // "mission" from colliding at the database.
            b.HasKey(e => new { e.TenantId, e.WorkflowId });
        });

        modelBuilder.Entity<SkillTenantOverrideEntity>(b =>
        {
            b.ToTable("skill_tenant_overrides");
            // COMPOSITE primary key (tenant_id, SkillId), mirroring workflow_tenant_overrides: every
            // tenant may hold its own on/off choice for the same built-in skill.
            b.HasKey(e => new { e.TenantId, e.SkillId });
        });

        modelBuilder.Entity<GovernanceAuditEventEntity>(b =>
        {
            b.ToTable("governance_audit_events");
            b.HasKey(e => e.Id);
            // The read paths: a session's audit trail over time, a run's, and a category scan (all the
            // permission decisions, all the interventions) over a window - each tenant-leading for the
            // global filter. SessionId/RunId are value references, never foreign keys - the trail outlives
            // the session and run it records.
            b.HasIndex(e => new { e.TenantId, e.SessionId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.RunId, e.OccurredUtc });
            b.HasIndex(e => new { e.TenantId, e.Category, e.OccurredUtc });
        });

        modelBuilder.Entity<EntitlementEntity>(b =>
        {
            // EXCLUDED FROM MIGRATIONS on purpose. This table belongs to the payment side, which creates it
            // and writes it as the service role; this Gateway holds SELECT and nothing more. Describing its
            // shape lets us read it; emitting a migration for it would have us create or alter a table we do
            // not own, on a schema boundary that is deliberate.
            //
            // DEFENSE-IN-DEPTH GUARD, behavior-preserving. On Postgres the generated SELECT is already
            // gateway.entitlements because HasDefaultSchema("gateway") below applies the schema to every
            // table, so naming the schema HERE changes no emitted SQL today. What it adds is INDEPENDENCE:
            // this table is excluded from migrations, so it is NOT covered by the migration set that pins the
            // owned tables to the gateway schema, and its qualification would otherwise rest entirely on the
            // model-wide default. Pinning it explicitly makes the gateway schema a property of this one read
            // in its own right, so the read stays qualified even if the model-wide default is later changed
            // or removed. SQLite is schemaless (one file, no schema namespace) and takes no schema argument -
            // so this is a provider CONDITIONAL, decided by the same IsNpgsql() signal the rest of the
            // Postgres-only model shape uses, never a try/catch fallback.
            if (Database.IsNpgsql())
                b.ToTable("entitlements", "gateway", t => t.ExcludeFromMigrations());
            else
                b.ToTable("entitlements", t => t.ExcludeFromMigrations());
            b.HasKey(e => e.Subject);
            b.Property(e => e.Subject).HasColumnName("subject").IsRequired();
            b.Property(e => e.Status).HasColumnName("status").IsRequired();
            b.Property(e => e.CurrentPeriodEnd).HasColumnName("current_period_end");
            b.Property(e => e.StripeSubscriptionId).HasColumnName("stripe_subscription_id");
            b.Property(e => e.UpdatedAt).HasColumnName("updated_at");
            b.Property(e => e.Livemode).HasColumnName("livemode");
            // The two-tier plan column (hosted|pro), nullable. A plain text column on BOTH providers - no
            // value converter and no uuid concern, unlike Subject - so it is mapped here in the provider-agnostic
            // block, not in the Postgres-only section below. Read and exposed by EntitlementRegistry; never
            // gates enrollment (either tier enrolls).
            b.Property(e => e.Tier).HasColumnName("tier");
        });

        modelBuilder.Entity<TenantEntity>(b =>
        {
            b.ToTable("tenants");
            // Id is the tenant's own id value (a code-generated GUID string), the natural primary key -
            // ordinally compared (SQLite's default BINARY collation), no surrogate. This table is the GLOBAL
            // account->tenant mapping, so it is deliberately NOT passed to ApplyTenantScope below: it carries
            // no tenant_id column and no query filter (it is the table the filter's tenant values come from).
            b.HasKey(e => e.Id);
            // The mapping is looked up by the verified Supabase subject; it is unique (1:1 account->tenant now,
            // enforced at the database, so a duplicate mint can never split one account across two tenants).
            b.HasIndex(e => e.AccountSubject).IsUnique();
        });

        modelBuilder.Entity<AccountTrialEntity>(b =>
        {
            b.ToTable("account_trials");
            // The account subject is the natural primary key - one trial per account, enforced at the
            // DATABASE rather than only in code, so two racing grants can never write an account two trials
            // (and therefore can never restart a free window). This table is GLOBAL, like tenants: it is
            // deliberately NOT passed to ApplyTenantScope, because it is keyed by subject and read before any
            // tenant is resolved.
            b.HasKey(e => e.Subject);
            b.Property(e => e.Subject).HasColumnName("subject").IsRequired();
            b.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
            b.Property(e => e.ExpiresAtUtc).HasColumnName("expires_at_utc").IsRequired();
            // The expiry sweep and the "who is still in trial" read both order by the end instant.
            b.HasIndex(e => e.ExpiresAtUtc);
        });

        modelBuilder.Entity<TrialExtensionEntity>(b =>
        {
            b.ToTable("trial_extensions");
            // The minted Guid, not the subject: one account may be extended more than once and each decision
            // is its own record, so the subject is an INDEX here rather than a key. GLOBAL for the same reason
            // account_trials is - keyed by account subject, which exists before any tenant does.
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.Subject).HasColumnName("subject").IsRequired();
            b.Property(e => e.MemberEmail).HasColumnName("member_email");
            b.Property(e => e.StartedAtUtc).HasColumnName("started_at_utc").IsRequired();
            b.Property(e => e.PreviousExpiresAtUtc).HasColumnName("previous_expires_at_utc").IsRequired();
            b.Property(e => e.NewExpiresAtUtc).HasColumnName("new_expires_at_utc").IsRequired();
            b.Property(e => e.Actor).HasColumnName("actor").IsRequired();
            b.Property(e => e.Reason).HasColumnName("reason").IsRequired();
            b.Property(e => e.RecordedUtc).HasColumnName("recorded_utc").IsRequired();
            // "What was done to this account?" is the question the ledger is read by.
            b.HasIndex(e => e.Subject);
        });

        modelBuilder.Entity<TurnLogSwitchEntity>(b =>
        {
            b.ToTable("turn_log_switches");
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasColumnName("id").ValueGeneratedNever();
            b.Property(e => e.Account).HasColumnName("account").IsRequired();
            b.Property(e => e.Machine).HasColumnName("machine").IsRequired();
            b.Property(e => e.Enabled).HasColumnName("enabled").IsRequired();
            b.Property(e => e.Actor).HasColumnName("actor").IsRequired();
            b.Property(e => e.Reason).HasColumnName("reason").IsRequired();
            b.Property(e => e.RecordedUtc).HasColumnName("recorded_utc").IsRequired();
            // One decision per scope. Without this, two rows could disagree about the same account and
            // machine and which one applied would depend on row order - a setting that changes meaning when
            // the table is re-packed. GLOBAL, so deliberately NOT passed to ApplyTenantScope below.
            b.HasIndex(e => new { e.Account, e.Machine }).IsUnique();
        });

        modelBuilder.Entity<DeviceCredentialEntity>(b =>
        {
            b.ToTable("device_credentials");
            // DeviceId is the natural primary key - the device's stable identity, globally unique, mirroring the
            // in-memory registry's _byDeviceId map keyed by device id. This table is GLOBAL (like tenants): it is
            // deliberately NOT passed to ApplyTenantScope, so it carries no query filter, because a presented key
            // is resolved to its device by hash BEFORE any tenant is known and the tenant is read off the record.
            b.HasKey(e => e.DeviceId);
            // The key-to-device lookup path (MTR-14B's authentication read) is by the stored hash, mirroring the
            // in-memory _byKeyHash index. Indexed for that O(1) lookup. Deliberately NOT unique: a hand-edited or
            // duplicate legacy devices.json must import rather than brick on a database constraint (the same
            // stance account_hosted_ai_spend takes on its dedup tuple), and the in-memory index already enforces
            // last-writer-wins uniqueness in code. The random 256-bit key space makes a genuine hash collision
            // between two distinct devices astronomically unlikely regardless.
            b.HasIndex(e => e.DeviceKeyHash);
        });

        modelBuilder.Entity<SessionKeyEntity>(b =>
        {
            b.ToTable("session_keys");
            // TENANT PLUS SESSION is the primary key - ONE live credential per session WITHIN A TENANT,
            // enforced at the DATABASE rather than only in code, so a re-registration can only ever rotate
            // that tenant's own row. Two rows for one session in one tenant would mean a revocation that
            // ends one credential and leaves the other accepted.
            //
            // THE TENANT IS PART OF THE KEY BECAUSE IT WAS NOT, AND THAT WAS A DEFECT. Keyed on SessionId
            // alone, one session id was a GLOBAL name: a Director registering a session id already held by
            // another tenant found and overwrote that row, taking the session's identity with it. Making
            // the tenant part of the key means two tenants naming the same session id are two rows that
            // cannot see each other, which is what tenant isolation means at the storage layer.
            //
            // The table is still deliberately NOT passed to ApplyTenantScope: a presented key is resolved
            // by hash before any tenant is known, so the READ has to be able to cross tenants to find out
            // which one it belongs to. The KeyHash index below stays globally unique for that reason - a
            // hash identifies exactly one row in the whole table, whichever tenant owns it.
            b.HasKey(e => new { e.TenantId, e.SessionId });
            // The authentication read is by the stored hash, on every agent request. Indexed for that lookup,
            // and UNIQUE: unlike device_credentials - which must tolerate a hand-edited legacy devices.json
            // importing duplicates rather than bricking - nothing imports this table. Every row here is
            // written by the Gateway itself from a freshly minted 256-bit key, so two rows sharing a hash is
            // not a state to tolerate, it is a state to refuse.
            b.HasIndex(e => e.KeyHash).IsUnique();
            // The expiry sweep reads by the lapse instant.
            b.HasIndex(e => e.ExpiresAtUtc);
        });

        modelBuilder.Entity<DeviceImportMarkerEntity>(b =>
        {
            b.ToTable("device_import_markers");
            // SourcePath (the absolute devices.json path) is the natural primary key - one marker per imported
            // source file. GLOBAL, like device_credentials: not tenant-scoped.
            b.HasKey(e => e.SourcePath);
        });

        // Tenant scoping - the tenant_id column plus the global query filter - applied uniformly to every
        // entity that derives from TenantScopedEntity, so future stores inherit it by deriving from the base.
        ApplyTenantScope<CronJobEntity>(modelBuilder);
        ApplyTenantScope<CronRunEntity>(modelBuilder);
        ApplyTenantScope<WorkListEntity>(modelBuilder);
        ApplyTenantScope<WorkListItemEntity>(modelBuilder);
        ApplyTenantScope<WorkflowEntity>(modelBuilder);
        ApplyTenantScope<WorkflowVersionEntity>(modelBuilder);
        ApplyTenantScope<WorkflowFileEntity>(modelBuilder);
        ApplyTenantScope<WorkflowRunEntity>(modelBuilder);
        ApplyTenantScope<SkillEntity>(modelBuilder);
        ApplyTenantScope<SkillVersionEntity>(modelBuilder);
        ApplyTenantScope<SkillFileEntity>(modelBuilder);
        ApplyTenantScope<SkillTenantOverrideEntity>(modelBuilder);
        ApplyTenantScope<SnoozeEntity>(modelBuilder);
        ApplyTenantScope<GovernanceEventEntity>(modelBuilder);
        ApplyTenantScope<PushSubscriptionEntity>(modelBuilder);
        ApplyTenantScope<WingmanInstructionEntity>(modelBuilder);
        ApplyTenantScope<SessionSpendEntity>(modelBuilder);
        ApplyTenantScope<AccountHostedAiSpendEntity>(modelBuilder);
        ApplyTenantScope<MissionNoteEntity>(modelBuilder);
        ApplyTenantScope<WorkspaceEntity>(modelBuilder);
        ApplyTenantScope<GovernanceAuditEventEntity>(modelBuilder);
        ApplyTenantScope<TenantSettingEntity>(modelBuilder);
        ApplyTenantScope<DictationTranscriptEntity>(modelBuilder);
        ApplyTenantScope<WorkflowTenantOverrideEntity>(modelBuilder);
        ApplyTenantScope<DictationSuggestionDismissalEntity>(modelBuilder);
        ApplyTenantScope<DictationSuggestionVerdictEntity>(modelBuilder);
        ApplyTenantScope<DictationSuggestionScanEntity>(modelBuilder);
        ApplyTenantScope<ActivityEventEntity>(modelBuilder);
        ApplyTenantScope<RepoStateEntity>(modelBuilder);
        ApplyTenantScope<SkillPlacementStateEntity>(modelBuilder);
        ApplyTenantScope<SessionHistoryEntity>(modelBuilder);
        ApplyTenantScope<KnownRepositoryEntity>(modelBuilder);
        ApplyTenantScope<SessionHistoryRollupEntity>(modelBuilder);
        ApplyTenantScope<SessionTurnEntity>(modelBuilder);
        ApplyTenantScope<SessionTurnHeadEntity>(modelBuilder);
        ApplyTenantScope<SessionRuleEntity>(modelBuilder);
        ApplyTenantScope<SessionRuleFiringEntity>(modelBuilder);

        ApplyCommonSubsetConventions(modelBuilder);

        // Provider-specific model shape. Guarded by Database.IsNpgsql() so the SQLite path is 100% unchanged
        // (its snapshot must not move) and Postgres alone gets the two things SQLite gives for free. IsNpgsql()
        // is available at model-build time because both the runtime pooled factory and the design-time factory
        // construct this context with a concrete provider already selected.
        if (Database.IsNpgsql())
        {
            // 1. A named default schema. SQLite is schemaless (one file, no schema namespace), so it needs
            //    none; Postgres puts every table under the "gateway" schema, matching the migrations history
            //    table (__EFMigrationsHistory in schema "gateway") the database and factory pin.
            modelBuilder.HasDefaultSchema("gateway");

            // 2. Byte-ordinal collation on the four natural-key string PRIMARY KEY columns. These keys rely on
            //    SQLite's DEFAULT BINARY collation, which compares raw bytes (memcmp) - the same ordering the
            //    legacy Dictionary(StringComparer.Ordinal) had. Postgres' default collation is LOCALE-based,
            //    NOT byte-ordinal, so without this the ordering and the case-of-uniqueness would silently
            //    differ between the two providers. Collation "C" collates raw UTF-8 bytes (memcmp) and SQLite's
            //    default BINARY collation also compares raw UTF-8 bytes, so the two providers AGREE with each
            //    other exactly on both ordering and uniqueness - which is the property we need here. (The only
            //    theoretical divergence is against .NET's UTF-16 StringComparer.Ordinal for rare supplementary-
            //    plane characters, where UTF-8 byte order and UTF-16 code-unit order can differ; that does not
            //    matter here because equality/uniqueness is exact regardless, and any in-memory ordering the
            //    stores do is done in .NET, not the database. So no assumption that these keys are ASCII is
            //    needed - mission_notes.Key in particular is only trimmed and lower-cased, not restricted.)
            modelBuilder.Entity<SnoozeEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<PushSubscriptionEntity>().Property(e => e.Endpoint).UseCollation("C");
            modelBuilder.Entity<SessionSpendEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<MissionNoteEntity>().Property(e => e.Key).UseCollation("C");
            // workspaces.Id is a slug in a composite primary key, compared ordinally by the store exactly
            // like the workflow and skill ids above - pin it to "C" so both providers agree on equality.
            modelBuilder.Entity<WorkspaceEntity>().Property(e => e.Id).UseCollation("C");
            // tenant_settings.Key is a fixed ordinally-compared identifier (issue #2017), same byte-ordinal
            // equality requirement as mission_notes.Key - pin it to "C" so both providers agree exactly.
            modelBuilder.Entity<TenantSettingEntity>().Property(e => e.Key).UseCollation("C");
            // workflow_tenant_overrides.WorkflowId is a workflow slug compared ordinally everywhere else
            // (the stores normalize to lower-case and compare Ordinal), so pin it to "C" the same way.
            modelBuilder.Entity<WorkflowTenantOverrideEntity>().Property(e => e.WorkflowId).UseCollation("C");
            // skills.Id and skill_tenant_overrides.SkillId are slugs in composite primary keys, compared
            // ordinally by the store exactly like the workflow ids above - pin both to "C" so the two
            // providers agree on equality and uniqueness.
            modelBuilder.Entity<SkillEntity>().Property(e => e.Id).UseCollation("C");
            modelBuilder.Entity<SkillTenantOverrideEntity>().Property(e => e.SkillId).UseCollation("C");
            // dictation_suggestion_dismissals.Term is the normalized canonical spelling, byte-ordinal equality
            // like the keys above (the miner folds a spelling and compares exactly), so pin it to "C" so both
            // providers agree on uniqueness and the miner's dismissed-exclusion check matches identically.
            modelBuilder.Entity<DictationSuggestionDismissalEntity>().Property(e => e.Term).UseCollation("C");
            // dictation_suggestion_verdicts.Term is the same normalized canonical spelling as the dismissal
            // Term above, with the same byte-ordinal equality requirement - pin it to "C" identically.
            modelBuilder.Entity<DictationSuggestionVerdictEntity>().Property(e => e.Term).UseCollation("C");
            // session_history.SessionId and session_history_rollups.RepoKey are caller-supplied natural-key
            // strings in composite primary keys (issue #2194), same byte-ordinal equality/uniqueness
            // requirement as session_spend.SessionId above - pin both to "C" so the two providers agree.
            modelBuilder.Entity<SessionHistoryEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<SessionTurnEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<SessionTurnEntity>().Property(e => e.Generation).UseCollation("C");
            modelBuilder.Entity<SessionTurnHeadEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<SessionTurnHeadEntity>().Property(e => e.Generation).UseCollation("C");
            modelBuilder.Entity<SessionHistoryRollupEntity>().Property(e => e.RepoKey).UseCollation("C");
            // Known-repository lookups use these normalized values as exact indexed predicates. Pin both
            // to byte-ordinal equality so SQLite and Postgres select the same bounded candidate set.
            modelBuilder.Entity<KnownRepositoryEntity>().Property(e => e.MachineKey).UseCollation("C");
            modelBuilder.Entity<KnownRepositoryEntity>().Property(e => e.PathKey).UseCollation("C");
            // The tenants mapping table's natural-key string columns (the tenant id primary key and the
            // account_subject unique index) rely on byte-ordinal equality/uniqueness, same as the keys above,
            // so pin them to "C" too - the account subject in particular must match EXACTLY on both providers.
            modelBuilder.Entity<TenantEntity>().Property(e => e.Id).UseCollation("C");
            modelBuilder.Entity<TenantEntity>().Property(e => e.AccountSubject).UseCollation("C");

            // account_trials.subject is the same verified account subject as tenants.account_subject, and it
            // is this table's PRIMARY KEY - the thing that enforces one trial per account. Its equality and
            // uniqueness must match SQLite's byte-ordinal BINARY collation exactly, or the same subject could
            // be treated as two different accounts on Postgres and be granted a second trial. Pin it to "C"
            // for the same reason as the keys above. It stays TEXT here (unlike entitlements.subject, which is
            // uuid because the website's schema owns that column) - this table is ours, and its column type
            // matches tenants.account_subject, the other subject-keyed table the Gateway itself creates.
            modelBuilder.Entity<AccountTrialEntity>().Property(e => e.Subject).UseCollation("C");

            // trial_extensions.subject holds the SAME verified account subject and is indexed for the "what
            // was done to this account?" read. Pinned to "C" for the same reason: the ledger must group by
            // exactly the identity the trial row is keyed on, or an extension could be recorded against a
            // subject that Postgres considers equal and SQLite does not.
            modelBuilder.Entity<TrialExtensionEntity>().Property(e => e.Subject).UseCollation("C");

            // turn_log_switches.(account, machine) is a UNIQUE scope key matched for exact equality by the
            // recorder on every turn end. Pinned to "C" for the same reason as the keys above: if Postgres
            // considered two account ids equal that SQLite does not, the unique index would refuse a second
            // scope on one provider and admit it on the other, and capture would silently be on for an
            // account somebody switched off.
            modelBuilder.Entity<TurnLogSwitchEntity>().Property(e => e.Account).UseCollation("C");
            modelBuilder.Entity<TurnLogSwitchEntity>().Property(e => e.Machine).UseCollation("C");

            // The device_credentials natural-key columns (MTR-14): the DeviceId primary key and the
            // DeviceKeyHash lookup index both rely on byte-ordinal equality/ordering, same as the keys above -
            // the key-hash lookup in particular must match EXACTLY on both providers (it is the authentication
            // read MTR-14B moves onto this table), so pin both to "C" (raw-byte, memcmp) to agree with SQLite's
            // default BINARY collation. The device_import_markers SourcePath key is likewise byte-ordinal.
            modelBuilder.Entity<DeviceCredentialEntity>().Property(e => e.DeviceId).UseCollation("C");
            modelBuilder.Entity<DeviceCredentialEntity>().Property(e => e.DeviceKeyHash).UseCollation("C");
            modelBuilder.Entity<DeviceImportMarkerEntity>().Property(e => e.SourcePath).UseCollation("C");

            // The session_keys natural-key columns (Remove-the-network-port phase 1b), for exactly the same
            // reason as device_credentials above: SessionId is the primary key and KeyHash is the
            // authentication lookup, and the key-hash equality in particular must match SQLite's byte-ordinal
            // BINARY collation EXACTLY or a locale-collating Postgres could treat two different hashes as the
            // same row - which on a UNIQUE index is a refused registration, and on a lookup is the wrong
            // session authenticated.
            modelBuilder.Entity<SessionKeyEntity>().Property(e => e.SessionId).UseCollation("C");
            modelBuilder.Entity<SessionKeyEntity>().Property(e => e.KeyHash).UseCollation("C");

            // 3. The entitlements.subject column is Postgres `uuid`, not text. The CLR property stays a string
            //    (its callers pass and compare a string), so map it through a string<->Guid value converter and
            //    pin the store type to `uuid`. This makes Npgsql emit a UUID parameter for the account-subject
            //    key lookup instead of a text one - the single change that stops Postgres rejecting the read
            //    with 42883 'operator does not exist: uuid = text' and 503'ing hosted enrollment. The `subject`
            //    column stays uuid by contract (the website's read_gateway_entitlement(p_subject uuid) and the
            //    Supabase auth identifier both depend on it); the Gateway is what was out of step. Postgres
            //    ONLY: SQLite has no uuid type, so off this branch the column stays a plain string, unchanged.
            //    Subject is the entity's primary key; a value converter on a key property is supported, and the
            //    generated key-lookup parameter carries the uuid store type this pins.
            modelBuilder.Entity<EntitlementEntity>()
                .Property(e => e.Subject)
                .HasConversion(EntitlementSubjectToGuidConverter)
                .HasColumnType("uuid");
        }
    }

    /// <summary>Map the tenant column and install the deny-by-default global query filter for one entity.</summary>
    private void ApplyTenantScope<TEntity>(ModelBuilder modelBuilder) where TEntity : TenantScopedEntity
    {
        modelBuilder.Entity<TEntity>(b =>
        {
            b.Property(e => e.TenantId).HasColumnName("tenant_id").IsRequired();
            b.HasIndex(e => e.TenantId);
            // Scope every read to the active tenant. ActiveTenant is a context member, so EF re-evaluates it
            // per query against the context instance running the query - "local" on the single-tenant install.
            b.HasQueryFilter(e => e.TenantId == ActiveTenant);
        });
    }

    /// <summary>The UTC timestamp converter: force UTC in, mark Kind UTC out. Shared by every DateTime column.</summary>
    private static readonly ValueConverter<DateTime, DateTime> UtcConverter = new(
        v => v.Kind == DateTimeKind.Utc ? v : v.ToUniversalTime(),
        v => DateTime.SpecifyKind(v, DateTimeKind.Utc));

    private static readonly ValueConverter<DateTime?, DateTime?> NullableUtcConverter = new(
        v => v.HasValue ? (v.Value.Kind == DateTimeKind.Utc ? v.Value : v.Value.ToUniversalTime()) : v,
        v => v.HasValue ? DateTime.SpecifyKind(v.Value, DateTimeKind.Utc) : v);

    /// <summary>
    /// The account-subject converter for the entitlements table, applied ONLY under Postgres. The
    /// <c>gateway.entitlements.subject</c> column is Postgres <c>uuid</c> (the website's
    /// <c>read_gateway_entitlement(p_subject uuid)</c> depends on it, and the subject IS the Supabase auth
    /// identifier), but the CLR property is a <see cref="string"/> - the form the token validator and the
    /// entitlement registry pass and compare. Without a converter Npgsql types the key-lookup parameter as
    /// text, so Postgres rejects <c>subject = @p</c> with 42883 'operator does not exist: uuid = text' and
    /// every entitlement read fails (which is what made hosted enrollment answer 503). Converting the string
    /// to a <see cref="Guid"/> on the way to the database makes Npgsql emit a UUID parameter that matches the
    /// column, and back to the canonical "D" string on the way out keeps the property a plain string for its
    /// callers. The subject is the canonical Supabase auth identifier (the "D" form), so ParseExact("D") is
    /// exact rather than lenient; a value that is not that form is a caller error, not something to normalise.
    /// </summary>
    private static readonly ValueConverter<string, Guid> EntitlementSubjectToGuidConverter = new(
        v => Guid.ParseExact(v, "D"),
        v => v.ToString("D"));

    /// <summary>
    /// Apply the model-wide correctness conventions across every mapped property: UTC DateTime storage, a
    /// hard ban on a bare decimal mapping, and code-generated GUID keys (no database default). These are the
    /// common subset that must hold identically on SQLite and Postgres, so they live on the model, not in a
    /// provider.
    /// </summary>
    private static void ApplyCommonSubsetConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var clr = property.ClrType;

                if (clr == typeof(DateTime))
                    property.SetValueConverter(UtcConverter);
                else if (clr == typeof(DateTime?))
                    property.SetValueConverter(NullableUtcConverter);

                if (clr == typeof(decimal) || clr == typeof(decimal?))
                    throw new InvalidOperationException(
                        $"Bare decimal mapping on {entityType.ClrType.Name}.{property.Name} is forbidden: " +
                        "SQLite has no real decimal and EF falls back to double, which would silently break " +
                        "the round-up-never-undercount money rule. Store money as integer smallest-units, or " +
                        "apply an explicit value-converted decimal.");

                if ((clr == typeof(Guid) || clr == typeof(Guid?)) && property.IsPrimaryKey())
                    property.ValueGenerated = ValueGenerated.Never;
            }
        }
    }
}
