using Microsoft.EntityFrameworkCore;
using Stamp.Application.Common;
using Stamp.Application.Receivers;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;

namespace Stamp.Application.Tests;

public sealed class SendStampTests
{
    [Fact]
    public async Task Sending_saves_an_unpaid_draft_and_asks_for_a_manual_capture_hold_paid_out_to_the_receiver()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(priceCents: 1_500);

        var sent = await app.SendStampAsync(receiver.Handle!, "ADA@Example.com");

        var request = Assert.Single(app.Payments.AuthorizationRequests);
        Assert.Equal(1_500, request.AmountMinor);
        Assert.Equal(150, request.PlatformFeeMinor);
        Assert.Equal("usd", request.Currency);
        Assert.Equal(receiver.PayoutAccountId, request.DestinationAccountId);
        Assert.Equal(sent.MessageId.ToString(), request.Metadata["message_id"]);
        Assert.Equal($"{sent.PaymentId}_secret", sent.ClientSecret);

        var message = await app.GetMessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.AwaitingPayment, message.Status);
        Assert.Equal(PaymentStatus.Created, message.PaymentStatus);
        Assert.Equal(sent.PaymentId, message.PaymentId);
        Assert.Equal("ada@example.com", message.SenderEmail);
        Assert.Empty(await app.EmailsAsync());
    }

    [Fact]
    public async Task Unknown_handles_are_not_found()
    {
        await using var app = await TestApp.CreateAsync();

        var result = await app.TrySendStampAsync("nobody_here");

        Assert.Equal(ErrorCodes.NotFound, result.Error?.Code);
        Assert.Empty(app.Payments.AuthorizationRequests);
    }

    [Fact]
    public async Task Receivers_who_have_not_finished_payouts_onboarding_do_not_accept_stamps()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);

        var result = await app.TrySendStampAsync(receiver.Handle!);

        Assert.Equal(ErrorCodes.NotAcceptingStamps, result.Error?.Code);
        Assert.Empty(app.Payments.AuthorizationRequests);
    }

    [Fact]
    public async Task Invalid_input_is_rejected_before_any_payment_is_created()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();

        var result = await app.TrySendStampAsync(receiver.Handle!, body: new string('x', 1_501));

        Assert.Equal(DomainErrorCodes.InvalidBody, result.Error?.Code);
        Assert.Equal("Body", result.Error?.Target);
        Assert.Empty(app.Payments.AuthorizationRequests);
    }

    [Fact]
    public async Task Blocked_senders_are_turned_away_whatever_the_casing()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        await app.Run<BlockListService, Result>(s => s.BlockAsync(receiver.Id, "ada@example.com", TestApp.Ct));

        var result = await app.TrySendStampAsync(receiver.Handle!, " Ada@EXAMPLE.com ");

        Assert.Equal(ErrorCodes.SenderBlocked, result.Error?.Code);
        Assert.Empty(app.Payments.AuthorizationRequests);
    }

    [Fact]
    public async Task Senders_are_rate_limited_per_email_across_all_receivers()
    {
        await using var app = await TestApp.CreateAsync(stamps => stamps.SenderRateLimit = 2);
        var first = await app.CreateReceiverAsync();
        var second = await app.CreateReceiverAsync();

        Assert.True((await app.TrySendStampAsync(first.Handle!)).IsSuccess);
        Assert.True((await app.TrySendStampAsync(second.Handle!)).IsSuccess);
        var limited = await app.TrySendStampAsync(first.Handle!, "Ada@example.com");
        var otherSender = await app.TrySendStampAsync(first.Handle!, "grace@example.com");

        Assert.Equal(ErrorCodes.RateLimited, limited.Error?.Code);
        Assert.True(otherSender.IsSuccess);

        app.Time.Advance(TimeSpan.FromHours(1));
        Assert.True((await app.TrySendStampAsync(first.Handle!)).IsSuccess);
    }

    [Fact]
    public async Task A_provider_outage_returns_a_retryable_error_and_saves_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        app.Payments.Unavailable = true;

        var result = await app.TrySendStampAsync(receiver.Handle!);

        Assert.Equal(ErrorCodes.PaymentUnavailable, result.Error?.Code);
        Assert.Equal(0, await app.Db(db => db.Messages.CountAsync(TestApp.Ct)));
    }
}
