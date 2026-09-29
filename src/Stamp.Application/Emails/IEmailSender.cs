namespace Stamp.Application.Emails;

public interface IEmailSender
{
    /// <summary>
    /// Sends one email. <paramref name="idempotencyKey"/> is stable across retries of the same
    /// outbox row, so providers that support it drop duplicates.
    /// </summary>
    Task SendAsync(EmailMessage message, string idempotencyKey, CancellationToken cancellationToken);
}
