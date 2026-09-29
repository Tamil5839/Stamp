using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Stamp.Application.Emails;
using Stamp.Infrastructure.Email;

namespace Stamp.IntegrationTests.Email;

public sealed class ResendEmailSenderTests
{
    private static readonly EmailMessage Message = new("ada@example.com", "Kalai replied: Hi", "Plain body", "<p>Html body</p>");

    private static ResendEmailSender Sender(HttpMessageHandler handler) => new(
        new HttpClient(handler) { BaseAddress = ResendEmailSender.BaseAddress },
        Options.Create(new EmailOptions { From = "Stamp <hello@stamp.test>", ResendApiKey = "re_test_key" }));

    [Fact]
    public async Task Posts_the_email_to_resend_with_the_api_key_and_idempotency_key()
    {
        var handler = new CapturingHandler(HttpStatusCode.OK, """{"id":"email_123"}""");

        await Sender(handler).SendAsync(Message, "outbox-123", TestContext.Current.CancellationToken);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal(new Uri("https://api.resend.com/emails"), handler.Request.RequestUri);
        Assert.Equal("Bearer re_test_key", handler.Request.Headers.Authorization!.ToString());
        Assert.Equal("outbox-123", Assert.Single(handler.Request.Headers.GetValues("Idempotency-Key")));

        using var json = JsonDocument.Parse(handler.Body!);
        var root = json.RootElement;
        Assert.Equal("Stamp <hello@stamp.test>", root.GetProperty("from").GetString());
        Assert.Equal("ada@example.com", Assert.Single(root.GetProperty("to").EnumerateArray()).GetString());
        Assert.Equal("Kalai replied: Hi", root.GetProperty("subject").GetString());
        Assert.Equal("<p>Html body</p>", root.GetProperty("html").GetString());
        Assert.Equal("Plain body", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Provider_errors_surface_so_the_outbox_retries()
    {
        var handler = new CapturingHandler(HttpStatusCode.UnprocessableEntity, """{"message":"The from address is not verified"}""");

        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            Sender(handler).SendAsync(Message, "outbox-123", TestContext.Current.CancellationToken));

        Assert.Contains("422", error.Message);
        Assert.Contains("not verified", error.Message);
    }

    private sealed class CapturingHandler(HttpStatusCode status, string responseBody) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        public string? Body { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Request = request;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(responseBody) };
        }
    }
}

public sealed class FileEmailSenderTests
{
    [Fact]
    public async Task Writes_each_email_to_an_html_file_under_the_content_root()
    {
        var root = Directory.CreateTempSubdirectory("stamp-emails-");
        try
        {
            var sender = new FileEmailSender(
                Options.Create(new EmailOptions { FileDirectory = ".emails" }),
                new HostingEnvironment { ContentRootPath = root.FullName },
                new FakeTimeProvider(new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero)),
                NullLogger<FileEmailSender>.Instance);

            await sender.SendAsync(
                new EmailMessage("Kalai@Example.com", "Your Stamp sign-in link", "Sign in: https://stamp.test/x", "<p>Sign in</p>"),
                "0123456789abcdef",
                TestContext.Current.CancellationToken);

            var file = Assert.Single(Directory.GetFiles(Path.Combine(root.FullName, ".emails")));
            Assert.EndsWith("20261001-120000-kalai-example-com-456789abcdef.html", file);
            var html = await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken);
            Assert.Contains("Subject: Your Stamp sign-in link", html);
            Assert.Contains("<p>Sign in</p>", html);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
