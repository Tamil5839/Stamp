using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Infrastructure.Persistence.Configurations;

internal sealed class ReceiverConfiguration : IEntityTypeConfiguration<Receiver>
{
    public void Configure(EntityTypeBuilder<Receiver> builder)
    {
        builder.ToTable("Receivers");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();

        builder.Property(r => r.Email).HasMaxLength(EmailAddress.MaxLength).IsRequired();
        builder.HasIndex(r => r.Email).IsUnique();

        builder.Property(r => r.Handle).HasMaxLength(HandleRules.MaxLength);
        builder.HasIndex(r => r.Handle).IsUnique();

        builder.Property(r => r.DisplayName).HasMaxLength(Receiver.MaxDisplayNameLength).IsRequired();
        builder.Property(r => r.Bio).HasMaxLength(Receiver.MaxBioLength).IsRequired();
        builder.Property(r => r.PhotoUrl).HasMaxLength(Receiver.MaxPhotoUrlLength);
        builder.Property(r => r.Currency).HasMaxLength(3).IsRequired();

        builder.Property(r => r.PayoutAccountId).HasMaxLength(255);
        builder.HasIndex(r => r.PayoutAccountId).IsUnique();

        builder.HasConcurrencyStamp();
    }
}
