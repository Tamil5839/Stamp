namespace Stamp.Application.Payments;

/// <summary>
/// Records a provider webhook event id once it has been applied, in the same transaction as its
/// effects, so a redelivered event is recognized and skipped.
/// </summary>
public sealed class ProcessedWebhookEvent
{
    private ProcessedWebhookEvent()
    {
    }

    public string EventId { get; private set; } = null!;

    public string EventType { get; private set; } = null!;

    public DateTimeOffset ProcessedAt { get; private set; }

    public static ProcessedWebhookEvent Create(string eventId, string eventType, DateTimeOffset now) => new()
    {
        EventId = eventId,
        EventType = eventType,
        ProcessedAt = now,
    };
}
