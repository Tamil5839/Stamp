using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Payments;
using Stamp.Domain.Messages;

namespace Stamp.Application.Messages;

public sealed record ExpiryRunResult(int Expired, int Recovered, int Abandoned, int Settled, int SettlementFailures);

/// <summary>
/// The hourly sweep. Expires pending stamps whose reply window has passed, abandons drafts that
/// were never paid for, and retries any capture or release that didn't go through. Safe to run
/// concurrently or repeatedly: every step re-checks state and saves under optimistic concurrency.
/// </summary>
public sealed class StampExpiryService(
    IStampDbContext db,
    IPaymentProvider payments,
    StampTransitions transitions,
    PaymentSettlement settlement,
    IOptions<StampOptions> options,
    TimeProvider time,
    ILogger<StampExpiryService> logger)
{
    /// <summary>Upper bound per step per run; anything beyond is picked up by the next run.</summary>
    public const int MaxPerRun = 500;

    public async Task<ExpiryRunResult> RunAsync(CancellationToken cancellationToken)
    {
        var expired = await ExpireOverdueStampsAsync(cancellationToken);
        var (recovered, abandoned) = await CloseUnpaidDraftsAsync(cancellationToken);
        var (settled, failures) = await SettleOutstandingPaymentsAsync(cancellationToken);

        var result = new ExpiryRunResult(expired, recovered, abandoned, settled, failures);
        logger.LogInformation(
            "Expiry run: {Expired} expired, {Recovered} recovered, {Abandoned} abandoned, {Settled} settled, {Failures} settlement failures.",
            result.Expired, result.Recovered, result.Abandoned, result.Settled, result.SettlementFailures);
        return result;
    }

    private async Task<int> ExpireOverdueStampsAsync(CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var ids = await db.Messages
            .Where(m => m.Status == MessageStatus.Pending && m.PaymentStatus == PaymentStatus.Authorized && m.ExpiresAt <= now)
            .OrderBy(m => m.ExpiresAt)
            .Select(m => m.Id)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var id in ids)
        {
            var message = await LoadFreshAsync(id, cancellationToken);
            if (message is null || !(message.Status == MessageStatus.Pending && message.ExpiresAt <= now))
            {
                continue;
            }

            await transitions.ExpireAsync(message, cancellationToken);
            if (await TrySaveAsync(message, cancellationToken))
            {
                expired++;
            }
        }

        return expired;
    }

    /// <summary>
    /// Before giving up on a draft, ask the provider: if the card was authorized but we never heard
    /// (missed webhook and the sender closed the tab), deliver the stamp instead. Its window counts
    /// from when the draft was created, which is never later than the real authorization, so it
    /// still closes before the hold lapses.
    /// </summary>
    private async Task<(int Recovered, int Abandoned)> CloseUnpaidDraftsAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow() - options.Value.AbandonUnpaidAfter;
        var ids = await db.Messages
            .Where(m => m.Status == MessageStatus.AwaitingPayment && m.CreatedAt <= cutoff)
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Id)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        int recovered = 0, abandoned = 0;
        foreach (var id in ids)
        {
            var message = await LoadFreshAsync(id, cancellationToken);
            if (message?.Status != MessageStatus.AwaitingPayment)
            {
                continue;
            }

            var state = ProviderPaymentState.AwaitingAuthorization;
            if (message.PaymentId is not null)
            {
                try
                {
                    state = await payments.GetPaymentStateAsync(message.PaymentId, cancellationToken);
                }
                catch (PaymentProviderException ex)
                {
                    logger.LogWarning(ex, "Couldn't check payment {PaymentId}; will retry next run.", message.PaymentId);
                    continue;
                }
            }

            switch (state)
            {
                case ProviderPaymentState.Authorized:
                    await transitions.AuthorizeAsync(message, message.CreatedAt, cancellationToken);
                    if (await TrySaveAsync(message, cancellationToken))
                    {
                        recovered++;
                        logger.LogWarning("Recovered stamp {MessageId}: its authorization was never reported to us.", message.Id);
                    }

                    break;

                case ProviderPaymentState.Captured:
                    logger.LogError("Unpaid draft {MessageId} has captured payment {PaymentId}; review it manually.", message.Id, message.PaymentId);
                    break;

                case ProviderPaymentState.Canceled:
                    await transitions.RecordPaymentCanceledAsync(message, cancellationToken);
                    if (await TrySaveAsync(message, cancellationToken))
                    {
                        abandoned++;
                    }

                    break;

                default:
                    message.Abandon(time.GetUtcNow());
                    if (await TrySaveAsync(message, cancellationToken))
                    {
                        abandoned++;
                    }

                    break;
            }
        }

        return (recovered, abandoned);
    }

    private async Task<(int Settled, int Failures)> SettleOutstandingPaymentsAsync(CancellationToken cancellationToken)
    {
        var ids = await db.Messages
            .Where(StampQueries.NeedsSettlement)
            .OrderBy(m => m.CreatedAt)
            .Select(m => m.Id)
            .Take(MaxPerRun)
            .ToListAsync(cancellationToken);

        int settled = 0, failures = 0;
        foreach (var id in ids)
        {
            var message = await LoadFreshAsync(id, cancellationToken);
            if (message is null)
            {
                continue;
            }

            switch (await settlement.SettleAsync(message, cancellationToken))
            {
                case SettlementResult.Settled:
                    settled++;
                    break;
                case SettlementResult.Failed:
                    failures++;
                    break;
            }
        }

        return (settled, failures);
    }

    private async Task<StampedMessage?> LoadFreshAsync(Guid id, CancellationToken cancellationToken)
    {
        // One message per unit of work, so a conflict on one can't poison the saves that follow.
        db.ChangeTracker.Clear();
        return await db.Messages.SingleOrDefaultAsync(m => m.Id == id, cancellationToken);
    }

    private async Task<bool> TrySaveAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("Message {MessageId} changed during the expiry run; leaving it to the change that won.", message.Id);
            return false;
        }
    }
}
