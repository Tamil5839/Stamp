using Stamp.Domain.Common;
using Stamp.Domain.Messages;

namespace Stamp.Domain.Tests;

public sealed class StampedMessageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan ReplyWindow = TimeSpan.FromDays(6);
    private static readonly string ValidReply = new('r', StampedMessage.MinReplyLength);

    private static StampedMessage NewDraft() => StampedMessage.Create(
        receiverId: Guid.NewGuid(),
        senderName: "  Ada Lovelace ",
        senderEmail: " Ada@Example.COM ",
        subject: "Quick question",
        body: "Would you review my engine notes?",
        amountCents: 500,
        platformFeeCents: 50,
        currency: "USD",
        now: T0);

    private static StampedMessage NewPending()
    {
        var message = NewDraft();
        message.AttachPayment("pi_123");
        Assert.True(message.MarkAuthorized(T0, ReplyWindow));
        return message;
    }

    private static StampedMessage InState(MessageStatus status)
    {
        var message = NewPending();
        switch (status)
        {
            case MessageStatus.Replied:
                message.Reply(ValidReply, T0.AddDays(1));
                break;
            case MessageStatus.Declined:
                message.Decline(T0.AddDays(1));
                break;
            case MessageStatus.Expired:
                message.Expire(T0 + ReplyWindow);
                break;
        }

        Assert.Equal(status, message.Status);
        return message;
    }

    [Fact]
    public void A_new_message_awaits_payment_with_normalized_sender_details()
    {
        var message = NewDraft();

        Assert.Equal(MessageStatus.AwaitingPayment, message.Status);
        Assert.Equal(PaymentStatus.None, message.PaymentStatus);
        Assert.Equal("Ada Lovelace", message.SenderName);
        Assert.Equal("ada@example.com", message.SenderEmail);
        Assert.Equal("usd", message.Currency);
        Assert.Equal(450, message.ReceiverEarningsCents);
        Assert.Null(message.ExpiresAt);
    }

    [Theory]
    [InlineData("", "ada@example.com", "Subject", "Body", DomainErrorCodes.InvalidSenderName)]
    [InlineData("Ada", "not-an-email", "Subject", "Body", DomainErrorCodes.InvalidEmail)]
    [InlineData("Ada", "ada@example.com", "   ", "Body", DomainErrorCodes.InvalidSubject)]
    [InlineData("Ada", "ada@example.com", "Subject", "", DomainErrorCodes.InvalidBody)]
    public void Creating_a_message_validates_sender_input(string name, string email, string subject, string body, string expectedCode)
    {
        var error = Assert.Throws<DomainException>(() =>
            StampedMessage.Create(Guid.NewGuid(), name, email, subject, body, 500, 50, "usd", T0));

        Assert.Equal(expectedCode, error.Code);
    }

    [Fact]
    public void Message_body_is_limited_to_1500_characters()
    {
        var atLimit = StampedMessage.Create(Guid.NewGuid(), "Ada", "ada@example.com", "Hi", new string('x', 1500), 500, 50, "usd", T0);
        Assert.Equal(1500, atLimit.Body.Length);

        var error = Assert.Throws<DomainException>(() =>
            StampedMessage.Create(Guid.NewGuid(), "Ada", "ada@example.com", "Hi", new string('x', 1501), 500, 50, "usd", T0));
        Assert.Equal(DomainErrorCodes.InvalidBody, error.Code);
    }

    [Fact]
    public void Attaching_a_payment_marks_it_created()
    {
        var message = NewDraft();

        message.AttachPayment("pi_123");

        Assert.Equal("pi_123", message.PaymentId);
        Assert.Equal(PaymentStatus.Created, message.PaymentStatus);
        Assert.Throws<InvalidOperationException>(() => message.AttachPayment("pi_456"));
    }

    [Fact]
    public void Authorization_makes_the_stamp_pending_with_a_six_day_reply_window()
    {
        var message = NewPending();

        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.Equal(PaymentStatus.Authorized, message.PaymentStatus);
        Assert.Equal(T0, message.AuthorizedAt);
        Assert.Equal(T0.AddDays(6), message.ExpiresAt);
        Assert.Equal(TimeSpan.FromDays(6), message.TimeLeft(T0));
    }

    [Fact]
    public void Authorization_is_idempotent()
    {
        var message = NewPending();

        var changed = message.MarkAuthorized(T0.AddHours(3), ReplyWindow);

        Assert.False(changed);
        Assert.Equal(T0.AddDays(6), message.ExpiresAt);
    }

    [Fact]
    public void Authorization_requires_an_attached_payment()
    {
        Assert.Throws<InvalidOperationException>(() => NewDraft().MarkAuthorized(T0, ReplyWindow));
    }

    [Fact]
    public void Pending_to_replied_when_the_receiver_replies_in_time()
    {
        var message = NewPending();

        message.Reply("  Thanks for reaching out, happy to help!  ", T0.AddDays(5));

        Assert.Equal(MessageStatus.Replied, message.Status);
        Assert.Equal("Thanks for reaching out, happy to help!", message.ReplyBody);
        Assert.Equal(T0.AddDays(5), message.ResolvedAt);
        Assert.True(message.NeedsCapture);
        Assert.False(message.NeedsCancellation);
    }

    [Fact]
    public void Replies_need_at_least_20_characters_after_trimming()
    {
        var message = NewPending();

        var error = Assert.Throws<DomainException>(() => message.Reply("   " + new string('r', 19) + "   ", T0.AddDays(1)));

        Assert.Equal(DomainErrorCodes.ReplyTooShort, error.Code);
        Assert.Equal(MessageStatus.Pending, message.Status);

        message.Reply(new string('r', 20), T0.AddDays(1));
        Assert.Equal(MessageStatus.Replied, message.Status);
    }

    [Fact]
    public void Replies_are_limited_to_5000_characters()
    {
        var error = Assert.Throws<DomainException>(() => NewPending().Reply(new string('r', 5001), T0.AddDays(1)));

        Assert.Equal(DomainErrorCodes.ReplyTooLong, error.Code);
    }

    [Fact]
    public void Replying_after_the_window_closes_is_rejected()
    {
        var message = NewPending();

        var error = Assert.Throws<DomainException>(() => message.Reply(ValidReply, T0 + ReplyWindow));

        Assert.Equal(DomainErrorCodes.ReplyWindowClosed, error.Code);
        Assert.Equal(MessageStatus.Pending, message.Status);
    }

    [Fact]
    public void Replying_before_payment_is_rejected()
    {
        var message = NewDraft();
        message.AttachPayment("pi_123");

        var error = Assert.Throws<DomainException>(() => message.Reply(ValidReply, T0));

        Assert.Equal(DomainErrorCodes.NotPending, error.Code);
    }

    [Fact]
    public void Pending_to_declined_releases_the_hold()
    {
        var message = NewPending();

        message.Decline(T0.AddDays(2));

        Assert.Equal(MessageStatus.Declined, message.Status);
        Assert.Equal(T0.AddDays(2), message.ResolvedAt);
        Assert.True(message.NeedsCancellation);
        Assert.False(message.NeedsCapture);
    }

    [Fact]
    public void Pending_to_expired_once_the_window_has_passed()
    {
        var message = NewPending();

        message.Expire(T0 + ReplyWindow);

        Assert.Equal(MessageStatus.Expired, message.Status);
        Assert.True(message.NeedsCancellation);
        Assert.Equal(TimeSpan.Zero, message.TimeLeft(T0.AddDays(7)));
    }

    [Fact]
    public void A_stamp_cannot_expire_early()
    {
        var message = NewPending();

        var error = Assert.Throws<DomainException>(() => message.Expire(T0 + ReplyWindow - TimeSpan.FromSeconds(1)));

        Assert.Equal(DomainErrorCodes.NotExpiredYet, error.Code);
        Assert.Equal(MessageStatus.Pending, message.Status);
    }

    [Theory]
    [InlineData(MessageStatus.Replied)]
    [InlineData(MessageStatus.Declined)]
    [InlineData(MessageStatus.Expired)]
    public void Closed_stamps_reject_every_further_transition(MessageStatus closed)
    {
        var message = InState(closed);
        var later = T0.AddDays(10);

        Assert.Equal(DomainErrorCodes.NotPending, Assert.Throws<DomainException>(() => message.Reply(ValidReply, later)).Code);
        Assert.Equal(DomainErrorCodes.NotPending, Assert.Throws<DomainException>(() => message.Decline(later)).Code);
        Assert.Equal(DomainErrorCodes.NotPending, Assert.Throws<DomainException>(() => message.Expire(later)).Code);
        Assert.Equal(closed, message.Status);
    }

    [Fact]
    public void An_unpaid_draft_can_be_abandoned()
    {
        var message = NewDraft();
        message.AttachPayment("pi_123");

        message.Abandon(T0.AddDays(1));

        Assert.Equal(MessageStatus.Abandoned, message.Status);
        Assert.True(message.NeedsCancellation);
        Assert.Throws<DomainException>(() => NewPending().Abandon(T0));
    }

    [Fact]
    public void Capturing_after_a_reply_settles_the_stamp()
    {
        var message = InState(MessageStatus.Replied);

        Assert.True(message.MarkPaymentCaptured());
        Assert.False(message.MarkPaymentCaptured());

        Assert.Equal(PaymentStatus.Captured, message.PaymentStatus);
        Assert.False(message.NeedsCapture);
    }

    [Fact]
    public void A_hold_released_while_pending_expires_the_stamp()
    {
        var message = NewPending();

        var outcome = message.MarkPaymentCanceled(T0.AddDays(3));

        Assert.Equal(PaymentCanceledOutcome.StampExpired, outcome);
        Assert.Equal(MessageStatus.Expired, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.False(message.NeedsCancellation);
    }

    [Fact]
    public void Canceling_after_a_decline_just_records_it()
    {
        var message = InState(MessageStatus.Declined);

        Assert.Equal(PaymentCanceledOutcome.Recorded, message.MarkPaymentCanceled(T0.AddDays(2)));
        Assert.Equal(PaymentCanceledOutcome.NoChange, message.MarkPaymentCanceled(T0.AddDays(2)));

        Assert.Equal(MessageStatus.Declined, message.Status);
        Assert.False(message.NeedsCancellation);
    }

    [Fact]
    public void A_voided_draft_is_abandoned()
    {
        var message = NewDraft();
        message.AttachPayment("pi_123");

        Assert.Equal(PaymentCanceledOutcome.DraftAbandoned, message.MarkPaymentCanceled(T0));
        Assert.Equal(MessageStatus.Abandoned, message.Status);
    }

    [Fact]
    public void A_reply_whose_hold_vanished_stays_replied_but_unpaid()
    {
        var message = InState(MessageStatus.Replied);

        Assert.Equal(PaymentCanceledOutcome.ReplyUnpaid, message.MarkPaymentCanceled(T0.AddDays(2)));

        Assert.Equal(MessageStatus.Replied, message.Status);
        Assert.False(message.NeedsCapture);
    }

    [Fact]
    public void Payment_status_never_moves_backwards()
    {
        var canceled = NewPending();
        canceled.MarkPaymentCanceled(T0);
        Assert.False(canceled.MarkAuthorized(T0, ReplyWindow));
        Assert.False(canceled.MarkPaymentCaptured());
        Assert.Equal(PaymentStatus.Canceled, canceled.PaymentStatus);

        var captured = InState(MessageStatus.Replied);
        captured.MarkPaymentCaptured();
        Assert.Equal(PaymentCanceledOutcome.NoChange, captured.MarkPaymentCanceled(T0));
        Assert.Equal(PaymentStatus.Captured, captured.PaymentStatus);
    }

    [Fact]
    public void An_authorization_arriving_after_abandonment_leaves_the_hold_to_be_released()
    {
        var message = NewDraft();
        message.AttachPayment("pi_123");
        message.Abandon(T0.AddDays(1));

        Assert.False(message.MarkAuthorized(T0.AddDays(1), ReplyWindow));

        Assert.Equal(MessageStatus.Abandoned, message.Status);
        Assert.Equal(PaymentStatus.Authorized, message.PaymentStatus);
        Assert.True(message.NeedsCancellation);
    }
}
