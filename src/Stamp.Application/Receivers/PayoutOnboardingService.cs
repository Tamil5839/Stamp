using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Payments;

namespace Stamp.Application.Receivers;

/// <summary>
/// Connects a receiver to the payment provider (a Stripe Express account) through the provider's
/// hosted onboarding. The public page takes stamps only once the provider reports the account ready.
/// </summary>
public sealed class PayoutOnboardingService(
    IStampDbContext db,
    IPaymentProvider payments,
    IAppUrls urls,
    TimeProvider time,
    ILogger<PayoutOnboardingService> logger)
{
    private static readonly Error ProviderUnavailable = new(
        ErrorCodes.PaymentUnavailable, "We couldn't reach our payment provider. Please try again in a moment.");

    /// <summary>Creates the connected account on first use, then returns a fresh onboarding link.</summary>
    public async Task<Result<Uri>> StartAsync(Guid receiverId, CancellationToken cancellationToken)
    {
        var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.Id == receiverId, cancellationToken);
        if (receiver is null)
        {
            return Error.NotFound();
        }

        try
        {
            if (receiver.PayoutAccountId is null)
            {
                // The idempotency key makes a retry after a crash return the same account.
                var accountId = await payments.CreatePayoutAccountAsync(
                    receiver.Email, $"payout-account-{receiver.Id}", cancellationToken);
                receiver.AttachPayoutAccount(accountId, time.GetUtcNow());
                await db.SaveChangesAsync(cancellationToken);
            }

            return await payments.CreateOnboardingLinkAsync(
                receiver.PayoutAccountId!,
                new Uri(urls.PayoutsReturn()),
                new Uri(urls.PayoutsRefresh()),
                cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            logger.LogError(ex, "Couldn't start payout onboarding for receiver {ReceiverId}.", receiverId);
            return ProviderUnavailable;
        }
    }

    /// <summary>Re-reads the account from the provider (on return from onboarding, or on demand).</summary>
    public async Task<Result<PayoutAccountStatus?>> RefreshAsync(Guid receiverId, CancellationToken cancellationToken)
    {
        var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.Id == receiverId, cancellationToken);
        if (receiver is null)
        {
            return Error.NotFound();
        }

        if (receiver.PayoutAccountId is null)
        {
            return Result<PayoutAccountStatus?>.Success(null);
        }

        PayoutAccountStatus status;
        try
        {
            status = await payments.GetPayoutAccountStatusAsync(receiver.PayoutAccountId, cancellationToken);
        }
        catch (PaymentProviderException ex)
        {
            logger.LogError(ex, "Couldn't refresh payout account {AccountId}.", receiver.PayoutAccountId);
            return ProviderUnavailable;
        }

        receiver.SetPayoutsEnabled(status.IsReady, time.GetUtcNow());
        await db.SaveChangesAsync(cancellationToken);
        return status;
    }
}
