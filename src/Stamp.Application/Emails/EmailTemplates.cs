using System.Net;
using System.Text;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Emails;

public sealed record RenderedEmail(string Template, EmailMessage Message);

public static class EmailTemplateNames
{
    public const string MagicLink = "auth.magic_link";
    public const string StampReceived = "stamp.received";
    public const string StampSent = "stamp.sent";
    public const string StampReplied = "stamp.replied";
    public const string StampDeclined = "stamp.declined";
    public const string StampExpired = "stamp.expired";
}

/// <summary>
/// Plain-text and HTML bodies for every email Stamp sends. Anything a user typed is HTML-encoded.
/// Emails to senders never reveal the receiver's email address.
/// </summary>
public sealed class EmailTemplates(IAppUrls urls, IOptions<StampOptions> options)
{
    private string Window => TimeText.Duration(options.Value.ReplyWindow);

    public RenderedEmail MagicLink(string email, string link, TimeSpan lifetime)
    {
        var validity = $"This link works once and expires in {TimeText.Duration(lifetime)}. If you didn't ask for it, you can ignore this email.";

        var text = $"""
            Sign in to Stamp:
            {link}

            {validity}
            """;

        var html = Layout(
            Paragraph("Click the button below to sign in to Stamp.")
            + Button(link, "Sign in to Stamp")
            + Muted(validity));

        return new(EmailTemplateNames.MagicLink, new EmailMessage(email, "Your Stamp sign-in link", text, html));
    }

    public RenderedEmail StampReceived(Receiver receiver, StampedMessage message)
    {
        var price = Price(message);
        var earnings = Money.Format(message.ReceiverEarningsCents, message.Currency);
        var deadline = TimeText.Moment(message.ExpiresAt!.Value);
        var link = urls.InboxMessage(message.Id);
        var rule = $"Reply by {deadline} and you'll receive {earnings}. If you decline or don't reply in time, {message.SenderName} isn't charged.";

        var text = $"""
            {message.SenderName} paid a {price} stamp to reach you.

            Subject: {message.Subject}

            {message.Body}

            {rule}

            Reply in your inbox: {link}
            """;

        var html = Layout(
            Paragraph($"{message.SenderName} paid a {price} stamp to reach you.")
            + Quote(message.Subject, message.Body)
            + Paragraph(rule)
            + Button(link, "Open in your inbox"));

        return new(
            EmailTemplateNames.StampReceived,
            new EmailMessage(receiver.Email, $"New stamped message from {message.SenderName}, expires in {Window}", text, html));
    }

    public RenderedEmail StampSent(Receiver receiver, StampedMessage message)
    {
        var price = Price(message);
        var deadline = TimeText.Moment(message.ExpiresAt!.Value);
        var hold = $"We've placed a {price} hold on your card. You're only charged if {receiver.DisplayName} replies by {deadline}. If they decline or don't reply in time, the hold is released and you pay nothing.";

        var text = $"""
            Hi {message.SenderName},

            Your message "{message.Subject}" was delivered to {receiver.DisplayName}.

            {hold}

            Stamp
            """;

        var html = Layout(
            Paragraph($"Hi {message.SenderName},")
            + Paragraph($"Your message was delivered to {receiver.DisplayName}.")
            + Quote(message.Subject, message.Body)
            + Paragraph(hold));

        return new(
            EmailTemplateNames.StampSent,
            new EmailMessage(message.SenderEmail, $"Your message to {receiver.DisplayName} is on its way", text, html));
    }

    public RenderedEmail StampReplied(Receiver receiver, StampedMessage message)
    {
        var charged = $"Because {receiver.DisplayName} replied within {Window}, your {Price(message)} stamp has been charged.";
        var again = urls.PublicPage(receiver.Handle!);

        var text = $"""
            {receiver.DisplayName} replied to your message "{message.Subject}":

            {message.ReplyBody}

            {charged}

            Want to write again? {again}
            """;

        var html = Layout(
            Paragraph($"{receiver.DisplayName} replied to your message “{message.Subject}”:")
            + Quote(null, message.ReplyBody!)
            + Muted(charged)
            + Muted($"Want to write again? Send another stamp at {again}"));

        return new(
            EmailTemplateNames.StampReplied,
            new EmailMessage(message.SenderEmail, $"{receiver.DisplayName} replied: {message.Subject}", text, html));
    }

    public RenderedEmail StampDeclined(Receiver receiver, StampedMessage message)
    {
        var body = $"{receiver.DisplayName} declined your message “{message.Subject}”. The {Price(message)} hold on your card has been released, so you weren't charged.";

        return new(
            EmailTemplateNames.StampDeclined,
            new EmailMessage(
                message.SenderEmail,
                $"{receiver.DisplayName} passed on your message. You weren't charged.",
                body,
                Layout(Paragraph(body))));
    }

    public RenderedEmail StampExpired(Receiver receiver, StampedMessage message)
    {
        var body = $"{receiver.DisplayName} didn't reply to “{message.Subject}” within {Window}, so the {Price(message)} hold on your card has been released. You weren't charged.";

        return new(
            EmailTemplateNames.StampExpired,
            new EmailMessage(
                message.SenderEmail,
                $"No reply from {receiver.DisplayName}. You weren't charged.",
                body,
                Layout(Paragraph(body))));
    }

    private static string Price(StampedMessage message) => Money.Format(message.AmountCents, message.Currency);

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    private static string Layout(string content) =>
        """<div style="font-family:system-ui,-apple-system,'Segoe UI',Roboto,sans-serif;max-width:560px;margin:0 auto;padding:24px;color:#1d1b16;line-height:1.5">"""
        + """<p style="font-weight:700;color:#b4441f;letter-spacing:.04em;margin:0 0 20px">STAMP</p>"""
        + content
        + "</div>";

    private static string Paragraph(string text) => $"""<p style="margin:0 0 16px">{Encode(text)}</p>""";

    private static string Muted(string text) => $"""<p style="margin:0 0 12px;color:#6b6558;font-size:14px">{Encode(text)}</p>""";

    private static string Button(string url, string label) =>
        $"""<p style="margin:24px 0"><a href="{Encode(url)}" style="display:inline-block;background:#b4441f;color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;font-weight:600">{Encode(label)}</a></p>""";

    private static string Quote(string? subject, string body)
    {
        var html = new StringBuilder("""<div style="border-left:3px solid #e2d9c3;padding:4px 0 4px 16px;margin:0 0 20px">""");
        if (subject is not null)
        {
            html.Append($"""<p style="margin:0 0 8px;font-weight:600">{Encode(subject)}</p>""");
        }

        html.Append($"""<p style="margin:0">{Encode(body).Replace("\n", "<br>", StringComparison.Ordinal)}</p>""");
        return html.Append("</div>").ToString();
    }
}

/// <summary>Queues emails in the current unit of work; they're sent only if the save commits.</summary>
public sealed class EmailOutbox(IStampDbContext db, TimeProvider time)
{
    public void Enqueue(RenderedEmail email, Guid? messageId = null) =>
        db.OutboxEmails.Add(OutboxEmail.Create(email.Message, email.Template, messageId, time.GetUtcNow()));
}
