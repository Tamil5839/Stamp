using Stamp.Domain.Common;

namespace Stamp.Domain.Receivers;

public static class StampPricing
{
    public const long MinPriceCents = 200;
    public const long MaxPriceCents = 50_000;
    public const long DefaultPriceCents = 500;
    public const string DefaultCurrency = "usd";

    public static bool IsValidPrice(long priceCents) => priceCents is >= MinPriceCents and <= MaxPriceCents;

    public static void EnsureValidPrice(long priceCents)
    {
        if (!IsValidPrice(priceCents))
        {
            throw new DomainException(
                DomainErrorCodes.PriceOutOfRange,
                $"Stamp price must be between {Money.Format(MinPriceCents, DefaultCurrency)} and {Money.Format(MaxPriceCents, DefaultCurrency)}.",
                nameof(Receiver.StampPriceCents));
        }
    }

    /// <summary>The platform's cut of a stamp, in minor units, rounded half away from zero.</summary>
    public static long PlatformFee(long amountCents, decimal feePercent)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(amountCents);
        if (feePercent is < 0 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(feePercent), feePercent, "Fee must be between 0 and 100 percent.");
        }

        return (long)Math.Round(amountCents * feePercent / 100m, MidpointRounding.AwayFromZero);
    }
}
