namespace Stamp.Domain.Messages;

/// <summary>Where a stamped message is in its lifecycle, from the receiver's point of view.</summary>
public enum MessageStatus
{
    /// <summary>Saved when the sender submits; the card is not authorized yet. Invisible to the receiver.</summary>
    AwaitingPayment,

    /// <summary>Card authorized (hold placed). The receiver has until <c>ExpiresAt</c> to reply.</summary>
    Pending,

    /// <summary>The receiver replied in time; the stamp is captured.</summary>
    Replied,

    /// <summary>The receiver declined; the hold is released.</summary>
    Declined,

    /// <summary>No reply in time; the hold is released.</summary>
    Expired,

    /// <summary>The sender never completed payment.</summary>
    Abandoned,
}
