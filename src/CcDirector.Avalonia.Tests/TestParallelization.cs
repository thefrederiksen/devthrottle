using Xunit;

// Run this assembly's tests sequentially. Many of these tests spin up background work
// (gateway hosts, supervisors, async clients) and assert on wall-clock-bounded outcomes,
// which flake under xUnit's default parallelism on CI's few vCPUs. Sequential execution gives
// each timing test the CPU to meet its deadline; coverage is unchanged. (issue: flaky CI red)
[assembly: CollectionBehavior(DisableTestParallelization = true)]
