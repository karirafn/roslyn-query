namespace roslyn_query.Tests.PipeProtocolTests;

// GetSocketDirectory and GetStateDirectory mutate process-global environment variables
// (XDG_RUNTIME_DIR, ROSLYN_QUERY_SOCKET_DIR, ROSLYN_QUERY_STATE_DIR) that PipeProtocol reads
// when deriving socket and PID paths. xUnit runs test classes in parallel by default, so without
// isolation a concurrent daemon test binds a socket under a bogus test directory and fails with
// EADDRNOTAVAIL. DisableParallelization = true keeps this collection from overlapping any other
// collection, serialising the env-mutating tests against the rest of the suite.
[CollectionDefinition("EnvironmentMutating", DisableParallelization = true)]
public sealed class EnvironmentMutatingCollection;
