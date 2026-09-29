using Microsoft.Extensions.Options;
using Stamp.Application.Payments;

namespace Stamp.Application.Common;

public sealed class StampOptions
{
    public const string SectionName = "Stamps";

    /// <summary>How long a receiver has to reply once the sender's card is authorized.</summary>
    public TimeSpan ReplyWindow { get; set; } = TimeSpan.FromDays(6);

    /// <summary>The platform's cut of every captured stamp.</summary>
    public decimal PlatformFeePercent { get; set; } = 10m;

    /// <summary>Drafts whose card was never authorized are abandoned after this long.</summary>
    public TimeSpan AbandonUnpaidAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>How many stamps one sender email may submit per <see cref="SenderRateLimitWindow"/>, across all receivers.</summary>
    public int SenderRateLimit { get; set; } = 5;

    public TimeSpan SenderRateLimitWindow { get; set; } = TimeSpan.FromHours(1);
}

public sealed class AuthOptions
{
    public const string SectionName = "Auth";

    public TimeSpan MagicLinkLifetime { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Caps sign-in emails per address, so the form can't be used to flood someone's inbox.</summary>
    public int MagicLinksPerEmailPerHour { get; set; } = 5;
}

/// <summary>
/// The reply window has to close before the card hold lapses at the provider, or replies would
/// arrive after the money is gone. Stripe holds online card authorizations for 7 days.
/// </summary>
internal sealed class StampOptionsValidator(IPaymentProvider payments) : IValidateOptions<StampOptions>
{
    public ValidateOptionsResult Validate(string? name, StampOptions options)
    {
        var failures = new List<string>();

        if (options.ReplyWindow <= TimeSpan.Zero)
        {
            failures.Add("Stamps:ReplyWindow must be positive.");
        }
        else if (options.ReplyWindow >= payments.MaxAuthorizationHold)
        {
            failures.Add(
                $"Stamps:ReplyWindow ({options.ReplyWindow}) must be shorter than the {payments.Name} authorization hold ({payments.MaxAuthorizationHold}).");
        }

        if (options.PlatformFeePercent is < 0 or > 100)
        {
            failures.Add("Stamps:PlatformFeePercent must be between 0 and 100.");
        }

        if (options.AbandonUnpaidAfter <= TimeSpan.Zero)
        {
            failures.Add("Stamps:AbandonUnpaidAfter must be positive.");
        }

        if (options.SenderRateLimit < 1 || options.SenderRateLimitWindow <= TimeSpan.Zero)
        {
            failures.Add("Stamps:SenderRateLimit and SenderRateLimitWindow must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
