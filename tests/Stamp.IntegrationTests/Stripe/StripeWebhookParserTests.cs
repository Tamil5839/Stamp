using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Stamp.Application.Payments;
using Stamp.Infrastructure.Payments;
using Stamp.Infrastructure.Payments.StripeConnect;

namespace Stamp.IntegrationTests.Stripe;

public sealed class StripeWebhookParserTests
{
    private const string Secret = "whsec_test_platform";
    private const string ConnectSecret = "whsec_test_connect";
    private static readonly DateTimeOffset Now = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private static StripeWebhookParser Parser(string? connectSecret = null) => new(
        Options.Create(new StripeOptions { WebhookSecret = Secret, ConnectWebhookSecret = connectSecret }),
        new FakeTimeProvider(Now));

    private static Dictionary<string, string> Signed(string payload, string secret = Secret, DateTimeOffset? at = null) =>
        new() { ["Stripe-Signature"] = StripeSignatures.Header(payload, secret, at ?? Now) };

    [Theory]
    [InlineData("payment_intent.amount_capturable_updated", "requires_capture", PaymentWebhookEventKind.PaymentAuthorized)]
    [InlineData("payment_intent.succeeded", "succeeded", PaymentWebhookEventKind.PaymentCaptured)]
    [InlineData("payment_intent.canceled", "canceled", PaymentWebhookEventKind.PaymentCanceled)]
    [InlineData("payment_intent.created", "requires_payment_method", PaymentWebhookEventKind.Ignored)]
    [InlineData("payment_intent.amount_capturable_updated", "succeeded", PaymentWebhookEventKind.Ignored)]
    public void Payment_intent_events_map_to_stamp_events(string type, string status, PaymentWebhookEventKind expected)
    {
        var created = Now.AddSeconds(-40);
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", type, "pi_123", status, created);

        var parsed = Parser().Parse(payload, Signed(payload));

        Assert.Equal(expected, parsed.Kind);
        Assert.Equal("evt_1", parsed.EventId);
        Assert.Equal(type, parsed.EventType);
        Assert.Equal(created, parsed.OccurredAt);
        if (expected != PaymentWebhookEventKind.Ignored)
        {
            Assert.Equal("pi_123", parsed.ObjectId);
        }
    }

    [Fact]
    public void Account_updates_carry_the_onboarding_status()
    {
        var payload = StripeSignatures.AccountUpdatedEvent("evt_2", "acct_123", detailsSubmitted: true, chargesEnabled: true, Now);

        var parsed = Parser().Parse(payload, Signed(payload));

        Assert.Equal(PaymentWebhookEventKind.PayoutAccountUpdated, parsed.Kind);
        Assert.Equal("acct_123", parsed.ObjectId);
        Assert.True(parsed.PayoutAccount!.IsReady);
    }

    [Fact]
    public void A_tampered_payload_is_rejected()
    {
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);
        var headers = Signed(payload);

        Assert.Throws<InvalidWebhookSignatureException>(() => Parser().Parse(payload.Replace("pi_123", "pi_999"), headers));
    }

    [Fact]
    public void A_payload_signed_with_another_secret_is_rejected()
    {
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);

        Assert.Throws<InvalidWebhookSignatureException>(() => Parser().Parse(payload, Signed(payload, "whsec_someone_else")));
    }

    [Fact]
    public void A_replayed_signature_older_than_five_minutes_is_rejected()
    {
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);

        Assert.Throws<InvalidWebhookSignatureException>(() => Parser().Parse(payload, Signed(payload, at: Now.AddMinutes(-6))));
        Assert.Equal(PaymentWebhookEventKind.PaymentCaptured, Parser().Parse(payload, Signed(payload, at: Now.AddMinutes(-4))).Kind);
    }

    [Fact]
    public void A_missing_signature_header_is_rejected()
    {
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);

        Assert.Throws<InvalidWebhookSignatureException>(() => Parser().Parse(payload, new Dictionary<string, string>()));
    }

    [Fact]
    public void Events_from_a_separate_connect_endpoint_verify_with_its_own_secret()
    {
        var payload = StripeSignatures.AccountUpdatedEvent("evt_3", "acct_123", true, true, Now);

        Assert.Throws<InvalidWebhookSignatureException>(() => Parser().Parse(payload, Signed(payload, ConnectSecret)));
        Assert.Equal(PaymentWebhookEventKind.PayoutAccountUpdated, Parser(ConnectSecret).Parse(payload, Signed(payload, ConnectSecret)).Kind);
    }

    [Fact]
    public void Header_names_are_matched_case_insensitively()
    {
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);
        var headers = new Dictionary<string, string> { ["stripe-signature"] = StripeSignatures.Header(payload, Secret, Now) };

        Assert.Equal(PaymentWebhookEventKind.PaymentCaptured, Parser().Parse(payload, headers).Kind);
    }

    [Fact]
    public void Without_a_configured_secret_every_webhook_is_refused()
    {
        var parser = new StripeWebhookParser(Options.Create(new StripeOptions()), new FakeTimeProvider(Now));
        var payload = StripeSignatures.PaymentIntentEvent("evt_1", "payment_intent.succeeded", "pi_123", "succeeded", Now);

        Assert.Throws<InvalidWebhookSignatureException>(() => parser.Parse(payload, Signed(payload)));
    }
}
