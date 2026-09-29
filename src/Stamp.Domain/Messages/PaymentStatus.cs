namespace Stamp.Domain.Messages;

/// <summary>
/// Mirrors the payment provider's state, separately from <see cref="MessageStatus"/>. It only ever
/// moves forward: None → Created → Authorized → Captured or Canceled.
/// </summary>
public enum PaymentStatus
{
    /// <summary>No provider payment exists yet.</summary>
    None,

    /// <summary>The provider payment exists but the card is not authorized yet.</summary>
    Created,

    /// <summary>The amount is on hold on the sender's card.</summary>
    Authorized,

    /// <summary>The hold was captured: the sender was charged.</summary>
    Captured,

    /// <summary>The hold was released (or the unconfirmed payment voided): the sender was not charged.</summary>
    Canceled,
}
