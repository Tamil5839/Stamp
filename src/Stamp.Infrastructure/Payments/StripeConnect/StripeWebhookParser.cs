using Microsoft.Extensions.Options;
using Stamp.Application.Payments;
using Stripe;

namespace Stamp.Infrastructure.Payments.StripeConnect;

/// <summary>
/// Verifies the Stripe-Signature header (HMAC-SHA256 over the raw body, with a 5-minute replay
/// window) and maps the few events Stamp cares about to provider-neutral events.
/// </summary>
public sealed class StripeWebhookParser(IOptions<StripeOptions> options, TimeProvider time) : IPaymentWebhookParser
{
    public const string SignatureHeader = "Stripe-Signature";

    private const long ToleranceSeconds = 300;

    public PaymentWebhookEvent Parse(string payload, IReadOnlyDictionary<string, string> headers)
    {
        var signature = headers
            .FirstOrDefault(h => string.Equals(h.Key, SignatureHeader, StringComparison.OrdinalIgnoreCase))
            .Value;
        if (string.IsNullOrWhiteSpace(signature))
        {
            throw new InvalidWebhookSignatureException($"The {SignatureHeader} header is missing.");
        }

        var stripeEvent = VerifyAndParse(payload, signature);
        var occurredAt = new DateTimeOffset(DateTime.SpecifyKind(stripeEvent.Created, DateTimeKind.Utc));

        return stripeEvent.Type switch
        {
            // Only a PaymentIntent that is actually waiting for capture counts as authorized.
            EventTypes.PaymentIntentAmountCapturableUpdated when stripeEvent.Data.Object is PaymentIntent { Status: "requires_capture" } intent =>
                new(stripeEvent.Id, stripeEvent.Type, PaymentWebhookEventKind.PaymentAuthorized, intent.Id, occurredAt),

            EventTypes.PaymentIntentSucceeded when stripeEvent.Data.Object is PaymentIntent intent =>
                new(stripeEvent.Id, stripeEvent.Type, PaymentWebhookEventKind.PaymentCaptured, intent.Id, occurredAt),

            EventTypes.PaymentIntentCanceled when stripeEvent.Data.Object is PaymentIntent intent =>
                new(stripeEvent.Id, stripeEvent.Type, PaymentWebhookEventKind.PaymentCanceled, intent.Id, occurredAt),

            EventTypes.AccountUpdated when stripeEvent.Data.Object is Account account =>
                new(stripeEvent.Id, stripeEvent.Type, PaymentWebhookEventKind.PayoutAccountUpdated, account.Id, occurredAt, StripePaymentProvider.ToStatus(account)),

            _ => new(stripeEvent.Id, stripeEvent.Type, PaymentWebhookEventKind.Ignored, null, occurredAt),
        };
    }

    private Event VerifyAndParse(string payload, string signature)
    {
        var settings = options.Value;
        var secrets = new[] { settings.WebhookSecret, settings.ConnectWebhookSecret }
            .Where(secret => !string.IsNullOrWhiteSpace(secret))
            .ToList();
        if (secrets.Count == 0)
        {
            throw new InvalidWebhookSignatureException("No Stripe webhook secret is configured.");
        }

        Exception? lastError = null;
        foreach (var secret in secrets)
        {
            try
            {
                // We read only fields that are stable across API versions, so a webhook endpoint
                // pinned to a different version than Stripe.net's must not be rejected.
                return EventUtility.ConstructEvent(
                    payload, signature, secret, ToleranceSeconds, time.GetUtcNow().ToUnixTimeSeconds(), throwOnApiVersionMismatch: false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastError = ex;
            }
        }

        throw new InvalidWebhookSignatureException("The Stripe webhook signature is invalid or expired.", lastError);
    }
}
