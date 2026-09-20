using CcDirector.Core.Tenancy;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// OUTPUT PARITY, on SQLite, between the JSON store
/// (<see cref="Gateway.Stats.GatewaySessionConcurrencyStats"/>, which writes
/// <c>gateway-concurrency-stats.json</c> to the shared file share) and the database store
/// (<see cref="Gateway.Stats.GatewaySessionConcurrencyStore"/>, which replaces it).
///
/// One fixture is driven through BOTH implementations, observation for observation, and what is compared is
/// the RENDERED snapshot - the exact JSON body the <c>/stats/data</c> route serves for its
/// <c>concurrency</c> property. That is deliberate, and it is not the same claim as "the same numbers are
/// stored": storing equal numbers and rendering an equal page are two different properties, and only the
/// second is what the owner sees. A difference in a null timestamp, a DateTime Kind, the order of the hourly
/// list, or an hour bucket that one store creates and the other does not, all show up here and none of them
/// would show up in a row-by-row comparison of the two stores' contents.
///
/// The fixture and the comparison live in <see cref="ConcurrencyStoreScenarios"/> and are run against real
/// PostgreSQL too, in <c>GatewaySessionConcurrencyPostgresTests</c> (CcDirector.Gateway.Postgres.Tests).
///
/// SCOPE. The main fixture moves forward in time, because production time does. Observations that arrive
/// for an EARLIER hour than one already folded are covered separately, by
/// <see cref="AnHourObservedAgainAfterALaterOne_MatchesTheFileStore"/> - and they were the one place these
/// two implementations really did disagree. An earlier draft of this file called that divergence out in
/// prose and left it uncovered on the grounds that production time is monotonic; review 3 was right that
/// naming a gap is not the same as closing it, and out-of-order observations are on the mission's boundary
/// list. The store now reproduces the file store's behaviour there and the test holds it to it.
/// </summary>
public sealed class GatewaySessionConcurrencyParityTests : IDisposable
{
    private readonly StatsConcurrencyTestDb _db = new();
    private readonly string _jsonPath =
        Path.Combine(Path.GetTempPath(), "cc-conc-parity-" + Guid.NewGuid().ToString("N") + ".json");

    public void Dispose()
    {
        _db.Dispose();
        try { File.Delete(_jsonPath); } catch (IOException) { /* temp artifact */ }
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_AcrossTheWholeFixture_AndAfterBothStoresRestart()
    {
        ConcurrencyStoreScenarios.AssertOutputParityAcrossTheFixture(() => _db.NewFactory(), _jsonPath);
    }

    [Fact]
    public void RenderedSnapshot_IsIdentical_OnTheRetentionBoundary()
    {
        ConcurrencyStoreScenarios.AssertOutputParityOnTheRetentionBoundary(
            () => _db.NewFactory(), _jsonPath, TenantId.Local);
    }

    [Fact]
    public void AnHourObservedAgainAfterALaterOne_MatchesTheFileStore()
    {
        ConcurrencyStoreScenarios.AssertAnHourObservedAgainAfterALaterOneMatchesTheFileStore(
            () => _db.NewFactory(), _jsonPath, TenantId.Local);
    }

    [Fact]
    public void TheParityComparison_NoticesWhenTheTwoStoresDiverge()
    {
        // Without this, every green above is consistent with a comparison that can never fail.
        ConcurrencyStoreScenarios.AssertTheParityComparisonDetectsADifference(
            () => _db.NewFactory(), _jsonPath, TenantId.Local);
    }
}
