using Stamp.Domain.Common;

namespace Stamp.Domain.Receivers;

/// <summary>A sender email a receiver no longer accepts stamps from.</summary>
public sealed class BlockedSender
{
    private BlockedSender()
    {
    }

    public Guid Id { get; private set; }

    public Guid ReceiverId { get; private set; }

    public string Email { get; private set; } = null!;

    public DateTimeOffset CreatedAt { get; private set; }

    public static BlockedSender Create(Guid receiverId, string email, DateTimeOffset now) => new()
    {
        Id = Guid.CreateVersion7(now),
        ReceiverId = receiverId,
        Email = EmailAddress.NormalizeValid(email),
        CreatedAt = now,
    };
}
