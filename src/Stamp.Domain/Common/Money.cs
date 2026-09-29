using System.Globalization;

namespace Stamp.Domain.Common;

/// <summary>
/// Amounts are always integer minor units (cents). Only two-decimal currencies are supported,
/// which covers USD today and INR later.
/// </summary>
public static class Money
{
    public static string Format(long amountMinor, string currency)
    {
        var amount = amountMinor / 100m;
        var number = amount % 1 == 0
            ? amount.ToString("#,##0", CultureInfo.InvariantCulture)
            : amount.ToString("#,##0.00", CultureInfo.InvariantCulture);

        return currency.ToLowerInvariant() switch
        {
            "usd" => "$" + number,
            "inr" => "₹" + number,
            _ => $"{number} {currency.ToUpperInvariant()}",
        };
    }
}
