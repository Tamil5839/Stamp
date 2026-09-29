using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stamp.Application.Payments;
using Stamp.Infrastructure.Payments;
using Stamp.Infrastructure.Payments.StripeConnect;
using Stripe;

namespace Stamp.IntegrationTests.Stripe;

/// <summary>
/// Runs <see cref="StripePaymentProvider"/> against the real Stripe API in test mode.
/// <list type="bullet">
/// <item>STRIPE_TEST_SECRET_KEY (sk_test_…) enables the suite; live keys are refused.</item>
/// <item>STRIPE_TEST_CONNECTED_ACCOUNT (acct_…, an onboarded test Express account) enables the payment tests,
/// since destination charges need a connected account whose transfers capability is active.</item>
/// </list>
/// </summary>
public sealed class StripeTestModeTests
{
    private const string SecretKeyVariable = "STRIPE_TEST_SECRET_KEY";
    private const string ConnectedAccountVariable = "STRIPE_TEST_CONNECTED_ACCOUNT";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task A_manual_capture_hold_is_authorized_then_captured_with_the_fee_and_destination()
    {
        var (client, provider) = Create();
        var destination = RequireConnectedAccount();

        var authorization = await provider.CreateAuthorizationAsync(Request(destination), Ct);
        Assert.Equal(ProviderPaymentState.AwaitingAuthorization, await provider.GetPaymentStateAsync(authorization.PaymentId, Ct));
        Assert.StartsWith(authorization.PaymentId, authorization.ClientSecret, StringComparison.Ordinal);

        await AuthorizeWithTestCardAsync(client, authorization.PaymentId);
        Assert.Equal(ProviderPaymentState.Authorized, await provider.GetPaymentStateAsync(authorization.PaymentId, Ct));

        var key = $"stamp-test-capture-{Guid.NewGuid():N}";
        Assert.Equal(ProviderPaymentState.Captured, await provider.CaptureAsync(authorization.PaymentId, key, Ct));
        Assert.Equal(ProviderPaymentState.Captured, await provider.CaptureAsync(authorization.PaymentId, key, Ct));
        Assert.Equal(ProviderPaymentState.Captured, await provider.CancelAsync(authorization.PaymentId, $"stamp-test-cancel-{Guid.NewGuid():N}", Ct));

        var intent = await new PaymentIntentService(client).GetAsync(authorization.PaymentId, cancellationToken: Ct);
        Assert.Equal("manual", intent.CaptureMethod);
        Assert.Equal(50, intent.ApplicationFeeAmount);
        Assert.Equal(destination, intent.TransferData.DestinationId);
    }

    [Fact]
    public async Task Canceling_releases_the_hold_and_a_later_capture_reports_it_canceled()
    {
        var (client, provider) = Create();
        var authorization = await provider.CreateAuthorizationAsync(Request(RequireConnectedAccount()), Ct);
        await AuthorizeWithTestCardAsync(client, authorization.PaymentId);

        Assert.Equal(ProviderPaymentState.Canceled, await provider.CancelAsync(authorization.PaymentId, $"stamp-test-cancel-{Guid.NewGuid():N}", Ct));
        Assert.Equal(ProviderPaymentState.Canceled, await provider.CaptureAsync(authorization.PaymentId, $"stamp-test-capture-{Guid.NewGuid():N}", Ct));
    }

    [Fact]
    public async Task A_payment_that_was_never_authorized_can_be_voided()
    {
        var (_, provider) = Create();
        var authorization = await provider.CreateAuthorizationAsync(Request(RequireConnectedAccount()), Ct);

        Assert.Equal(ProviderPaymentState.Canceled, await provider.CancelAsync(authorization.PaymentId, $"stamp-test-cancel-{Guid.NewGuid():N}", Ct));
    }

    [Fact]
    public async Task A_new_express_account_starts_unready_with_a_hosted_onboarding_link()
    {
        var (client, provider) = Create();

        var accountId = await provider.CreatePayoutAccountAsync($"stamp-test-{Guid.NewGuid():N}@example.com", $"stamp-test-account-{Guid.NewGuid():N}", Ct);
        try
        {
            Assert.StartsWith("acct_", accountId, StringComparison.Ordinal);
            Assert.False((await provider.GetPayoutAccountStatusAsync(accountId, Ct)).IsReady);

            var link = await provider.CreateOnboardingLinkAsync(accountId, new Uri("https://example.com/payouts/return"), new Uri("https://example.com/payouts/refresh"), Ct);
            Assert.Equal("connect.stripe.com", link.Host);
        }
        finally
        {
            await new AccountService(client).DeleteAsync(accountId, cancellationToken: Ct);
        }
    }

    private static (StripeClient Client, StripePaymentProvider Provider) Create()
    {
        var key = Environment.GetEnvironmentVariable(SecretKeyVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), $"Set {SecretKeyVariable} (sk_test_…) to run the Stripe test-mode suite.");
        if (!key.StartsWith("sk_test_", StringComparison.Ordinal))
        {
            Assert.Fail($"{SecretKeyVariable} must be a test-mode key (sk_test_…). Refusing to run against live Stripe.");
        }

        var client = new StripeClient(key);
        return (client, new StripePaymentProvider(client, Options.Create(new StripeOptions()), NullLogger<StripePaymentProvider>.Instance));
    }

    private static string RequireConnectedAccount()
    {
        var account = Environment.GetEnvironmentVariable(ConnectedAccountVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(account), $"Set {ConnectedAccountVariable} to an onboarded test Express account (acct_…) to run payment tests.");
        return account;
    }

    private static AuthorizationRequest Request(string destination) => new(
        AmountMinor: 500,
        Currency: "usd",
        PlatformFeeMinor: 50,
        DestinationAccountId: destination,
        Description: "Stamp integration test",
        Metadata: new Dictionary<string, string> { ["source"] = "stamp-integration-tests" },
        IdempotencyKey: $"stamp-test-authorize-{Guid.NewGuid():N}");

    /// <summary>What Stripe.js does in the browser when the sender enters a card.</summary>
    private static Task AuthorizeWithTestCardAsync(StripeClient client, string paymentIntentId) =>
        new PaymentIntentService(client).ConfirmAsync(
            paymentIntentId, new PaymentIntentConfirmOptions { PaymentMethod = "pm_card_visa" }, cancellationToken: Ct);
}
