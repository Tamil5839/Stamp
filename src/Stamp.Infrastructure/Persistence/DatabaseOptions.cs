namespace Stamp.Infrastructure.Persistence;

public enum DatabaseProvider
{
    Sqlite,
    Postgres,
}

public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>The connection string is read from <c>ConnectionStrings:Stamp</c>.</summary>
    public const string ConnectionStringName = "Stamp";

    public const string DefaultSqliteConnectionString = "Data Source=App_Data/stamp.db";

    public DatabaseProvider Provider { get; set; } = DatabaseProvider.Sqlite;

    /// <summary>Apply pending migrations when the app starts.</summary>
    public bool AutoMigrate { get; set; } = true;
}
