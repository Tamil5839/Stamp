using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Messages;

/// <summary>
/// Applies a lifecycle transition and queues the emails it causes, in the caller's unit of work.
/// Callers save; nothing here talks to the payment provider.
/// </summary>
public sealed class StampTransitions(
    IStampDbContext db,
    EmailTemplates templates,
    EmailOutbox outbox,
    IOptions<StampOptions> options,
    TimeProvider time,
    ILogger<StampTransitions> logger)
{
    /// <summary>
    /// The sender's card was authorized at <paramref name="authorizedAt"/>. The reply window counts
    /// from then, not from when we heard about it, so it always closes before the hold lapses.
    /// </summary>
    public async Task<bool> AuthorizeAsync(StampedMessage message, DateTimeOffset authorizedAt, CancellationToken cancellationToken)
    {
        var now = time.GetUtcNow();
        if (!message.MarkAuthorized(authorizedAt < now ? authorizedAt : now, options.Value.ReplyWindow))
        {
            return false;
        }

        var receiver = await ReceiverOfAsync(message, cancellationToken);
        outbox.Enqueue(templates.StampReceived(receiver, message), message.Id);
        outbox.Enqueue(templates.StampSent(receiver, message), message.Id);
        return true;
    }

    public async Task ReplyAsync(StampedMessage message, string reply, CancellationToken cancellationToken)
    {
        message.Reply(reply, time.GetUtcNow());
        outbox.Enqueue(templates.StampReplied(await ReceiverOfAsync(message, cancellationToken), message), message.Id);
    }

    public async Task DeclineAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        message.Decline(time.GetUtcNow());
        outbox.Enqueue(templates.StampDeclined(await ReceiverOfAsync(message, cancellationToken), message), message.Id);
    }

    public async Task ExpireAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        message.Expire(time.GetUtcNow());
        outbox.Enqueue(templates.StampExpired(await ReceiverOfAsync(message, cancellationToken), message), message.Id);
    }

    public bool RecordPaymentCaptured(StampedMessage message)
    {
        var changed = message.MarkPaymentCaptured();
        if (changed && message.Status != MessageStatus.Replied)
        {
            logger.LogError(
                "Payment {PaymentId} for message {MessageId} was captured while the message is {Status}. The sender was charged without a reply; review it manually.",
                message.PaymentId, message.Id, message.Status);
        }

        return changed;
    }

    public async Task<PaymentCanceledOutcome> RecordPaymentCanceledAsync(StampedMessage message, CancellationToken cancellationToken)
    {
        var outcome = message.MarkPaymentCanceled(time.GetUtcNow());

        switch (outcome)
        {
            case PaymentCanceledOutcome.StampExpired:
                outbox.Enqueue(templates.StampExpired(await ReceiverOfAsync(message, cancellationToken), message), message.Id);
                break;

            case PaymentCanceledOutcome.ReplyUnpaid:
                logger.LogWarning(
                    "Message {MessageId} was replied to, but the hold on payment {PaymentId} was released before it could be captured. The stamp went unpaid.",
                    message.Id, message.PaymentId);
                break;
        }

        return outcome;
    }

    private async Task<Receiver> ReceiverOfAsync(StampedMessage message, CancellationToken cancellationToken) =>
        await db.Receivers.FindAsync([message.ReceiverId], cancellationToken)
        ?? throw new InvalidOperationException($"Receiver {message.ReceiverId} of message {message.Id} doesn't exist.");
}
