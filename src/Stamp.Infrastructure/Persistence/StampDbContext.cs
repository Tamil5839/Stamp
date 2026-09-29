using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Stamp.Application.Abstractions;
using Stamp.Application.Emails;
using Stamp.Application.Payments;
using Stamp.Domain.Auth;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Infrastructure.Persistence;

/// <summary>
/// Provider-neutral model. Each database provider has its own subclass so that it gets its own
/// migration set (<see cref="SqliteStampDbContext"/>, <see cref="PostgresStampDbContext"/>).
/// </summary>
public abstract class StampDbContext : DbContext, IStampDbContext, IDataProtectionKeyContext
{
    /// <summary>Shadow property regenerated on every save and checked by UPDATE statements.</summary>
    public const string ConcurrencyStamp = "ConcurrencyStamp";

    protected StampDbContext(DbContextOptions options)
        : base(options)
    {
    }

    public DbSet<Receiver> Receivers => Set<Receiver>();

    public DbSet<StampedMessage> Messages => Set<StampedMessage>();

    public DbSet<BlockedSender> BlockedSenders => Set<BlockedSender>();

    public DbSet<LoginToken> LoginTokens => Set<LoginToken>();

    public DbSet<OutboxEmail> OutboxEmails => Set<OutboxEmail>();

    public DbSet<ProcessedWebhookEvent> ProcessedWebhookEvents => Set<ProcessedWebhookEvent>();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        RenewConcurrencyStamps();
        try
        {
            return base.SaveChanges(acceptAllChangesOnSuccess);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && IsUniqueViolation(ex))
        {
            throw new DuplicateKeyException("A record with the same unique key already exists.", ex);
        }
    }

    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        RenewConcurrencyStamps();
        try
        {
            return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
        }
        catch (DbUpdateException ex) when (ex is not DbUpdateConcurrencyException && IsUniqueViolation(ex))
        {
            throw new DuplicateKeyException("A record with the same unique key already exists.", ex);
        }
    }

    protected abstract bool IsUniqueViolation(DbUpdateException exception);

    protected override void OnModelCreating(ModelBuilder modelBuilder) =>
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(StampDbContext).Assembly);

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Enums are stored by name so the database stays readable and reordering them is safe.
        configurationBuilder.Properties<MessageStatus>().HaveConversion<string>().HaveMaxLength(32);
        configurationBuilder.Properties<PaymentStatus>().HaveConversion<string>().HaveMaxLength(32);
    }

    private void RenewConcurrencyStamps()
    {
        foreach (var entry in ChangeTracker.Entries())
        {
            if (entry.State is EntityState.Added or EntityState.Modified
                && entry.Metadata.FindProperty(ConcurrencyStamp) is not null)
            {
                entry.Property(ConcurrencyStamp).CurrentValue = Guid.NewGuid();
            }
        }
    }
}
