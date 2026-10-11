using Xunit;

// Four test threads, as Core.UnitTests runs, instead of one.
//
// This assembly ran SEQUENTIALLY for a long time: a handful of its tests spin up real processes, set
// process-wide environment variables (CC_DIRECTOR_ROOT above all), or pin static product seams, and under
// xUnit's default of one thread per processor those collided with everything else and failed a different
// few each run. Serial was the right call then and it cost about seven minutes a run in October 2026.
//
// The collisions are now confined instead of the whole assembly: every class that touches process-wide
// state - an environment variable, a static seam, a real process, the storage root - sits in a collection
// defined with DisableParallelization (CcStorageRoot, ConfigEnvSerial, PromptDeliveryFailures,
// BrowserLauncherDetectCache, ProcessWideState). xUnit runs such a collection on its own, with nothing beside
// it; every other class runs four wide. The timing tests that once needed the whole machine now wait on
// injected clocks (TerminalSubmit, Session, the scripted terminal, the delivery record, the scheduler), so
// they no longer need it.
//
// A class that starts touching process-wide state joins one of those collections. A failure that appears
// only under parallel runs is a class that should be in one and is not.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
