using Xunit;

namespace CcDirector.Gateway.Tests.Data;

/// <summary>
/// THE READABLE HALF OF THE RIG GUARD (issue #2834).
///
/// The enforcement itself is a [ModuleInitializer] on <see cref="PostgresRigGate"/>, because a test can
/// be excluded by `-Filter` and the thing that proves a run is sound must not be excludable. What this
/// class adds is a NAMED RESULT in an ordinary run - "A_run_that_promised_a_database_has_one, passed" -
/// rather than the absence of a crash, and a red with a proper message in the narrow window where the
/// initializer has run but something has changed underneath it since.
///
/// It is also what fails if the initializer ever stops running: delete the [ModuleInitializer] attribute
/// and this test is the only thing left that notices, which is exactly the job the repository already
/// gives the pinning test beside TestStorageRootRedirect.
///
/// WHAT IT WOULD HAVE CAUGHT. On 14 September 2026 the shared rig container stopped an hour before the
/// v2.1.2 release gate ran. Eight tests in one class failed with "Failed to connect to 127.0.0.1:55432",
/// spread through a suite of four thousand, and the release looked like a product defect for twenty
/// minutes.
///
/// Linked into both Postgres-bearing assemblies rather than copied, so neither can lose it in a split.
/// </summary>
public sealed class PostgresRigIsPresentWhenRequiredTests
{
    /// <summary>
    /// When the run declared which rig it built, both connections must lead to THAT rig - the right
    /// database on both, and the restricted login role on the statistics one.
    ///
    /// It is not enough that something answers. A connection that opens and returns a row proves a
    /// PostgreSQL exists somewhere, not that these tests are pointed at the database this run created;
    /// both variables aimed at one superuser database passed the earlier heartbeat version of this check
    /// while leaving the statistics proofs meaningless.
    ///
    /// When the run declared nothing, this passes without touching the network. That is not a weakened
    /// assertion - there is nothing and nobody to hold to anything.
    /// </summary>
    [Fact]
    public void A_run_that_promised_a_database_has_one()
    {
        var faults = PostgresRigGate.Faults();
        Assert.True(faults.Count == 0, PostgresRigGate.Explain(faults));
    }

    /// <summary>
    /// The fail-fast is wired, and it is wired to the same decision this test makes.
    ///
    /// Asserting on the SHARED RULE rather than re-deriving it: if <see cref="PostgresRigGate.Faults"/>
    /// is empty then the module initializer let the assembly load, and if it is not empty the assembly
    /// would not have loaded at all and nothing here would be running. The two cannot disagree, and this
    /// says so out loud so nobody later adds a second, softer rule beside it.
    /// </summary>
    [Fact]
    public void The_assembly_only_loaded_because_the_same_rule_was_satisfied()
    {
        // Reaching this line at all means the [ModuleInitializer] did not throw.
        Assert.Empty(PostgresRigGate.Faults());
    }
}
