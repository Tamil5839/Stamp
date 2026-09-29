using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stamp.Application.Abstractions;
using Stamp.Application.Messages;

namespace Stamp.Application.Payments;

public enum WebhookHandlingResult
{
    Processed,
    Duplicate,
    Ignored,
}

/// <summary>
/// Applies provider webhooks. Idempotent twice over: the event id is recorded in the same
/// transaction as its effects, and every transition is state-based, so redelivered or out-of-order
/// events can't double-apply.
/// </summary>
public sealed class PaymentWebhookHandler(
    IStampDbContext db,
    StampTransitions transitions,
    TimeProvider time,
    ILogger<PaymentWebhookHandler> logger)
{
    private const int MaxAttempts = 3;

    public async Task<WebhookHandlingResult> HandleAsync(PaymentWebhookEvent webhookEvent, CancellationToken cancellationToken)
    {
        if (webhookEvent.Kind == PaymentWebhookEventKind.Ignored || webhookEvent.ObjectId is null)
        {
            return WebhookHandlingResult.Ignored;
        }

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();

            if (await db.ProcessedWebhookEvents.AnyAsync(e => e.EventId == webhookEvent.EventId, cancellationToken))
            {
                return WebhookHandlingResult.Duplicate;
            }

            await ApplyAsync(webhookEvent, webhookEvent.ObjectId, cancellationToken);
            db.ProcessedWebhookEvents.Add(ProcessedWebhookEvent.Create(webhookEvent.EventId, webhookEvent.EventType, time.GetUtcNow()));

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return WebhookHandlingResult.Processed;
            }
            catch (DuplicateKeyException)
            {
                // The same event was delivered twice at once and the other delivery committed first.
                return WebhookHandlingResult.Duplicate;
            }
            catch (DbUpdateConcurrencyException) when (attempt < MaxAttempts)
            {
                // Someone changed the message meanwhile (e.g. the receiver replied); re-read and re-apply.
                logger.LogInformation("Webhook {EventId} raced another update; retrying.", webhookEvent.EventId);
            }
        }
    }

    private async Task ApplyAsync(PaymentWebhookEvent webhookEvent, string objectId, CancellationToken cancellationToken)
    {
        if (webhookEvent.Kind == PaymentWebhookEventKind.PayoutAccountUpdated)
        {
            var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.PayoutAccountId == objectId, cancellationToken);
            if (receiver is null)
            {
                logger.LogInformation("Webhook {EventId} is about unknown payout account {AccountId}.", webhookEvent.EventId, objectId);
                return;
            }

            receiver.SetPayoutsEnabled(webhookEvent.PayoutAccount?.IsReady ?? false, time.GetUtcNow());
            return;
        }

        var message = await db.Messages.SingleOrDefaultAsync(m => m.PaymentId == objectId, cancellationToken);
        if (message is null)
        {
            logger.LogInformation("Webhook {EventId} is about unknown payment {PaymentId}.", webhookEvent.EventId, objectId);
            return;
        }

        switch (webhookEvent.Kind)
        {
            case PaymentWebhookEventKind.PaymentAuthorized:
                await transitions.AuthorizeAsync(message, webhookEvent.OccurredAt, cancellationToken);
                break;

            case PaymentWebhookEventKind.PaymentCaptured:
                transitions.RecordPaymentCaptured(message);
                break;

            case PaymentWebhookEventKind.PaymentCanceled:
                await transitions.RecordPaymentCanceledAsync(message, cancellationToken);
                break;
        }
    }
}
