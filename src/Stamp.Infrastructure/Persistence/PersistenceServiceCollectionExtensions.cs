using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Stamp.Application.Abstractions;

namespace Stamp.Infrastructure.Persistence;

public static class PersistenceServiceCollectionExtensions
{
    public static IServiceCollection AddStampPersistence(
        this IServiceCollection services, IConfiguration configuration, string contentRootPath)
    {
        var section = configuration.GetSection(DatabaseOptions.SectionName);
        services.Configure<DatabaseOptions>(section);

        var options = section.Get<DatabaseOptions>() ?? new DatabaseOptions();
        var connectionString = configuration.GetConnectionString(DatabaseOptions.ConnectionStringName);

        switch (options.Provider)
        {
            case DatabaseProvider.Postgres:
                if (string.IsNullOrWhiteSpace(connectionString))
                {
                    throw new InvalidOperationException(
                        $"ConnectionStrings:{DatabaseOptions.ConnectionStringName} is required when Database:Provider is Postgres.");
                }

                services.AddDbContext<PostgresStampDbContext>((sp, db) =>
                    db.UseNpgsql(connectionString).AddInterceptors(sp.GetServices<IInterceptor>()));
                services.AddScoped<StampDbContext>(sp => sp.GetRequiredService<PostgresStampDbContext>());
                break;

            case DatabaseProvider.Sqlite:
                var sqlite = ResolveSqliteConnectionString(
                    string.IsNullOrWhiteSpace(connectionString) ? DatabaseOptions.DefaultSqliteConnectionString : connectionString,
                    contentRootPath);

                services.AddDbContext<SqliteStampDbContext>((sp, db) =>
                    db.UseSqlite(sqlite).AddInterceptors(sp.GetServices<IInterceptor>()));
                services.AddScoped<StampDbContext>(sp => sp.GetRequiredService<SqliteStampDbContext>());
                break;

            default:
                throw new InvalidOperationException($"Unsupported database provider '{options.Provider}'.");
        }

        services.AddScoped<IStampDbContext>(sp => sp.GetRequiredService<StampDbContext>());
        return services;
    }

    /// <summary>Applies pending migrations (and turns on WAL for SQLite files so background jobs don't block requests).</summary>
    public static async Task MigrateStampDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken = default)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<StampDbContext>();

        await db.Database.MigrateAsync(cancellationToken);

        if (db.Database.IsSqlite())
        {
            await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", cancellationToken);
        }
    }

    /// <summary>Relative SQLite paths are resolved against the content root, and the folder is created.</summary>
    internal static string ResolveSqliteConnectionString(string connectionString, string contentRootPath)
    {
        var builder = new SqliteConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;

        var inMemory = builder.Mode == SqliteOpenMode.Memory
                       || dataSource is "" or ":memory:"
                       || dataSource.StartsWith("file:", StringComparison.OrdinalIgnoreCase);
        if (inMemory)
        {
            return builder.ToString();
        }

        if (!Path.IsPathRooted(dataSource))
        {
            builder.DataSource = Path.GetFullPath(Path.Combine(contentRootPath, dataSource));
        }

        var directory = Path.GetDirectoryName(builder.DataSource);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        return builder.ToString();
    }
}
