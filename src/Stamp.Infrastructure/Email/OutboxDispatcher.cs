using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Emails;

namespace Stamp.Infrastructure.Email;

/// <summary>Runs <see cref="OutboxProcessor"/> whenever emails are queued, and on a slow poll as a fallback.</summary>
public sealed class OutboxDispatcher(
    IServiceScopeFactory scopes,
    OutboxSignal signal,
    IOptions<EmailOptions> options,
    ILogger<OutboxDispatcher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<OutboxProcessor>().ProcessDueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Email dispatch failed; retrying shortly.");
            }

            await signal.WaitAsync(options.Value.Outbox.PollInterval, stoppingToken);
        }
    }
}

/// <summary>Wakes the dispatcher as soon as a save that queued emails commits.</summary>
public sealed class OutboxSignal
{
    private readonly SemaphoreSlim _signal = new(0, 1);

    public void Notify()
    {
        try
        {
            _signal.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already signaled.
        }
    }

    public async Task WaitAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        try
        {
            await _signal.WaitAsync(timeout, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }
}

/// <summary>Scoped (one per DbContext), so it can remember whether the save in flight added emails.</summary>
internal sealed class OutboxSignalInterceptor(OutboxSignal signal) : SaveChangesInterceptor
{
    private bool _queuedEmails;

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Inspect(eventData.Context);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Inspect(eventData.Context);
        return ValueTask.FromResult(result);
    }

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        Flush();
        return result;
    }

    public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        Flush();
        return ValueTask.FromResult(result);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData) => _queuedEmails = false;

    public override Task SaveChangesFailedAsync(DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        _queuedEmails = false;
        return Task.CompletedTask;
    }

    private void Inspect(DbContext? context) =>
        _queuedEmails = context?.ChangeTracker.Entries<OutboxEmail>().Any(e => e.State == EntityState.Added) == true;

    private void Flush()
    {
        if (_queuedEmails)
        {
            _queuedEmails = false;
            signal.Notify();
        }
    }
}
