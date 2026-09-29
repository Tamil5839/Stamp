using System.Collections.Concurrent;
using Stamp.Application.Payments;

namespace Stamp.Application.Tests.Support;

/// <summary>An in-memory payment provider with switches for the failures we must survive.</summary>
public sealed class FakePaymentProvider : IPaymentProvider
{
    private readonly ConcurrentDictionary<string, ProviderPaymentState> _payments = new();
    private readonly ConcurrentDictionary<string, string> _paymentsByKey = new();
    private readonly ConcurrentDictionary<string, string> _accountsByKey = new();

    public string Name => "Fake";

    public TimeSpan MaxAuthorizationHold => TimeSpan.FromDays(7);

    public List<AuthorizationRequest> AuthorizationRequests { get; } = [];

    /// <summary>Every capture/cancel call as (operation, paymentId, idempotencyKey).</summary>
    public List<(string Operation, string PaymentId, string IdempotencyKey)> Settlements { get; } = [];

    public HashSet<string> ReadyAccounts { get; } = [];

    public int AccountsCreated => _accountsByKey.Count;

    /// <summary>Every call throws, as if the provider were down.</summary>
    public bool Unavailable { get; set; }

    public bool FailCaptures { get; set; }

    public bool FailCancels { get; set; }

    public ProviderPaymentState StateOf(string paymentId) => _payments[paymentId];

    /// <summary>The sender completed the card form.</summary>
    public void Authorize(string paymentId) => _payments[paymentId] = ProviderPaymentState.Authorized;

    /// <summary>The hold lapsed or was canceled outside Stamp.</summary>
    public void ReleaseHold(string paymentId) => _payments[paymentId] = ProviderPaymentState.Canceled;

    public Task<PaymentAuthorization> CreateAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        AuthorizationRequests.Add(request);

        var paymentId = _paymentsByKey.GetOrAdd(request.IdempotencyKey, _ => $"pi_test_{Guid.NewGuid():N}");
        _payments.TryAdd(paymentId, ProviderPaymentState.AwaitingAuthorization);
        return Task.FromResult(new PaymentAuthorization(paymentId, $"{paymentId}_secret"));
    }

    public Task<ProviderPaymentState> GetPaymentStateAsync(string paymentId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return Task.FromResult(_payments[paymentId]);
    }

    public Task<ProviderPaymentState> CaptureAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Settlements.Add(("capture", paymentId, idempotencyKey));
        ThrowIfUnavailable();
        if (FailCaptures)
        {
            throw new PaymentProviderException("Capture failed (simulated).");
        }

        var state = _payments.AddOrUpdate(
            paymentId,
            _ => throw new PaymentProviderException("No such payment."),
            (_, current) => current switch
            {
                ProviderPaymentState.Authorized or ProviderPaymentState.Captured => ProviderPaymentState.Captured,
                ProviderPaymentState.Canceled => ProviderPaymentState.Canceled,
                _ => throw new PaymentProviderException("Payment isn't authorized."),
            });
        return Task.FromResult(state);
    }

    public Task<ProviderPaymentState> CancelAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken)
    {
        Settlements.Add(("cancel", paymentId, idempotencyKey));
        ThrowIfUnavailable();
        if (FailCancels)
        {
            throw new PaymentProviderException("Cancel failed (simulated).");
        }

        var state = _payments.AddOrUpdate(
            paymentId,
            _ => throw new PaymentProviderException("No such payment."),
            (_, current) => current == ProviderPaymentState.Captured ? ProviderPaymentState.Captured : ProviderPaymentState.Canceled);
        return Task.FromResult(state);
    }

    public Task<string> CreatePayoutAccountAsync(string email, string idempotencyKey, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return Task.FromResult(_accountsByKey.GetOrAdd(idempotencyKey, _ => $"acct_test_{Guid.NewGuid():N}"));
    }

    public Task<Uri> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        return Task.FromResult(new Uri($"https://connect.test/onboarding/{accountId}?return={Uri.EscapeDataString(returnUrl.ToString())}"));
    }

    public Task<PayoutAccountStatus> GetPayoutAccountStatusAsync(string accountId, CancellationToken cancellationToken)
    {
        ThrowIfUnavailable();
        var ready = ReadyAccounts.Contains(accountId);
        return Task.FromResult(new PayoutAccountStatus(ready, ready, ready));
    }

    private void ThrowIfUnavailable()
    {
        if (Unavailable)
        {
            throw new PaymentProviderException("Provider unavailable (simulated).");
        }
    }
}
