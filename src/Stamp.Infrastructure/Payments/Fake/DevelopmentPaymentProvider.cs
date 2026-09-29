using System.Collections.Concurrent;
using Stamp.Application.Payments;

namespace Stamp.Infrastructure.Payments.Fake;

/// <summary>
/// An in-memory payment provider for trying Stamp locally without Stripe keys. The public page
/// shows a "simulate card" button instead of Stripe Elements, and onboarding completes instantly.
/// State lives in memory and is lost on restart. Registration refuses it in Production.
/// </summary>
public sealed class DevelopmentPaymentProvider : IPaymentProvider
{
    public const string OnboardingPath = "/dev/payments/onboard";

    private readonly ConcurrentDictionary<string, ProviderPaymentState> _payments = new();
    private readonly ConcurrentDictionary<string, string> _paymentsByKey = new();
    private readonly ConcurrentDictionary<string, string> _accountsByKey = new();
    private readonly ConcurrentDictionary<string, bool> _accountsReady = new();

    public string Name => "Fake";

    public TimeSpan MaxAuthorizationHold => TimeSpan.FromDays(7);

    /// <summary>What Stripe.js would do when the sender submits a valid card.</summary>
    public bool SimulateAuthorization(string paymentId) =>
        _payments.TryUpdate(paymentId, ProviderPaymentState.Authorized, ProviderPaymentState.AwaitingAuthorization);

    /// <summary>What Stripe's hosted onboarding would do when the receiver finishes it.</summary>
    public bool CompleteOnboarding(string accountId)
    {
        if (!_accountsReady.ContainsKey(accountId))
        {
            return false;
        }

        _accountsReady[accountId] = true;
        return true;
    }

    public Task<PaymentAuthorization> CreateAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var paymentId = _paymentsByKey.GetOrAdd(request.IdempotencyKey, _ => $"fake_pi_{Guid.NewGuid():N}");
        _payments.TryAdd(paymentId, ProviderPaymentState.AwaitingAuthorization);
        return Task.FromResult(new PaymentAuthorization(paymentId, $"{paymentId}_secret_fake"));
    }

    public Task<ProviderPaymentState> GetPaymentStateAsync(string paymentId, CancellationToken cancellationToken) =>
        Task.FromResult(Find(paymentId));

    public Task<ProviderPaymentState> CaptureAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var state = Find(paymentId) switch
        {
            ProviderPaymentState.Authorized or ProviderPaymentState.Captured => ProviderPaymentState.Captured,
            ProviderPaymentState.Canceled => ProviderPaymentState.Canceled,
            _ => throw new PaymentProviderException($"Payment {paymentId} isn't authorized."),
        };

        _payments[paymentId] = state;
        return Task.FromResult(state);
    }

    public Task<ProviderPaymentState> CancelAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        var state = Find(paymentId) == ProviderPaymentState.Captured ? ProviderPaymentState.Captured : ProviderPaymentState.Canceled;
        _payments[paymentId] = state;
        return Task.FromResult(state);
    }

    public Task<string> CreatePayoutAccountAsync(string email, string idempotencyKey, CancellationToken cancellationToken)
    {
        var accountId = _accountsByKey.GetOrAdd(idempotencyKey, _ => $"fake_acct_{Guid.NewGuid():N}");
        _accountsReady.TryAdd(accountId, false);
        return Task.FromResult(accountId);
    }

    public Task<Uri> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken) =>
        Task.FromResult(new Uri(returnUrl, $"{OnboardingPath}?account={Uri.EscapeDataString(accountId)}"));

    public Task<PayoutAccountStatus> GetPayoutAccountStatusAsync(string accountId, CancellationToken cancellationToken)
    {
        // Accounts from before a restart are forgotten; treat them as onboarded so local data stays usable.
        var ready = _accountsReady.GetValueOrDefault(accountId, true);
        return Task.FromResult(new PayoutAccountStatus(ready, ready, ready));
    }

    private ProviderPaymentState Find(string paymentId) =>
        _payments.TryGetValue(paymentId, out var state)
            ? state
            : throw new PaymentProviderException($"Unknown payment {paymentId} (the fake provider forgets payments on restart).");
}
