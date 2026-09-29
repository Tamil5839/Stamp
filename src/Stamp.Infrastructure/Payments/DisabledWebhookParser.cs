using Stamp.Application.Payments;

namespace Stamp.Infrastructure.Payments;

/// <summary>Used when no webhook signing secret is configured: every webhook is rejected.</summary>
internal sealed class DisabledWebhookParser : IPaymentWebhookParser
{
    public PaymentWebhookEvent Parse(string payload, IReadOnlyDictionary<string, string> headers) =>
        throw new InvalidWebhookSignatureException("Webhooks are not configured (set Stripe:WebhookSecret).");
}
