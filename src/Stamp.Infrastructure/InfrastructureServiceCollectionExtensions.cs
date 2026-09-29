using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Stamp.Application.Emails;
using Stamp.Application.Payments;
using Stamp.Infrastructure.Email;
using Stamp.Infrastructure.Jobs;
using Stamp.Infrastructure.Payments;
using Stamp.Infrastructure.Payments.Fake;
using Stamp.Infrastructure.Payments.StripeConnect;
using Stamp.Infrastructure.Persistence;
using Stripe;

namespace Stamp.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    public static IServiceCollection AddStampInfrastructure(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddStampPersistence(configuration, environment.ContentRootPath);

        // Keeps sign-in cookies valid across restarts and instances.
        services.AddDataProtection()
            .SetApplicationName("Stamp")
            .PersistKeysToDbContext<StampDbContext>();

        AddPayments(services, configuration, environment);
        AddEmail(services, configuration, environment);

        services.AddOptions<ExpiryJobOptions>()
            .Bind(configuration.GetSection(ExpiryJobOptions.SectionName))
            .Validate(o => o.Interval > TimeSpan.Zero, "Jobs:Expiry:Interval must be positive.")
            .ValidateOnStart();
        services.AddSingleton<StampExpiryJob>();
        services.AddHostedService(sp => sp.GetRequiredService<StampExpiryJob>());

        return services;
    }

    private static void AddPayments(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var provider = configuration.GetSection(PaymentsOptions.SectionName).Get<PaymentsOptions>()?.Provider ?? PaymentProviderKind.Stripe;
        services.Configure<PaymentsOptions>(configuration.GetSection(PaymentsOptions.SectionName));

        var stripe = services.AddOptions<StripeOptions>().Bind(configuration.GetSection(StripeOptions.SectionName));

        if (provider == PaymentProviderKind.Fake)
        {
            if (environment.IsProduction())
            {
                throw new InvalidOperationException("Payments:Provider=Fake is for local development and can't run in Production.");
            }

            services.AddSingleton<DevelopmentPaymentProvider>();
            services.AddSingleton<IPaymentProvider>(sp => sp.GetRequiredService<DevelopmentPaymentProvider>());
        }
        else
        {
            stripe
                .Validate(o => o.SecretKey.StartsWith("sk_", StringComparison.Ordinal) || o.SecretKey.StartsWith("rk_", StringComparison.Ordinal),
                    "Stripe:SecretKey is missing or malformed (expected sk_… from the Stripe dashboard). Set it with user-secrets or Stripe__SecretKey.")
                .Validate(o => o.PublishableKey.StartsWith("pk_", StringComparison.Ordinal),
                    "Stripe:PublishableKey is missing or malformed (expected pk_…).")
                .Validate(o => o.WebhookSecret.StartsWith("whsec_", StringComparison.Ordinal),
                    "Stripe:WebhookSecret is missing or malformed (expected whsec_…, printed by `stripe listen`).")
                .ValidateOnStart();

            services.AddSingleton<IStripeClient>(sp => new StripeClient(sp.GetRequiredService<IOptions<StripeOptions>>().Value.SecretKey));
            services.AddSingleton<IPaymentProvider, StripePaymentProvider>();
        }

        // Stripe-format webhooks are accepted whenever a signing secret is configured (always, for the
        // Stripe provider); without one, every webhook is refused.
        services.AddSingleton<IPaymentWebhookParser>(sp =>
            string.IsNullOrWhiteSpace(sp.GetRequiredService<IOptions<StripeOptions>>().Value.WebhookSecret)
                ? new DisabledWebhookParser()
                : ActivatorUtilities.CreateInstance<StripeWebhookParser>(sp));
    }

    private static void AddEmail(IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection(EmailOptions.SectionName);
        var settings = section.Get<EmailOptions>() ?? new EmailOptions();
        services.AddOptions<EmailOptions>()
            .Bind(section)
            .Validate(o => !string.IsNullOrWhiteSpace(o.From), "Email:From is required.")
            .Validate(o => o.Provider != EmailProviderKind.Resend || !string.IsNullOrWhiteSpace(o.ResendApiKey),
                "Email:ResendApiKey is required when Email:Provider is Resend. Set it with user-secrets or Email__ResendApiKey.")
            .ValidateOnStart();

        switch (settings.Provider)
        {
            case EmailProviderKind.Resend:
                services.AddHttpClient<IEmailSender, ResendEmailSender>(client =>
                {
                    client.BaseAddress = ResendEmailSender.BaseAddress;
                    client.Timeout = TimeSpan.FromSeconds(20);
                });
                break;

            case EmailProviderKind.File:
                if (environment.IsProduction())
                {
                    throw new InvalidOperationException("Email:Provider=File only writes emails to disk; configure Resend for Production.");
                }

                services.AddSingleton<IEmailSender, FileEmailSender>();
                break;
        }

        services.AddSingleton<OutboxSignal>();
        services.AddScoped<IInterceptor, OutboxSignalInterceptor>();
        services.AddScoped<OutboxProcessor>();

        if (settings.Outbox.DispatcherEnabled)
        {
            services.AddHostedService<OutboxDispatcher>();
        }
    }
}
