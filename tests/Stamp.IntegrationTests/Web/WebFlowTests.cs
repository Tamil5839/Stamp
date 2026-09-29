using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Stamp.Application.Emails;
using Stamp.Domain.Messages;
using Stamp.Infrastructure.Jobs;
using Stamp.Infrastructure.Payments.Fake;
using Stamp.Application.Payments;
using Stamp.IntegrationTests.Stripe;

namespace Stamp.IntegrationTests.Web;

/// <summary>End-to-end flows through HTTP: pages, forms with antiforgery, the JSON endpoint and signed webhooks.</summary>
public sealed class WebFlowTests(StampWebFactory factory) : IClassFixture<StampWebFactory>
{
    private static CancellationToken Ct => StampWebFactory.Ct;

    private static string NewHandle() => $"r{Guid.NewGuid():N}"[..16];

    [Fact]
    public async Task A_stamp_goes_from_public_page_to_inbox_to_a_captured_reply()
    {
        var handle = NewHandle();
        var receiver = await factory.CreateReceiverAsync(handle, price: 7.5m);
        var sender = factory.Browser();

        var page = await sender.GetStringAsync($"/{handle}", Ct);
        Assert.Contains("$7.50", page);
        Assert.Contains("only charged if Kalai replies within 6 days", page);

        var sent = await factory.SendAuthorizedStampAsync(sender, handle);
        Assert.Equal($"http://localhost/{handle}/sent?m={sent.MessageId}", sent.ReturnUrl);
        Assert.Equal(HttpStatusCode.OK, (await factory.DeliverAuthorizedWebhookAsync(sent.PaymentId)).StatusCode);

        var inbox = await receiver.GetStringAsync("/inbox", Ct);
        Assert.Contains("Ada Lovelace", inbox);
        Assert.Contains("Quick question", inbox);
        Assert.Contains("6d 0h left", inbox);
        Assert.Contains("$6.75", inbox); // earnings after the 10% fee

        var reply = await factory.PostFormAsync(
            receiver,
            $"/inbox/{sent.MessageId}?handler=Reply",
            new() { ["Reply"] = "Happy to help. Send the notes over on Friday." },
            pagePath: $"/inbox/{sent.MessageId}");
        var afterReply = await reply.Content.ReadAsStringAsync(Ct);
        Assert.Contains("Reply sent. You earned $6.75.", afterReply);

        var message = await factory.MessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Replied, message.Status);
        Assert.Equal(PaymentStatus.Captured, message.PaymentStatus);

        var toSender = await factory.EmailsToAsync("ada@example.com");
        Assert.Contains(toSender, e => e.Template == EmailTemplateNames.StampSent);
        Assert.Contains(toSender, e => e.Template == EmailTemplateNames.StampReplied && e.TextBody.Contains("Send the notes over on Friday"));
        Assert.Contains(await factory.EmailsToAsync($"{handle}@receivers.test"), e => e.Template == EmailTemplateNames.StampReceived);
    }

    [Fact]
    public async Task The_return_page_confirms_the_stamp_even_before_the_webhook_arrives()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);
        var sender = factory.Browser();
        var sent = await factory.SendAuthorizedStampAsync(sender, handle);

        var confirmation = await sender.GetStringAsync(sent.ReturnUrl + "&payment_intent=" + sent.PaymentId + "&redirect_status=succeeded", Ct);

        Assert.Contains("Stamped and delivered", confirmation);
        Assert.Equal(MessageStatus.Pending, (await factory.MessageAsync(sent.MessageId)).Status);
    }

    [Fact]
    public async Task Declining_from_the_inbox_releases_the_hold()
    {
        var handle = NewHandle();
        var receiver = await factory.CreateReceiverAsync(handle);
        var sent = await factory.SendAuthorizedStampAsync(factory.Browser(), handle, "grace@example.com");
        await factory.DeliverAuthorizedWebhookAsync(sent.PaymentId);

        var response = await factory.PostFormAsync(receiver, $"/inbox/{sent.MessageId}?handler=Decline", new(), pagePath: $"/inbox/{sent.MessageId}");

        Assert.Contains("Declined. The sender wasn&#x27;t charged.", await response.Content.ReadAsStringAsync(Ct));
        var message = await factory.MessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Declined, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.Contains(await factory.EmailsToAsync("grace@example.com"), e => e.Template == EmailTemplateNames.StampDeclined);
    }

    [Fact]
    public async Task A_blocked_sender_cannot_send_another_stamp()
    {
        var handle = NewHandle();
        var receiver = await factory.CreateReceiverAsync(handle);
        var sender = factory.Browser();
        var sent = await factory.SendAuthorizedStampAsync(sender, handle, "pest@example.com");
        await factory.DeliverAuthorizedWebhookAsync(sent.PaymentId);

        await factory.PostFormAsync(receiver, $"/inbox/{sent.MessageId}?handler=Block", new(), pagePath: $"/inbox/{sent.MessageId}");
        var retry = await factory.TrySendStampAsync(sender, handle, "PEST@example.com");

        Assert.Equal(HttpStatusCode.Conflict, retry.StatusCode);
        var body = await retry.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("sender_blocked", body.GetProperty("error").GetString());
        Assert.Contains("pest@example.com", await receiver.GetStringAsync("/settings/blocked", Ct));
    }

    [Fact]
    public async Task Pages_for_unknown_handles_are_not_found_and_capitals_redirect()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);
        var visitor = factory.Browser(followRedirects: false);

        Assert.Equal(HttpStatusCode.NotFound, (await visitor.GetAsync("/nobody_home", Ct)).StatusCode);

        var capitalized = await visitor.GetAsync("/" + handle.ToUpperInvariant(), Ct);
        Assert.Equal(HttpStatusCode.MovedPermanently, capitalized.StatusCode);
        Assert.Equal($"/{handle}", capitalized.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task A_page_whose_payouts_are_not_set_up_does_not_take_stamps()
    {
        var client = factory.Browser();
        var handle = NewHandle();
        await factory.SignInAsync(client, $"{handle}@receivers.test");
        await factory.PostFormAsync(client, "/settings", new()
        {
            ["Input.Handle"] = handle,
            ["Input.DisplayName"] = "Kalai",
            ["Input.PriceDollars"] = "5",
        });

        var page = await factory.Browser().GetStringAsync($"/{handle}", Ct);

        Assert.Contains("Not accepting stamps yet", page);
        Assert.DoesNotContain("stamp-form", page);
    }

    [Fact]
    public async Task Receiver_pages_require_signing_in()
    {
        var anonymous = factory.Browser(followRedirects: false);

        foreach (var path in new[] { "/inbox", "/settings", "/payouts", $"/inbox/{Guid.NewGuid()}" })
        {
            var response = await anonymous.GetAsync(path, Ct);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.StartsWith("http://localhost/auth/login", response.Headers.Location!.ToString());
        }
    }

    [Fact]
    public async Task Receivers_cannot_open_each_others_messages()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);
        var sent = await factory.SendAuthorizedStampAsync(factory.Browser(), handle);
        await factory.DeliverAuthorizedWebhookAsync(sent.PaymentId);
        var someoneElse = await factory.CreateReceiverAsync(NewHandle());

        var response = await someoneElse.GetAsync($"/inbox/{sent.MessageId}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task A_sign_in_link_works_only_once()
    {
        var email = $"{NewHandle()}@receivers.test";
        await factory.SignInAsync(factory.Browser(), email);
        var token = await factory.LatestMagicLinkTokenAsync(email);

        var replaying = factory.Browser(followRedirects: false);
        var verifyPath = "/auth/verify?token=" + Uri.EscapeDataString(token);
        Assert.Contains("This link has expired", await replaying.GetStringAsync(verifyPath, Ct));

        // Even a direct POST (with a valid antiforgery token from another page) can't reuse it.
        var replay = await factory.PostFormAsync(replaying, verifyPath, new() { ["Token"] = token }, pagePath: "/auth/login");

        Assert.Contains("This link has expired", await replay.Content.ReadAsStringAsync(Ct));
        Assert.Equal(HttpStatusCode.Redirect, (await replaying.GetAsync("/inbox", Ct)).StatusCode);
    }

    [Fact]
    public async Task The_stamp_endpoint_requires_the_antiforgery_token()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);

        var response = await factory.TrySendStampAsync(factory.Browser(), handle, includeToken: false);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Invalid_messages_come_back_with_the_offending_field()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);
        var sender = factory.Browser();
        var token = await factory.TokenFromAsync(sender, $"/{handle}");

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{handle}/stamp")
        {
            Content = JsonContent.Create(new { senderName = "Ada", senderEmail = "not-an-email", subject = "Hi", body = "Hello" }),
        };
        request.Headers.Add("RequestVerificationToken", token);
        var response = await sender.SendAsync(request, Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Ct);
        Assert.Equal("senderEmail", body.GetProperty("field").GetString());
    }

    [Fact]
    public async Task Webhooks_with_a_bad_signature_are_rejected_and_redeliveries_apply_once()
    {
        var handle = NewHandle();
        await factory.CreateReceiverAsync(handle);
        var sent = await factory.SendAuthorizedStampAsync(factory.Browser(), handle, "hopper@example.com");
        var payload = StripeSignatures.PaymentIntentEvent("evt_" + Guid.NewGuid().ToString("N"), "payment_intent.amount_capturable_updated", sent.PaymentId, "requires_capture", factory.Time.GetUtcNow());

        Assert.Equal(HttpStatusCode.BadRequest, (await factory.DeliverWebhookAsync(payload, secret: "whsec_forged")).StatusCode);
        Assert.Equal(MessageStatus.AwaitingPayment, (await factory.MessageAsync(sent.MessageId)).Status);

        var first = await factory.DeliverWebhookAsync(payload);
        var second = await factory.DeliverWebhookAsync(payload);

        Assert.Contains("Processed", await first.Content.ReadAsStringAsync(Ct));
        Assert.Contains("Duplicate", await second.Content.ReadAsStringAsync(Ct));
        Assert.Single(await factory.EmailsToAsync("hopper@example.com"), e => e.Template == EmailTemplateNames.StampSent);
    }

    [Fact]
    public async Task Health_endpoint_answers()
    {
        Assert.Equal("ok", await factory.Browser().GetStringAsync("/healthz", Ct));
    }
}

/// <summary>Scenarios that need their own app instance: a moving clock or tight limits.</summary>
public sealed class IsolatedWebTests
{
    private static CancellationToken Ct => StampWebFactory.Ct;

    [Fact]
    public async Task Unanswered_stamps_expire_after_six_days_and_show_in_history()
    {
        await using var factory = new StampWebFactory();
        var receiver = await factory.CreateReceiverAsync("kalai");
        var sent = await factory.SendAuthorizedStampAsync(factory.Browser(), "kalai");
        await factory.DeliverAuthorizedWebhookAsync(sent.PaymentId);

        factory.Time.Advance(TimeSpan.FromDays(6));
        var run = await factory.Services.GetRequiredService<StampExpiryJob>().RunOnceAsync(Ct);

        Assert.Equal(1, run!.Expired);
        var message = await factory.MessageAsync(sent.MessageId);
        Assert.Equal(MessageStatus.Expired, message.Status);
        Assert.Equal(PaymentStatus.Canceled, message.PaymentStatus);
        Assert.Equal(ProviderPaymentState.Canceled, await factory.Services.GetRequiredService<DevelopmentPaymentProvider>().GetPaymentStateAsync(sent.PaymentId, Ct));

        var history = await receiver.GetStringAsync("/inbox?view=history", Ct);
        Assert.Contains("Expired", history);
        Assert.Contains("stamp-chip--void", history); // shown as not collected
        Assert.Contains(await factory.EmailsToAsync("ada@example.com"), e => e.Template == EmailTemplateNames.StampExpired);
    }

    [Fact]
    public async Task Stamp_submissions_are_rate_limited_per_client()
    {
        await using var factory = StampWebFactory.With(settings => settings["RateLimits:SendStampPerIp"] = "2");
        await factory.CreateReceiverAsync("kalai");
        var sender = factory.Browser();

        Assert.Equal(HttpStatusCode.OK, (await factory.TrySendStampAsync(sender, "kalai", "one@example.com")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await factory.TrySendStampAsync(sender, "kalai", "two@example.com")).StatusCode);
        var third = await factory.TrySendStampAsync(sender, "kalai", "three@example.com");

        Assert.Equal(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    [Fact]
    public async Task Sign_in_requests_are_rate_limited_per_client()
    {
        await using var factory = StampWebFactory.With(settings => settings["RateLimits:LoginPerIp"] = "1");
        var visitor = factory.Browser();

        Assert.Equal(HttpStatusCode.OK, (await factory.PostFormAsync(visitor, "/auth/login", new() { ["Email"] = "a@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await factory.PostFormAsync(visitor, "/auth/login", new() { ["Email"] = "b@example.com" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await visitor.GetAsync("/auth/login", Ct)).StatusCode);
    }
}
