using Testcontainers.MsSql;

namespace MiniMes.IntegrationTests.Infrastructure;

// One real SQL Server container for the whole test run (starting one takes 10-30 s).
// Same image as docker-compose, so tests see exactly what dev and production see:
// unique indexes, triggers, transactions. No InMemory/SQLite fakes.
public sealed class SqlServerFixture : IAsyncLifetime
{
    // Testcontainers 4.x takes the image in the constructor (the parameterless one is obsolete).
    private readonly MsSqlContainer _db = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _db.GetConnectionString();

    // Docker not running -> this throws and every test in the collection fails loudly (never skipped).
    public Task InitializeAsync() => _db.StartAsync();

    public Task DisposeAsync() => _db.DisposeAsync().AsTask();
}
