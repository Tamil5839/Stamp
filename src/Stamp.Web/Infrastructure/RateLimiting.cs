using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;

namespace Stamp.Web.Infrastructure;

public sealed class RateLimitOptions
{
    public const string SectionName = "RateLimits";

    public TimeSpan Window { get; set; } = TimeSpan.FromMinutes(10);

    /// <summary>Stamp submissions per client IP per window (the per-sender-email limit is in Stamps:SenderRateLimit).</summary>
    public int SendStampPerIp { get; set; } = 10;

    /// <summary>Sign-in link requests per client IP per window.</summary>
    public int LoginPerIp { get; set; } = 10;
}

public static class RateLimitPolicies
{
    public const string SendStamp = "send-stamp";
    public const string Login = "login";

    public static IServiceCollection AddStampRateLimiting(this IServiceCollection services, IConfiguration configuration)
    {
        var limits = configuration.GetSection(RateLimitOptions.SectionName).Get<RateLimitOptions>() ?? new RateLimitOptions();

        return services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            options.AddPolicy(SendStamp, context => PerIp(context, limits.SendStampPerIp, limits.Window));

            // GETs of the sign-in page are free; only submitting the form counts.
            options.AddPolicy(Login, context => HttpMethods.IsPost(context.Request.Method)
                ? PerIp(context, limits.LoginPerIp, limits.Window)
                : RateLimitPartition.GetNoLimiter("login-page"));

            options.OnRejected = async (rejection, cancellationToken) =>
            {
                var response = rejection.HttpContext.Response;
                if (rejection.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    response.Headers.RetryAfter = ((int)retryAfter.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                const string message = "Too many attempts from your network. Please wait a few minutes and try again.";
                if (rejection.HttpContext.Request.HasJsonContentType())
                {
                    await response.WriteAsJsonAsync(new { error = "rate_limited", message }, cancellationToken);
                }
                else
                {
                    await response.WriteAsync(message, cancellationToken);
                }
            };
        });
    }

    /// <summary>
    /// Partitions by client IP. Behind a reverse proxy, enable forwarded headers (see README) or every
    /// request will look like it comes from the proxy.
    /// </summary>
    private static RateLimitPartition<string> PerIp(HttpContext context, int permits, TimeSpan window) =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = window, QueueLimit = 0 });
}
