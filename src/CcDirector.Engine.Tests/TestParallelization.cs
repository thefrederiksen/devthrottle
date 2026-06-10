using Xunit;

// Run this assembly's tests sequentially - same rationale as CcDirector.Core.Tests: several
// scheduler/timing tests spin up background work and assert on wall-clock-bounded outcomes, which
// flake under xUnit's default parallelism on CI's few vCPUs. Sequential execution gives them the
// CPU to meet their deadlines; coverage is unchanged.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
