using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Payments;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Messages;

public sealed record SendStampCommand(string Handle, string SenderName, string SenderEmail, string Subject, string Body);

/// <param name="ClientSecret">Handed to the browser so the sender can authorize the card.</param>
public sealed record SendStampResult(Guid MessageId, string PaymentId, string ClientSecret);

public sealed record SentStampView(
    Guid MessageId,
    string ReceiverHandle,
    string ReceiverDisplayName,
    MessageStatus Status,
    long AmountCents,
    string Currency,
    DateTimeOffset? ExpiresAt);

/// <summary>The sender's side: submit a message with a stamp, then confirm it once the card is authorized.</summary>
public sealed class SendStampService(
    IStampDbContext db,
    IPaymentProvider payments,
    StampTransitions transitions,
    IOptions<StampOptions> options,
    TimeProvider time,
    ILogger<SendStampService> logger)
{
    /// <summary>
    /// Saves the message as <see cref="MessageStatus.AwaitingPayment"/> and creates the uncaptured
    /// payment. The receiver sees nothing until the card is authorized.
    /// </summary>
    public async Task<Result<SendStampResult>> SendAsync(SendStampCommand command, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        var settings = options.Value;

        var handle = HandleRules.Normalize(command.Handle);
        var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.Handle == handle, cancellationToken);
        if (receiver is null)
        {
            return Error.NotFound("There's no Stamp page here.");
        }

        if (!receiver.IsAcceptingStamps)
        {
            return new Error(ErrorCodes.NotAcceptingStamps, $"{receiver.DisplayName} isn't accepting stamps yet.");
        }

        StampedMessage message;
        try
        {
            message = StampedMessage.Create(
                receiver.Id,
                command.SenderName,
                command.SenderEmail,
                command.Subject,
                command.Body,
                receiver.StampPriceCents,
                StampPricing.PlatformFee(receiver.StampPriceCents, settings.PlatformFeePercent),
                receiver.Currency,
                now);
        }
        catch (DomainException ex)
        {
            return Error.FromDomain(ex);
        }

        if (await db.BlockedSenders.AnyAsync(b => b.ReceiverId == receiver.Id && b.Email == message.SenderEmail, cancellationToken))
        {
            return new Error(ErrorCodes.SenderBlocked, $"{receiver.DisplayName} isn't accepting stamps from this address.", nameof(command.SenderEmail));
        }

        var windowStart = now - settings.SenderRateLimitWindow;
        var recentFromSender = await db.Messages.CountAsync(
            m => m.SenderEmail == message.SenderEmail && m.CreatedAt > windowStart, cancellationToken);
        if (recentFromSender >= settings.SenderRateLimit)
        {
            return new Error(ErrorCodes.RateLimited, "You've sent several stamps recently. Please try again a little later.");
        }

        PaymentAuthorization authorization;
        try
        {
            authorization = await payments.CreateAuthorizationAsync(
                new AuthorizationRequest(
                    AmountMinor: message.AmountCents,
                    Currency: message.Currency,
                    PlatformFeeMinor: message.PlatformFeeCents,
                    DestinationAccountId: receiver.PayoutAccountId!,
                    Description: $"Stamp for @{receiver.Handle}",
                    Metadata: new Dictionary<string, string>
                    {
                        ["message_id"] = message.Id.ToString(),
                        ["receiver_id"] = receiver.Id.ToString(),
                    },
                    IdempotencyKey: $"stamp-authorize-{message.Id}"),
                cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            logger.LogError(ex, "Couldn't create a payment for a stamp to receiver {ReceiverId}.", receiver.Id);
            return new Error(ErrorCodes.PaymentUnavailable, "We couldn't start the payment. Please try again in a moment.");
        }

        message.AttachPayment(authorization.PaymentId);
        db.Messages.Add(message);
        await db.SaveChangesAsync(cancellationToken);

        return new SendStampResult(message.Id, authorization.PaymentId, authorization.ClientSecret);
    }

    /// <summary>
    /// Called when the browser returns from the payment page. Asks the provider directly rather than
    /// waiting for the webhook, and applies the same idempotent transition the webhook would.
    /// </summary>
    public async Task<Result<SentStampView>> ConfirmAsync(Guid messageId, CancellationToken cancellationToken)
    {
        var message = await db.Messages.SingleOrDefaultAsync(m => m.Id == messageId, cancellationToken);
        if (message is null)
        {
            return Error.NotFound();
        }

        if (message is { Status: MessageStatus.AwaitingPayment, PaymentId: not null }
            && !await TryConfirmAuthorizationAsync(message, cancellationToken))
        {
            // The webhook confirmed it at the same moment; show what it recorded.
            db.ChangeTracker.Clear();
            message = await db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, cancellationToken);
        }

        var receiver = await db.Receivers.AsNoTracking().SingleAsync(r => r.Id == message.ReceiverId, cancellationToken);
        return new SentStampView(
            message.Id,
            receiver.Handle ?? string.Empty,
            receiver.DisplayName,
            message.Status,
            message.AmountCents,
            message.Currency,
            message.ExpiresAt);
    }

    /// <summary>Returns false when a concurrent update won the race and the message should be re-read.</summary>
    private async Task<bool> TryConfirmAuthorizationAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        ProviderPaymentState state;
        try
        {
            state = await payments.GetPaymentStateAsync(message.PaymentId!, cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            logger.LogWarning(ex, "Couldn't check payment {PaymentId}; the webhook will confirm it.", message.PaymentId);
            return true;
        }

        var changed = state switch
        {
            ProviderPaymentState.Authorized => await transitions.AuthorizeAsync(message, time.GetUtcNow(), cancellationToken),
            ProviderPaymentState.Canceled => await transitions.RecordPaymentCanceledAsync(message, cancellationToken) != PaymentCanceledOutcome.NoChange,
            _ => false,
        };

        if (!changed)
        {
            return true;
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateConcurrencyException)
        {
            return false;
        }
    }
}
