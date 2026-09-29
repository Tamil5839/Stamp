using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Stamp.IntegrationTests.Stripe;

/// <summary>Builds webhook payloads and Stripe-Signature headers the way Stripe does.</summary>
public static class StripeSignatures
{
    public static string Header(string payload, string secret, DateTimeOffset timestamp)
    {
        var t = timestamp.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes($"{t}.{payload}"));
        return $"t={t},v1={Convert.ToHexStringLower(mac)}";
    }

    public static string PaymentIntentEvent(string eventId, string type, string paymentIntentId, string status, DateTimeOffset created) => $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2019-02-19",
          "created": {{created.ToUnixTimeSeconds()}},
          "livemode": false,
          "pending_webhooks": 1,
          "type": "{{type}}",
          "data": {
            "object": {
              "id": "{{paymentIntentId}}",
              "object": "payment_intent",
              "amount": 500,
              "amount_capturable": {{(status == "requires_capture" ? 500 : 0)}},
              "capture_method": "manual",
              "currency": "usd",
              "status": "{{status}}"
            }
          }
        }
        """;

    public static string AccountUpdatedEvent(string eventId, string accountId, bool detailsSubmitted, bool chargesEnabled, DateTimeOffset created) => $$"""
        {
          "id": "{{eventId}}",
          "object": "event",
          "api_version": "2019-02-19",
          "created": {{created.ToUnixTimeSeconds()}},
          "livemode": false,
          "type": "account.updated",
          "account": "{{accountId}}",
          "data": {
            "object": {
              "id": "{{accountId}}",
              "object": "account",
              "type": "express",
              "details_submitted": {{(detailsSubmitted ? "true" : "false")}},
              "charges_enabled": {{(chargesEnabled ? "true" : "false")}},
              "payouts_enabled": false
            }
          }
        }
        """;
}
