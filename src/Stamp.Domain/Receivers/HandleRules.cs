using System.Collections.Frozen;
using System.Text.RegularExpressions;
using Stamp.Domain.Common;

namespace Stamp.Domain.Receivers;

/// <summary>
/// Handles are the public page path (/{handle}), so they share the URL space with the app's
/// own routes. Anything the app routes to, or that could be mistaken for an official page, is reserved.
/// </summary>
public static partial class HandleRules
{
    public const int MinLength = 3;
    public const int MaxLength = 30;

    private static readonly FrozenSet<string> Reserved = new[]
    {
        "about", "account", "accounts", "admin", "api", "assets", "auth", "billing", "blocked", "css",
        "dashboard", "dev", "error", "favicon", "health", "healthz", "help", "home", "img", "images",
        "inbox", "index", "js", "legal", "lib", "login", "logout", "mail", "me", "messages", "new",
        "null", "payouts", "privacy", "register", "root", "security", "sent", "settings", "signin",
        "signout", "signup", "stamp", "stamps", "static", "status", "stripe", "support", "terms",
        "undefined", "webhooks", "www",
    }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex("^[a-z0-9_]{3,30}$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Normalize(string handle) => handle.Trim().TrimStart('@').ToLowerInvariant();

    public static bool IsReserved(string normalizedHandle) => Reserved.Contains(normalizedHandle);

    /// <summary>Validates and normalizes, or throws <see cref="DomainException"/>.</summary>
    public static string NormalizeValid(string? handle)
    {
        var normalized = Normalize(handle ?? string.Empty);

        if (!Pattern().IsMatch(normalized))
        {
            throw new DomainException(
                DomainErrorCodes.InvalidHandle,
                $"Handles are {MinLength}–{MaxLength} characters: lowercase letters, numbers and underscores.",
                nameof(Receiver.Handle));
        }

        if (IsReserved(normalized))
        {
            throw new DomainException(
                DomainErrorCodes.ReservedHandle, "That handle is reserved. Try another one.", nameof(Receiver.Handle));
        }

        return normalized;
    }
}
