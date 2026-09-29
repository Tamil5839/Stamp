using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stamp.Application.Abstractions;
using Stamp.Application.Emails;
using Stamp.Application.Tests.Support;
using Stamp.Infrastructure.Email;

namespace Stamp.Application.Tests.Infrastructure;

public sealed class OutboxProcessorTests
{
    private static async Task<int> ProcessAsync(TestApp app, IEmailSender sender, int maxAttempts = 8)
    {
        await using var scope = app.Services.CreateAsyncScope();
        var processor = new OutboxProcessor(
            scope.ServiceProvider.GetRequiredService<IStampDbContext>(),
            sender,
            app.Time,
            Options.Create(new EmailOptions { Outbox = { MaxAttempts = maxAttempts } }),
            NullLogger<OutboxProcessor>.Instance);
        return await processor.ProcessDueAsync(TestApp.Ct);
    }

    private static async Task<OutboxEmail> SingleEmailAsync(TestApp app) =>
        await app.Db(db => db.OutboxEmails.AsNoTracking().SingleAsync(TestApp.Ct));

    [Fact]
    public async Task Queued_emails_are_sent_once_with_their_outbox_id_as_idempotency_key()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        var sender = new RecordingEmailSender();

        Assert.Equal(2, await ProcessAsync(app, sender));
        Assert.Equal(0, await ProcessAsync(app, sender));

        var emails = await app.EmailsAsync(stamp.Id);
        Assert.All(emails, e => Assert.NotNull(e.SentAt));
        Assert.Equal(emails.Select(e => e.Id.ToString("N")).Order(), sender.Sent.Select(s => s.Key).Order());
        Assert.Contains(sender.Sent, s => s.Message.To == receiver.Email);
        Assert.Contains(sender.Sent, s => s.Message.To == "ada@example.com");
    }

    [Fact]
    public async Task A_failed_send_backs_off_and_is_retried()
    {
        await using var app = await TestApp.CreateAsync();
        await app.Run<Auth.MagicLinkService, Common.Result>(s => s.RequestLinkAsync("kalai@example.com", TestApp.Ct));
        var sender = new RecordingEmailSender { Fail = true };

        Assert.Equal(0, await ProcessAsync(app, sender));

        var failed = await SingleEmailAsync(app);
        Assert.Equal(1, failed.Attempts);
        Assert.Equal("Provider down (simulated).", failed.LastError);
        Assert.Equal(app.Time.GetUtcNow().AddSeconds(30), failed.NextAttemptAt);

        sender.Fail = false;
        app.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.Equal(0, await ProcessAsync(app, sender));

        app.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(1, await ProcessAsync(app, sender));
        Assert.NotNull((await SingleEmailAsync(app)).SentAt);
    }

    [Fact]
    public async Task The_dispatcher_gives_up_after_the_maximum_attempts()
    {
        await using var app = await TestApp.CreateAsync();
        await app.Run<Auth.MagicLinkService, Common.Result>(s => s.RequestLinkAsync("kalai@example.com", TestApp.Ct));
        var sender = new RecordingEmailSender { Fail = true };

        await ProcessAsync(app, sender, maxAttempts: 2);
        app.Time.Advance(TimeSpan.FromMinutes(1));
        await ProcessAsync(app, sender, maxAttempts: 2);
        app.Time.Advance(TimeSpan.FromHours(2));
        await ProcessAsync(app, sender, maxAttempts: 2);

        var email = await SingleEmailAsync(app);
        Assert.Equal(2, email.Attempts);
        Assert.NotNull(email.FailedAt);
        Assert.Equal(2, sender.Attempts);
    }

    [Fact]
    public async Task An_email_leased_by_another_dispatcher_is_skipped_until_the_lease_ends()
    {
        await using var app = await TestApp.CreateAsync();
        await app.Run<Auth.MagicLinkService, Common.Result>(s => s.RequestLinkAsync("kalai@example.com", TestApp.Ct));
        await app.Db(async db =>
        {
            var email = await db.OutboxEmails.SingleAsync(TestApp.Ct);
            email.Lease(app.Time.GetUtcNow(), TimeSpan.FromMinutes(2));
            return await db.SaveChangesAsync(TestApp.Ct);
        });
        var sender = new RecordingEmailSender();

        Assert.Equal(0, await ProcessAsync(app, sender));

        app.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal(1, await ProcessAsync(app, sender));
    }

    private sealed class RecordingEmailSender : IEmailSender
    {
        public List<(EmailMessage Message, string Key)> Sent { get; } = [];

        public bool Fail { get; set; }

        public int Attempts { get; private set; }

        public Task SendAsync(EmailMessage message, string idempotencyKey, CancellationToken cancellationToken)
        {
            Attempts++;
            if (Fail)
            {
                throw new EmailDeliveryException("Provider down (simulated).");
            }

            Sent.Add((message, idempotencyKey));
            return Task.CompletedTask;
        }
    }
}
