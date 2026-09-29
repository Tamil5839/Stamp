using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Stamp.Application.Emails;

namespace Stamp.Infrastructure.Email;

/// <summary>
/// Sends through Resend's REST API (POST /emails). The outbox row id is passed as Resend's
/// Idempotency-Key, so a retry after a timeout can't deliver the same email twice.
/// </summary>
public sealed class ResendEmailSender(HttpClient http, IOptions<EmailOptions> options) : IEmailSender
{
    public static readonly Uri BaseAddress = new("https://api.resend.com/");

    public async Task SendAsync(EmailMessage message, string idempotencyKey, CancellationToken cancellationToken)
    {
        var settings = options.Value;

        using var request = new HttpRequestMessage(HttpMethod.Post, "emails")
        {
            Content = JsonContent.Create(new ResendEmail(settings.From, [message.To], message.Subject, message.HtmlBody, message.TextBody)),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", settings.ResendApiKey);
        request.Headers.Add("Idempotency-Key", idempotencyKey);

        using var response = await http.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new EmailDeliveryException(
                $"Resend returned {(int)response.StatusCode} {response.ReasonPhrase}: {(body.Length > 500 ? body[..500] : body)}");
        }
    }

    private sealed record ResendEmail(
        [property: JsonPropertyName("from")] string From,
        [property: JsonPropertyName("to")] string[] To,
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("html")] string Html,
        [property: JsonPropertyName("text")] string Text);
}

public sealed class EmailDeliveryException(string message) : Exception(message);
