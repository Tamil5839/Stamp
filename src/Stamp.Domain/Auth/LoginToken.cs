using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Stamp.Domain.Common;

namespace Stamp.Domain.Auth;

/// <summary>
/// A single-use magic sign-in link. Only a SHA-256 hash of the token is stored; the raw token
/// exists only in the email, so a database leak can't be used to sign in.
/// </summary>
public sealed class LoginToken
{
    public const int TokenBytes = 32;

    private LoginToken()
    {
    }

    public Guid Id { get; private set; }

    public string Email { get; private set; } = null!;

    public string TokenHash { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? ConsumedAt { get; private set; }

    public static (LoginToken Token, string RawToken) Issue(string email, DateTimeOffset now, TimeSpan lifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(lifetime, TimeSpan.Zero);

        var rawToken = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(TokenBytes));
        var token = new LoginToken
        {
            Id = Guid.CreateVersion7(now),
            Email = EmailAddress.NormalizeValid(email),
            TokenHash = Hash(rawToken),
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };

        return (token, rawToken);
    }

    public static string Hash(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));

    public bool IsUsable(DateTimeOffset now) => ConsumedAt is null && now < ExpiresAt;

    public void Consume(DateTimeOffset now)
    {
        if (!IsUsable(now))
        {
            throw new DomainException(
                DomainErrorCodes.LoginTokenUnusable, "This sign-in link has expired or was already used.");
        }

        ConsumedAt = now;
    }
}
