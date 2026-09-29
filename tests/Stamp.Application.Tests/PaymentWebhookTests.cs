using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Receivers;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Messages;

namespace Stamp.Application.Tests;

public sealed class PaymentWebhookTests
{
    [Fact]
    public async Task The_provider_releasing_a_pending_hold_expires_the_stamp()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);

        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentCanceled, stamp.PaymentId!));

        var message = await app.GetMessageAsync(stamp.Id);
        Assert.Equal(MessageStatus.Expired, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.Single(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampExpired);
    }

    [Fact]
    public async Task A_cancel_webhook_after_a_decline_just_confirms_it()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        await app.Run<InboxService, Result<StampOutcome>>(inbox => inbox.DeclineAsync(receiver.Id, stamp.Id, TestApp.Ct));

        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentCanceled, stamp.PaymentId!));

        Assert.Equal(MessageStatus.Declined, (await app.GetMessageAsync(stamp.Id)).Status);
        Assert.DoesNotContain(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampExpired);
    }

    [Fact]
    public async Task The_capture_webhook_records_a_capture_the_reply_could_not_confirm()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Payments.FailCaptures = true;
        await app.Run<InboxService, Result<StampOutcome>>(inbox =>
            inbox.ReplyAsync(receiver.Id, stamp.Id, "Here is a thoughtful answer for you.", TestApp.Ct));

        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentCaptured, stamp.PaymentId!));

        Assert.Equal(PaymentStatus.Captured, (await app.GetMessageAsync(stamp.Id)).PaymentStatus);
    }

    [Fact]
    public async Task Payout_account_updates_switch_the_public_page_on()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);
        await app.Run<PayoutOnboardingService, Result<Uri>>(s => s.StartAsync(receiver.Id, TestApp.Ct));
        var accountId = await app.PayoutAccountIdAsync(receiver.Id);

        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PayoutAccountUpdated, accountId) with
        {
            PayoutAccount = new PayoutAccountStatus(DetailsSubmitted: true, ChargesEnabled: true, PayoutsEnabled: true),
        });

        var profile = await app.Run<PublicProfileService, PublicProfile?>(s => s.GetAsync(receiver.Handle!, TestApp.Ct));
        Assert.True(profile!.IsAcceptingStamps);
    }
}
