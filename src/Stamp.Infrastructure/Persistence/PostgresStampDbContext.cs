using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Stamp.Infrastructure.Persistence;

public sealed class PostgresStampDbContext(DbContextOptions<PostgresStampDbContext> options) : StampDbContext(options)
{
    protected override bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
