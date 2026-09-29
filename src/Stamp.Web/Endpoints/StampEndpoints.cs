using Microsoft.AspNetCore.Antiforgery;
using Stamp.Application.Common;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Infrastructure.Payments.Fake;
using Stamp.Web.Infrastructure;

namespace Stamp.Web.Endpoints;

public sealed record SendStampRequest(string? SenderName, string? SenderEmail, string? Subject, string? Body);

public sealed record SendStampResponse(Guid MessageId, string PaymentId, string ClientSecret, string ReturnUrl);

public static class StampEndpoints
{
    public static IEndpointRouteBuilder MapStampEndpoints(this IEndpointRouteBuilder app)
    {
        // Called by the public page's script: saves the message and creates the uncaptured payment,
        // then the browser authorizes the card with the returned client secret.
        app.MapPost($"/{{handle:{HandleRouteConstraint.Name}}}/stamp", SendStampAsync)
            .RequireRateLimiting(RateLimitPolicies.SendStamp)
            .DisableAntiforgery(); // validated explicitly below: JSON bodies aren't covered by the automatic check

        app.MapPost("/webhooks/stripe", HandleStripeWebhookAsync).DisableAntiforgery();

        app.MapGet("/healthz", () => Results.Text("ok"));

        return app;
    }

    /// <summary>Only mapped with the development payment provider, to stand in for Stripe.js and hosted onboarding.</summary>
    public static IEndpointRouteBuilder MapDevelopmentPaymentEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/dev/payments/{paymentId}/authorize", async (string paymentId, HttpContext context, IAntiforgery antiforgery, DevelopmentPaymentProvider payments) =>
        {
            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.BadRequest();
            }

            return payments.SimulateAuthorization(paymentId) ? Results.NoContent() : Results.NotFound();
        }).DisableAntiforgery();

        app.MapGet(DevelopmentPaymentProvider.OnboardingPath, (string account, DevelopmentPaymentProvider payments) =>
            payments.CompleteOnboarding(account) ? Results.Redirect("/payouts/return") : Results.NotFound());

        return app;
    }

    private static async Task<IResult> SendStampAsync(
        string handle,
        SendStampRequest request,
        HttpContext context,
        IAntiforgery antiforgery,
        SendStampService sending,
        AppUrls urls,
        CancellationToken cancellationToken)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.BadRequest(new { error = "antiforgery", message = "Your session expired. Reload the page and try again." });
        }

        var result = await sending.SendAsync(
            new SendStampCommand(handle, request.SenderName ?? "", request.SenderEmail ?? "", request.Subject ?? "", request.Body ?? ""),
            cancellationToken);

        if (!result.IsSuccess)
        {
            var status = result.Error.Code switch
            {
                ErrorCodes.NotFound => StatusCodes.Status404NotFound,
                ErrorCodes.RateLimited => StatusCodes.Status429TooManyRequests,
                ErrorCodes.PaymentUnavailable => StatusCodes.Status503ServiceUnavailable,
                ErrorCodes.NotAcceptingStamps or ErrorCodes.SenderBlocked => StatusCodes.Status409Conflict,
                _ => StatusCodes.Status400BadRequest,
            };
            return Results.Json(new { error = result.Error.Code, message = result.Error.Message, field = FieldName(result.Error.Target) }, statusCode: status);
        }

        var sent = result.Value;
        return Results.Ok(new SendStampResponse(sent.MessageId, sent.PaymentId, sent.ClientSecret, urls.SentPage(handle.ToLowerInvariant(), sent.MessageId)));
    }

    private static async Task<IResult> HandleStripeWebhookAsync(
        HttpRequest request,
        IPaymentWebhookParser parser,
        PaymentWebhookHandler handler,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(request.Body);
        var payload = await reader.ReadToEndAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        PaymentWebhookEvent webhookEvent;
        try
        {
            webhookEvent = parser.Parse(payload, headers);
        }
        catch (InvalidWebhookSignatureException ex)
        {
            loggers.CreateLogger("Stamp.Webhooks").LogWarning(ex, "Rejected a webhook with an invalid signature.");
            return Results.BadRequest();
        }

        // Any exception from here on returns 500, so Stripe retries the delivery.
        var outcome = await handler.HandleAsync(webhookEvent, cancellationToken);
        return Results.Ok(new { received = true, outcome = outcome.ToString() });
    }

    /// <summary>Maps the domain's property names to the form's field names.</summary>
    private static string? FieldName(string? target) => target switch
    {
        "SenderName" => "senderName",
        "SenderEmail" => "senderEmail",
        "Subject" => "subject",
        "Body" => "body",
        _ => null,
    };
}
