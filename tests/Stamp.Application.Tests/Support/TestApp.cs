using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Application.Emails;
using Stamp.Application.Messages;
using Stamp.Application.Payments;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;
using Stamp.Infrastructure.Persistence;

namespace Stamp.Application.Tests.Support;

/// <summary>
/// The real use cases wired to an in-memory SQLite database, a fake payment provider and a fake
/// clock. Each <see cref="Run{TService,T}"/> call is its own unit of work, like one HTTP request.
/// </summary>
public sealed class TestApp : IAsyncDisposable
{
    public static readonly DateTimeOffset Start = new(2026, 10, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly SqliteConnection _connection;
    private readonly ServiceProvider _services;

    private TestApp(SqliteConnection connection, ServiceProvider services, FakeTimeProvider time, FakePaymentProvider payments)
    {
        _connection = connection;
        _services = services;
        Time = time;
        Payments = payments;
    }

    public FakeTimeProvider Time { get; }

    public FakePaymentProvider Payments { get; }

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static async Task<TestApp> CreateAsync(Action<StampOptions>? configureStamps = null, Action<AuthOptions>? configureAuth = null)
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();

        var time = new FakeTimeProvider(Start);
        var payments = new FakePaymentProvider();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<IPaymentProvider>(payments);
        services.AddSingleton<IAppUrls, TestUrls>();
        services.AddDbContext<SqliteStampDbContext>(options => options.UseSqlite(connection));
        services.AddScoped<StampDbContext>(sp => sp.GetRequiredService<SqliteStampDbContext>());
        services.AddScoped<IStampDbContext>(sp => sp.GetRequiredService<StampDbContext>());
        services.Configure<StampOptions>(options => configureStamps?.Invoke(options));
        services.Configure<AuthOptions>(options => configureAuth?.Invoke(options));
        services.AddStampApplication();

        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        await using (var scope = provider.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<StampDbContext>().Database.MigrateAsync();
        }

        return new TestApp(connection, provider, time, payments);
    }

    public async Task<T> Run<TService, T>(Func<TService, Task<T>> action)
        where TService : notnull
    {
        await using var scope = _services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<TService>());
    }

    public Task<T> Db<T>(Func<IStampDbContext, Task<T>> query) => Run(query);

    public async Task<Receiver> CreateReceiverAsync(string? handle = null, long priceCents = 500, bool payoutsReady = true)
    {
        handle ??= $"r{Guid.NewGuid():N}"[..16];
        var now = Time.GetUtcNow();

        var receiver = Receiver.Register($"{handle}@receivers.test", now);
        receiver.UpdateProfile(handle, "Kalai", "Busy founder", null, priceCents, false, now);
        if (payoutsReady)
        {
            receiver.AttachPayoutAccount($"acct_{handle}", now);
            receiver.SetPayoutsEnabled(true, now);
        }

        await Db(async db =>
        {
            db.Receivers.Add(receiver);
            return await db.SaveChangesAsync(Ct);
        });
        return receiver;
    }

    public Task<Result<SendStampResult>> TrySendStampAsync(string handle, string senderEmail = "ada@example.com", string body = "Would you review my engine notes?") =>
        Run<SendStampService, Result<SendStampResult>>(service => service.SendAsync(
            new SendStampCommand(handle, "Ada Lovelace", senderEmail, "Quick question", body), Ct));

    public async Task<SendStampResult> SendStampAsync(string handle, string senderEmail = "ada@example.com")
    {
        var result = await TrySendStampAsync(handle, senderEmail);
        Assert.True(result.IsSuccess, result.Error?.Message);
        return result.Value;
    }

    public Task<WebhookHandlingResult> DeliverAsync(PaymentWebhookEvent webhookEvent) =>
        Run<PaymentWebhookHandler, WebhookHandlingResult>(handler => handler.HandleAsync(webhookEvent, Ct));

    public PaymentWebhookEvent Event(PaymentWebhookEventKind kind, string objectId, DateTimeOffset? occurredAt = null, string? eventId = null) =>
        new(eventId ?? $"evt_{Guid.NewGuid():N}", kind.ToString(), kind, objectId, occurredAt ?? Time.GetUtcNow());

    /// <summary>Sends a stamp, authorizes the card, and delivers the provider's webhook.</summary>
    public async Task<StampedMessage> CreatePendingStampAsync(Receiver receiver, string senderEmail = "ada@example.com")
    {
        var sent = await SendStampAsync(receiver.Handle!, senderEmail);
        Payments.Authorize(sent.PaymentId);
        await DeliverAsync(Event(PaymentWebhookEventKind.PaymentAuthorized, sent.PaymentId));
        return await GetMessageAsync(sent.MessageId);
    }

    public Task<string> PayoutAccountIdAsync(Guid receiverId) =>
        Db(db => db.Receivers.Where(r => r.Id == receiverId).Select(r => r.PayoutAccountId!).SingleAsync(Ct));

    public Task<StampedMessage> GetMessageAsync(Guid messageId) =>
        Db(db => db.Messages.AsNoTracking().SingleAsync(m => m.Id == messageId, Ct));

    public Task<List<OutboxEmail>> EmailsAsync(Guid? messageId = null) =>
        Db(db => db.OutboxEmails.AsNoTracking()
            .Where(e => messageId == null || e.MessageId == messageId)
            .ToListAsync(Ct));

    public Task<ExpiryRunResult> RunExpiryJobAsync() =>
        Run<StampExpiryService, ExpiryRunResult>(job => job.RunAsync(Ct));

    public async ValueTask DisposeAsync()
    {
        await _services.DisposeAsync();
        await _connection.DisposeAsync();
    }
}

public sealed class TestUrls : IAppUrls
{
    public string MagicLink(string token) => $"https://stamp.test/auth/verify?token={Uri.EscapeDataString(token)}";

    public string Inbox() => "https://stamp.test/inbox";

    public string InboxMessage(Guid messageId) => $"https://stamp.test/inbox/{messageId}";

    public string PublicPage(string handle) => $"https://stamp.test/{handle}";

    public string PayoutsReturn() => "https://stamp.test/payouts/return";

    public string PayoutsRefresh() => "https://stamp.test/payouts/refresh";
}
