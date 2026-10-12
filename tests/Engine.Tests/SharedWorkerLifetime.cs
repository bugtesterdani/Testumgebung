#if DB_PROVIDERS
namespace Testumgebung.Engine.Tests;

// Keep the shared workers alive until every consumer has finished, then send
// their normal exit command before the test host and coverage collector stop.
[CollectionDefinition("Shared code workers")]
public sealed class SharedWorkerCollection : ICollectionFixture<SharedWorkerLifetime> { }

public sealed class SharedWorkerLifetime : IDisposable
{
    public void Dispose()
    {
        if (CodeBlockTests.SharedRuntime.IsValueCreated) CodeBlockTests.SharedRuntime.Value.Dispose();
        CppAndLabViewTests.DisposeSharedRuntime();
    }
}
#endif
