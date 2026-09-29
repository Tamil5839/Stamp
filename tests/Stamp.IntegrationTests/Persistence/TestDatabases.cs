using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Stamp.Infrastructure.Persistence;

namespace Stamp.IntegrationTests.Persistence;

public interface ITestDatabase
{
    StampDbContext CreateContext();

    void SkipIfUnavailable();
}

/// <summary>A migrated in-memory SQLite database that lives as long as the fixture.</summary>
public sealed class SqliteTestDatabase : ITestDatabase, IAsyncLifetime
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public async ValueTask InitializeAsync()
    {
        await _connection.OpenAsync();
        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public StampDbContext CreateContext() =>
        new SqliteStampDbContext(new DbContextOptionsBuilder<SqliteStampDbContext>().UseSqlite(_connection).Options);

    public void SkipIfUnavailable()
    {
    }

    public ValueTask DisposeAsync() => _connection.DisposeAsync();
}

/// <summary>
/// A throwaway PostgreSQL database, created from the migrations and dropped afterwards. Runs only
/// when STAMP_TEST_POSTGRES holds a connection string to a server we may create databases on.
/// </summary>
public sealed class PostgresTestDatabase : ITestDatabase, IAsyncLifetime
{
    public const string ConnectionStringVariable = "STAMP_TEST_POSTGRES";

    private string? _connectionString;

    public async ValueTask InitializeAsync()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(ConnectionStringVariable);
        if (string.IsNullOrWhiteSpace(baseConnectionString))
        {
            return;
        }

        _connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            Database = $"stamp_test_{Guid.NewGuid():N}",
        }.ConnectionString;

        await using var db = CreateContext();
        await db.Database.MigrateAsync();
    }

    public StampDbContext CreateContext() =>
        new PostgresStampDbContext(new DbContextOptionsBuilder<PostgresStampDbContext>().UseNpgsql(_connectionString).Options);

    public void SkipIfUnavailable() =>
        Assert.SkipWhen(_connectionString is null, $"Set {ConnectionStringVariable} to run the PostgreSQL tests.");

    public async ValueTask DisposeAsync()
    {
        if (_connectionString is null)
        {
            return;
        }

        await using var db = CreateContext();
        await db.Database.EnsureDeletedAsync();
    }
}
