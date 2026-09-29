using Stamp.Application.Common;
using Stamp.Application.Payments;
using Stamp.Application.Receivers;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Tests;

public sealed class ReceiverServicesTests
{
    private static Task<Result<ReceiverSettings>> UpdateAsync(TestApp app, Guid receiverId, string handle, long priceCents = 900) =>
        app.Run<ReceiverSettingsService, Result<ReceiverSettings>>(s => s.UpdateAsync(
            receiverId,
            new UpdateSettingsCommand(handle, "Kalai B", "Founder. Replies to thoughtful notes.", "https://example.com/me.png", priceCents, DonateToCharity: true),
            TestApp.Ct));

    [Fact]
    public async Task Settings_are_validated_and_saved()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);

        var result = await UpdateAsync(app, receiver.Id, "@Kalai_B");

        Assert.Equal("kalai_b", result.Value.Handle);
        Assert.Equal(900, result.Value.StampPriceCents);
        Assert.True(result.Value.DonateToCharity);
        Assert.False(result.Value.IsAcceptingStamps);

        var tooCheap = await UpdateAsync(app, receiver.Id, "kalai_b", priceCents: 150);
        Assert.Equal(DomainErrorCodes.PriceOutOfRange, tooCheap.Error?.Code);
        Assert.Equal(nameof(Receiver.StampPriceCents), tooCheap.Error?.Target);
    }

    [Fact]
    public async Task Handles_must_be_unique()
    {
        await using var app = await TestApp.CreateAsync();
        await app.CreateReceiverAsync(handle: "kalai");
        var other = await app.CreateReceiverAsync();

        var result = await UpdateAsync(app, other.Id, "Kalai");

        Assert.Equal(ErrorCodes.HandleTaken, result.Error?.Code);
        Assert.Equal(nameof(Receiver.Handle), result.Error?.Target);
    }

    [Fact]
    public async Task Onboarding_creates_the_payout_account_only_once()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);

        var first = await app.Run<PayoutOnboardingService, Result<Uri>>(s => s.StartAsync(receiver.Id, TestApp.Ct));
        var second = await app.Run<PayoutOnboardingService, Result<Uri>>(s => s.StartAsync(receiver.Id, TestApp.Ct));

        Assert.Equal(1, app.Payments.AccountsCreated);
        Assert.Equal(first.Value, second.Value);
        Assert.Contains("return=https%3A%2F%2Fstamp.test%2Fpayouts%2Freturn", first.Value.ToString());
    }

    [Fact]
    public async Task Stamps_open_up_once_the_provider_reports_the_account_ready()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);
        await app.Run<PayoutOnboardingService, Result<Uri>>(s => s.StartAsync(receiver.Id, TestApp.Ct));

        var notYet = await app.Run<PayoutOnboardingService, Result<PayoutAccountStatus?>>(s => s.RefreshAsync(receiver.Id, TestApp.Ct));
        Assert.False(notYet.Value!.IsReady);
        Assert.False((await app.TrySendStampAsync(receiver.Handle!)).IsSuccess);

        app.Payments.ReadyAccounts.Add(await app.PayoutAccountIdAsync(receiver.Id));
        var ready = await app.Run<PayoutOnboardingService, Result<PayoutAccountStatus?>>(s => s.RefreshAsync(receiver.Id, TestApp.Ct));
        var settings = await app.Run<ReceiverSettingsService, ReceiverSettings?>(s => s.GetAsync(receiver.Id, TestApp.Ct));

        Assert.True(ready.Value!.IsReady);
        Assert.True(settings!.PayoutAccountConnected);
        Assert.True(settings.IsAcceptingStamps);
        Assert.True((await app.TrySendStampAsync(receiver.Handle!)).IsSuccess);
    }

    [Fact]
    public async Task Onboarding_reports_a_provider_outage()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(payoutsReady: false);
        app.Payments.Unavailable = true;

        var result = await app.Run<PayoutOnboardingService, Result<Uri>>(s => s.StartAsync(receiver.Id, TestApp.Ct));

        Assert.Equal(ErrorCodes.PaymentUnavailable, result.Error?.Code);
    }

    [Fact]
    public async Task The_public_profile_shows_the_price_and_whether_stamps_are_accepted()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync(handle: "kalai", priceCents: 2_500);

        var profile = await app.Run<PublicProfileService, PublicProfile?>(s => s.GetAsync("Kalai", TestApp.Ct));

        Assert.NotNull(profile);
        Assert.Equal("kalai", profile.Handle);
        Assert.Equal(2_500, profile.StampPriceCents);
        Assert.True(profile.IsAcceptingStamps);
        Assert.Equal(TimeSpan.FromDays(6), profile.ReplyWindow);
        Assert.Null(await app.Run<PublicProfileService, PublicProfile?>(s => s.GetAsync("nobody", TestApp.Ct)));
    }

    [Fact]
    public async Task Blocking_the_sender_of_a_message_stops_their_next_stamp_until_unblocked()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver, "Spammer@Example.com");

        await app.Run<BlockListService, Result>(s => s.BlockSenderOfAsync(receiver.Id, stamp.Id, TestApp.Ct));
        await app.Run<BlockListService, Result>(s => s.BlockAsync(receiver.Id, "spammer@example.com", TestApp.Ct));

        var blocked = await app.Run<BlockListService, IReadOnlyList<BlockedSenderItem>>(s => s.ListAsync(receiver.Id, TestApp.Ct));
        Assert.Equal("spammer@example.com", Assert.Single(blocked).Email);
        Assert.Equal(ErrorCodes.SenderBlocked, (await app.TrySendStampAsync(receiver.Handle!, "spammer@example.com")).Error?.Code);

        await app.Run<BlockListService, Result>(s => s.UnblockAsync(receiver.Id, "SPAMMER@example.com", TestApp.Ct));
        Assert.True((await app.TrySendStampAsync(receiver.Handle!, "spammer@example.com")).IsSuccess);
    }
}
