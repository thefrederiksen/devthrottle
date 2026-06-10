using Xunit;

// Run this assembly's tests SEQUENTIALLY (no cross-collection parallelism).
//
// Why: a chunk of these tests are real-time-dependent - they spin up their own background
// Tasks/threads and assert on wall-clock deadlines (the CircularTerminalBuffer concurrency
// stress test's 2s window, the Scheduler's background fire + persist, the AtReference watchdog
// beats, the SessionLogWriter file I/O). Under xUnit's default parallelism the runner schedules
// ~1700 tests at once, and on CI's 2-4 vCPU box the thread pool is starved hard enough that those
// timing tests miss their deadlines and fail nondeterministically - a different few each run, so
// CI was red on essentially every push.
//
// Disabling parallelization gives each timing test the CPU it needs to meet its deadline. It does
// NOT reduce coverage: the concurrency test still runs its own internal threads, so it still
// exercises CircularTerminalBuffer's thread-safety - it just no longer competes with the rest of
// the suite for scheduling. The cost is a slower sequential run (most tests are sub-millisecond),
// which is the right trade for a CI signal you can trust.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
