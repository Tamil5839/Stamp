using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Emails;

namespace Stamp.Infrastructure.Email;

/// <summary>
/// Sends queued emails that are due. Each email is leased (with optimistic concurrency) before it
/// is sent, so several app instances can dispatch at once without sending it twice; a failure
/// backs off and retries, and the provider-level idempotency key covers a crash mid-send.
/// </summary>
public sealed class OutboxProcessor(
    IStampDbContext db,
    IEmailSender sender,
    TimeProvider time,
    IOptions<EmailOptions> options,
    ILogger<OutboxProcessor> logger)
{
    private const int BatchSize = 50;

    /// <returns>The number of emails sent.</returns>
    public async Task<int> ProcessDueAsync(CancellationToken cancellationToken)
    {
        var settings = options.Value.Outbox;
        var now = time.GetUtcNow();

        var due = await db.OutboxEmails
            .Where(e => e.SentAt == null && e.FailedAt == null && e.NextAttemptAt <= now)
            .OrderBy(e => e.NextAttemptAt)
            .Select(e => e.Id)
            .Take(BatchSize)
            .ToListAsync(cancellationToken);

        var sent = 0;
        foreach (var id in due)
        {
            db.ChangeTracker.Clear();
            var email = await db.OutboxEmails.SingleAsync(e => e.Id == id, cancellationToken);
            if (email.SentAt is not null || email.FailedAt is not null || email.NextAttemptAt > time.GetUtcNow())
            {
                continue;
            }

            email.Lease(time.GetUtcNow(), settings.LeaseDuration);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateConcurrencyException)
            {
                continue; // another dispatcher took it
            }

            try
            {
                await sender.SendAsync(email.ToEmailMessage(), email.Id.ToString("N"), cancellationToken);
                email.MarkSent(time.GetUtcNow());
                sent++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                email.MarkFailed(ex.Message, time.GetUtcNow(), settings.MaxAttempts);
                if (email.FailedAt is null)
                {
                    logger.LogWarning(ex, "Email {EmailId} ({Template}) failed on attempt {Attempt}; retrying at {NextAttemptAt}.", email.Id, email.Template, email.Attempts, email.NextAttemptAt);
                }
                else
                {
                    logger.LogError(ex, "Giving up on email {EmailId} ({Template}) after {Attempts} attempts.", email.Id, email.Template, email.Attempts);
                }
            }

            await db.SaveChangesAsync(cancellationToken);
        }

        return sent;
    }
}
