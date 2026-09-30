using Xunit;

// Almost every test class here initializes a node, i.e. runs Argon2id through the process-wide
// KeyDerivation gate. The gate's capacity is half the cores (two units on a 4-core CI runner) and it
// fails fast with KdfBusyException when more than 16 derivations are queued, or after 30 s in the queue.
// More test threads than the gate can serve only lengthen the queue: eight threads on a 4-core runner
// left a few tests waiting the full 30 s and failing (GitHub Actions, release 1.0.13), while the suite is
// no faster than with four. Four keeps the queue short on the smallest runner and on a 24-thread
// workstation alike, while still running in parallel.
[assembly: CollectionBehavior(MaxParallelThreads = 4)]
