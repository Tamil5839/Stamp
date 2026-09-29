using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Domain.Tests;

public sealed class ReceiverTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Registering_normalizes_email_and_sets_defaults()
    {
        var receiver = Receiver.Register("  Kalai@Example.com ", Now);

        Assert.Equal("kalai@example.com", receiver.Email);
        Assert.Null(receiver.Handle);
        Assert.Equal(StampPricing.DefaultPriceCents, receiver.StampPriceCents);
        Assert.Equal("usd", receiver.Currency);
        Assert.False(receiver.HasProfile);
        Assert.False(receiver.IsAcceptingStamps);
    }

    [Fact]
    public void Updating_the_profile_normalizes_and_stores_every_field()
    {
        var receiver = Receiver.Register("kalai@example.com", Now);

        receiver.UpdateProfile("@Kalai", "  Kalai  ", "  Founder. Busy.  ", " https://example.com/me.jpg ", 1500, donateToCharity: true, Now.AddMinutes(1));

        Assert.Equal("kalai", receiver.Handle);
        Assert.Equal("Kalai", receiver.DisplayName);
        Assert.Equal("Founder. Busy.", receiver.Bio);
        Assert.Equal("https://example.com/me.jpg", receiver.PhotoUrl);
        Assert.Equal(1500, receiver.StampPriceCents);
        Assert.True(receiver.DonateToCharity);
        Assert.Equal(Now.AddMinutes(1), receiver.UpdatedAt);
    }

    [Theory]
    [InlineData("kalai", "", "", null, 500, DomainErrorCodes.InvalidDisplayName)]
    [InlineData("kalai", "Kalai", "", "http://example.com/me.jpg", 500, DomainErrorCodes.InvalidPhotoUrl)]
    [InlineData("kalai", "Kalai", "", "not a url", 500, DomainErrorCodes.InvalidPhotoUrl)]
    [InlineData("kalai", "Kalai", "", null, 199, DomainErrorCodes.PriceOutOfRange)]
    [InlineData("kalai", "Kalai", "", null, 50_001, DomainErrorCodes.PriceOutOfRange)]
    [InlineData("ka", "Kalai", "", null, 500, DomainErrorCodes.InvalidHandle)]
    [InlineData("inbox", "Kalai", "", null, 500, DomainErrorCodes.ReservedHandle)]
    public void Invalid_profiles_are_rejected_without_partial_updates(
        string handle, string displayName, string bio, string? photoUrl, long price, string expectedCode)
    {
        var receiver = Receiver.Register("kalai@example.com", Now);

        var error = Assert.Throws<DomainException>(() =>
            receiver.UpdateProfile(handle, displayName, bio, photoUrl, price, false, Now));

        Assert.Equal(expectedCode, error.Code);
        Assert.Null(receiver.Handle);
        Assert.Equal(StampPricing.DefaultPriceCents, receiver.StampPriceCents);
    }

    [Fact]
    public void Bio_is_limited_to_280_characters()
    {
        var receiver = Receiver.Register("kalai@example.com", Now);

        var error = Assert.Throws<DomainException>(() =>
            receiver.UpdateProfile("kalai", "Kalai", new string('b', 281), null, 500, false, Now));

        Assert.Equal(DomainErrorCodes.InvalidBio, error.Code);
    }

    [Fact]
    public void Accepting_stamps_requires_a_profile_and_completed_payouts()
    {
        var receiver = Receiver.Register("kalai@example.com", Now);
        receiver.UpdateProfile("kalai", "Kalai", "", null, 500, false, Now);
        Assert.False(receiver.IsAcceptingStamps);

        receiver.AttachPayoutAccount("acct_123", Now);
        Assert.False(receiver.IsAcceptingStamps);

        receiver.SetPayoutsEnabled(true, Now);
        Assert.True(receiver.IsAcceptingStamps);

        receiver.SetPayoutsEnabled(false, Now);
        Assert.False(receiver.IsAcceptingStamps);
    }

    [Fact]
    public void A_payout_account_can_only_be_attached_once()
    {
        var receiver = Receiver.Register("kalai@example.com", Now);
        receiver.AttachPayoutAccount("acct_123", Now);

        receiver.AttachPayoutAccount("acct_123", Now);
        var error = Assert.Throws<DomainException>(() => receiver.AttachPayoutAccount("acct_456", Now));

        Assert.Equal(DomainErrorCodes.PayoutAccountAlreadyAttached, error.Code);
        Assert.Equal("acct_123", receiver.PayoutAccountId);
    }
}
