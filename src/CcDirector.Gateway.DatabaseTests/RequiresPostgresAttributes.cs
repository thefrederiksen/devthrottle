using CcDirector.Gateway.Tests.Data;
using Xunit;

namespace CcDirector.Gateway.Tests;

/// <summary>
/// THE ONE RULE FOR WHEN A POSTGRESQL TEST RUNS. Every test in this project carries this attribute or its
/// theory twin below, and there is no other copy of the decision anywhere.
///
/// It replaces twenty-four private attributes, one per class, that each looked at a different connection
/// variable and each wrote its own skip sentence. They drifted: two of them keyed on a variable the rig
/// never set (<c>CC_GATEWAY_DB_CONNECTION</c>), so their five tests skipped in every recorded run, and nothing
/// anywhere said so. A rule kept in twenty-four places is twenty-four rules.
///
/// THE RULE: a test runs when the run was PROMISED a database - when <see cref="PostgresRigGate.RequiredEnvVar"/>
/// names the rig that <c>scripts\test-database.ps1</c> built for it. That promise is then held, before any test
/// is selected, by <see cref="PostgresRigGate"/>'s module initializer: both connections must lead to the exact
/// databases, and the restricted role, that the rig built. So "the attribute let it run" and "it runs against
/// the right database" are the same statement, and a test here never decides for itself which variable to trust.
///
/// When nothing was promised - a plain <c>dotnet test</c> of the solution, or continuous integration, neither of
/// which builds a database - every test here reports SKIPPED with a reason that names the one command that
/// runs them. That skip cannot hide inside the gate any more: this project is not in <c>scripts\test-local.ps1</c>
/// at all, and <c>scripts\test-database.ps1</c> always makes the promise and fails a run that skipped anything.
///
/// IT IS DECIDED AT DISCOVERY, IN THE ATTRIBUTE, ON PURPOSE. A test class here may need the database in its
/// constructor (several reset a schema there). xUnit does not construct a class for a test it has already
/// decided to skip, but it does construct one before running any other test - so a check inside the method body
/// comes too late, and the constructor throws instead of the test skipping.
/// </summary>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (!PostgresRigGate.IsRequired) Skip = RequiresPostgres.SkipReason;
    }
}

/// <summary>The theory twin of <see cref="RequiresPostgresFactAttribute"/>, holding the same rule.</summary>
public sealed class RequiresPostgresTheoryAttribute : TheoryAttribute
{
    public RequiresPostgresTheoryAttribute()
    {
        if (!PostgresRigGate.IsRequired) Skip = RequiresPostgres.SkipReason;
    }
}

internal static class RequiresPostgres
{
    internal const string SkipReason =
        "This test needs the throwaway PostgreSQL that scripts\\test-database.ps1 builds and destroys for its run. " +
        "Run that script; nothing else provides the database these tests are held to.";
}
