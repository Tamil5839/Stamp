using System.Globalization;
using System.Security.Claims;
using System.Text.RegularExpressions;

namespace Stamp.Web.Infrastructure;

public static class ClaimsPrincipalExtensions
{
    public static Guid ReceiverId(this ClaimsPrincipal user) =>
        Guid.TryParse(user.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
            ? id
            : throw new InvalidOperationException("The signed-in user has no receiver id.");
}

/// <summary>Matches the shape of a handle in /{handle}; the page itself decides whether it exists.</summary>
public sealed partial class HandleRouteConstraint : IRouteConstraint
{
    public const string Name = "handle";

    [GeneratedRegex("^[A-Za-z0-9_]{3,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public bool Match(HttpContext? httpContext, IRouter? route, string routeKey, RouteValueDictionary values, RouteDirection routeDirection) =>
        values.TryGetValue(routeKey, out var value)
        && value is string handle
        && Shape().IsMatch(handle);
}

public static class Countdown
{
    /// <summary>"5d 3h left", "3h 12m left", "12m left", or "Expiring".</summary>
    public static string Format(DateTimeOffset expiresAt, DateTimeOffset now)
    {
        var left = expiresAt - now;
        if (left <= TimeSpan.Zero)
        {
            return "Expiring";
        }

        var text = left.TotalDays >= 1
            ? $"{(int)left.TotalDays}d {left.Hours}h"
            : left.TotalHours >= 1
                ? $"{(int)left.TotalHours}h {left.Minutes}m"
                : $"{Math.Max(1, (int)Math.Ceiling(left.TotalMinutes))}m";
        return text + " left";
    }

    public static bool IsUrgent(DateTimeOffset expiresAt, DateTimeOffset now) => expiresAt - now < TimeSpan.FromHours(24);

    public static string Iso(DateTimeOffset moment) =>
        moment.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
