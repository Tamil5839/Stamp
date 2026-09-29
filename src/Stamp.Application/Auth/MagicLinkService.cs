using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Domain.Auth;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Auth;

public sealed record SignedInReceiver(Guid ReceiverId, string Email, bool HasProfile);

/// <summary>Passwordless sign-in: email a single-use link, then redeem it for a session.</summary>
public sealed class MagicLinkService(
    IStampDbContext db,
    EmailTemplates templates,
    EmailOutbox outbox,
    IAppUrls urls,
    IOptions<AuthOptions> options,
    TimeProvider time,
    ILogger<MagicLinkService> logger)
{
    private const int MaxTokenLength = 128;
    private const int MaxAttempts = 3;

    private static readonly Error LinkInvalid = new(
        ErrorCodes.LoginLinkInvalid, "This sign-in link has expired or was already used. Request a new one.");

    /// <summary>
    /// Emails a sign-in link. Succeeds for any well-formed address (known or not, throttled or
    /// not) so the form doesn't reveal who has an account.
    /// </summary>
    public async Task<Result> RequestLinkAsync(string email, CancellationToken cancellationToken)
    {
        if (!EmailAddress.IsValid(email))
        {
            return new Error(DomainErrorCodes.InvalidEmail, "Enter a valid email address.", "Email");
        }

        var normalized = EmailAddress.Normalize(email);
        var now = time.GetUtcNow();
        var settings = options.Value;

        var since = now - TimeSpan.FromHours(1);
        var recent = await db.LoginTokens.CountAsync(t => t.Email == normalized && t.CreatedAt > since, cancellationToken);
        if (recent >= settings.MagicLinksPerEmailPerHour)
        {
            logger.LogWarning("Sign-in link limit reached for an address; not sending another.");
            return Result.Success();
        }

        var (token, rawToken) = LoginToken.Issue(normalized, now, settings.MagicLinkLifetime);
        db.LoginTokens.Add(token);
        outbox.Enqueue(templates.MagicLink(normalized, urls.MagicLink(rawToken), settings.MagicLinkLifetime));
        await db.SaveChangesAsync(cancellationToken);

        return Result.Success();
    }

    /// <summary>For the confirmation page: is this link still good? Doesn't use it up.</summary>
    public async Task<bool> IsUsableAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (!IsPlausible(rawToken))
        {
            return false;
        }

        var hash = LoginToken.Hash(rawToken);
        var token = await db.LoginTokens.AsNoTracking().SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
        return token?.IsUsable(time.GetUtcNow()) == true;
    }

    /// <summary>Uses up the link and returns the receiver, registering them on first sign-in.</summary>
    public async Task<Result<SignedInReceiver>> RedeemAsync(string? rawToken, CancellationToken cancellationToken)
    {
        if (!IsPlausible(rawToken))
        {
            return LinkInvalid;
        }

        var hash = LoginToken.Hash(rawToken);

        for (var attempt = 1; ; attempt++)
        {
            db.ChangeTracker.Clear();
            var now = time.GetUtcNow();

            var token = await db.LoginTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, cancellationToken);
            if (token is null || !token.IsUsable(now))
            {
                return LinkInvalid;
            }

            token.Consume(now);

            var receiver = await db.Receivers.SingleOrDefaultAsync(r => r.Email == token.Email, cancellationToken);
            if (receiver is null)
            {
                receiver = Receiver.Register(token.Email, now);
                db.Receivers.Add(receiver);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken);
                return new SignedInReceiver(receiver.Id, receiver.Email, receiver.HasProfile);
            }
            catch (DbUpdateConcurrencyException)
            {
                // Someone redeemed the same link a moment earlier.
                return LinkInvalid;
            }
            catch (DuplicateKeyException) when (attempt < MaxAttempts)
            {
                // Two different links for the same new address were redeemed at once and the other
                // one registered the receiver; retry to sign in to that account.
            }
        }
    }

    private static bool IsPlausible([System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? rawToken) =>
        !string.IsNullOrWhiteSpace(rawToken) && rawToken.Length <= MaxTokenLength;
}
