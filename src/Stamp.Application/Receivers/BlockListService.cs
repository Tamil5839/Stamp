using Microsoft.EntityFrameworkCore;
using Stamp.Application.Abstractions;
using Stamp.Application.Common;
using Stamp.Domain.Common;
using Stamp.Domain.Receivers;

namespace Stamp.Application.Receivers;

public sealed record BlockedSenderItem(string Email, DateTimeOffset BlockedAt);

/// <summary>Sender emails a receiver no longer takes stamps from. Checked when a stamp is submitted.</summary>
public sealed class BlockListService(IStampDbContext db, TimeProvider time)
{
    public async Task<IReadOnlyList<BlockedSenderItem>> ListAsync(Guid receiverId, CancellationToken cancellationToken) =>
        await db.BlockedSenders.AsNoTracking()
            .Where(b => b.ReceiverId == receiverId)
            .OrderBy(b => b.Email)
            .Select(b => new BlockedSenderItem(b.Email, b.CreatedAt))
            .ToListAsync(cancellationToken);

    /// <summary>Idempotent: blocking an address twice is fine.</summary>
    public async Task<Result> BlockAsync(Guid receiverId, string email, CancellationToken cancellationToken)
    {
        if (!EmailAddress.IsValid(email))
        {
            return new Error(DomainErrorCodes.InvalidEmail, "Enter a valid email address.", "Email");
        }

        var normalized = EmailAddress.Normalize(email);
        if (await db.BlockedSenders.AnyAsync(b => b.ReceiverId == receiverId && b.Email == normalized, cancellationToken))
        {
            return Result.Success();
        }

        db.BlockedSenders.Add(BlockedSender.Create(receiverId, normalized, time.GetUtcNow()));
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DuplicateKeyException)
        {
            // Blocked from another tab at the same time.
        }

        return Result.Success();
    }

    /// <summary>Blocks whoever sent the given message.</summary>
    public async Task<Result> BlockSenderOfAsync(Guid receiverId, Guid messageId, CancellationToken cancellationToken)
    {
        var senderEmail = await db.Messages.AsNoTracking()
            .Where(m => m.Id == messageId && m.ReceiverId == receiverId)
            .Select(m => m.SenderEmail)
            .SingleOrDefaultAsync(cancellationToken);

        return senderEmail is null ? Error.NotFound() : await BlockAsync(receiverId, senderEmail, cancellationToken);
    }

    public async Task<Result> UnblockAsync(Guid receiverId, string email, CancellationToken cancellationToken)
    {
        var normalized = EmailAddress.Normalize(email ?? string.Empty);
        var entry = await db.BlockedSenders.SingleOrDefaultAsync(b => b.ReceiverId == receiverId && b.Email == normalized, cancellationToken);
        if (entry is not null)
        {
            db.BlockedSenders.Remove(entry);
            await db.SaveChangesAsync(cancellationToken);
        }

        return Result.Success();
    }
}
