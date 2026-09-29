using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Messages;

namespace Stamp.Application.Tests;

public sealed class ExpiryJobTests
{
    [Fact]
    public async Task Stamps_expire_after_six_days_the_hold_is_released_and_the_sender_is_told()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);

        app.Time.Advance(TimeSpan.FromDays(6) - TimeSpan.FromMinutes(1));
        Assert.Equal(new ExpiryRunResult(0, 0, 0, 0, 0), await app.RunExpiryJobAsync());
        Assert.Equal(MessageStatus.Pending, (await app.GetMessageAsync(stamp.Id)).Status);

        app.Time.Advance(TimeSpan.FromMinutes(1));
        var run = await app.RunExpiryJobAsync();

        Assert.Equal(1, run.Expired);
        Assert.Equal(1, run.Settled);
        var message = await app.GetMessageAsync(stamp.Id);
        Assert.Equal(MessageStatus.Expired, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.Equal(ProviderPaymentState.Canceled, app.Payments.StateOf(stamp.PaymentId!));

        var email = Assert.Single(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampExpired);
        Assert.Equal("ada@example.com", email.To);
        Assert.Equal("No reply from Kalai. You weren't charged.", email.Subject);
    }

    [Fact]
    public async Task Running_the_job_again_changes_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Time.Advance(TimeSpan.FromDays(7));

        await app.RunExpiryJobAsync();
        var second = await app.RunExpiryJobAsync();

        Assert.Equal(new ExpiryRunResult(0, 0, 0, 0, 0), second);
        Assert.Single(await app.EmailsAsync(stamp.Id), e => e.Template == EmailTemplateNames.StampExpired);
        Assert.Single(app.Payments.Settlements);
    }

    [Fact]
    public async Task Pending_stamps_that_still_have_time_are_left_alone()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var overdue = await app.CreatePendingStampAsync(receiver, "first@example.com");
        app.Time.Advance(TimeSpan.FromDays(3));
        var fresh = await app.CreatePendingStampAsync(receiver, "second@example.com");
        app.Time.Advance(TimeSpan.FromDays(3));

        var run = await app.RunExpiryJobAsync();

        Assert.Equal(1, run.Expired);
        Assert.Equal(MessageStatus.Expired, (await app.GetMessageAsync(overdue.Id)).Status);
        Assert.Equal(MessageStatus.Pending, (await app.GetMessageAsync(fresh.Id)).Status);
    }

    [Fact]
    public async Task A_release_that_fails_is_retried_on_the_next_run()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Time.Advance(TimeSpan.FromDays(6));
        app.Payments.FailCancels = true;

        var first = await app.RunExpiryJobAsync();

        Assert.Equal(1, first.Expired);
        Assert.Equal(1, first.SettlementFailures);
        Assert.Equal(PaymentStatus.Authorized, (await app.GetMessageAsync(stamp.Id)).PaymentStatus);

        app.Payments.FailCancels = false;
        app.Time.Advance(TimeSpan.FromHours(1));
        var second = await app.RunExpiryJobAsync();

        Assert.Equal(1, second.Settled);
        Assert.Equal(PaymentStatus.Canceled, (await app.GetMessageAsync(stamp.Id)).PaymentStatus);
        Assert.All(app.Payments.Settlements, call => Assert.Equal($"stamp-cancel-{stamp.Id}", call.IdempotencyKey));
    }

    [Fact]
    public async Task Drafts_never_paid_for_are_abandoned_after_24_hours_and_voided()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);

        app.Time.Advance(TimeSpan.FromHours(23));
        Assert.Equal(0, (await app.RunExpiryJobAsync()).Abandoned);

        app.Time.Advance(TimeSpan.FromHours(1));
        var run = await app.RunExpiryJobAsync();

        Assert.Equal(1, run.Abandoned);
        var message = await app.GetMessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Abandoned, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.Empty(await app.EmailsAsync());
    }

    [Fact]
    public async Task A_draft_whose_authorization_was_never_reported_is_recovered_not_abandoned()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var sent = await app.SendStampAsync(receiver.Handle!);
        app.Payments.Authorize(sent.PaymentId); // but no webhook, and the sender closed the tab

        app.Time.Advance(TimeSpan.FromHours(24));
        var run = await app.RunExpiryJobAsync();

        Assert.Equal(1, run.Recovered);
        var message = await app.GetMessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Pending, message.Status);
        // Dated from creation, so the window still closes before the card hold lapses.
        Assert.Equal(TestApp.Start.AddDays(6), message.ExpiresAt);
        Assert.Equal(2, (await app.EmailsAsync(sent.MessageId)).Count);
    }
}
