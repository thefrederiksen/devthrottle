using CcDirector.Core.Tenancy;
using CcDirector.Core.Utilities;
using CcDirector.Gateway.Data;
using CcDirector.Gateway.Data.Entities;
using CcDirector.Gateway.Tenancy;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace CcDirector.Gateway.Tests.Data;
/// <summary>
/// The two halves of <see cref="EntitlementSchemaQualificationTests"/> that need a real PostgreSQL server: the
/// gateway-schema-qualified entitlement read really resolves the table, and a real <see cref="PostgresException"/>
/// on that read is logged with its SQLSTATE and message text and never with the account subject. The model-only
/// halves - what EF generates under each provider - need no server and stay with the Gateway tests.
/// </summary>
public sealed class EntitlementSchemaQualificationPostgresTests
{
    private const string PgTestConnectionEnvVar = "CC_GATEWAY_TEST_PG_CONNECTION";

    /// <summary>
    /// PROOF (b), end-to-end on a REAL Postgres server. The gateway-schema-qualified read really resolves the
    /// entitlements table when that table exists in the <c>gateway</c> schema - so the qualification is not
    /// just in the generated string, it hits the right relation. The Gateway migrates the gateway schema but
    /// deliberately does NOT create entitlements (it is excluded from migrations and owned by the payment
    /// side), so the test creates it in the gateway schema exactly as the payment side would, then reads it
    /// through the Gateway's own <see cref="EntitlementRegistry"/>.
    /// </summary>
    [RequiresPostgresFact]
    public void EntitlementRead_AgainstRealPostgres_ResolvesGatewaySchemaTable()
    {
        var conn = Environment.GetEnvironmentVariable(PgTestConnectionEnvVar)!;
        // A CANONICAL D-form uuid subject. The subject column is Postgres uuid and the Gateway reads it through
        // the string<->Guid value converter, so a non-uuid value (the old "sub-live-..." marker) would throw
        // Guid.ParseExact before the read ever reached Postgres. A fresh uuid keeps the fixture rerunnable and
        // unique per run.
        var subject = Guid.NewGuid().ToString("D");
        var subjectGuid = Guid.Parse(subject);
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, conn);
        try
        {
            using var db = new GatewayDatabase(new SingleTenantContext());
            using (var ctx = db.CreateUnscopedContext())
            {
                // The payment side owns this table; create it in the gateway schema as that side would - with
                // subject as `uuid`, MATCHING the live column the converter is typed against, so this proves the
                // real scenario (uuid column + the converter's uuid parameter), not a text-column stand-in. Drop
                // first so a stale text-column table from an earlier test build cannot linger and defeat the fix.
                ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS gateway.entitlements;");
                ctx.Database.ExecuteSqlRaw(
                    "CREATE TABLE gateway.entitlements (" +
                    "subject uuid NOT NULL PRIMARY KEY, status text NOT NULL, " +
                    "current_period_end timestamptz NULL, stripe_subscription_id text NULL, " +
                    "updated_at timestamptz NULL, livemode boolean NULL, tier text NULL);");
                // Seed one live, active entitlement on the hosted tier. The subject is bound as a native uuid
                // parameter so it lands in the uuid column exactly as the payment side would write it.
                ctx.Database.ExecuteSqlRaw(
                    "INSERT INTO gateway.entitlements (subject, status, livemode, tier) VALUES ({0}, 'active', true, 'hosted');",
                    subjectGuid);
            }

            var registry = new EntitlementRegistry(db, requireLivemode: true);
            // The full read: the join lands on the uuid column (the 42883 trap is guarded), the outcome is
            // Entitled, and the tier is read back off the same row.
            var decision = registry.Evaluate(subject, DateTime.UtcNow);
            Assert.Equal(EntitlementOutcome.Entitled, decision.Outcome);
            Assert.Equal(EntitlementRegistry.TierHosted, decision.Tier);
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
        }
    }

    /// <summary>
    /// PROOF (c). When the entitlement read fails with a <see cref="PostgresException"/>, the diagnostic log
    /// carries the two fields that let the server's own error be read - the SQLSTATE code and the server's
    /// message text - and NEVER the account subject. Driven through the Gateway's own read path against a real
    /// server: the Gateway migrates the gateway schema but not the entitlements table (excluded from
    /// migrations), so the read throws SQLSTATE 42P01 (undefined_table) - a genuine PostgresException, not a
    /// constructed one.
    /// </summary>
    [RequiresPostgresFact]
    public void EntitlementReadFailure_OnPostgresException_LogsSqlStateAndMessageText_NeverSubject()
    {
        var conn = Environment.GetEnvironmentVariable(PgTestConnectionEnvVar)!;
        // A CANONICAL D-form uuid subject, unique per run. It MUST be a valid uuid: the converter parses it on
        // the way to Postgres, so a non-uuid marker (the old "sub-PII-marker-...") would throw Guid.ParseExact
        // before the read reached the server, and the intended undefined-table 42P01 would never be produced -
        // the diagnostic path this fact exists to prove would go unexercised. A fresh uuid is still a unique,
        // searchable token, so the never-log-the-subject assertion below is just as unmistakable.
        var subject = Guid.NewGuid().ToString("D");
        var previous = Environment.GetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar);
        Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, conn);

        IReadOnlyList<string> lines;
        EntitlementOutcome outcome;
        try
        {
            using var db = new GatewayDatabase(new SingleTenantContext());

            // Make sure the entitlements table is ABSENT so the gateway-qualified read fails with 42P01.
            using (var ctx = db.CreateUnscopedContext())
                ctx.Database.ExecuteSqlRaw("DROP TABLE IF EXISTS gateway.entitlements;");

            var registry = new EntitlementRegistry(db, requireLivemode: true);

            using var scope = FileLog.RedirectForTests();
            outcome = registry.LookupBySubject(subject, DateTime.UtcNow);
            lines = scope.DrainAndReadLines();
        }
        finally
        {
            Environment.SetEnvironmentVariable(GatewayDatabase.PostgresConnectionEnvVar, previous);
        }

        // Ignorance, not absence: a failed read is Unknown, never a denial or a grant.
        Assert.Equal(EntitlementOutcome.Unknown, outcome);

        var diagnostic = Assert.Single(lines, l => l.Contains("PostgreSQL error"));
        Assert.Contains("SqlState=42P01", diagnostic);
        Assert.Contains("MessageText=", diagnostic);

        // The never-log-the-subject rule holds across EVERY line the read produced, not just the new one.
        Assert.DoesNotContain(lines, l => l.Contains(subject));
    }
}
