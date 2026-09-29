using Microsoft.EntityFrameworkCore;
using Stamp.Application.Abstractions;
using Stamp.Application.Emails;
using Stamp.Application.Payments;
using Stamp.Domain.Auth;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;
using Stamp.Infrastructure.Persistence;

namespace Stamp.IntegrationTests.Persistence;

/// <summary>Runs against every provider we ship migrations for.</summary>
public abstract class PersistenceTests(ITestDatabase database)
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Receivers_and_messages_round_trip_with_every_field()
    {
        database.SkipIfUnavailable();
        var receiver = await AddReceiverAsync();
        var message = NewPendingMessage(receiver.Id);
        message.Reply("Thanks for the stamp, here is my answer.", T0.AddDays(2));

        await using (var db = database.CreateContext())
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = database.CreateContext())
        {
            var loaded = await db.Messages.SingleAsync(m => m.Id == message.Id, Ct);
            Assert.Equal(receiver.Id, loaded.ReceiverId);
            Assert.Equal("ada@example.com", loaded.SenderEmail);
            Assert.Equal(MessageStatus.Replied, loaded.Status);
            Assert.Equal(PaymentStatus.Authorized, loaded.PaymentStatus);
            Assert.Equal(message.PaymentId, loaded.PaymentId);
            Assert.Equal(T0, loaded.AuthorizedAt);
            Assert.Equal(T0.AddDays(6), loaded.ExpiresAt);
            Assert.Equal(T0.AddDays(2), loaded.ResolvedAt);
            Assert.Equal(TimeSpan.Zero, loaded.ExpiresAt!.Value.Offset);
            Assert.Equal("Thanks for the stamp, here is my answer.", loaded.ReplyBody);
            Assert.Equal(500, loaded.AmountCents);
            Assert.Equal(50, loaded.PlatformFeeCents);

            var loadedReceiver = await db.Receivers.SingleAsync(r => r.Id == receiver.Id, Ct);
            Assert.Equal(receiver.Handle, loadedReceiver.Handle);
            Assert.Equal("https://example.com/me.jpg", loadedReceiver.PhotoUrl);
            Assert.True(loadedReceiver.DonateToCharity);
        }
    }

    [Fact]
    public async Task Enums_are_stored_by_name()
    {
        database.SkipIfUnavailable();
        var receiver = await AddReceiverAsync();
        var message = NewPendingMessage(receiver.Id);

        await using var db = database.CreateContext();
        db.Messages.Add(message);
        await db.SaveChangesAsync(Ct);

        var stored = await db.Database
            .SqlQueryRaw<string>("""SELECT "Status" AS "Value" FROM "Messages" WHERE "PaymentId" = {0}""", message.PaymentId!)
            .SingleAsync(Ct);
        Assert.Equal("Pending", stored);
    }

    [Fact]
    public async Task Expiry_queries_filter_and_sort_timestamps_in_the_database()
    {
        database.SkipIfUnavailable();
        var receiver = await AddReceiverAsync();
        var late = NewPendingMessage(receiver.Id, authorizedAt: T0.AddHours(5));
        var early = NewPendingMessage(receiver.Id, authorizedAt: T0.AddHours(1));
        var notDue = NewPendingMessage(receiver.Id, authorizedAt: T0.AddDays(3));

        await using var db = database.CreateContext();
        db.Messages.AddRange(late, early, notDue);
        await db.SaveChangesAsync(Ct);

        var cutoff = T0.AddDays(6).AddHours(6);
        var due = await db.Messages
            .Where(m => m.ReceiverId == receiver.Id && m.Status == MessageStatus.Pending && m.ExpiresAt <= cutoff)
            .OrderBy(m => m.ExpiresAt)
            .Select(m => m.Id)
            .ToListAsync(Ct);

        Assert.Equal([early.Id, late.Id], due);
    }

    [Fact]
    public async Task Concurrent_updates_to_a_message_are_detected()
    {
        database.SkipIfUnavailable();
        var receiver = await AddReceiverAsync();
        var message = NewPendingMessage(receiver.Id);
        await using (var db = database.CreateContext())
        {
            db.Messages.Add(message);
            await db.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var replying = await first.Messages.SingleAsync(m => m.Id == message.Id, Ct);
        var expiring = await second.Messages.SingleAsync(m => m.Id == message.Id, Ct);

        replying.Reply("A reply that wins the race to the database.", T0.AddDays(5));
        await first.SaveChangesAsync(Ct);

        expiring.Expire(T0.AddDays(6));
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));

        await using var check = database.CreateContext();
        Assert.Equal(MessageStatus.Replied, (await check.Messages.SingleAsync(m => m.Id == message.Id, Ct)).Status);
    }

    [Fact]
    public async Task A_login_token_cannot_be_redeemed_twice_concurrently()
    {
        database.SkipIfUnavailable();
        var (token, _) = LoginToken.Issue($"{Guid.NewGuid():N}@example.com", T0, TimeSpan.FromMinutes(15));
        await using (var db = database.CreateContext())
        {
            db.LoginTokens.Add(token);
            await db.SaveChangesAsync(Ct);
        }

        await using var first = database.CreateContext();
        await using var second = database.CreateContext();
        var a = await first.LoginTokens.SingleAsync(t => t.Id == token.Id, Ct);
        var b = await second.LoginTokens.SingleAsync(t => t.Id == token.Id, Ct);

        a.Consume(T0.AddMinutes(1));
        b.Consume(T0.AddMinutes(1));
        await first.SaveChangesAsync(Ct);

        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => second.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task Handles_are_unique()
    {
        database.SkipIfUnavailable();
        var existing = await AddReceiverAsync();

        await using var db = database.CreateContext();
        var other = Receiver.Register($"{Guid.NewGuid():N}@example.com", T0);
        other.UpdateProfile(existing.Handle!, "Someone else", null, null, 500, false, T0);
        db.Receivers.Add(other);

        await Assert.ThrowsAsync<DuplicateKeyException>(() => db.SaveChangesAsync(Ct));
    }

    [Fact]
    public async Task A_webhook_event_can_only_be_recorded_once()
    {
        database.SkipIfUnavailable();
        var eventId = $"evt_{Guid.NewGuid():N}";
        await using (var db = database.CreateContext())
        {
            db.ProcessedWebhookEvents.Add(ProcessedWebhookEvent.Create(eventId, "payment_intent.succeeded", T0));
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = database.CreateContext())
        {
            db.ProcessedWebhookEvents.Add(ProcessedWebhookEvent.Create(eventId, "payment_intent.succeeded", T0));
            await Assert.ThrowsAsync<DuplicateKeyException>(() => db.SaveChangesAsync(Ct));
        }
    }

    [Fact]
    public async Task Outbox_emails_round_trip()
    {
        database.SkipIfUnavailable();
        var email = OutboxEmail.Create(new EmailMessage("ada@example.com", "Hi", "text", "<p>html</p>"), "test", Guid.NewGuid(), T0);
        email.MarkFailed("provider down", T0, maxAttempts: 5);

        await using (var db = database.CreateContext())
        {
            db.OutboxEmails.Add(email);
            await db.SaveChangesAsync(Ct);
        }

        await using (var db = database.CreateContext())
        {
            var loaded = await db.OutboxEmails.SingleAsync(e => e.Id == email.Id, Ct);
            Assert.Equal(1, loaded.Attempts);
            Assert.Equal("provider down", loaded.LastError);
            Assert.Equal(T0.AddSeconds(30), loaded.NextAttemptAt);
            Assert.Null(loaded.SentAt);
        }
    }

    private async Task<Receiver> AddReceiverAsync()
    {
        var receiver = Receiver.Register($"{Guid.NewGuid():N}@example.com", T0);
        receiver.UpdateProfile($"r{Guid.NewGuid():N}"[..20], "Kalai", "Busy founder", "https://example.com/me.jpg", 500, true, T0);

        await using var db = database.CreateContext();
        db.Receivers.Add(receiver);
        await db.SaveChangesAsync(Ct);
        return receiver;
    }

    private static StampedMessage NewPendingMessage(Guid receiverId, DateTimeOffset? authorizedAt = null)
    {
        var message = StampedMessage.Create(receiverId, "Ada", "Ada@Example.com", "Hello", "A question for you", 500, 50, "usd", T0);
        message.AttachPayment($"pi_{Guid.NewGuid():N}");
        message.MarkAuthorized(authorizedAt ?? T0, TimeSpan.FromDays(6));
        return message;
    }
}

public sealed class SqlitePersistenceTests(SqliteTestDatabase database)
    : PersistenceTests(database), IClassFixture<SqliteTestDatabase>;

public sealed class PostgresPersistenceTests(PostgresTestDatabase database)
    : PersistenceTests(database), IClassFixture<PostgresTestDatabase>;

public sealed class MigrationTests
{
    [Fact]
    public void Sqlite_migrations_match_the_model()
    {
        using var db = new SqliteStampDbContext(
            new DbContextOptionsBuilder<SqliteStampDbContext>().UseSqlite("Data Source=:memory:").Options);

        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void Postgres_migrations_match_the_model()
    {
        using var db = new PostgresStampDbContext(
            new DbContextOptionsBuilder<PostgresStampDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

        Assert.False(db.Database.HasPendingModelChanges());
    }
}
