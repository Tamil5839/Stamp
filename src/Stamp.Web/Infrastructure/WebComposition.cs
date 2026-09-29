using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Infrastructure.Payments;
using Stamp.Web.Endpoints;

namespace Stamp.Web.Infrastructure;

public static class WebComposition
{
    /// <summary>Environment name used by the integration tests.</summary>
    public const string TestingEnvironment = "Testing";

    public static IServiceCollection AddStampWeb(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddRazorPages(options =>
        {
            options.Conventions.AuthorizeFolder("/Inbox");
            options.Conventions.AuthorizeFolder("/Settings");
            options.Conventions.AuthorizeFolder("/Payouts");
        });

        services.Configure<RouteOptions>(options =>
        {
            options.ConstraintMap[HandleRouteConstraint.Name] = typeof(HandleRouteConstraint);
            options.LowercaseUrls = true;
        });

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(options =>
            {
                options.LoginPath = "/auth/login";
                options.LogoutPath = "/auth/logout";
                options.AccessDeniedPath = "/auth/login";
                options.Cookie.Name = "stamp.auth";
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Lax;
                options.Cookie.SecurePolicy = environment.IsDevelopment() || environment.IsEnvironment(TestingEnvironment)
                    ? CookieSecurePolicy.SameAsRequest
                    : CookieSecurePolicy.Always;
                options.ExpireTimeSpan = TimeSpan.FromDays(14);
                options.SlidingExpiration = true;
            });
        services.AddAuthorization();
        services.AddAntiforgery(options => options.HeaderName = "RequestVerificationToken");

        services.AddOptions<AppOptions>()
            .Bind(configuration.GetSection(AppOptions.SectionName))
            .Validate(o => Uri.TryCreate(o.BaseUrl, UriKind.Absolute, out _), "App:BaseUrl must be the site's absolute URL, e.g. https://stamp.example.com.")
            .ValidateOnStart();
        services.AddSingleton<AppUrls>();
        services.AddSingleton<IAppUrls>(sp => sp.GetRequiredService<AppUrls>());

        // Options owned by the application layer are bound here, at the composition root.
        services.Configure<StampOptions>(configuration.GetSection(StampOptions.SectionName));
        services.Configure<AuthOptions>(configuration.GetSection(AuthOptions.SectionName));

        services.AddStampRateLimiting(configuration);
        return services;
    }

    public static WebApplication UseStampWeb(this WebApplication app)
    {
        if (!app.Environment.IsDevelopment())
        {
            app.UseExceptionHandler("/Error");
            app.UseHsts();
        }

        app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");
        app.UseHttpsRedirection();

        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
            await next();
        });

        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();

        app.MapStaticAssets();
        app.MapRazorPages().WithStaticAssets();
        app.MapStampEndpoints();

        if (app.Services.GetRequiredService<IOptions<PaymentsOptions>>().Value.Provider == PaymentProviderKind.Fake)
        {
            app.MapDevelopmentPaymentEndpoints();
        }

        return app;
    }
}
