using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stamp.Application.Emails;
using Stamp.Application.Payments;
using Stamp.Domain.Auth;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Infrastructure.Persistence.Configurations;

internal sealed class BlockedSenderConfiguration : IEntityTypeConfiguration<BlockedSender>
{
    public void Configure(EntityTypeBuilder<BlockedSender> builder)
    {
        builder.ToTable("BlockedSenders");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).ValueGeneratedNever();

        builder.HasOne<Receiver>()
            .WithMany()
            .HasForeignKey(b => b.ReceiverId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Property(b => b.Email).HasMaxLength(EmailAddress.MaxLength).IsRequired();
        builder.HasIndex(b => new { b.ReceiverId, b.Email }).IsUnique();
    }
}

internal sealed class LoginTokenConfiguration : IEntityTypeConfiguration<LoginToken>
{
    public void Configure(EntityTypeBuilder<LoginToken> builder)
    {
        builder.ToTable("LoginTokens");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Id).ValueGeneratedNever();

        builder.Property(t => t.Email).HasMaxLength(EmailAddress.MaxLength).IsRequired();
        builder.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
        builder.HasIndex(t => t.TokenHash).IsUnique();
        builder.HasIndex(t => new { t.Email, t.CreatedAt });

        // Two simultaneous redemptions of one link: only the first save wins.
        builder.HasConcurrencyStamp();
    }
}

internal sealed class OutboxEmailConfiguration : IEntityTypeConfiguration<OutboxEmail>
{
    public void Configure(EntityTypeBuilder<OutboxEmail> builder)
    {
        builder.ToTable("OutboxEmails");
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.Template).HasMaxLength(64).IsRequired();
        builder.Property(e => e.To).HasMaxLength(EmailAddress.MaxLength).IsRequired();
        builder.Property(e => e.Subject).HasMaxLength(300).IsRequired();
        builder.Property(e => e.TextBody).IsRequired();
        builder.Property(e => e.HtmlBody).IsRequired();
        builder.Property(e => e.LastError).HasMaxLength(OutboxEmail.MaxErrorLength);

        // Dispatcher: unsent emails that are due.
        builder.HasIndex(e => new { e.SentAt, e.FailedAt, e.NextAttemptAt });
        builder.HasIndex(e => e.MessageId);

        // Two dispatchers leasing the same email: only one wins.
        builder.HasConcurrencyStamp();
    }
}

internal sealed class ProcessedWebhookEventConfiguration : IEntityTypeConfiguration<ProcessedWebhookEvent>
{
    public void Configure(EntityTypeBuilder<ProcessedWebhookEvent> builder)
    {
        builder.ToTable("ProcessedWebhookEvents");
        builder.HasKey(e => e.EventId);
        builder.Property(e => e.EventId).HasMaxLength(255);
        builder.Property(e => e.EventType).HasMaxLength(100).IsRequired();
    }
}

internal static class ConfigurationExtensions
{
    public static void HasConcurrencyStamp<T>(this EntityTypeBuilder<T> builder)
        where T : class =>
        builder.Property<Guid>(StampDbContext.ConcurrencyStamp).IsConcurrencyToken();
}
