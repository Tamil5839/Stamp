using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Messages;

namespace Stamp.Infrastructure.Jobs;

public sealed class ExpiryJobOptions
{
    public const string SectionName = "Jobs:Expiry";

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to sweep. Stamps can outlive their window by up to one interval, so the reply
    /// window plus this must stay under the card hold (6 days + 1 hour &lt; 7 days).
    /// </summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Delay before the first sweep after startup.</summary>
    public TimeSpan InitialDelay { get; set; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Runs <see cref="StampExpiryService"/> on a timer inside the web app. Safe with several instances:
/// the sweep itself is idempotent. Run it once from the command line with <c>expire-stamps</c>.
/// </summary>
public sealed class StampExpiryJob(
    IServiceScopeFactory scopes,
    IOptions<ExpiryJobOptions> options,
    TimeProvider time,
    ILogger<StampExpiryJob> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var settings = options.Value;
        if (!settings.Enabled)
        {
            logger.LogInformation("The stamp expiry job is disabled (Jobs:Expiry:Enabled=false).");
            return;
        }

        try
        {
            await Task.Delay(settings.InitialDelay, time, stoppingToken);

            using var timer = new PeriodicTimer(settings.Interval, time);
            do
            {
                await RunOnceAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    public async Task<ExpiryRunResult?> RunOnceAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<StampExpiryService>().RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "The stamp expiry run failed; it will run again in {Interval}.", options.Value.Interval);
            return null;
        }
    }
}
