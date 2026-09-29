using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stamp.Domain.Common;
using Stamp.Domain.Messages;
using Stamp.Domain.Receivers;

namespace Stamp.Infrastructure.Persistence.Configurations;

internal sealed class StampedMessageConfiguration : IEntityTypeConfiguration<StampedMessage>
{
    public void Configure(EntityTypeBuilder<StampedMessage> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).ValueGeneratedNever();

        builder.HasOne<Receiver>()
            .WithMany()
            .HasForeignKey(m => m.ReceiverId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.Property(m => m.SenderName).HasMaxLength(StampedMessage.MaxSenderNameLength).IsRequired();
        builder.Property(m => m.SenderEmail).HasMaxLength(EmailAddress.MaxLength).IsRequired();
        builder.Property(m => m.Subject).HasMaxLength(StampedMessage.MaxSubjectLength).IsRequired();
        builder.Property(m => m.Body).HasMaxLength(StampedMessage.MaxBodyLength).IsRequired();
        builder.Property(m => m.ReplyBody).HasMaxLength(StampedMessage.MaxReplyLength);
        builder.Property(m => m.Currency).HasMaxLength(3).IsRequired();

        builder.Property(m => m.PaymentId).HasMaxLength(255);
        builder.HasIndex(m => m.PaymentId).IsUnique();

        // Inbox: a receiver's pending stamps ordered by expiry.
        builder.HasIndex(m => new { m.ReceiverId, m.Status, m.ExpiresAt });
        // Expiry job: pending stamps whose window has passed.
        builder.HasIndex(m => new { m.Status, m.ExpiresAt });
        // Per-sender rate limit.
        builder.HasIndex(m => new { m.SenderEmail, m.CreatedAt });

        builder.HasConcurrencyStamp();
    }
}
