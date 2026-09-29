using Stamp.Domain.Common;

namespace Stamp.Domain.Receivers;

/// <summary>A person who owns a public page and receives stamped messages. Logs in by email.</summary>
public sealed class Receiver
{
    public const int MaxDisplayNameLength = 60;
    public const int MaxBioLength = 280;
    public const int MaxPhotoUrlLength = 500;

    private Receiver()
    {
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; } = null!;

    /// <summary>Null until the receiver picks one in settings.</summary>
    public string? Handle { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public string Bio { get; private set; } = string.Empty;

    public string? PhotoUrl { get; private set; }

    public long StampPriceCents { get; private set; }

    public string Currency { get; private set; } = StampPricing.DefaultCurrency;

    /// <summary>Stored only; charity payouts are not implemented yet.</summary>
    public bool DonateToCharity { get; private set; }

    /// <summary>The payment provider's connected account (a Stripe Express account id).</summary>
    public string? PayoutAccountId { get; private set; }

    /// <summary>True once the provider reports onboarding complete and charges enabled.</summary>
    public bool PayoutsEnabled { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasProfile => Handle is not null;

    /// <summary>The public page only takes new stamps once the profile and payouts are both set up.</summary>
    public bool IsAcceptingStamps => HasProfile && PayoutAccountId is not null && PayoutsEnabled;

    public static Receiver Register(string email, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Email = EmailAddress.NormalizeValid(email),
        StampPriceCents = StampPricing.DefaultPriceCents,
        Currency = StampPricing.DefaultCurrency,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void UpdateProfile(
        string handle,
        string displayName,
        string? bio,
        string? photoUrl,
        long stampPriceCents,
        bool donateToCharity,
        DateTimeOffset now)
    {
        var normalizedHandle = HandleRules.NormalizeValid(handle);

        var trimmedName = displayName?.Trim() ?? string.Empty;
        if (trimmedName.Length is 0 or > MaxDisplayNameLength)
        {
            throw new DomainException(
                DomainErrorCodes.InvalidDisplayName,
                $"Display name is required and can be up to {MaxDisplayNameLength} characters.",
                nameof(DisplayName));
        }

        var trimmedBio = bio?.Trim() ?? string.Empty;
        if (trimmedBio.Length > MaxBioLength)
        {
            throw new DomainException(
                DomainErrorCodes.InvalidBio, $"Bio can be up to {MaxBioLength} characters.", nameof(Bio));
        }

        var normalizedPhotoUrl = NormalizePhotoUrl(photoUrl);
        StampPricing.EnsureValidPrice(stampPriceCents);

        Handle = normalizedHandle;
        DisplayName = trimmedName;
        Bio = trimmedBio;
        PhotoUrl = normalizedPhotoUrl;
        StampPriceCents = stampPriceCents;
        DonateToCharity = donateToCharity;
        UpdatedAt = now;
    }

    public void AttachPayoutAccount(string accountId, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);

        if (PayoutAccountId == accountId)
        {
            return;
        }

        if (PayoutAccountId is not null)
        {
            throw new DomainException(
                DomainErrorCodes.PayoutAccountAlreadyAttached, "A payout account is already connected.");
        }

        PayoutAccountId = accountId;
        UpdatedAt = now;
    }

    public void SetPayoutsEnabled(bool enabled, DateTimeOffset now)
    {
        if (PayoutsEnabled == enabled)
        {
            return;
        }

        PayoutsEnabled = enabled;
        UpdatedAt = now;
    }

    private static string? NormalizePhotoUrl(string? photoUrl)
    {
        var trimmed = photoUrl?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        if (trimmed.Length > MaxPhotoUrlLength
            || !Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new DomainException(
                DomainErrorCodes.InvalidPhotoUrl, "Photo URL must be an https:// link.", nameof(PhotoUrl));
        }

        return uri.AbsoluteUri;
    }
}
