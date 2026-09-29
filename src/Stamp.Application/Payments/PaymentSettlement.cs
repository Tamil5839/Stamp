using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stamp.Application.Abstractions;
using Stamp.Application.Messages;
using Stamp.Domain.Messages;

namespace Stamp.Application.Payments;

public enum SettlementResult
{
    NothingToDo,
    Settled,
    Failed,
}

/// <summary>
/// Moves the money to match a transition that is already committed: captures replied stamps and
/// releases the rest. Provider failures are logged and left for the expiry job to retry.
/// </summary>
public sealed class PaymentSettlement(
    IStampDbContext db,
    IPaymentProvider payments,
    StampTransitions transitions,
    ILogger<PaymentSettlement> logger)
{
    public static string CaptureKey(Guid messageId) => $"stamp-capture-{messageId}";

    public static string CancelKey(Guid messageId) => $"stamp-cancel-{messageId}";

    public async Task<SettlementResult> SettleAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        if (message.PaymentId is null || !(message.NeedsCapture || message.NeedsCancellation))
        {
            return SettlementResult.NothingToDo;
        }

        ProviderPaymentState state;
        try
        {
            state = message.NeedsCapture
                ? await payments.CaptureAsync(message.PaymentId, CaptureKey(message.Id), cancellationToken)
                : await payments.CancelAsync(message.PaymentId, CancelKey(message.Id), cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            logger.LogWarning(ex, "Couldn't settle payment {PaymentId} for message {MessageId}; the expiry job will retry.", message.PaymentId, message.Id);
            return SettlementResult.Failed;
        }

        switch (state)
        {
            case ProviderPaymentState.Captured:
                transitions.RecordPaymentCaptured(message);
                break;

            case ProviderPaymentState.Canceled:
                await transitions.RecordPaymentCanceledAsync(message, cancellationToken);
                break;

            default:
                logger.LogWarning("Settling payment {PaymentId} left it {State}; the expiry job will retry.", message.PaymentId, state);
                return SettlementResult.Failed;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A webhook for the same capture or cancellation got there first and recorded the same outcome.
            logger.LogDebug("Payment outcome for message {MessageId} was already recorded.", message.Id);
        }

        return SettlementResult.Settled;
    }
}
