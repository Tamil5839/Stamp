using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Stamp.Domain.Common;

/// <summary>
/// Email addresses are stored trimmed and lower-cased so that block lists, rate limits and
/// logins compare them reliably.
/// </summary>
public static partial class EmailAddress
{
    public const int MaxLength = 254;

    [GeneratedRegex(@"^[^@\s]+@[^@\s]+\.[^@\s]+$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public static bool IsValid([NotNullWhen(true)] string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var trimmed = email.Trim();
        return trimmed.Length <= MaxLength && Pattern().IsMatch(trimmed);
    }

    /// <summary>Validates and normalizes, or throws <see cref="DomainException"/>.</summary>
    public static string NormalizeValid(string? email, string? target = null)
    {
        if (!IsValid(email))
        {
            throw new DomainException(DomainErrorCodes.InvalidEmail, "Enter a valid email address.", target);
        }

        return Normalize(email);
    }
}
