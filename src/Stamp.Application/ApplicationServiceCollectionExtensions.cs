using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Stamp.Application.Auth;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Application.Receivers;

namespace Stamp.Application;

public static class ApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the use cases. The host binds <see cref="StampOptions"/> and <see cref="AuthOptions"/>
    /// and provides the ports: IStampDbContext, IPaymentProvider (singleton), IAppUrls and IEmailSender.
    /// </summary>
    public static IServiceCollection AddStampApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);

        services.AddOptions<StampOptions>().ValidateOnStart();
        services.AddOptions<AuthOptions>();
        services.AddSingleton<IValidateOptions<StampOptions>, StampOptionsValidator>();

        services.AddScoped<EmailTemplates>();
        services.AddScoped<EmailOutbox>();
        services.AddScoped<StampTransitions>();
        services.AddScoped<PaymentSettlement>();
        services.AddScoped<PaymentWebhookHandler>();

        services.AddScoped<SendStampService>();
        services.AddScoped<InboxService>();
        services.AddScoped<StampExpiryService>();

        services.AddScoped<MagicLinkService>();
        services.AddScoped<ReceiverSettingsService>();
        services.AddScoped<PayoutOnboardingService>();
        services.AddScoped<BlockListService>();
        services.AddScoped<PublicProfileService>();

        return services;
    }
}
