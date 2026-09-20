using Xunit;

// Run this assembly's tests one at a time.
//
// THIS IS NOT A PREFERENCE, IT IS WHAT THESE TESTS ALREADY HAD. Every class in here came from
// CcDirector.Gateway.Tests, which disables parallelism for the whole assembly, or was the only
// database-backed class in CcDirector.Gateway.UnitTests. Gathering them into one assembly without this
// line would run them beside each other for the first time - and several of them DROP the statistics
// schema, or delete and re-migrate the proof database, as their first act. Two of those overlapping
// would destroy each other's rows mid-test and report failures nobody caused, which is the corrupted
// evidence the whole arrangement exists to avoid. The move must not change what a test does, and running
// alone is what these tests have always done.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
