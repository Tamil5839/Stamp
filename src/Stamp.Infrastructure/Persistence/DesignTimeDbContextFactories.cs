using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Stamp.Infrastructure.Persistence;

// Used only by `dotnet ef migrations add`, which never opens these connections.

internal sealed class SqliteDesignTimeDbContextFactory : IDesignTimeDbContextFactory<SqliteStampDbContext>
{
    public SqliteStampDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<SqliteStampDbContext>().UseSqlite("Data Source=design-time.db").Options);
}

internal sealed class PostgresDesignTimeDbContextFactory : IDesignTimeDbContextFactory<PostgresStampDbContext>
{
    public PostgresStampDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<PostgresStampDbContext>().UseNpgsql("Host=localhost;Database=stamp_design_time").Options);
}
