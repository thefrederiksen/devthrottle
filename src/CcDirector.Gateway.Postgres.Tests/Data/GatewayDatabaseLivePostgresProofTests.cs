using CcDirector.Core.Tenancy;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// Live integration proof for the Hosted Gateway data layer against a REAL, configured PostgreSQL server
/// (the hosted Supabase target), driven through the SAME startup path the running Gateway uses:
/// constructing a <see cref="GatewayDatabase"/> reads <c>CC_GATEWAY_DB_CONNECTION</c> and runs
/// <c>Database.Migrate()</c>, applying the Postgres migration set into the <c>gateway</c> schema. Each fact
/// then round-trips one of the shapes that could diverge between providers - a JSON-owned column, a
/// natural-key column whose ordering depends on the collation, and a UTC timestamp - and DELETES the rows it
/// wrote so the schema is left clean and deploy-ready (empty tables, migration applied).
///
/// Unlike the throwaway-container proof (<see cref="PostgresProviderProofTests"/>), this NEVER drops a
/// database or a schema: the hosted role is scoped to the <c>gateway</c> schema and cannot create or drop
/// databases, so the proof is a real integration run that leaves the applied schema in place.
///
/// GATING: every fact skips itself unless <c>CC_GATEWAY_DB_CONNECTION</c> is set to a non-blank value - the
/// exact environment variable the runtime Gateway uses to select Postgres. With it UNSET (automated CI and
/// the normal SQLite test run) nothing here connects to any server and no secret is needed; the facts report
/// SKIPPED. It runs only locally/manually with the connection string exported.
/// </summary>
public sealed class GatewayDatabaseLivePostgresProofTests
{
    /// <summary>A Fact that skips itself unless the runtime Postgres selector <c>CC_GATEWAY_DB_CONNECTION</c>
    /// is set to a non-blank value, so CI never reaches out to the hosted database and never needs the
    /// secret.</summary>
    private sealed class RequiresConfiguredPostgresFactAttribute : FactAttribute
    {
        public RequiresConfiguredPostgresFactAttribute()
        {
            if (string.IsNullOrWhiteSpace(
                    Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar)))
                Skip = $"Set {GatewayDatabase.PostgresConnectionEnvVar} to a real PostgreSQL connection " +
                       "string to run the live hosted-Postgres integration proof.";
        }
    }

    /// <summary>Open the Gateway database exactly as the runtime does: the constructor selects Postgres from
    /// <c>CC_GATEWAY_DB_CONNECTION</c> and applies the migration set into the gateway schema. The caller
    /// disposes it.
    ///
    /// A construction failure is re-thrown as a NEW, credential-free exception with NO inner exception
    /// attached. GatewayDatabase's own failure path deliberately preserves the raw provider exception as the
    /// InnerException (for a real host's diagnostics), and a provider exception can echo the connection
    /// string; xUnit prints inner exceptions on a failed test. Stripping the chain here keeps any
    /// connection-string material out of test output. Only the failing exception's TYPE name is surfaced,
    /// never its message.</summary>
    private static GatewayDatabase OpenGateway()
    {
        try
        {
            return new GatewayDatabase(new SingleTenantContext());
        }
        catch (Exception ex)
        {
            var kind = ex is InvalidOperationException && ex.InnerException is not null
                ? ex.InnerException.GetType().Name
                : ex.GetType().Name;
            throw new InvalidOperationException(
                "Opening the live Gateway database against the configured PostgreSQL server failed " +
                $"({kind}). The original exception is intentionally not attached, so no connection-string " +
                "material reaches test output. Check the server, the gateway schema, and the connection.");
        }
    }

    /// <summary>
    /// The real startup path: constructing GatewayDatabase runs Database.Migrate() against the configured
    /// Postgres, which must create the gateway schema, EVERY table the model maps and this Gateway owns, and
    /// the migrations history table under that schema (and nothing under public). This is read-only after the
    /// migrate, so it deliberately leaves the applied - empty - schema in place, which is the desired
    /// deploy-ready state.
    ///
    /// "Every" is meant literally and is derived from the model below. It used to say "all 16 mapped tables"
    /// while iterating a hand-kept list of 16 out of the 38 the model maps - a census in the summary and a
    /// sample in the code, which is green in precisely the case it exists for.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void Startup_AppliesGatewaySchemaAndTables_OnConfiguredPostgres()
    {
        using var db = OpenGateway();
        using var ctx = db.CreateContext();

        Assert.Equal(1, ScalarInt(ctx,
            "SELECT count(*) FROM information_schema.schemata WHERE schema_name = 'gateway'"));

        // EVERY mapped table, asked of the MODEL - not of the hand-kept list below, which named 16 of the
        // 38 the model actually maps while the assertion around it read as a schema-isolation census. A
        // sample presenting itself as a census is green in exactly the case it exists for: a table added
        // today, whose schema placement nobody has checked yet, is not in a list written months ago.
        // THE DESIGN-TIME MODEL, not ctx.Model: the runtime model is read-optimized and has dropped the
        // migrations metadata, so it throws rather than answering "is this excluded from migrations?".
        var mapped = ctx.GetService<Microsoft.EntityFrameworkCore.Metadata.IDesignTimeModel>().Model.GetEntityTypes()
            .Where(e => !e.IsOwned())
            // EXCLUDED FROM MIGRATIONS MEANS WE DO NOT CREATE IT. `entitlements` belongs to the payment
            // side and this Gateway only READS it, so its absence from a Gateway-applied schema is the
            // correct outcome, not a defect. Mapped is not the same question as ours-to-create.
            .Where(e => !e.IsTableExcludedFromMigrations())
            .Select(e => e.GetTableName())
            .Where(t => !string.IsNullOrEmpty(t))
            .Select(t => t!)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(t => t, StringComparer.Ordinal)
            .ToList();

        // The scan found the model - otherwise the loop below is vacuous and this passes proving nothing.
        Assert.True(mapped.Count >= MappedTables.Length,
            $"the model mapped only {mapped.Count} tables, fewer than the {MappedTables.Length} known-good " +
            "names - the model scan is broken, not the schema");
        foreach (var known in MappedTables)
            Assert.Contains(known, mapped, StringComparer.Ordinal);

        foreach (var table in mapped)
        {
            Assert.Equal(1, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = '{table}'"));
            // And NOT in public - the hosted schema is isolated from the website's tables.
            Assert.Equal(0, ScalarInt(ctx,
                $"SELECT count(*) FROM information_schema.tables WHERE table_schema = 'public' AND table_name = '{table}'"));
        }

        Assert.Equal(1, ScalarInt(ctx,
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = 'gateway' AND table_name = '__EFMigrationsHistory'"));
    }

    /// <summary>
    /// JSON-owned columns on real Postgres: a WorkflowVersion's Steps and OutcomeCriteria are owned
    /// collections serialized to jsonb. Write, read back in a fresh context, assert field-for-field, then
    /// delete the row so the table is left empty.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void JsonOwnedColumns_RoundTrip_OnConfiguredPostgres()
    {
        using var db = OpenGateway();
        // The key is minted by GatewayMintedKeyEntity, not chosen here - read it back off the row we added.
        // Left empty until then so the cleanup below is a no-op if the insert never happened.
        var id = Guid.Empty;
        try
        {
            using (var ctx = db.CreateContext())
            {
                var row = new WorkflowVersionEntity
                {
                    TenantId = TenantId.Local.Value,
                    WorkflowId = "wf-live-json-proof",
                    Version = 1,
                    Status = WorkflowVersionStatus.Published,
                    Name = "live JSON proof",
                    Summary = "owned collections to jsonb on Supabase",
                    Steps = new List<WorkflowStepDto>
                    {
                        new() { Name = "Plan", Description = "d1", Doer = "architect", Reviewer = null, Done = "planned" },
                        new() { Name = "Build", Description = "d2", Doer = "worker", Reviewer = "qa", Done = "merged" },
                    },
                    OutcomeCriteria = new List<WorkflowOutcomeCriterionDto>
                    {
                        new() { CriterionId = "merged-pr", Description = "the PR is merged", ProofHint = "the merged pull request URL" },
                        new() { CriterionId = "green-ci", Description = "CI is green", ProofHint = null },
                    },
                    InstructionsMarkdown = "# conduct",
                    ContentHash = "hash",
                    AuthoredBy = "test",
                    CreatedUtc = DateTime.UtcNow,
                };
                ctx.WorkflowVersions.Add(row);
                ctx.SaveChanges();
                id = row.Id;
            }

            using (var ctx = db.CreateContext())
            {
                var read = ctx.WorkflowVersions.Single(v => v.Id == id);

                // Every field of both owned Steps, in order.
                Assert.Equal(2, read.Steps.Count);
                Assert.Equal("Plan", read.Steps[0].Name);
                Assert.Equal("d1", read.Steps[0].Description);
                Assert.Equal("architect", read.Steps[0].Doer);
                Assert.Null(read.Steps[0].Reviewer);
                Assert.Equal("planned", read.Steps[0].Done);
                Assert.Equal("Build", read.Steps[1].Name);
                Assert.Equal("d2", read.Steps[1].Description);
                Assert.Equal("worker", read.Steps[1].Doer);
                Assert.Equal("qa", read.Steps[1].Reviewer);
                Assert.Equal("merged", read.Steps[1].Done);

                // Every field of both owned OutcomeCriteria, in order (including a null ProofHint).
                Assert.Equal(2, read.OutcomeCriteria.Count);
                Assert.Equal("merged-pr", read.OutcomeCriteria[0].CriterionId);
                Assert.Equal("the PR is merged", read.OutcomeCriteria[0].Description);
                Assert.Equal("the merged pull request URL", read.OutcomeCriteria[0].ProofHint);
                Assert.Equal("green-ci", read.OutcomeCriteria[1].CriterionId);
                Assert.Equal("CI is green", read.OutcomeCriteria[1].Description);
                Assert.Null(read.OutcomeCriteria[1].ProofHint);
            }
        }
        finally
        {
            using var ctx = db.CreateContext();
            ctx.WorkflowVersions.Where(v => v.Id == id).ExecuteDelete();
        }
    }

    /// <summary>
    /// Natural-key byte-ordinal collation on real Postgres: the push_subscriptions Endpoint primary key
    /// carries an explicit "C" collation, so keys differing by byte order come back in byte order (not a
    /// locale grouping) and the primary key rejects a duplicate. Write, assert, then delete the rows.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void NaturalKeyByteOrdinalCollation_RoundTrip_OnConfiguredPostgres()
    {
        using var db = OpenGateway();
        // A per-run GUID prefix scopes this test to exactly the three rows it writes: another live-proof run
        // (or a pre-existing row) cannot be read, ordered, or deleted by this one. The three keys share the
        // prefix and differ only in the final byte, so their relative order is decided by that byte:
        // 'B'(66) < 'C'(67) < 'a'(97). A locale collation would group case-insensitively and put the "a" key
        // first, so asserting [B, C, a] distinguishes the explicit "C" collation from a locale one.
        var prefix = "live_" + Guid.NewGuid().ToString("N") + "_";
        var endpoints = new[] { prefix + "a", prefix + "C", prefix + "B" };
        try
        {
            using (var ctx = db.CreateContext())
            {
                foreach (var e in endpoints)
                    ctx.PushSubscriptions.Add(new PushSubscriptionEntity
                    {
                        Endpoint = e,
                        TenantId = TenantId.Local.Value,
                        P256dh = "p",
                        Auth = "a",
                        CreatedAtUtc = DateTime.UtcNow,
                    });
                ctx.SaveChanges();
            }

            using (var ctx = db.CreateContext())
            {
                var ordered = ctx.PushSubscriptions
                    .Where(p => p.Endpoint.StartsWith(prefix))
                    .OrderBy(p => p.Endpoint)
                    .Select(p => p.Endpoint)
                    .ToList();
                Assert.Equal(new[] { prefix + "B", prefix + "C", prefix + "a" }, ordered);
            }

            using (var ctx = db.CreateContext())
            {
                ctx.PushSubscriptions.Add(new PushSubscriptionEntity
                {
                    Endpoint = prefix + "B",
                    TenantId = TenantId.Local.Value,
                    P256dh = "p2",
                    Auth = "a2",
                    CreatedAtUtc = DateTime.UtcNow,
                });
                Assert.Throws<DbUpdateException>(() => ctx.SaveChanges());
            }
        }
        finally
        {
            // Delete by the per-run GUID prefix: it matches exactly the three rows this run wrote and nothing
            // else, so cleanup is as tightly scoped as an explicit key list, and StartsWith translates to a
            // clean LIKE 'prefix%' in ExecuteDelete (an array .Contains() resolves to the ReadOnlySpan overload
            // on this runtime and breaks EF's ExecuteDelete translation).
            using var ctx = db.CreateContext();
            ctx.PushSubscriptions.Where(p => p.Endpoint.StartsWith(prefix)).ExecuteDelete();
        }
    }

    /// <summary>
    /// UTC timestamp on real Postgres: a session_spend row written then read in a fresh context comes back
    /// equal and with Kind == Utc (Npgsql maps a Kind=Utc DateTime to timestamp with time zone). Write,
    /// assert, then delete the row.
    /// </summary>
    [RequiresConfiguredPostgresFact]
    public void UtcTimestampRoundTrip_OnConfiguredPostgres()
    {
        using var db = OpenGateway();
        var sessionId = "live-" + Guid.NewGuid().ToString();
        var first = new DateTime(2026, 7, 18, 13, 45, 30, DateTimeKind.Utc);
        var last = new DateTime(2026, 7, 18, 14, 05, 00, DateTimeKind.Utc);
        try
        {
            using (var ctx = db.CreateContext())
            {
                ctx.SessionSpend.Add(new SessionSpendEntity
                {
                    SessionId = sessionId,
                    TenantId = TenantId.Local.Value,
                    AgentKind = "test-agent",
                    TokensCaptured = true,
                    InputTokens = 1000,
                    OutputTokens = 200,
                    BillingMode = "metered",
                    MeteredCostMicros = 12345,
                    FirstObservedUtc = first,
                    LastObservedUtc = last,
                });
                ctx.SaveChanges();
            }

            using (var ctx = db.CreateContext())
            {
                var read = ctx.SessionSpend.Single(s => s.SessionId == sessionId);
                Assert.Equal("test-agent", read.AgentKind);
                Assert.Equal(12345, read.MeteredCostMicros);
                Assert.Equal(DateTimeKind.Utc, read.FirstObservedUtc.Kind);
                Assert.Equal(first, read.FirstObservedUtc);
                Assert.Equal(DateTimeKind.Utc, read.LastObservedUtc.Kind);
                Assert.Equal(last, read.LastObservedUtc);
            }
        }
        finally
        {
            using var ctx = db.CreateContext();
            ctx.SessionSpend.Where(s => s.SessionId == sessionId).ExecuteDelete();
        }
    }

    /// <summary>
    /// A FLOOR of known-good table names, NOT the set the isolation assertion iterates - that is derived
    /// from the model, because this array said "the 16 mapped tables" while the model mapped 38. It is kept
    /// because it asserts something the derived check cannot: that these particular tables have not
    /// silently stopped being mapped at all.
    /// </summary>
    private static readonly string[] MappedTables =
    {
        "cron_jobs", "cron_runs", "worklists", "worklist_items", "workflows", "workflow_versions",
        "workflow_files", "workflow_runs", "snoozes", "governance_events", "push_subscriptions",
        "wingman_instructions", "session_spend", "account_hosted_ai_spend", "mission_notes",
        "governance_audit_events",
    };

    private static int ScalarInt(GatewayDbContext ctx, string sql)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            conn.Open();
            opened = true;
        }
        try
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = sql;
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
        finally
        {
            if (opened) conn.Close();
        }
    }
}
