namespace Stamp.Infrastructure.Payments;

public enum PaymentProviderKind
{
    Stripe,

    /// <summary>In-memory stand-in for local development without Stripe keys. Refused in Production.</summary>
    Fake,
}

public sealed class PaymentsOptions
{
    public const string SectionName = "Payments";

    public PaymentProviderKind Provider { get; set; } = PaymentProviderKind.Stripe;
}

/// <summary>Bound from the "Stripe" section. Keys come from user-secrets or environment variables only.</summary>
public sealed class StripeOptions
{
    public const string SectionName = "Stripe";

    /// <summary>sk_test_… or sk_live_… (or a restricted rk_ key).</summary>
    public string SecretKey { get; set; } = string.Empty;

    /// <summary>pk_test_… or pk_live_…, used by Stripe.js in the browser.</summary>
    public string PublishableKey { get; set; } = string.Empty;

    /// <summary>whsec_… signing secret of the webhook endpoint (or the one `stripe listen` prints).</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>
    /// Signing secret of a separate endpoint for Connect events (account.updated) in production.
    /// Not needed locally: `stripe listen` signs both kinds with one secret.
    /// </summary>
    public string? ConnectWebhookSecret { get; set; }

    /// <summary>Two-letter country for new Express accounts. Empty means the platform's own country.</summary>
    public string? ConnectAccountCountry { get; set; }
}
