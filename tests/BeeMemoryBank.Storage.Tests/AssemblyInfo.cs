using Xunit;

// SQLite's temp_store_directory pragma is process-global. The spill regression test changes it
// to observe a private directory, so no Storage test may use SQLite concurrently with it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
