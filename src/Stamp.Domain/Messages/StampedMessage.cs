using Stamp.Domain.Common;
using Stamp.Domain.Receivers;
using static System.FormattableString;

namespace Stamp.Domain.Messages;

/// <summary>
/// A message a stranger paid a stamp to send. The workflow (<see cref="Status"/>) and the money
/// (<see cref="PaymentStatus"/>) are tracked separately: a transition is committed first, and the
/// matching capture or cancellation happens afterwards (see <see cref="NeedsCapture"/> and
/// <see cref="NeedsCancellation"/>), so a provider outage can delay money movement but never lose
/// a reply or charge a sender who didn't get one.
/// </summary>
public sealed class StampedMessage
{
    public const int MaxSenderNameLength = 100;
    public const int MaxSubjectLength = 150;
    public const int MaxBodyLength = 1_500;
    public const int MinReplyLength = 20;
    public const int MaxReplyLength = 5_000;

    private StampedMessage()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReceiverId { get; private set; }

    public string SenderName { get; private set; } = null!;

    public string SenderEmail { get; private set; } = null!;

    public string Subject { get; private set; } = null!;

    public string Body { get; private set; } = null!;

    /// <summary>The stamp price when the message was sent; later price changes don't affect it.</summary>
    public long AmountCents { get; private set; }

    public long PlatformFeeCents { get; private set; }

    public string Currency { get; private set; } = null!;

    /// <summary>The provider's payment id (a Stripe PaymentIntent id).</summary>
    public string? PaymentId { get; private set; }

    public MessageStatus Status { get; private set; }

    public PaymentStatus PaymentStatus { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? AuthorizedAt { get; private set; }

    /// <summary>End of the reply window; set when the card is authorized.</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>When the stamp was replied to, declined, expired or abandoned.</summary>
    public DateTimeOffset? ResolvedAt { get; private set; }

    public string? ReplyBody { get; private set; }

    public long ReceiverEarningsCents => AmountCents - PlatformFeeCents;

    /// <summary>A reply is committed but its payment hasn't been captured yet.</summary>
    public bool NeedsCapture => Status == MessageStatus.Replied && PaymentStatus == PaymentStatus.Authorized;

    /// <summary>The stamp closed without a reply but the provider payment is still open.</summary>
    public bool NeedsCancellation =>
        Status is MessageStatus.Declined or MessageStatus.Expired or MessageStatus.Abandoned
        && PaymentStatus is PaymentStatus.Created or PaymentStatus.Authorized;

    public static StampedMessage Create(
        Guid receiverId,
        string senderName,
        string senderEmail,
        string subject,
        string body,
        long amountCents,
        long platformFeeCents,
        string currency,
        DateTimeOffset now)
    {
        var name = Required(TextRules.SingleLine(senderName), MaxSenderNameLength, DomainErrorCodes.InvalidSenderName, "Your name", nameof(SenderName));
        var email = EmailAddress.NormalizeValid(senderEmail, nameof(SenderEmail));
        var trimmedSubject = Required(TextRules.SingleLine(subject), MaxSubjectLength, DomainErrorCodes.InvalidSubject, "Subject", nameof(Subject));
        var trimmedBody = Required(TextRules.MultiLine(body), MaxBodyLength, DomainErrorCodes.InvalidBody, "Message", nameof(Body));

        StampPricing.EnsureValidPrice(amountCents);
        ArgumentOutOfRangeException.ThrowIfNegative(platformFeeCents);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(platformFeeCents, amountCents);
        ArgumentException.ThrowIfNullOrWhiteSpace(currency);

        return new StampedMessage
        {
            Id = Guid.CreateVersion7(now),
            ReceiverId = receiverId,
            SenderName = name,
            SenderEmail = email,
            Subject = trimmedSubject,
            Body = trimmedBody,
            AmountCents = amountCents,
            PlatformFeeCents = platformFeeCents,
            Currency = currency.ToLowerInvariant(),
            Status = MessageStatus.AwaitingPayment,
            PaymentStatus = PaymentStatus.None,
            CreatedAt = now,
        };
    }

    public void AttachPayment(string paymentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentId);
        EnsureStatus(MessageStatus.AwaitingPayment, DomainErrorCodes.NotAwaitingPayment);

        if (PaymentId is not null)
        {
            throw new InvalidOperationException($"Message {Id} already has payment {PaymentId}.");
        }

        PaymentId = paymentId;
        AdvancePayment(PaymentStatus.Created);
    }

    /// <summary>
    /// The sender's card was authorized: the stamp becomes pending and its reply window starts.
    /// Safe to call repeatedly (webhook retries, the return page); returns false when nothing changed.
    /// </summary>
    public bool MarkAuthorized(DateTimeOffset now, TimeSpan replyWindow)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(replyWindow, TimeSpan.Zero);
        if (PaymentId is null)
        {
            throw new InvalidOperationException($"Message {Id} has no payment to authorize.");
        }

        AdvancePayment(PaymentStatus.Authorized);

        if (Status != MessageStatus.AwaitingPayment)
        {
            return false;
        }

        Status = MessageStatus.Pending;
        AuthorizedAt = now;
        ExpiresAt = now + replyWindow;
        return true;
    }

    public bool IsReplyWindowOpen(DateTimeOffset now) => Status == MessageStatus.Pending && now < ExpiresAt;

    public TimeSpan TimeLeft(DateTimeOffset now) =>
        ExpiresAt is { } expiresAt && expiresAt > now ? expiresAt - now : TimeSpan.Zero;

    /// <summary>The receiver replied in time. The payment then needs capturing.</summary>
    public void Reply(string replyBody, DateTimeOffset now)
    {
        EnsureStatus(MessageStatus.Pending, DomainErrorCodes.NotPending);

        if (!IsReplyWindowOpen(now))
        {
            throw new DomainException(
                DomainErrorCodes.ReplyWindowClosed, "The reply window for this stamp has closed.", nameof(ReplyBody));
        }

        var trimmed = TextRules.MultiLine(replyBody);
        if (trimmed.Length < MinReplyLength)
        {
            throw new DomainException(
                DomainErrorCodes.ReplyTooShort, $"Replies need at least {MinReplyLength} characters.", nameof(ReplyBody));
        }

        if (trimmed.Length > MaxReplyLength)
        {
            throw new DomainException(
                DomainErrorCodes.ReplyTooLong, Invariant($"Replies can be up to {MaxReplyLength:N0} characters."), nameof(ReplyBody));
        }

        ReplyBody = trimmed;
        Status = MessageStatus.Replied;
        ResolvedAt = now;
    }

    /// <summary>The receiver declined. The hold then needs releasing.</summary>
    public void Decline(DateTimeOffset now)
    {
        EnsureStatus(MessageStatus.Pending, DomainErrorCodes.NotPending);

        Status = MessageStatus.Declined;
        ResolvedAt = now;
    }

    /// <summary>The reply window passed without a reply. The hold then needs releasing.</summary>
    public void Expire(DateTimeOffset now)
    {
        EnsureStatus(MessageStatus.Pending, DomainErrorCodes.NotPending);

        if (now < ExpiresAt)
        {
            throw new DomainException(DomainErrorCodes.NotExpiredYet, "This stamp hasn't expired yet.");
        }

        Status = MessageStatus.Expired;
        ResolvedAt = now;
    }

    /// <summary>The sender never completed payment.</summary>
    public void Abandon(DateTimeOffset now)
    {
        EnsureStatus(MessageStatus.AwaitingPayment, DomainErrorCodes.NotAwaitingPayment);

        Status = MessageStatus.Abandoned;
        ResolvedAt = now;
    }

    /// <summary>The provider captured the payment. Returns false when this was already recorded.</summary>
    public bool MarkPaymentCaptured() => AdvancePayment(PaymentStatus.Captured);

    /// <summary>
    /// The provider canceled the payment: because we asked, or because the hold lapsed or was
    /// canceled elsewhere. A pending stamp can no longer be paid for, so it expires.
    /// </summary>
    public PaymentCanceledOutcome MarkPaymentCanceled(DateTimeOffset now)
    {
        if (!AdvancePayment(PaymentStatus.Canceled))
        {
            return PaymentCanceledOutcome.NoChange;
        }

        switch (Status)
        {
            case MessageStatus.Pending:
                Status = MessageStatus.Expired;
                ResolvedAt = now;
                return PaymentCanceledOutcome.StampExpired;

            case MessageStatus.AwaitingPayment:
                Status = MessageStatus.Abandoned;
                ResolvedAt = now;
                return PaymentCanceledOutcome.DraftAbandoned;

            case MessageStatus.Replied:
                return PaymentCanceledOutcome.ReplyUnpaid;

            default:
                return PaymentCanceledOutcome.Recorded;
        }
    }

    private bool AdvancePayment(PaymentStatus next)
    {
        if (Rank(next) <= Rank(PaymentStatus))
        {
            return false;
        }

        PaymentStatus = next;
        return true;

        static int Rank(PaymentStatus status) => status switch
        {
            PaymentStatus.None => 0,
            PaymentStatus.Created => 1,
            PaymentStatus.Authorized => 2,
            PaymentStatus.Captured or PaymentStatus.Canceled => 3,
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        };
    }

    private void EnsureStatus(MessageStatus expected, string errorCode)
    {
        if (Status == expected)
        {
            return;
        }

        var reason = Status switch
        {
            MessageStatus.AwaitingPayment => "This message hasn't been paid for yet.",
            MessageStatus.Pending => "This message is still pending.",
            MessageStatus.Replied => "This message was already replied to.",
            MessageStatus.Declined => "This message was declined.",
            MessageStatus.Expired => "This message has expired.",
            MessageStatus.Abandoned => "This message was never paid for.",
            _ => "This message can't be changed.",
        };

        throw new DomainException(errorCode, reason);
    }

    private static string Required(string normalized, int maxLength, string errorCode, string label, string target)
    {
        if (normalized.Length == 0 || normalized.Length > maxLength)
        {
            throw new DomainException(errorCode, Invariant($"{label} is required and can be up to {maxLength:N0} characters."), target);
        }

        return normalized;
    }
}
