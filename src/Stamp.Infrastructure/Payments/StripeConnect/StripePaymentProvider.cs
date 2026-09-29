using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Payments;
using Stripe;

namespace Stamp.Infrastructure.Payments.StripeConnect;

/// <summary>
/// Stripe destination charges with manual capture: the PaymentIntent is created on the platform
/// with <c>capture_method=manual</c>, <c>transfer_data.destination</c> set to the receiver's Express
/// account and a platform <c>application_fee_amount</c>. The card is only held until we capture or cancel.
/// </summary>
public sealed class StripePaymentProvider(
    IStripeClient client,
    IOptions<StripeOptions> options,
    ILogger<StripePaymentProvider> logger) : IPaymentProvider
{
    private readonly PaymentIntentService _paymentIntents = new(client);
    private readonly AccountService _accounts = new(client);
    private readonly AccountLinkService _accountLinks = new(client);

    public string Name => "Stripe";

    /// <summary>Stripe releases uncaptured online card authorizations after 7 days.</summary>
    public TimeSpan MaxAuthorizationHold => TimeSpan.FromDays(7);

    public async Task<PaymentAuthorization> CreateAuthorizationAsync(AuthorizationRequest request, CancellationToken cancellationToken)
    {
        var intent = await CallAsync("create a payment", () => _paymentIntents.CreateAsync(
            new PaymentIntentCreateOptions
            {
                Amount = request.AmountMinor,
                Currency = request.Currency,
                CaptureMethod = "manual",
                PaymentMethodTypes = ["card"],
                ApplicationFeeAmount = request.PlatformFeeMinor,
                TransferData = new PaymentIntentTransferDataOptions { Destination = request.DestinationAccountId },
                Description = request.Description,
                Metadata = new Dictionary<string, string>(request.Metadata),
            },
            new RequestOptions { IdempotencyKey = request.IdempotencyKey },
            cancellationToken));

        return new PaymentAuthorization(intent.Id, intent.ClientSecret);
    }

    public async Task<ProviderPaymentState> GetPaymentStateAsync(string paymentId, CancellationToken cancellationToken)
    {
        var intent = await CallAsync("read a payment", () => _paymentIntents.GetAsync(paymentId, cancellationToken: cancellationToken));
        return MapStatus(intent.Status);
    }

    public Task<ProviderPaymentState> CaptureAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken) =>
        SettleAsync(
            "capture a payment",
            paymentId,
            () => _paymentIntents.CaptureAsync(paymentId, new PaymentIntentCaptureOptions(), new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
            cancellationToken);

    public Task<ProviderPaymentState> CancelAsync(string paymentId, string idempotencyKey, CancellationToken cancellationToken) =>
        SettleAsync(
            "cancel a payment",
            paymentId,
            () => _paymentIntents.CancelAsync(paymentId, new PaymentIntentCancelOptions(), new RequestOptions { IdempotencyKey = idempotencyKey }, cancellationToken),
            cancellationToken);

    public async Task<string> CreatePayoutAccountAsync(string email, string idempotencyKey, CancellationToken cancellationToken)
    {
        var country = options.Value.ConnectAccountCountry;
        var account = await CallAsync("create a payout account", () => _accounts.CreateAsync(
            new AccountCreateOptions
            {
                Type = "express",
                Email = email,
                Country = string.IsNullOrWhiteSpace(country) ? null : country,
                Capabilities = new AccountCapabilitiesOptions
                {
                    CardPayments = new AccountCapabilitiesCardPaymentsOptions { Requested = true },
                    Transfers = new AccountCapabilitiesTransfersOptions { Requested = true },
                },
            },
            new RequestOptions { IdempotencyKey = idempotencyKey },
            cancellationToken));

        return account.Id;
    }

    public async Task<Uri> CreateOnboardingLinkAsync(string accountId, Uri returnUrl, Uri refreshUrl, CancellationToken cancellationToken)
    {
        var link = await CallAsync("create an onboarding link", () => _accountLinks.CreateAsync(
            new AccountLinkCreateOptions
            {
                Account = accountId,
                ReturnUrl = returnUrl.ToString(),
                RefreshUrl = refreshUrl.ToString(),
                Type = "account_onboarding",
            },
            cancellationToken: cancellationToken));

        return new Uri(link.Url);
    }

    public async Task<PayoutAccountStatus> GetPayoutAccountStatusAsync(string accountId, CancellationToken cancellationToken)
    {
        var account = await CallAsync("read a payout account", () => _accounts.GetAsync(accountId, cancellationToken: cancellationToken));
        return ToStatus(account);
    }

    internal static PayoutAccountStatus ToStatus(Account account) =>
        new(account.DetailsSubmitted, account.ChargesEnabled, account.PayoutsEnabled);

    internal static ProviderPaymentState MapStatus(string status) => status switch
    {
        "requires_capture" => ProviderPaymentState.Authorized,
        "succeeded" => ProviderPaymentState.Captured,
        "canceled" => ProviderPaymentState.Canceled,
        // requires_payment_method, requires_confirmation, requires_action, processing
        _ => ProviderPaymentState.AwaitingAuthorization,
    };

    /// <summary>
    /// Captures or cancels. If Stripe refuses because of the payment's state (already captured,
    /// already canceled, hold expired), report where the payment actually ended up instead of failing.
    /// </summary>
    private async Task<ProviderPaymentState> SettleAsync(
        string action, string paymentId, Func<Task<PaymentIntent>> call, CancellationToken cancellationToken)
    {
        try
        {
            return MapStatus((await call()).Status);
        }
        catch (StripeException ex) when (ex.StripeError?.Type == "invalid_request_error")
        {
            var state = await GetPaymentStateAsync(paymentId, cancellationToken);
            if (state is ProviderPaymentState.Captured or ProviderPaymentState.Canceled)
            {
                logger.LogInformation("Stripe refused to {Action} {PaymentId} ({Code}); it is already {State}.", action, paymentId, ex.StripeError.Code, state);
                return state;
            }

            throw new PaymentProviderException($"Stripe couldn't {action} {paymentId}: {ex.Message}", ex);
        }
        catch (StripeException ex)
        {
            throw new PaymentProviderException($"Stripe couldn't {action} {paymentId}: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException($"Couldn't reach Stripe to {action}.", ex);
        }
    }

    private static async Task<T> CallAsync<T>(string action, Func<Task<T>> call)
    {
        try
        {
            return await call();
        }
        catch (StripeException ex)
        {
            throw new PaymentProviderException($"Stripe couldn't {action}: {ex.Message}", ex);
        }
        catch (HttpRequestException ex)
        {
            throw new PaymentProviderException($"Couldn't reach Stripe to {action}.", ex);
        }
    }
}
