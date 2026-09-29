namespace Stamp.Application.Emails;

public sealed record EmailMessage(string To, string Subject, string TextBody, string HtmlBody);
