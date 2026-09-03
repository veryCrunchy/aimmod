using NUnit.Framework;

// Realm's native coordinator is process-global. Running snapshot fixtures beside
// catalog fixtures can close a Realm while another fixture is committing a
// transaction, which aborts the entire test host instead of reporting a failure.
[assembly: LevelOfParallelism(1)]
