using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Messages;

namespace Stamp.Application.Tests;

public sealed class AuthorizationTests
{
    [Fact]
    public async Task The_authorization_webhook_delivers_the_stamp_and_emails_both_sides()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);
        app.Payments.Authorize(sent.PaymentId);

        var result = await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentAuthorized, sent.PaymentId));

        Assert.Equal(WebhookHandlingResult.Processed, result);
        var message = await app.GetMessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Pending, message.Status);
        Assert.Equal(PaymentStatus.Authorized, message.PaymentStatus);
        Assert.Equal(TestApp.Start.AddDays(6), message.ExpiresAt);

        var emails = await app.EmailsAsync(sent.MessageId);
        var toReceiver = Assert.Single(emails, e => e.Template == EmailTemplateNames.StampReceived);
        Assert.Equal(receiver.Email, toReceiver.To);
        Assert.Equal("New stamped message from Ada Lovelace, expires in 6 days", toReceiver.Subject);
        Assert.Contains("Would you review my engine notes?", toReceiver.TextBody);

        var toSender = Assert.Single(emails, e => e.Template == EmailTemplateNames.StampSent);
        Assert.Equal("ada@example.com", toSender.To);
        Assert.Contains("only charged if Kalai replies", toSender.TextBody);
    }

    [Fact]
    public async Task A_redelivered_webhook_is_applied_once()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);
        var authorized = app.Event(PaymentWebhookEventKind.PaymentAuthorized, sent.PaymentId);

        Assert.Equal(WebhookHandlingResult.Processed, await app.DeliverAsync(authorized));
        Assert.Equal(WebhookHandlingResult.Duplicate, await app.DeliverAsync(authorized));

        Assert.Equal(2, (await app.EmailsAsync(sent.MessageId)).Count);
    }

    [Fact]
    public async Task A_second_authorization_event_for_the_same_payment_changes_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var message = await app.CreatePendingStampAsync(receiver);

        app.Time.Advance(TimeSpan.FromHours(2));
        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentAuthorized, message.PaymentId!));

        Assert.Equal(message.ExpiresAt, (await app.GetMessageAsync(message.Id)).ExpiresAt);
        Assert.Equal(2, (await app.EmailsAsync(message.Id)).Count);
    }

    [Fact]
    public async Task The_reply_window_counts_from_when_the_provider_authorized_not_from_delivery()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);
        var authorizedAt = app.Time.GetUtcNow();

        app.Time.Advance(TimeSpan.FromHours(20)); // webhook delivery was delayed
        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentAuthorized, sent.PaymentId, occurredAt: authorizedAt));

        Assert.Equal(authorizedAt.AddDays(6), (await app.GetMessageAsync(sent.MessageId)).ExpiresAt);
    }

    [Fact]
    public async Task The_return_page_confirms_the_stamp_without_waiting_for_the_webhook()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);
        app.Payments.Authorize(sent.PaymentId);

        var view = await app.Run<SendStampService, Result<SentStampView>>(s => s.ConfirmAsync(sent.MessageId, TestApp.Ct));

        Assert.Equal(MessageStatus.Pending, view.Value.Status);
        Assert.Equal("Kalai", view.Value.ReceiverDisplayName);
        Assert.Equal(TestApp.Start.AddDays(6), view.Value.ExpiresAt);

        // The webhook arriving afterwards must not send the emails again.
        await app.DeliverAsync(app.Event(PaymentWebhookEventKind.PaymentAuthorized, sent.PaymentId));
        Assert.Equal(2, (await app.EmailsAsync(sent.MessageId)).Count);
    }

    [Fact]
    public async Task The_return_page_before_authorization_leaves_the_draft_waiting()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);

        var view = await app.Run<SendStampService, Result<SentStampView>>(s => s.ConfirmAsync(sent.MessageId, TestApp.Ct));

        Assert.Equal(MessageStatus.AwaitingPayment, view.Value.Status);
        Assert.Empty(await app.EmailsAsync());
    }

    [Fact]
    public async Task Webhooks_about_unknown_payments_are_recorded_and_ignored()
    {
        await using var app = await TestApp.CreateAsync();
        var unknown = app.Event(PaymentWebhookEventKind.PaymentAuthorized, "pi_not_ours");

        Assert.Equal(WebhookHandlingResult.Processed, await app.DeliverAsync(unknown));
        Assert.Equal(WebhookHandlingResult.Duplicate, await app.DeliverAsync(unknown));
    }

    [Fact]
    public async Task Events_stamp_does_not_act_on_are_ignored()
    {
        await using var app = await TestApp.CreateAsync();

        var result = await app.DeliverAsync(app.Event(PaymentWebhookEventKind.Ignored, "ch_123"));

        Assert.Equal(WebhookHandlingResult.Ignored, result);
    }
}
