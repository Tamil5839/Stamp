namespace Stamp.Application.Payments;

/// <summary>Verifies a provider webhook's signature and translates it into a provider-neutral event.</summary>
public interface IPaymentWebhookParser
{
    /// <exception cref="InvalidWebhookSignatureException">The payload wasn't signed by the provider.</exception>
    PaymentWebhookEvent Parse(string payload, IReadOnlyDictionary<string, string> headers);
}

public enum PaymentWebhookEventKind
{
    /// <summary>A validly signed event Stamp doesn't act on.</summary>
    Ignored,

    PaymentAuthorized,
    PaymentCaptured,
    PaymentCanceled,
    PayoutAccountUpdated,
}

/// <param name="EventId">The provider's unique event id, used to skip redeliveries.</param>
/// <param name="ObjectId">The payment id, or the connected account id for <see cref="PaymentWebhookEventKind.PayoutAccountUpdated"/>.</param>
/// <param name="OccurredAt">When the provider created the event (not when we received it).</param>
public sealed record PaymentWebhookEvent(
    string EventId,
    string EventType,
    PaymentWebhookEventKind Kind,
    string? ObjectId,
    DateTimeOffset OccurredAt,
    PayoutAccountStatus? PayoutAccount = null);

public sealed class InvalidWebhookSignatureException(string message, Exception? innerException = null)
    : Exception(message, innerException);
