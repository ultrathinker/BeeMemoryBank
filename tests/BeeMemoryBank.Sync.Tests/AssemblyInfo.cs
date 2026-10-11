using Xunit;

// Almost every test class here initializes a node, i.e. runs Argon2id through the process-wide
// KeyDerivation gate. The gate's capacity is half the cores (two units on the smallest CI runner),
// so scheduling more test classes than that can leave setup derivations queued until the gate's
// 30-second deadline. Two test threads match the smallest gate, keep suite-level KDF work bounded,
// and still let independent tests run in parallel.
[assembly: CollectionBehavior(MaxParallelThreads = 2)]
