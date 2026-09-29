using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;

namespace Stamp.Application.Tests;

public sealed class ReplyAndDeclineTests
{
    private const string Reply = "Happy to help. Send me the notes on Friday.";

    private static Task<Result<StampOutcome>> ReplyAsync(TestApp app, Guid receiverId, Guid messageId, string reply = Reply) =>
        app.Run<InboxService, Result<StampOutcome>>(inbox => inbox.ReplyAsync(receiverId, messageId, reply, TestApp.Ct));

    private static Task<Result<StampOutcome>> DeclineAsync(TestApp app, Guid receiverId, Guid messageId) =>
        app.Run<InboxService, Result<StampOutcome>>(inbox => inbox.DeclineAsync(receiverId, messageId, TestApp.Ct));

    [Fact]
    public async Task Replying_emails_the_reply_captures_the_stamp_and_marks_it_replied()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Time.Advance(TimeSpan.FromDays(5));

        var result = await ReplyAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(new StampOutcome(MessageStatus.Replied, PaymentStatus.Captured, 450, "usd"), result.Value);
        Assert.Equal(("capture", stamp.PaymentId!, $"stamp-capture-{stamp.Id}"), Assert.Single(app.Payments.Settlements));
        Assert.Equal(ProviderPaymentState.Captured, app.Payments.StateOf(stamp.PaymentId!));

        var message = await app.GetMessageAsync(stamp.Id);
        Assert.Equal(Reply, message.ReplyBody);
        Assert.Equal(app.Time.GetUtcNow(), message.ResolvedAt);

        var email = Assert.Single(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampReplied);
        Assert.Equal("ada@example.com", email.To);
        Assert.Equal("Kalai replied: Quick question", email.Subject);
        Assert.Contains(Reply, email.TextBody);
        Assert.DoesNotContain(receiver.Email, email.TextBody + email.HtmlBody);
    }

    [Fact]
    public async Task Replies_under_20_characters_are_rejected_and_nothing_is_charged()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);

        var result = await ReplyAsync(app, receiver.Id, stamp.Id, "Thanks, will do!");

        Assert.Equal(DomainErrorCodes.ReplyTooShort, result.Error?.Code);
        Assert.Empty(app.Payments.Settlements);
        Assert.Equal(MessageStatus.Pending, (await app.GetMessageAsync(stamp.Id)).Status);
        Assert.DoesNotContain(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampReplied);
    }

    [Fact]
    public async Task Replies_after_the_six_day_window_are_rejected()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Time.Advance(TimeSpan.FromDays(6));

        var result = await ReplyAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(DomainErrorCodes.ReplyWindowClosed, result.Error?.Code);
        Assert.Empty(app.Payments.Settlements);
    }

    [Fact]
    public async Task Receivers_can_only_act_on_their_own_stamps()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var someoneElse = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);

        Assert.Equal(ErrorCodes.NotFound, (await ReplyAsync(app, someoneElse.Id, stamp.Id)).Error?.Code);
        Assert.Equal(ErrorCodes.NotFound, (await DeclineAsync(app, someoneElse.Id, stamp.Id)).Error?.Code);
        Assert.Null(await app.Run<InboxService, MessageDetails?>(inbox => inbox.GetAsync(someoneElse.Id, stamp.Id, TestApp.Ct)));
    }

    [Fact]
    public async Task Unpaid_drafts_never_reach_the_receiver()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);

        var pending = await app.Run<InboxService, IReadOnlyList<InboxItem>>(inbox => inbox.ListAsync(receiver.Id, InboxView.Pending, TestApp.Ct));

        Assert.Empty(pending);
        Assert.Null(await app.Run<InboxService, MessageDetails?>(inbox => inbox.GetAsync(receiver.Id, sent.MessageId, TestApp.Ct)));
        Assert.Equal(ErrorCodes.NotFound, (await ReplyAsync(app, receiver.Id, sent.MessageId)).Error?.Code);
    }

    [Fact]
    public async Task If_capture_fails_the_reply_still_goes_out_and_the_job_captures_it_later()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Payments.FailCaptures = true;

        var result = await ReplyAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(MessageStatus.Replied, result.Value.Status);
        Assert.Equal(PaymentStatus.Authorized, result.Value.PaymentStatus);
        Assert.Contains(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampReplied);

        app.Payments.FailCaptures = false;
        var run = await app.RunExpiryJobAsync();

        Assert.Equal(1, run.Settled);
        Assert.Equal(PaymentStatus.Captured, (await app.GetMessageAsync(stamp.Id)).PaymentStatus);
        Assert.All(app.Payments.Settlements, call => Assert.Equal($"stamp-capture-{stamp.Id}", call.IdempotencyKey));
    }

    [Fact]
    public async Task If_the_hold_is_already_gone_the_reply_still_goes_out_unpaid()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Payments.ReleaseHold(stamp.PaymentId!);

        var result = await ReplyAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(new StampOutcome(MessageStatus.Replied, PaymentStatus.Canceled, 450, "usd"), result.Value);
        Assert.Contains(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampReplied);
    }

    [Fact]
    public async Task Declining_releases_the_hold_and_tells_the_sender_they_were_not_charged()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);

        var result = await DeclineAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(MessageStatus.Declined, result.Value.Status);
        Assert.Equal(PaymentStatus.Canceled, result.Value.PaymentStatus);
        Assert.Equal(("cancel", stamp.PaymentId!, $"stamp-cancel-{stamp.Id}"), Assert.Single(app.Payments.Settlements));

        var email = Assert.Single(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampDeclined);
        Assert.Equal("ada@example.com", email.To);
        Assert.Contains("weren't charged", email.Subject);
    }

    [Fact]
    public async Task A_replied_stamp_cannot_then_be_declined()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        await ReplyAsync(app, receiver.Id, stamp.Id);

        var result = await DeclineAsync(app, receiver.Id, stamp.Id);

        Assert.Equal(DomainErrorCodes.NotPending, result.Error?.Code);
        Assert.Equal(PaymentStatus.Captured, (await app.GetMessageAsync(stamp.Id)).PaymentStatus);
    }

    [Fact]
    public async Task The_inbox_lists_pending_stamps_soonest_to_expire_first()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var older = await app.CreatePendingStampAsync(receiver, "first@example.com");
        app.Time.Advance(TimeSpan.FromHours(3));
        var newer = await app.CreatePendingStampAsync(receiver, "second@example.com");
        var replied = await app.CreatePendingStampAsync(receiver, "third@example.com");
        await ReplyAsync(app, receiver.Id, replied.Id);

        var pending = await app.Run<InboxService, IReadOnlyList<InboxItem>>(inbox => inbox.ListAsync(receiver.Id, InboxView.Pending, TestApp.Ct));
        var history = await app.Run<InboxService, IReadOnlyList<InboxItem>>(inbox => inbox.ListAsync(receiver.Id, InboxView.History, TestApp.Ct));

        Assert.Equal([older.Id, newer.Id], pending.Select(i => i.Id));
        Assert.Equal(replied.Id, Assert.Single(history).Id);
        Assert.Equal(450, pending[0].EarningsCents);
    }
}
