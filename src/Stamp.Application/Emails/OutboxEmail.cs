namespace Stamp.Application.Emails;

/// <summary>
/// An email waiting to be sent. Rows are written in the same transaction as the state change that
/// caused them, and a background dispatcher delivers them with retries, so an email is never lost
/// because the provider was briefly down (or sent for a change that was rolled back).
/// </summary>
public sealed class OutboxEmail
{
    public const int MaxErrorLength = 2_000;

    private OutboxEmail()
    {
    }

    public Guid Id { get; private set; }

    /// <summary>Which template produced this email, e.g. "stamp.replied". Useful for support and tests.</summary>
    public string Template { get; private set; } = null!;

    /// <summary>The stamped message this email is about, if any.</summary>
    public Guid? MessageId { get; private set; }

    public string To { get; private set; } = null!;

    public string Subject { get; private set; } = null!;

    public string TextBody { get; private set; } = null!;

    public string HtmlBody { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public int Attempts { get; private set; }

    public DateTimeOffset? SentAt { get; private set; }

    /// <summary>Set when the dispatcher gave up after too many failures.</summary>
    public DateTimeOffset? FailedAt { get; private set; }

    public string? LastError { get; private set; }

    public static OutboxEmail Create(EmailMessage email, string template, Guid? messageId, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        Template = template,
        MessageId = messageId,
        To = email.To,
        Subject = email.Subject,
        TextBody = email.TextBody,
        HtmlBody = email.HtmlBody,
        CreatedAt = now,
        NextAttemptAt = now,
    };

    public EmailMessage ToEmailMessage() => new(To, Subject, TextBody, HtmlBody);

    /// <summary>Claims the email for one send attempt so other dispatchers skip it until the lease ends.</summary>
    public void Lease(DateTimeOffset now, TimeSpan duration) => NextAttemptAt = now + duration;

    public void MarkSent(DateTimeOffset now)
    {
        SentAt = now;
        LastError = null;
    }

    public void MarkFailed(string error, DateTimeOffset now, int maxAttempts)
    {
        Attempts++;
        LastError = error.Length > MaxErrorLength ? error[..MaxErrorLength] : error;

        if (Attempts >= maxAttempts)
        {
            FailedAt = now;
            return;
        }

        // 30s, 1m, 2m, 4m ... capped at 1h.
        var backoff = TimeSpan.FromSeconds(Math.Min(30 * Math.Pow(2, Attempts - 1), 3_600));
        NextAttemptAt = now + backoff;
    }
}
