using Xunit;

namespace ServiceLib.Tests;

/// <summary>
/// Test classes in this collection share process-wide state: the global SQLite
/// database (SQLiteHelper.Instance → guiNDB.db) and the AppManager singleton
/// config. xUnit runs test classes in parallel by default, so two classes could
/// delete/insert the same RoutingItem or ProfileItem rows concurrently (or bind
/// different configs into AppManager at once), making the integration tests
/// flaky. DisableParallelization forces this collection to run serially, both
/// internally and against every other collection.
/// </summary>
[CollectionDefinition("SharedDatabase", DisableParallelization = true)]
public sealed class SharedDatabaseCollection;
