namespace Stamp.Domain.Messages;

/// <summary>What recording a provider-side cancellation did to the message.</summary>
public enum PaymentCanceledOutcome
{
    /// <summary>The cancellation was already recorded (or the payment was already captured).</summary>
    NoChange,

    /// <summary>Expected: the stamp was already closed without a reply.</summary>
    Recorded,

    /// <summary>The hold was released while the stamp was still pending, so the stamp expired.</summary>
    StampExpired,

    /// <summary>The payment was voided before the sender completed it.</summary>
    DraftAbandoned,

    /// <summary>The receiver replied but the hold was gone before it could be captured.</summary>
    ReplyUnpaid,
}
