namespace Stamp.Domain.Common;

public static class DomainErrorCodes
{
    public const string InvalidEmail = "email.invalid";

    public const string InvalidHandle = "handle.invalid";
    public const string ReservedHandle = "handle.reserved";

    public const string InvalidDisplayName = "profile.display_name_invalid";
    public const string InvalidBio = "profile.bio_invalid";
    public const string InvalidPhotoUrl = "profile.photo_url_invalid";
    public const string PriceOutOfRange = "profile.price_out_of_range";
    public const string PayoutAccountAlreadyAttached = "profile.payout_account_already_attached";

    public const string InvalidSenderName = "message.sender_name_invalid";
    public const string InvalidSubject = "message.subject_invalid";
    public const string InvalidBody = "message.body_invalid";
    public const string NotPending = "message.not_pending";
    public const string NotAwaitingPayment = "message.not_awaiting_payment";
    public const string ReplyWindowClosed = "message.reply_window_closed";
    public const string ReplyTooShort = "message.reply_too_short";
    public const string ReplyTooLong = "message.reply_too_long";
    public const string NotExpiredYet = "message.not_expired_yet";

    public const string LoginTokenUnusable = "login.token_unusable";
}
