using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE ONE TEST THAT CANNOT SKIP (issue #2834).
///
/// Every other Postgres-backed proof in this repository is allowed to skip, because a developer on a
/// laptop with no database should not be blocked by them. This one is not, and that asymmetry is the
/// whole point: it does not ask "is a database available", it asks "did this run provide what it said it
/// would provide", and a run that answers no has a fault in it whatever the other suites report.
///
/// WHAT IT WOULD HAVE CAUGHT. On 14 September 2026 the shared rig container stopped an hour before the
/// v2.1.2 release gate ran. Eight tests in one class failed with
/// "Failed to connect to 127.0.0.1:55432", spread through a suite of four thousand, and the release
/// looked like a product defect for twenty minutes. With this test present the output is one red line
/// naming the variable, the server and the reason nothing answered.
///
/// It is linked into both Postgres-bearing assemblies rather than copied, so neither can lose it in a
/// split. There is deliberately no [Trait] and no filtering on it: it must run in every run that runs
/// anything here.
/// </summary>
public sealed class PostgresRigIsPresentWhenRequiredTests
{
    /// <summary>
    /// When the run declared that it built a database, one must actually be reachable on BOTH connection
    /// strings - the superuser database the provider proofs drop and re-create, and the statistics
    /// database the restricted role writes to. A run can lose either independently.
    ///
    /// When the run declared nothing, this passes without touching the network. That is not a weakened
    /// assertion: there is nothing to hold anyone to.
    /// </summary>
    [Fact]
    public void A_run_that_promised_a_database_has_one()
    {
        if (!PostgresRigGate.IsRequired) return;

        var faults = new List<string>();
        foreach (var variable in new[]
                 {
                     "CC_GATEWAY_TEST_PG_CONNECTION",
                     "CC_GATEWAY_TEST_PG_STATS_CONNECTION",
                 })
        {
            var why = PostgresRigGate.WhyUnreachable(variable);
            if (why is not null) faults.Add(why);
        }

        Assert.True(faults.Count == 0,
            $"This run set {PostgresRigGate.RequiredEnvVar}=1, which says it provisioned a throwaway "
            + "PostgreSQL for the Postgres-backed proofs - but it is not there now:"
            + Environment.NewLine + Environment.NewLine
            + string.Join(Environment.NewLine, faults)
            + Environment.NewLine + Environment.NewLine
            + "The database is created and destroyed by scripts\\test-local.ps1 for the run that needs "
            + "it. If this fails, the rig died mid-run or never came up - read the run's own output "
            + "above, not the other Postgres failures, which are all this same fault repeated.");
    }

    /// <summary>
    /// The other half, and it is the half that keeps a laptop usable: with nothing promised, the proofs
    /// are allowed to skip and this says so out loud rather than leaving the reader to infer it.
    ///
    /// Asserting on the SKIP REASON rather than on a boolean, because the reason is what a person reads
    /// when they wonder why a suite reported fewer tests than they expected.
    /// </summary>
    [Fact]
    public void With_nothing_promised_a_proof_skips_and_says_how_to_run_it()
    {
        if (PostgresRigGate.IsRequired) return;

        var reason = PostgresRigGate.SkipReason("CC_GATEWAY_TEST_PG_CONNECTION", "the provider proof");

        // Null here means a database IS configured by hand, which is a perfectly good state - the proof
        // runs. Only the unconfigured case has a reason to state.
        if (reason is null) return;

        Assert.Contains("test-local.ps1", reason);
    }
}
