using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Stamp.Application.Tests.Support;
using Stamp.Domain.Messages;
using Stamp.Infrastructure.Jobs;

namespace Stamp.Application.Tests.Infrastructure;

public sealed class ExpiryJobHostingTests
{
    [Fact]
    public async Task The_hosted_job_sweeps_at_startup_and_then_every_interval()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var first = await app.CreatePendingStampAsync(receiver, "first@example.com");
        app.Time.Advance(TimeSpan.FromDays(6));

        var job = new StampExpiryJob(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExpiryJobOptions { InitialDelay = TimeSpan.Zero, Interval = TimeSpan.FromHours(1) }),
            app.Time,
            NullLogger<StampExpiryJob>.Instance);

        await job.StartAsync(TestApp.Ct);
        try
        {
            await WaitUntilSettledAsync(app, first.Id);

            var second = await app.CreatePendingStampAsync(receiver, "second@example.com");
            app.Time.Advance(TimeSpan.FromDays(6));

            await WaitUntilSettledAsync(app, second.Id);
        }
        finally
        {
            await job.StopAsync(TestApp.Ct);
        }
    }

    [Fact]
    public async Task A_disabled_job_does_nothing()
    {
        await using var app = await TestApp.CreateAsync();
        var receiver = await app.CreateReceiverAsync();
        var stamp = await app.CreatePendingStampAsync(receiver);
        app.Time.Advance(TimeSpan.FromDays(7));

        var job = new StampExpiryJob(
            app.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ExpiryJobOptions { Enabled = false, InitialDelay = TimeSpan.Zero }),
            app.Time,
            NullLogger<StampExpiryJob>.Instance);

        await job.StartAsync(TestApp.Ct);
        await job.StopAsync(TestApp.Ct);

        Assert.Equal(MessageStatus.Pending, (await app.GetMessageAsync(stamp.Id)).Status);
    }

    /// <summary>Waits for expiry and the hold release, so the job is idle before the test touches the database again.</summary>
    private static async Task WaitUntilSettledAsync(TestApp app, Guid messageId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (true)
        {
            var message = await app.GetMessageAsync(messageId);
            if (message is { Status: MessageStatus.Expired, PaymentStatus: PaymentStatus.Canceled })
            {
                return;
            }

            Assert.True(DateTime.UtcNow < deadline, $"Message {messageId} is still {message.Status}/{message.PaymentStatus}.");
            await Task.Delay(20, TestApp.Ct);
        }
    }
}
