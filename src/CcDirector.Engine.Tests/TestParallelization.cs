using Xunit;

// Four test threads instead of one. The classes that run REAL PROCESSES through the job executor (the
// scheduler, the claim, the stop, the shared-database race) sit in the RealProcesses collection, defined with
// DisableParallelization, so xUnit runs them with nothing beside them; the database, cron and dispatcher
// classes run four wide. The scheduler's waits are on an injected clock since October 2026, so they no
// longer need the whole machine to meet a wall-clock deadline.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
