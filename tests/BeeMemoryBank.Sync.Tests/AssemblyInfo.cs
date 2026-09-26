using Xunit;

// Almost every test class here initializes a node, i.e. runs Argon2id through the process-wide
// KeyDerivation gate, which fails fast with KdfBusyException once more than 16 derivations are
// queued. xUnit runs one class per core by default: fine on a 4-core CI runner, but on a
// 24-thread workstation the classes flood the queue and a random dozen fail. Eight keeps the
// queue well under the limit on any machine while still running in parallel.
[assembly: CollectionBehavior(MaxParallelThreads = 8)]
