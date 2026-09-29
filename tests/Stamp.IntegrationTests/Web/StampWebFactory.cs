using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Stamp.Application.Abstractions;
using Stamp.Application.Emails;
using Stamp.Domain.Messages;
using Stamp.IntegrationTests.Stripe;
using Stamp.Web.Endpoints;
using Stamp.Web.Infrastructure;

namespace Stamp.IntegrationTests.Web;

/// <summary>
/// The real web app on a throwaway SQLite file, with the development payment provider standing in
/// for Stripe (Stripe-format webhooks are still verified with <see cref="WebhookSecret"/>), emails
/// kept in the outbox table, background services off and a controllable clock.
/// </summary>
public sealed partial class StampWebFactory : WebApplicationFactory<Program>
{
    public const string WebhookSecret = "whsec_integration_tests";

    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"stamp-web-{Guid.NewGuid():N}.db");
    private readonly DirectoryInfo _emailDirectory = Directory.CreateTempSubdirectory("stamp-web-emails-");
    private readonly Dictionary<string, string> _settings;

    public StampWebFactory()
        : this(_ => { })
    {
    }

    private StampWebFactory(Action<Dictionary<string, string>> configure)
    {
        _settings = new Dictionary<string, string>
        {
            ["ConnectionStrings:Stamp"] = $"Data Source={_databasePath};Pooling=False",
            ["Database:Provider"] = "Sqlite",
            ["Payments:Provider"] = "Fake",
            ["Stripe:WebhookSecret"] = WebhookSecret,
            ["Email:Provider"] = "File",
            ["Email:FileDirectory"] = _emailDirectory.FullName,
            ["Email:Outbox:DispatcherEnabled"] = "false",
            ["Jobs:Expiry:Enabled"] = "false",
            ["App:BaseUrl"] = "http://localhost",
            ["RateLimits:SendStampPerIp"] = "1000",
            ["RateLimits:LoginPerIp"] = "1000",
            ["Stamps:SenderRateLimit"] = "1000",
            ["Logging:LogLevel:Default"] = "Warning",
            ["Logging:LogLevel:Microsoft.AspNetCore.HttpsPolicy"] = "Error",
            ["Logging:LogLevel:Microsoft.AspNetCore.DataProtection"] = "Error",
        };
        configure(_settings);
    }

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    /// <summary>An app instance with some settings overridden (the fixture constructor must stay parameterless).</summary>
    public static StampWebFactory With(Action<Dictionary<string, string>> configure) => new(configure);

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    [GeneratedRegex("""<input[^>]*name="__RequestVerificationToken"[^>]*value="([^"]+)"[^>]*>""")]
    private static partial Regex AntiforgeryTokenPattern();

    [GeneratedRegex(@"token=([A-Za-z0-9_\-%]+)")]
    private static partial Regex MagicTokenPattern();

    public HttpClient Browser(bool followRedirects = true) =>
        CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true });

    public async Task<string> TokenFromAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path, Ct);
        var match = AntiforgeryTokenPattern().Match(html);
        Assert.True(match.Success, $"No antiforgery token on {path}.");
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }

    /// <summary>Submits a form the way a browser would, with the token from the page that renders it.</summary>
    public async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string path, Dictionary<string, string> fields, string? pagePath = null)
    {
        var form = new Dictionary<string, string>(fields)
        {
            ["__RequestVerificationToken"] = await TokenFromAsync(client, pagePath ?? path),
        };
        return await client.PostAsync(path, new FormUrlEncodedContent(form), Ct);
    }

    public async Task SignInAsync(HttpClient client, string email)
    {
        var request = await PostFormAsync(client, "/auth/login", new() { ["Email"] = email });
        Assert.Equal(HttpStatusCode.OK, request.StatusCode);

        var token = await LatestMagicLinkTokenAsync(email);
        var verify = await PostFormAsync(client, "/auth/verify?token=" + Uri.EscapeDataString(token), new() { ["Token"] = token });
        Assert.Equal(HttpStatusCode.OK, verify.StatusCode);
    }

    /// <summary>Signs in, publishes a profile and completes (fake) payouts onboarding.</summary>
    public async Task<HttpClient> CreateReceiverAsync(string handle, decimal price = 5m)
    {
        var client = Browser();
        await SignInAsync(client, $"{handle}@receivers.test");

        var saved = await PostFormAsync(client, "/settings", new()
        {
            ["Input.Handle"] = handle,
            ["Input.DisplayName"] = "Kalai",
            ["Input.Bio"] = "Founder. I read everything with a stamp.",
            ["Input.PhotoUrl"] = "",
            ["Input.PriceDollars"] = price.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Input.DonateToCharity"] = "false",
        });
        Assert.Equal(HttpStatusCode.OK, saved.StatusCode);

        var payouts = await PostFormAsync(client, "/payouts?handler=Start", new(), pagePath: "/payouts");
        Assert.Contains("Payouts are set up", await payouts.Content.ReadAsStringAsync(Ct));
        return client;
    }

    public async Task<HttpResponseMessage> TrySendStampAsync(HttpClient sender, string handle, string senderEmail = "ada@example.com", bool includeToken = true)
    {
        var token = await TokenFromAsync(sender, $"/{handle}");
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/{handle}/stamp")
        {
            Content = JsonContent.Create(new SendStampRequest("Ada Lovelace", senderEmail, "Quick question", "Would you review my engine notes?")),
        };
        if (includeToken)
        {
            request.Headers.Add("RequestVerificationToken", token);
        }

        return await sender.SendAsync(request, Ct);
    }

    /// <summary>Sends a stamp and has the (fake) card authorized, as Stripe.js would.</summary>
    public async Task<SendStampResponse> SendAuthorizedStampAsync(HttpClient sender, string handle, string senderEmail = "ada@example.com")
    {
        var response = await TrySendStampAsync(sender, handle, senderEmail);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sent = (await response.Content.ReadFromJsonAsync<SendStampResponse>(Ct))!;

        using var authorize = new HttpRequestMessage(HttpMethod.Post, $"/dev/payments/{sent.PaymentId}/authorize");
        authorize.Headers.Add("RequestVerificationToken", await TokenFromAsync(sender, $"/{handle}"));
        Assert.Equal(HttpStatusCode.NoContent, (await sender.SendAsync(authorize, Ct)).StatusCode);
        return sent;
    }

    public async Task<HttpResponseMessage> DeliverWebhookAsync(string payload, string? secret = null)
    {
        using var client = Browser();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/webhooks/stripe")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.Add("Stripe-Signature", StripeSignatures.Header(payload, secret ?? WebhookSecret, Time.GetUtcNow()));
        return await client.SendAsync(request, Ct);
    }

    public Task<HttpResponseMessage> DeliverAuthorizedWebhookAsync(string paymentId, string? eventId = null) =>
        DeliverWebhookAsync(StripeSignatures.PaymentIntentEvent(
            eventId ?? $"evt_{Guid.NewGuid():N}", "payment_intent.amount_capturable_updated", paymentId, "requires_capture", Time.GetUtcNow()));

    public async Task<T> QueryAsync<T>(Func<IStampDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        return await query(scope.ServiceProvider.GetRequiredService<IStampDbContext>());
    }

    public Task<StampedMessage> MessageAsync(Guid id) =>
        QueryAsync(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == id, Ct));

    public Task<List<OutboxEmail>> EmailsToAsync(string address) =>
        QueryAsync(db => db.OutboxEmails.AsNoTracking().Where(e => e.To == address).ToListAsync(Ct));

    public async Task<string> LatestMagicLinkTokenAsync(string email)
    {
        var link = (await EmailsToAsync(email.ToLowerInvariant()))
            .Where(e => e.Template == EmailTemplateNames.MagicLink)
            .OrderBy(e => e.CreatedAt)
            .LastOrDefault() ?? throw new InvalidOperationException($"No sign-in email was queued for {email}.");
        return Uri.UnescapeDataString(MagicTokenPattern().Match(link.TextBody).Groups[1].Value);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(WebComposition.TestingEnvironment);
        foreach (var (key, value) in _settings)
        {
            builder.UseSetting(key, value);
        }

        builder.ConfigureTestServices(services => services.AddSingleton<TimeProvider>(Time));
    }

    public override async ValueTask DisposeAsync()
    {
        await base.DisposeAsync();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm", "-journal" })
        {
            File.Delete(_databasePath + suffix);
        }

        _emailDirectory.Delete(recursive: true);
    }
}
