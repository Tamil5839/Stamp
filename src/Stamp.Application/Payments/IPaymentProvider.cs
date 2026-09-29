namespace Stamp.Application.Payments;

/// <summary>
/// Everything Stamp needs from a payment provider: manual-capture card authorizations paid out to
/// a receiver's connected account, and hosted onboarding for those accounts. Stripe implements it
/// today; the shape is deliberately provider-neutral so another provider (e.g. Razorpay) can too.
/// </summary>
public interface IPaymentProvider
{
    string Name { get; }

    /// <summary>How long the provider keeps an uncaptured authorization before releasing it.</summary>
    TimeSpan MaxAuthorizationHold { get; }

    /// <summary>
    /// Creates an uncaptured payment for the sender to authorize in the browser. The destination
    /// account receives the amount minus the platform fee once it is captured.
    /// </summary>
    Task<PaymentAuthorization> CreateAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken);

    Task<ProviderPaymentState> GetPaymentStateAsync(string paymentId, CancellationToken cancellationToken);

    /// <summary>
    /// Captures an authorized payment and returns the resulting state: Captured, or Canceled if the
    /// hold was already gone. Safe to retry with the same key. Throws <see cref="PaymentProviderException"/>
    /// when the provider couldn't be reached or refused for another reason.
    /// </summary>
    Task<ProviderPaymentState> CaptureAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken);

    /// <summary>
    /// Releases the hold (or voids a payment that was never authorized) and returns the resulting
    /// state: Canceled, or Captured if it had already been captured. Safe to retry with the same key.
    /// </summary>
    Task<ProviderPaymentState> CancelAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken);

    Task<string> CreatePayoutAccountAsync(string email, string idempotencyKey, CancellationToken cancellationToken);

    Task<Uri> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken);

    Task<PayoutAccountStatus> GetPayoutAccountStatusAsync(string accountId, CancellationToken cancellationToken);
}

public sealed record AuthorizationRequest(
    long AmountMinor,
    string Currency,
    long PlatformFeeMinor,
    string DestinationAccountId,
    string Description,
    IReadOnlyDictionary<string, string> Metadata,
    string IdempotencyKey);

/// <summary>The provider's payment id plus the secret the browser needs to authorize it.</summary>
public sealed record PaymentAuthorization(string PaymentId, string ClientSecret);

public enum ProviderPaymentState
{
    /// <summary>Created but not yet authorized (needs a card, 3-D Secure, or is processing).</summary>
    AwaitingAuthorization,

    Authorized,
    Captured,
    Canceled,
}

public sealed record PayoutAccountStatus(bool DetailsSubmitted, bool ChargesEnabled, bool PayoutsEnabled)
{
    /// <summary>The account can receive stamps once the provider has what it needs and allows charges.</summary>
    public bool IsReady => DetailsSubmitted && ChargesEnabled;
}

/// <summary>The provider couldn't be reached, or refused the operation for a reason other than the payment's state.</summary>
public sealed class PaymentProviderException(string message, Exception? innerException = null)
    : Exception(message, innerException);
