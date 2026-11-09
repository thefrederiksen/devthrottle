using CcDirector.CliExplorer.Execution;
using CcDirector.CliExplorer.Scenarios;

namespace CcDirector.CliExplorer.Reporting;

public enum TestOutcome
{
    Pass,
    Fail,
    Skip,
    Error
}

public record TestResult(
    TestScenario Scenario,
    RunResult? RunResult,
    TestOutcome Outcome,
    string? Notes = null);
