using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Stamp.Application.Emails;
using Stamp.Application.Payments;
using Stamp.Domain.Auth;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Abstractions;

/// <summary>
/// The unit of work. Every entity carries an optimistic-concurrency stamp, so a save that races
/// another writer throws <see cref="DbUpdateConcurrencyException"/>; a unique-index clash throws
/// <see cref="DuplicateKeyException"/> whatever the database provider.
/// </summary>
public interface IStampDbContext
{
    DbSet<Receiver> Receivers { get; }

    DbSet<StampedMessage> Messages { get; }

    DbSet<BlockedSender> BlockedSenders { get; }

    DbSet<LoginToken> LoginTokens { get; }

    DbSet<OutboxEmail> OutboxEmails { get; }

    DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents { get; }

    ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}
