using Xunit;

// One test at a time, and it is load-bearing, not caution. The proof classes share ONE database per run
// (PostgresProofDatabase) and most of them call EnsureDeleted() on it before migrating from nothing, and the
// statistics classes drop and rebuild one schema in the statistics database. Run in parallel, one class
// drops the database out from under another mid-test. GatewayHostBootPostgresTests also sets the Gateway's
// process-wide connection variable for the length of one test, which is safe only because nothing else runs
// beside it. Giving every class its own database is what would let this go; until then it stays.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
