using Microsoft.Extensions.Options;
using Stamp.Application.Messages;
using Stamp.Infrastructure.Email;
using Stamp.Infrastructure.Persistence;

namespace Stamp.Web.Infrastructure;

/// <summary>
/// One-shot commands for cron jobs and operators:
/// <c>dotnet run --project src/Stamp.Web -- expire-stamps</c> runs the expiry sweep once and sends the
/// emails it queued; <c>migrate</c> applies database migrations.
/// </summary>
public static class Cli
{
    public const string ExpireStamps = "expire-stamps";
    public const string Migrate = "migrate";

    public static string? CommandIn(string[] args) =>
        args.Length > 0 && args[0] is ExpireStamps or Migrate ? args[0] : null;

    public static async Task<int> RunAsync(WebApplication app, string command)
    {
        var autoMigrate = app.Services.GetRequiredService<IOptions<DatabaseOptions>>().Value.AutoMigrate;
        if (command == Migrate || autoMigrate)
        {
            await app.Services.MigrateStampDatabaseAsync();
        }

        if (command == Migrate)
        {
            Console.WriteLine("Database is up to date.");
            return 0;
        }

        ExpiryRunResult result;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            result = await scope.ServiceProvider.GetRequiredService<StampExpiryService>().RunAsync(CancellationToken.None);
        }

        int sent;
        await using (var scope = app.Services.CreateAsyncScope())
        {
            sent = await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessDueAsync(CancellationToken.None);
        }

        Console.WriteLine(
            $"Expired {result.Expired}, recovered {result.Recovered}, abandoned {result.Abandoned}, " +
            $"settled {result.Settled} payments ({result.SettlementFailures} failed, will retry); sent {sent} emails.");
        return result.SettlementFailures == 0 ? 0 : 1;
    }
}
